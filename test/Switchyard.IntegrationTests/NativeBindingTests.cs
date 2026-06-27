using AsmResolver.DotNet;
using Xunit;

namespace Switchyard.IntegrationTests;

/// <summary>
/// Level 2 + 3 tests for the managed + native-dependency scenario (the Avalonia
/// + higher-SkiaSharp pattern at a small scale).
/// </summary>
/// <remarks>
/// Serialized via the shared <c>Integration</c> collection — see
/// <see cref="PipelineInterceptTests"/>.
/// </remarks>
[Collection("Integration")]
public class NativeBindingTests
{
    private readonly LocalFeedFixture _fixture;

    public NativeBindingTests(LocalFeedFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void Build_NativeBindingApp_ProducesRenamedManagedAndNativeAssemblies()
    {
        string projectPath = Path.Combine(BuildUtility.TestSamplesPath, "NativeBindingApp", "NativeBindingApp.csproj");
        BuildUtility.CleanProject(projectPath);

        var buildResult = BuildUtility.BuildProject(projectPath);
        Assert.True(buildResult.Success,
            $"Build failed:\nSTDOUT:\n{buildResult.StandardOutput}\nSTDERR:\n{buildResult.StandardError}");

        string binDir = BuildUtility.GetBinDirectory("NativeBindingApp");

        // Both routed managed versions must be present.
        string routed1 = Path.Combine(binDir, "NativeBindingLib.Switchyard.1.0.0.dll");
        string routed35 = Path.Combine(binDir, "NativeBindingLib.Switchyard.3.5.0.dll");
        Assert.True(File.Exists(routed1), $"Expected {routed1}");
        Assert.True(File.Exists(routed35), $"Expected {routed35}");

        // The renamed native libraries must be present (one per routed version).
        // The native basename is "libnativebinding" (lib prefix on every
        // platform, mirroring SkiaSharp's libskiasharp convention).
        string nativeName1 = NativeLibName("libnativebinding.Switchyard.1.0.0");
        string nativeName35 = NativeLibName("libnativebinding.Switchyard.3.5.0");
        Assert.True(File.Exists(Path.Combine(binDir, nativeName1)), $"Expected renamed native {nativeName1}");
        Assert.True(File.Exists(Path.Combine(binDir, nativeName35)), $"Expected renamed native {nativeName35}");

        // The original un-routed managed DLL must be blocked from bin.
        Assert.False(File.Exists(Path.Combine(binDir, "NativeBindingLib.dll")),
            "Original NativeBindingLib.dll must be blocked from bin");

        // The original native library must NOT be present in bin (only the
        // renamed per-version copies are shipped).
        string origNative = NativeLibName("libnativebinding");
        Assert.False(File.Exists(Path.Combine(binDir, origNative)),
            $"Original native {origNative} must be blocked from bin");
    }

    [Fact]
    public void Build_NativeBindingApp_RewroteDllImport_ToRoutedNativeName()
    {
        string projectPath = Path.Combine(BuildUtility.TestSamplesPath, "NativeBindingApp", "NativeBindingApp.csproj");
        BuildUtility.CleanProject(projectPath);

        var buildResult = BuildUtility.BuildProject(projectPath);
        Assert.True(buildResult.Success, $"Build failed:\n{buildResult.StandardError}");

        string binDir = BuildUtility.GetBinDirectory("NativeBindingApp");
        string routed1 = Path.Combine(binDir, "NativeBindingLib.Switchyard.1.0.0.dll");

        // Deep-inspect the routed managed DLL and assert its DllImport module
        // (ModuleRef) now points at the routed native name, not the original
        // "libnativebinding". This is the actual native-isolation mechanism.
        var module = ModuleDefinition.FromFile(routed1);
        var moduleRefs = module.ModuleReferences.Select(r => r.Name?.Value).ToList();
        Assert.Contains("libnativebinding.Switchyard.1.0.0", moduleRefs);
        Assert.DoesNotContain("libnativebinding", moduleRefs);
    }

    [Fact]
    public void Run_NativeBindingApp_BindsEachRoutedVersion_ToItsOwnNativeLib()
    {
        string projectPath = Path.Combine(BuildUtility.TestSamplesPath, "NativeBindingApp", "NativeBindingApp.csproj");
        BuildUtility.CleanProject(projectPath);

        var buildResult = BuildUtility.BuildProject(projectPath);
        Assert.True(buildResult.Success, $"Build failed:\n{buildResult.StandardError}");

        string exePath = BuildUtility.FindBuiltExecutable("NativeBindingApp");
        string binDir = BuildUtility.GetBinDirectory("NativeBindingApp");
        Assert.True(File.Exists(exePath), $"Built executable not found at {exePath}");

        var runResult = BuildUtility.RunExecutable(exePath, binDir);
        Assert.Equal(0, runResult.ExitCode);

        // The main app (routed to 1.0.0) must bind its OWN native lib, which
        // returns native version constant 1. The consumer module (routed to
        // 3.5.0) must bind its OWN native lib, returning 3. Distinct native
        // values prove native-lib isolation rather than a single shared load.
        Assert.Contains("[NATIVE_APP] Native version : 1", runResult.StandardOutput);
        Assert.Contains("[NATIVE_CONSUMER] Native version : 3", runResult.StandardOutput);
    }

    private static string NativeLibName(string baseName)
    {
        // baseName already carries the "lib" prefix on every platform
        // (mirroring SkiaSharp's libskiasharp), so only the extension differs.
        if (OperatingSystem.IsWindows()) return baseName + ".dll";
        if (OperatingSystem.IsMacOS()) return baseName + ".dylib";
        return baseName + ".so";
    }
}