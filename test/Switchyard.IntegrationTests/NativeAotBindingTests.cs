using Xunit;

namespace Switchyard.IntegrationTests;

/// <summary>
/// Level 2 + 3 tests for NativeAOT native-library isolation. NativeAOT's lazy
/// P/Invoke path consumes Switchyard's rewritten DllImport module names
/// directly. Direct P/Invoke can only be enabled automatically when routed
/// native entry point names are unique; two versions of the same native package
/// normally share symbol names and must stay on the lazy path.
/// </summary>
/// <remarks>
/// Serialized via the shared <c>Integration</c> collection — see
/// <see cref="PipelineInterceptTests"/>.
/// </remarks>
[Collection("Integration")]
public class NativeAotBindingTests
{
    private readonly LocalFeedFixture _fixture;

    public NativeAotBindingTests(LocalFeedFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void Publish_NativeAotBindingApp_UsesLazyPInvokeForDuplicateRoutedEntryPoints_AndRuns()
    {
        string projectPath = Path.Combine(BuildUtility.TestSamplesPath, "NativeAotBindingApp", "NativeAotBindingApp.csproj");
        BuildUtility.CleanProject(projectPath);

        var publishResult = BuildUtility.PublishProject(
            projectPath,
            "Release",
            $"-r {BuildUtility.HostRuntimeIdentifier}");
        Assert.True(publishResult.Success,
            $"Publish failed:\nSTDOUT:\n{publishResult.StandardOutput}\nSTDERR:\n{publishResult.StandardError}");

        string objDir = Path.Combine(
            BuildUtility.TestSamplesPath,
            "NativeAotBindingApp",
            "obj",
            "Release",
            "net10.0",
            BuildUtility.HostRuntimeIdentifier,
            "native");
        string rspPath = Path.Combine(objDir, "NativeAotBindingApp.ilc.rsp");
        Assert.True(File.Exists(rspPath), $"Expected ilc response file at {rspPath}");

        string rsp = File.ReadAllText(rspPath);
        Assert.DoesNotContain("--directpinvoke:libnativebinding.Switchyard.1.0.0", rsp);
        Assert.DoesNotContain("--directpinvoke:libnativebinding.Switchyard.3.5.0", rsp);

        string publishDir = BuildUtility.GetPublishDirectory(
            "NativeAotBindingApp",
            "Release",
            "net10.0",
            BuildUtility.HostRuntimeIdentifier);
        string exeName = OperatingSystem.IsWindows() ? "NativeAotBindingApp.exe" : "NativeAotBindingApp";
        string exePath = Path.Combine(publishDir, exeName);
        Assert.True(File.Exists(exePath), $"Published executable not found at {exePath}");

        var runResult = BuildUtility.RunExecutable(exePath, publishDir);
        Assert.Equal(0, runResult.ExitCode);
        Assert.Contains("[NATIVE_AOT_APP] Native version : 1", runResult.StandardOutput);
        Assert.Contains("[NATIVE_CONSUMER] Native version : 3", runResult.StandardOutput);
    }
}
