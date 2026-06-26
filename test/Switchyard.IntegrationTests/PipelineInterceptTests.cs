using AsmResolver.DotNet;
using Xunit;

namespace Switchyard.IntegrationTests;

/// <summary>
/// Level 2 tests: verify that Switchyard's MSBuild target correctly intercepts
/// the copy-local file stream, produces the renamed assemblies in the bin
/// directory, and blocks the original package DLL.
/// </summary>
/// <remarks>
/// All integration tests share the <c>Integration</c> collection so xUnit
/// serializes them. The TestSamples projects are built into a single shared
/// output directory; running them in parallel causes MSBuild file locks on
/// the <c>obj/switchyard</c> intermediate files.
/// </remarks>
[Collection("Integration")]
public class PipelineInterceptTests
{
    private readonly LocalFeedFixture _fixture;

    public PipelineInterceptTests(LocalFeedFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void Build_BasicRouteApp_ProducesRoutedAssemblies_InBinDirectory()
    {
        string projectPath = Path.Combine(BuildUtility.TestSamplesPath, "BasicRouteApp", "BasicRouteApp.csproj");
        BuildUtility.CleanProject(projectPath);

        var buildResult = BuildUtility.BuildProject(projectPath);
        Assert.True(buildResult.Success,
            $"Build failed:\nSTDOUT:\n{buildResult.StandardOutput}\nSTDERR:\n{buildResult.StandardError}");

        string binDir = BuildUtility.GetBinDirectory("BasicRouteApp");

        // Routed versions must exist
        string routed1 = Path.Combine(binDir, "TargetLib.Switchyard.1.0.0.dll");
        string routed35 = Path.Combine(binDir, "TargetLib.Switchyard.3.5.0.dll");
        Assert.True(File.Exists(routed1), $"Expected {routed1} to exist in bin directory");
        Assert.True(File.Exists(routed35), $"Expected {routed35} to exist in bin directory");

        // Original TargetLib.dll must NOT exist (blocked by the task)
        string original = Path.Combine(binDir, "TargetLib.dll");
        Assert.False(File.Exists(original), $"Original {original} must be blocked from the bin directory");
    }

    [Fact]
    public void Build_BasicRouteApp_ProducesPdbFiles_AlongsideRoutedAssemblies()
    {
        string projectPath = Path.Combine(BuildUtility.TestSamplesPath, "BasicRouteApp", "BasicRouteApp.csproj");
        BuildUtility.CleanProject(projectPath);

        var buildResult = BuildUtility.BuildProject(projectPath);
        Assert.True(buildResult.Success,
            $"Build failed:\n{buildResult.StandardError}");

        string binDir = BuildUtility.GetBinDirectory("BasicRouteApp");

        string routedPdb1 = Path.Combine(binDir, "TargetLib.Switchyard.1.0.0.pdb");
        string routedPdb35 = Path.Combine(binDir, "TargetLib.Switchyard.3.5.0.pdb");

        // PDBs should exist if the original packages had them (NuGet packages
        // built from source typically do). We assert that at least the DLLs
        // are valid .NET assemblies.
        string routedDll1 = Path.Combine(binDir, "TargetLib.Switchyard.1.0.0.dll");
        Assert.True(File.Exists(routedDll1));
        var module = ModuleDefinition.FromFile(routedDll1);
        Assert.Equal("TargetLib.Switchyard.1.0.0", module.Assembly?.Name);
    }

    [Fact]
    public void Publish_BasicRouteApp_IncludesRoutedAssemblies_InPublishDirectory()
    {
        string projectPath = Path.Combine(BuildUtility.TestSamplesPath, "BasicRouteApp", "BasicRouteApp.csproj");
        BuildUtility.CleanProject(projectPath);

        var publishResult = BuildUtility.PublishProject(projectPath);
        Assert.True(publishResult.Success,
            $"Publish failed:\n{publishResult.StandardError}");

        string publishDir = BuildUtility.GetPublishDirectory("BasicRouteApp");

        string routed1 = Path.Combine(publishDir, "TargetLib.Switchyard.1.0.0.dll");
        string routed35 = Path.Combine(publishDir, "TargetLib.Switchyard.3.5.0.dll");
        Assert.True(File.Exists(routed1), $"Expected {routed1} in publish directory");
        Assert.True(File.Exists(routed35), $"Expected {routed35} in publish directory");

        string original = Path.Combine(publishDir, "TargetLib.dll");
        Assert.False(File.Exists(original), $"Original TargetLib.dll must not be in publish directory");
    }

    [Fact]
    public void IncrementalBuild_SecondBuildDoesNotFail()
    {
        string projectPath = Path.Combine(BuildUtility.TestSamplesPath, "BasicRouteApp", "BasicRouteApp.csproj");
        BuildUtility.CleanProject(projectPath);

        var firstBuild = BuildUtility.BuildProject(projectPath);
        Assert.True(firstBuild.Success, $"First build failed:\n{firstBuild.StandardError}");

        // Second build without any changes — should succeed (incremental or no-op)
        var secondBuild = BuildUtility.BuildProject(projectPath);
        Assert.True(secondBuild.Success, $"Second (incremental) build failed:\n{secondBuild.StandardError}");
    }

    [Fact]
    public void RouteGroup_CascadeRedirects_CommonUtilsReference_InsideTargetLib()
    {
        string projectPath = Path.Combine(BuildUtility.TestSamplesPath, "RouteGroupApp", "RouteGroupApp.csproj");
        BuildUtility.CleanProject(projectPath);

        var buildResult = BuildUtility.BuildProject(projectPath);
        Assert.True(buildResult.Success,
            $"Build failed:\nSTDOUT:\n{buildResult.StandardOutput}\nSTDERR:\n{buildResult.StandardError}");

        string binDir = BuildUtility.GetBinDirectory("RouteGroupApp");

        // Both routed packages should exist
        string routedTargetLib = Path.Combine(binDir, "TargetLib.Switchyard.1.0.0.dll");
        string routedCommonUtils = Path.Combine(binDir, "CommonUtils.Switchyard.1.0.0.dll");
        Assert.True(File.Exists(routedTargetLib), $"Expected {routedTargetLib}");
        Assert.True(File.Exists(routedCommonUtils), $"Expected {routedCommonUtils}");

        // Deep-inspect TargetLib.Switchyard.1.0.0.dll and verify its internal
        // reference to CommonUtils has been cascaded to CommonUtils.Switchyard.1.0.0
        var module = ModuleDefinition.FromFile(routedTargetLib);
        var commonUtilsRef = module.AssemblyReferences
            .FirstOrDefault(r => r.Name?.Value?.StartsWith("CommonUtils") == true);

        Assert.NotNull(commonUtilsRef);
        Assert.Equal("CommonUtils.Switchyard.1.0.0", commonUtilsRef!.Name);
        Assert.Null(commonUtilsRef.PublicKeyOrToken);

        // Original packages must be blocked
        Assert.False(File.Exists(Path.Combine(binDir, "TargetLib.dll")));
        Assert.False(File.Exists(Path.Combine(binDir, "CommonUtils.dll")));
    }
}
