using Microsoft.Build.Utilities;
using Switchyard;
using Xunit;

namespace Switchyard.Core.Tests;

public class NativeAotDirectPInvokeFilterTests
{
    [Fact]
    public void Execute_KeepsLibrariesWithUniqueEntryPoints()
    {
        var first = Native("libone.Switchyard.1.0.0", "one");
        var second = Native("libtwo.Switchyard.1.0.0", "two");
        var task = new SwitchyardNativeAotDirectPInvokeFilterTask
        {
            RoutedNativeLibraries = new[] { first, second }
        };

        Assert.True(task.Execute());

        var modules = task.SafeDirectPInvokeLibraries!
            .Select(i => i.GetMetadata("ModuleName"))
            .ToArray();
        Assert.Equal(new[] { "libone.Switchyard.1.0.0", "libtwo.Switchyard.1.0.0" }, modules);
    }

    [Fact]
    public void Execute_DropsLibrariesWithDuplicateEntryPoints()
    {
        var first = Native("libsame.Switchyard.1.0.0", "native_get_version");
        var second = Native("libsame.Switchyard.3.5.0", "native_get_version");
        var task = new SwitchyardNativeAotDirectPInvokeFilterTask
        {
            RoutedNativeLibraries = new[] { first, second }
        };

        Assert.True(task.Execute());
        Assert.Empty(task.SafeDirectPInvokeLibraries!);
    }

    [Fact]
    public void Execute_KeepsPrefixedLibrariesWhoseEntryPointsBecomeUnique()
    {
        var first = Native("libsame.Switchyard.1.0.0", "switchyard_libsame_Switchyard_1_0_0_native_get_version");
        var second = Native("libsame.Switchyard.3.5.0", "switchyard_libsame_Switchyard_3_5_0_native_get_version");
        var task = new SwitchyardNativeAotDirectPInvokeFilterTask
        {
            RoutedNativeLibraries = new[] { first, second }
        };

        Assert.True(task.Execute());
        Assert.Equal(2, task.SafeDirectPInvokeLibraries!.Length);
    }

    private static TaskItem Native(string moduleName, string entryPointNames)
    {
        var item = new TaskItem(moduleName + ".dll");
        item.SetMetadata("ModuleName", moduleName);
        item.SetMetadata("EntryPointNames", entryPointNames);
        return item;
    }
}
