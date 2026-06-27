using Xunit;

namespace Switchyard.IntegrationTests;

/// <summary>
/// Level 3 test that detects dual-version coexistence purely through runtime
/// reflection, with no compile-time reference to a specific routed version.
/// The sample app loads both routed DLLs via <c>Assembly.LoadFrom</c> and
/// reflects to invoke each version's method; the test asserts on the probe's
/// Stdout and exit code.
/// </summary>
[Collection("Integration")]
public class ReflectionProbeTests
{
    private readonly LocalFeedFixture _fixture;

    public ReflectionProbeTests(LocalFeedFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void Run_ReflectionProbeApp_DetectsTwoDistinctVersionsViaReflection()
    {
        string projectPath = Path.Combine(BuildUtility.TestSamplesPath, "ReflectionProbeApp", "ReflectionProbeApp.csproj");
        BuildUtility.CleanProject(projectPath);

        var buildResult = BuildUtility.BuildProject(projectPath);
        Assert.True(buildResult.Success, $"Build failed:\n{buildResult.StandardError}");

        string exePath = BuildUtility.FindBuiltExecutable("ReflectionProbeApp");
        string binDir = BuildUtility.GetBinDirectory("ReflectionProbeApp");
        Assert.True(File.Exists(exePath), $"Built executable not found at {exePath}");

        var runResult = BuildUtility.RunExecutable(exePath, binDir);
        Assert.Equal(0, runResult.ExitCode);

        // The compile-time reference (rewritten by Switchyard to
        // TargetLib.Switchyard.1.0.0) must report 1.0.0.0.
        Assert.Contains("[PROBE] Compile-time TargetLib version: 1.0.0.0", runResult.StandardOutput);

        // The reflection probe must have loaded BOTH routed assemblies and
        // observed DISTINCT versions — proving two unrelated identities
        // coexist in one process, discovered without ALC or compile-time ties.
        Assert.Contains("[PROBE] Reflection 1.0.0 assembly=TargetLib.Switchyard.1.0.0 version=1.0.0.0", runResult.StandardOutput);
        Assert.Contains("[PROBE] Reflection 3.5.0 assembly=TargetLib.Switchyard.3.5.0 version=3.5.0.0", runResult.StandardOutput);
    }
}