using Switchyard.Pipeline;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Switchyard.Core.Tests;

/// <summary>
/// Level 1 unit tests for <see cref="DepsJsonPatcher"/>. These exercise the
/// deps.json rewriting logic directly (no physical build) to verify:
/// <list type="bullet">
/// <item>Missing deps file surfaces a failure (-1) rather than silent success.</item>
/// <item>Routed assemblies are injected as synthetic project libraries with the
/// actual AssemblyVersion (not the package version).</item>
/// <item>Original package runtime entries are stripped when requested.</item>
/// <item>The original formatting (minified vs indented) is preserved.</item>
/// <item>A multi-target deps file without a runtimeTarget name fails loudly
/// instead of guessing the wrong frame.</item>
/// </list>
/// </summary>
public class DepsJsonPatcherTests : IDisposable
{
    private readonly string _tempDir;

    public DepsJsonPatcherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "switchyard-deps-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    [Fact]
    public void AddRoutedAssemblies_ReturnsNegativeOne_WhenDepsFileMissing()
    {
        // C2 fix: a missing deps.json must not be treated as success. The task
        // surfaces this as a build error so the app does not throw
        // FileNotFoundException at runtime for un-TPA-listed routed assemblies.
        string missingPath = Path.Combine(_tempDir, "does-not-exist.deps.json");
        var routed = new[] { ("RoutedLib", "1.0.0", "RoutedLib.dll", "1.0.0.0") };

        int result = DepsJsonPatcher.AddRoutedAssemblies(missingPath, routed);

        Assert.Equal(-1, result);
    }

    [Fact]
    public void AddRoutedAssemblies_ReturnsNegativeOne_WhenJsonUnparseable()
    {
        string path = Path.Combine(_tempDir, "bad.deps.json");
        File.WriteAllText(path, "this is not json {{{");
        var routed = new[] { ("RoutedLib", "1.0.0", "RoutedLib.dll", "1.0.0.0") };

        int result = DepsJsonPatcher.AddRoutedAssemblies(path, routed);

        Assert.Equal(-1, result);
    }

