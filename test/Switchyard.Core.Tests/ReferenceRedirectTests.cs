using AsmResolver.DotNet;
using Switchyard.Weaver;
using Xunit;

namespace Switchyard.Core.Tests;

/// <summary>
/// Level 1 unit tests for <see cref="ReferenceRedirector"/>.
/// Verifies that the assembly reference table of a caller assembly is
/// rewritten so that references to routed packages point at the renamed
/// <c>{Package}.Switchyard.{Version}</c> assemblies, and that the public key
/// token is cleared on the redirected references.
/// </summary>
public class ReferenceRedirectTests : IDisposable
{
    private readonly string _tempDir;

    public ReferenceRedirectTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "switchyard-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    [Fact]
    public void RedirectReferences_RenamesTargetReference_AndClearsPublicKeyToken()
    {
        string caller = TestAssemblyFactory.CreateCallerReferencingTargetLib(
            Path.Combine(_tempDir, "MainApp.dll"));

        var redirections = new Dictionary<string, string>
        {
            ["TargetLib"] = "TargetLib.Switchyard.1.0.0"
        };
        string outputPath = Path.Combine(_tempDir, "MainApp.Redirected.dll");

        ReferenceRedirector.RedirectReferences(caller, redirections, outputPath);

        var module = ModuleDefinition.FromFile(outputPath);
        var targetRef = module.AssemblyReferences.FirstOrDefault(r => r.Name == "TargetLib.Switchyard.1.0.0");
        Assert.NotNull(targetRef);
        Assert.Null(targetRef.PublicKeyOrToken);
        Assert.False(targetRef.HasPublicKey);

        // The original TargetLib reference must be gone.
        Assert.DoesNotContain(module.AssemblyReferences, r => r.Name == "TargetLib");
    }

    [Fact]
    public void RedirectReferences_WithEmptyRedirections_ProducesNoOutput()
    {
        string caller = TestAssemblyFactory.CreateCallerReferencingTargetLib(
            Path.Combine(_tempDir, "MainApp.dll"));
        string outputPath = Path.Combine(_tempDir, "MainApp.Redirected.dll");

        ReferenceRedirector.RedirectReferences(caller, new Dictionary<string, string>(), outputPath);

        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public void RedirectReferences_WithNoMatchingReference_ProducesNoOutput()
    {
        string caller = TestAssemblyFactory.CreateCallerReferencingTargetLib(
            Path.Combine(_tempDir, "MainApp.dll"));
        var redirections = new Dictionary<string, string>
        {
            ["SomeOtherLib"] = "SomeOtherLib.Switchyard.1.0.0"
        };
        string outputPath = Path.Combine(_tempDir, "MainApp.Redirected.dll");

        ReferenceRedirector.RedirectReferences(caller, redirections, outputPath);

        // No reference matched, so no output file should be written.
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public void RedirectReferences_MultipleRedirections_AppliedSimultaneously()
    {
        string caller = TestAssemblyFactory.CreateAssemblyWithMultipleReferences(
            Path.Combine(_tempDir, "RouteGroupTarget.dll"),
            "RouteGroupTarget",
            ("TargetLib", new Version(1, 0, 0, 0)),
            ("CommonUtils", new Version(1, 0, 0, 0)));

        var redirections = new Dictionary<string, string>
        {
            ["TargetLib"] = "TargetLib.Switchyard.1.0.0",
            ["CommonUtils"] = "CommonUtils.Switchyard.1.0.0"
        };
        string outputPath = Path.Combine(_tempDir, "RouteGroupTarget.Redirected.dll");

        ReferenceRedirector.RedirectReferences(caller, redirections, outputPath);

        var module = ModuleDefinition.FromFile(outputPath);
        Assert.Contains(module.AssemblyReferences, r => r.Name == "TargetLib.Switchyard.1.0.0");
        Assert.Contains(module.AssemblyReferences, r => r.Name == "CommonUtils.Switchyard.1.0.0");
        Assert.DoesNotContain(module.AssemblyReferences, r => r.Name == "TargetLib");
        Assert.DoesNotContain(module.AssemblyReferences, r => r.Name == "CommonUtils");

        // All redirected references should have null public key tokens.
        foreach (var r in module.AssemblyReferences)
        {
            var nameStr = r.Name?.Value ?? string.Empty;
            if (nameStr.EndsWith(".Switchyard.1.0.0", StringComparison.Ordinal))
                Assert.Null(r.PublicKeyOrToken);
        }
    }

    [Fact]
    public void ReferencesAnyPackage_DetectsExistingReference()
    {
        string caller = TestAssemblyFactory.CreateCallerReferencingTargetLib(
            Path.Combine(_tempDir, "MainApp.dll"));

        Assert.True(ReferenceRedirector.ReferencesAnyPackage(caller, new[] { "TargetLib" }));
        Assert.False(ReferenceRedirector.ReferencesAnyPackage(caller, new[] { "NonExistent" }));
    }

    [Fact]
    public void RedirectReferences_RewritesPInvokeModule_ToRoutedNativeName()
    {
        // The caller P/Invokes "nativebinding". Switchyard must rewrite the
        // DllImport module to "nativebinding.Switchyard.1.0.0" so the routed
        // managed version binds its own renamed native library instead of a
        // single shared one.
        string caller = TestAssemblyFactory.CreateAssemblyWithPInvoke(
            Path.Combine(_tempDir, "MainApp.dll"),
            "MainApp",
            "nativebinding");

        var managedRedirections = new Dictionary<string, string>();
        var nativeRedirections = new Dictionary<string, string>
        {
            ["nativebinding"] = "nativebinding.Switchyard.1.0.0",
        };
        string outputPath = Path.Combine(_tempDir, "MainApp.Redirected.dll");

        ReferenceRedirector.RedirectReferences(caller, managedRedirections, outputPath, nativeRedirections);

        var module = ModuleDefinition.FromFile(outputPath);
        var moduleRef = module.ModuleReferences.FirstOrDefault(r => r.Name?.Value == "nativebinding.Switchyard.1.0.0");
        Assert.NotNull(moduleRef);
        Assert.DoesNotContain(module.ModuleReferences, r => r.Name?.Value == "nativebinding");
    }

    [Fact]
    public void GetPInvokeModuleNames_ReportsDeclaredNativeTargets()
    {
        string caller = TestAssemblyFactory.CreateAssemblyWithPInvoke(
            Path.Combine(_tempDir, "ProbeApp.dll"),
            "ProbeApp",
            "nativebinding");

        var names = ReferenceRedirector.GetPInvokeModuleNames(caller);
        Assert.Contains("nativebinding", names);
    }

    [Fact]
    public void GetPInvokeModuleNames_ExcludesModuleRefsNotBackingPInvoke()
    {
        // The assembly has a P/Invoke module ref "nativebinding" AND a stray
        // ModuleRef "unrelated_module" that is not the target of any DllImport.
        // GetPInvokeModuleNames must return ONLY the P/Invoke-backed module,
        // not every ModuleRef in the module. A previous implementation iterated
        // module.ModuleReferences directly and returned all of them, which
        // would cause coincidental basename collisions to rename unrelated
        // native files and corrupt the build.
        string caller = TestAssemblyFactory.CreateAssemblyWithPInvokeAndStrayModuleRef(
            Path.Combine(_tempDir, "ProbeApp.dll"),
            "ProbeApp",
            "nativebinding",
            "unrelated_module");

        var names = ReferenceRedirector.GetPInvokeModuleNames(caller);
        Assert.Contains("nativebinding", names);
        Assert.DoesNotContain("unrelated_module", names);
    }
}
