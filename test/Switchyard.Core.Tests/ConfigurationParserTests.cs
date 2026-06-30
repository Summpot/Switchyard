using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Switchyard.Configuration;
using Xunit;

namespace Switchyard.Core.Tests;

/// <summary>
/// Level 1 unit tests for <see cref="ConfigurationParser"/> and
/// <see cref="RouteConfiguration"/>. These tests use fake
/// <see cref="ITaskItem"/> objects (via <see cref="TaskItem"/>) to simulate
/// the <c>@(PackageReference)</c> items that MSBuild passes to the task.
/// </summary>
public class ConfigurationParserTests
{
    private static ITaskItem MakePackageReference(string id, string version, string routes, string? group = null)
    {
        var item = new TaskItem(id);
        item.SetMetadata("Version", version);
        item.SetMetadata("SwitchyardRoutes", routes);
        if (group is not null)
            item.SetMetadata("SwitchyardRouteGroup", group);
        return item;
    }

    [Fact]
    public void Parse_IgnoresItemsWithoutSwitchyardRoutes()
    {
        var items = new[]
        {
            new TaskItem("NormalLib")
        };

        var configs = ConfigurationParser.Parse(items);
        Assert.Empty(configs);
    }

    [Fact]
    public void Parse_ExtractsRoutesAndOriginalVersion()
    {
        var items = new[]
        {
            MakePackageReference("TargetLib", "2.0.0", "MainApp=1.0.0;PaymentModule=3.5.0;*=2.0.0")
        };

        var configs = ConfigurationParser.Parse(items);
        var cfg = Assert.Single(configs);
        Assert.Equal("TargetLib", cfg.PackageId);
        Assert.Equal("2.0.0", cfg.OriginalVersion);
        Assert.Null(cfg.RouteGroup);
        Assert.Equal(3, cfg.Routes.Count);
    }

    [Fact]
    public void ResolveVersionForCaller_PrefersExplicitOverWildcard()
    {
        var items = new[]
        {
            MakePackageReference("TargetLib", "2.0.0", "MainApp=1.0.0;*=2.0.0")
        };
        var cfg = ConfigurationParser.Parse(items)[0];

        Assert.Equal("1.0.0", cfg.ResolveVersionForCaller("MainApp"));
        Assert.Equal("2.0.0", cfg.ResolveVersionForCaller("OtherModule"));
    }

    [Fact]
    public void ResolveVersionForCaller_ReturnsNull_WhenNoWildcardAndNoMatch()
    {
        var items = new[]
        {
            MakePackageReference("TargetLib", "2.0.0", "MainApp=1.0.0")
        };
        var cfg = ConfigurationParser.Parse(items)[0];

        Assert.Equal("1.0.0", cfg.ResolveVersionForCaller("MainApp"));
        Assert.Null(cfg.ResolveVersionForCaller("OtherModule"));
    }

    [Fact]
    public void GetRoutedName_ProducesExpectedIdentity()
    {
        var items = new[]
        {
            MakePackageReference("TargetLib", "2.0.0", "*=1.0.0")
        };
        var cfg = ConfigurationParser.Parse(items)[0];

        Assert.Equal("TargetLib.Switchyard.1.0.0", cfg.GetRoutedName("1.0.0"));
    }

    [Fact]
    public void GetAllTargetVersions_ReturnsDistinctVersions()
    {
        var items = new[]
        {
            MakePackageReference("TargetLib", "2.0.0", "MainApp=1.0.0;PaymentModule=3.5.0;*=1.0.0")
        };
        var cfg = ConfigurationParser.Parse(items)[0];

        var versions = cfg.GetAllTargetVersions().ToList();
        Assert.Equal(2, versions.Count);
        Assert.Contains("1.0.0", versions);
        Assert.Contains("3.5.0", versions);
    }

    [Fact]
    public void BuildGroups_GroupsByExplicitRouteGroup()
    {
        var items = new[]
        {
            MakePackageReference("TargetLib", "2.0.0", "*=1.0.0", "AuthSandbox"),
            MakePackageReference("CommonUtils", "2.0.0", "*=1.0.0", "AuthSandbox"),
            MakePackageReference("StandaloneLib", "2.0.0", "*=1.0.0")
        };

        var configs = ConfigurationParser.Parse(items);
        var groups = ConfigurationParser.BuildGroups(configs);

        Assert.Equal(2, groups.Count);
        var authGroup = groups.First(g => g.Name == "AuthSandbox");
        Assert.True(authGroup.IsExplicit);
        Assert.Equal(2, authGroup.Members.Count);
    }

    [Fact]
    public void Parse_HandlesWhitespaceInRouteEntries()
    {
        var items = new[]
        {
            MakePackageReference("TargetLib", "2.0.0", "  MainApp = 1.0.0 ; * = 2.0.0  ")
        };

        var configs = ConfigurationParser.Parse(items);
        var cfg = Assert.Single(configs);
        Assert.Equal(2, cfg.Routes.Count);
        Assert.Equal("1.0.0", cfg.ResolveVersionForCaller("MainApp"));
        Assert.Equal("2.0.0", cfg.ResolveVersionForCaller("AnyOther"));
    }

    [Fact]
    public void Parse_ThrowsOnMissingVersion_WhenSwitchyardRoutesPresent()
    {
        // A PackageReference with SwitchyardRoutes but no Version is a
        // configuration error. Previously the parser defaulted Version to "*",
        // which caused IsOriginalVersion to never match and the pipeline to
        // throw a misleading "package not restored" error. The parser now
        // surfaces the real cause.
        var item = new TaskItem("TargetLib");
        item.SetMetadata("SwitchyardRoutes", "*=1.0.0");
        // Version metadata is deliberately NOT set.

        Assert.Throws<InvalidOperationException>(() => ConfigurationParser.Parse(new[] { item }));
    }
}