    [Fact]
    public void AddRoutedAssemblies_InjectsRoutedLibraryWithActualAssemblyVersion()
    {
        // The routed assembly's ACTUAL AssemblyVersion (2.88.0.0) must be
        // written into deps.json, not the NuGet package version (2.88.9). The
        // CLR binds on (Name, Version); writing the package version would make
        // hostpolicy record a TPA entry the binder cannot match.
        string path = Path.Combine(_tempDir, "app.deps.json");
        File.WriteAllText(path, BuildMinifiedDepsJson());

        var routed = new[]
        {
            ("SkiaSharp.Switchyard.2.88.9", "2.88.9", "SkiaSharp.Switchyard.2.88.9.dll", "2.88.0.0")
        };

        int result = DepsJsonPatcher.AddRoutedAssemblies(path, routed);

        Assert.Equal(1, result);
        var doc = JsonNode.Parse(File.ReadAllText(path))!;
        var runtimeEntry = doc["targets"]![".NETCoreApp,Version=v10.0"]!["SkiaSharp.Switchyard.2.88.9/2.88.9"]!["runtime"]!["SkiaSharp.Switchyard.2.88.9.dll"]!;
        Assert.Equal("2.88.0.0", runtimeEntry["assemblyVersion"]!.GetValue<string>());
        Assert.Equal("2.88.0.0", runtimeEntry["fileVersion"]!.GetValue<string>());
        Assert.Equal("project", doc["libraries"]!["SkiaSharp.Switchyard.2.88.9/2.88.9"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void AddRoutedAssemblies_StripsOriginalRuntimeEntry()
    {
        string path = Path.Combine(_tempDir, "app.deps.json");
        File.WriteAllText(path, BuildMinifiedDepsJson());

        var routed = new[]
        {
            ("SkiaSharp.Switchyard.2.88.9", "2.88.9", "SkiaSharp.Switchyard.2.88.9.dll", "2.88.0.0")
        };

        // Use lowercase to verify the strip matches case-insensitively —
        // NuGet package ids are case-insensitive and the deps.json key casing
        // follows the nuspec's id casing which may differ from the
        // PackageReference's casing.
        int result = DepsJsonPatcher.AddRoutedAssemblies(path, routed, stripOriginalRuntimePackageIds: new[] { "skiasharp" });

        Assert.Equal(1, result);
        var doc = JsonNode.Parse(File.ReadAllText(path))!;
        // The original SkiaSharp runtime entry should be removed so publish
        // does not resurrect the un-routed DLL.
        var originalTarget = doc["targets"]![".NETCoreApp,Version=v10.0"]!["SkiaSharp/2.88.9"]!;
        Assert.Null(originalTarget["runtime"]);
    }

    [Fact]
    public void AddRoutedAssemblies_PreservesMinifiedFormatting()
    {
        // M8 fix: the SDK writes deps.json minified. The patcher must not
        // reformat it to indented (which creates spurious diffs).
        string path = Path.Combine(_tempDir, "app.deps.json");
        string original = BuildMinifiedDepsJson();
        File.WriteAllText(path, original);

        var routed = new[] { ("RoutedLib", "1.0.0", "RoutedLib.dll", "1.0.0.0") };
        DepsJsonPatcher.AddRoutedAssemblies(path, routed);

        string patched = File.ReadAllText(path);
        Assert.DoesNotContain("\n", patched);
    }

    [Fact]
    public void AddRoutedAssemblies_PreservesIndentedFormatting()
    {
        string path = Path.Combine(_tempDir, "app.deps.json");
        string original = BuildIndentedDepsJson();
        File.WriteAllText(path, original);

        var routed = new[] { ("RoutedLib", "1.0.0", "RoutedLib.dll", "1.0.0.0") };
        DepsJsonPatcher.AddRoutedAssemblies(path, routed);

        string patched = File.ReadAllText(path);
        Assert.Contains("\n", patched);
    }

    [Fact]
    public void AddRoutedAssemblies_FailsLoudly_OnMultiTargetWithoutRuntimeTargetName()
    {
        // M7 fix: when runtimeTarget.name is empty and there is more than one
        // target frame, the patcher must not guess — it returns -1 so the task
        // surfaces a build error rather than injecting into the wrong frame.
        string path = Path.Combine(_tempDir, "multi.deps.json");
        var doc = JsonNode.Parse(BuildMinifiedDepsJson())!;
        // Add a second target frame and clear the runtimeTarget name.
        doc["targets"]![".NETCoreApp,Version=v8.0"] = new JsonObject();
        doc["runtimeTarget"]!["name"] = "";
        File.WriteAllText(path, doc.ToJsonString());

        var routed = new[] { ("RoutedLib", "1.0.0", "RoutedLib.dll", "1.0.0.0") };

        int result = DepsJsonPatcher.AddRoutedAssemblies(path, routed);

        Assert.Equal(-1, result);
    }

    [Fact]
    public void NormaliseVersion_PadsToFourComponents()
    {
        // Verified indirectly: a 3-component version must be padded to 4
        // components so the CLR does not read the 0xFFFF "unspecified" sentinel
        // as 65535.
        string path = Path.Combine(_tempDir, "app.deps.json");
        File.WriteAllText(path, BuildMinifiedDepsJson());

        var routed = new[] { ("RoutedLib", "1.0.0", "RoutedLib.dll", "1.0.0") };
        DepsJsonPatcher.AddRoutedAssemblies(path, routed);

        var doc = JsonNode.Parse(File.ReadAllText(path))!;
        var runtimeEntry = doc["targets"]![".NETCoreApp,Version=v10.0"]!["RoutedLib/1.0.0"]!["runtime"]!["RoutedLib.dll"]!;
        Assert.Equal("1.0.0.0", runtimeEntry["assemblyVersion"]!.GetValue<string>());
    }

    private static string BuildMinifiedDepsJson()
    {
        var root = new JsonObject
        {
            ["runtimeTarget"] = new JsonObject { ["name"] = ".NETCoreApp,Version=v10.0" },
            ["targets"] = new JsonObject
            {
                [".NETCoreApp,Version=v10.0"] = new JsonObject
                {
                    ["SkiaSharp/2.88.9"] = new JsonObject
                    {
                        ["runtime"] = new JsonObject
                        {
                            ["lib/netstandard2.0/SkiaSharp.dll"] = new JsonObject
                            {
                                ["assemblyVersion"] = "2.88.0.0",
                                ["fileVersion"] = "2.88.9.0",
                            }
                        }
                    }
                }
            },
            ["libraries"] = new JsonObject
            {
                ["SkiaSharp/2.88.9"] = new JsonObject
                {
                    ["type"] = "package",
                    ["serviceable"] = true,
                    ["sha512"] = "abc",
                    ["path"] = "skiasharp/2.88.9",
                }
            }
        };
        return root.ToJsonString();
    }

    private static string BuildIndentedDepsJson()
    {
        var root = JsonNode.Parse(BuildMinifiedDepsJson())!;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
