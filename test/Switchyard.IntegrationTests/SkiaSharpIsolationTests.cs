using AsmResolver.DotNet;
using Xunit;

namespace Switchyard.IntegrationTests;

/// <summary>
/// Level 2 + 3 test for the real Avalonia + higher-SkiaSharp scenario.
/// </summary>
/// <remarks>
/// Uses the actual SkiaSharp NuGet packages (managed assembly + native
/// libskiasharp), restored from nuget.org by the sample project. This is the
/// concrete situation Switchyard was created for: pin an older SkiaSharp for a
/// framework while a different caller binds a newer one, with both native
/// copies coexisting in a single process. Serialized via the shared
/// <c>Integration</c> collection.
/// </remarks>
[Collection("Integration")]
public class SkiaSharpIsolationTests
{
    private readonly LocalFeedFixture _fixture;

    public SkiaSharpIsolationTests(LocalFeedFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void Build_SkiaSharpIsolationApp_ProducesRenamedManagedAndNativeAssemblies()
    {
        string projectPath = Path.Combine(BuildUtility.TestSamplesPath, "SkiaSharpIsolationApp", "SkiaSharpIsolationApp.csproj");
        BuildUtility.CleanProject(projectPath);

        var buildResult = BuildUtility.BuildProject(projectPath);
        Assert.True(buildResult.Success,
            $"Build failed:\nSTDOUT:\n{buildResult.StandardOutput}\nSTDERR:\n{buildResult.StandardError}");

        string binDir = BuildUtility.GetBinDirectory("SkiaSharpIsolationApp");

        // Both routed managed versions must be present.
        Assert.True(File.Exists(Path.Combine(binDir, "SkiaSharp.Switchyard.2.88.9.dll")),
            "Expected SkiaSharp.Switchyard.2.88.9.dll in bin");
        Assert.True(File.Exists(Path.Combine(binDir, "SkiaSharp.Switchyard.3.116.1.dll")),
            "Expected SkiaSharp.Switchyard.3.116.1.dll in bin");

        // The renamed native libraries must be present (one per routed version).
        // SkiaSharp's native file is "libSkiaSharp" (the on-disk casing; the
        // managed DllImport uses the same casing in its ModuleRef).
        string nativeName288 = NativeLibName("libSkiaSharp.Switchyard.2.88.9");
        string nativeName3116 = NativeLibName("libSkiaSharp.Switchyard.3.116.1");
        Assert.True(File.Exists(Path.Combine(binDir, nativeName288)),
            $"Expected renamed native {nativeName288} in bin");
        Assert.True(File.Exists(Path.Combine(binDir, nativeName3116)),
            $"Expected renamed native {nativeName3116} in bin");

        // The original un-routed managed DLL must be blocked from bin.
        Assert.False(File.Exists(Path.Combine(binDir, "SkiaSharp.dll")),
            "Original SkiaSharp.dll must be blocked from bin");
    }

    [Fact]
    public void Build_SkiaSharpIsolationApp_RewroteDllImport_ToRoutedNativeName()
    {
        string projectPath = Path.Combine(BuildUtility.TestSamplesPath, "SkiaSharpIsolationApp", "SkiaSharpIsolationApp.csproj");
        BuildUtility.CleanProject(projectPath);

        var buildResult = BuildUtility.BuildProject(projectPath);
        Assert.True(buildResult.Success, $"Build failed:\n{buildResult.StandardError}");

        string binDir = BuildUtility.GetBinDirectory("SkiaSharpIsolationApp");
        string routed288 = Path.Combine(binDir, "SkiaSharp.Switchyard.2.88.9.dll");

        // Deep-inspect the routed managed DLL and assert its DllImport module
        // (ModuleRef) now points at the routed native name, not the original
        // "libSkiaSharp". This is the actual native-isolation mechanism applied
        // to the real SkiaSharp assembly.
        var module = ModuleDefinition.FromFile(routed288);
        var moduleRefs = module.ModuleReferences.Select(r => r.Name?.Value).ToList();
        Assert.Contains("libSkiaSharp.Switchyard.2.88.9", moduleRefs);
        Assert.DoesNotContain("libSkiaSharp", moduleRefs);
    }

    [Fact]
    public void Run_SkiaSharpIsolationApp_BothNativeVersionsCoexist()
    {
        string projectPath = Path.Combine(BuildUtility.TestSamplesPath, "SkiaSharpIsolationApp", "SkiaSharpIsolationApp.csproj");
        BuildUtility.CleanProject(projectPath);

        var buildResult = BuildUtility.BuildProject(projectPath);
        Assert.True(buildResult.Success, $"Build failed:\n{buildResult.StandardError}");

        string exePath = BuildUtility.FindBuiltExecutable("SkiaSharpIsolationApp");
        string binDir = BuildUtility.GetBinDirectory("SkiaSharpIsolationApp");
        Assert.True(File.Exists(exePath), $"Built executable not found at {exePath}");

        var runResult = BuildUtility.RunExecutable(exePath, binDir);
        Assert.Equal(0, runResult.ExitCode);

        // Both routed managed versions must report DISTINCT versions and both
        // native libraries must load (creating an SKBitmap forces the native
        // load; a missing/wrong native throws DllNotFoundException and a non-zero
        // exit). This is the headline proof that the real Avalonia + higher
        // SkiaSharp scenario works end-to-end.
        Assert.Contains("[SKIA_APP] SkiaSharp managed version: 2.88.0.0", runResult.StandardOutput);
        Assert.Contains("[SKIA_APP] libskiasharp native loaded OK (2.88.9)", runResult.StandardOutput);
        Assert.Contains("[SKIA_CONSUMER] SkiaSharp managed version: 3.116.0.0", runResult.StandardOutput);
        Assert.Contains("[SKIA_CONSUMER] libskiasharp native loaded OK (3.116.1)", runResult.StandardOutput);
    }

    private static string NativeLibName(string baseName)
    {
        // baseName already carries the "lib" prefix (libskiasharp), so only the
        // extension differs by platform.
        if (OperatingSystem.IsWindows()) return baseName + ".dll";
        if (OperatingSystem.IsMacOS()) return baseName + ".dylib";
        return baseName + ".so";
    }
}