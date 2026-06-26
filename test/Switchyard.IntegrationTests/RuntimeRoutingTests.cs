using Xunit;

namespace Switchyard.IntegrationTests;

/// <summary>
/// Level 3 tests: run the compiled executables and assert on their console
/// output to prove that multiple versions of the same NuGet package coexist
/// in a single process without ALC isolation.
/// </summary>
/// <remarks>
/// Serialized via the shared <c>Integration</c> collection — see
/// <see cref="PipelineInterceptTests"/> for the rationale.
/// </remarks>
[Collection("Integration")]
public class RuntimeRoutingTests
{
    private readonly LocalFeedFixture _fixture;

    public RuntimeRoutingTests(LocalFeedFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void Run_BasicRouteApp_AssertsDualVersionDiversion()
    {
        string projectPath = Path.Combine(BuildUtility.TestSamplesPath, "BasicRouteApp", "BasicRouteApp.csproj");
        BuildUtility.CleanProject(projectPath);

        var buildResult = BuildUtility.BuildProject(projectPath);
        Assert.True(buildResult.Success,
            $"Build failed:\n{buildResult.StandardError}");

        string exePath = BuildUtility.FindBuiltExecutable("BasicRouteApp");
        string binDir = BuildUtility.GetBinDirectory("BasicRouteApp");
        Assert.True(File.Exists(exePath), $"Built executable not found at {exePath}");

        var runResult = BuildUtility.RunExecutable(exePath, binDir);

        // The main app should load TargetLib.Switchyard.1.0.0 → version 1.0.0.0
        Assert.Contains("[MAIN_APP] TargetLib loaded version: 1.0.0.0", runResult.StandardOutput);
        // The payment module should load TargetLib.Switchyard.3.5.0 → version 3.5.0.0
        Assert.Contains("[PAYMENT_MODULE] TargetLib loaded version: 3.5.0.0", runResult.StandardOutput);
    }

    [Fact]
    public void Run_InvalidCastApp_AssertsTypeBoundaryTearing()
    {
        string projectPath = Path.Combine(BuildUtility.TestSamplesPath, "InvalidCastApp", "InvalidCastApp.csproj");
        BuildUtility.CleanProject(projectPath);

        var buildResult = BuildUtility.BuildProject(projectPath);
        Assert.True(buildResult.Success,
            $"Build failed:\n{buildResult.StandardError}");

        string exePath = BuildUtility.FindBuiltExecutable("InvalidCastApp");
        string binDir = BuildUtility.GetBinDirectory("InvalidCastApp");
        Assert.True(File.Exists(exePath), $"Built executable not found at {exePath}");

        var runResult = BuildUtility.RunExecutable(exePath, binDir);

        // The process should exit 0 (the InvalidCastException is caught in code)
        Assert.Equal(0, runResult.ExitCode);
        Assert.Contains("InvalidCastException", runResult.StandardOutput);
        Assert.Contains("Type boundary successfully torn", runResult.StandardOutput);
    }

    [Fact]
    public void Run_RouteGroupApp_AssertsCascadeVersions()
    {
        string projectPath = Path.Combine(BuildUtility.TestSamplesPath, "RouteGroupApp", "RouteGroupApp.csproj");
        BuildUtility.CleanProject(projectPath);

        var buildResult = BuildUtility.BuildProject(projectPath);
        Assert.True(buildResult.Success,
            $"Build failed:\n{buildResult.StandardError}");

        string exePath = BuildUtility.FindBuiltExecutable("RouteGroupApp");
        string binDir = BuildUtility.GetBinDirectory("RouteGroupApp");
        Assert.True(File.Exists(exePath), $"Built executable not found at {exePath}");

        var runResult = BuildUtility.RunExecutable(exePath, binDir);

        // TargetLib is routed to 1.0.0
        Assert.Contains("[ROUTE_GROUP_APP] TargetLib version: 1.0.0.0", runResult.StandardOutput);
        // CommonUtils is also routed to 1.0.0 (cascade sandbox)
        Assert.Contains("[ROUTE_GROUP_APP] CommonUtils (direct): 1.0.0.0", runResult.StandardOutput);
    }
}
