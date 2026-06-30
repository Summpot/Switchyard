using System.Text.Json.Nodes;

namespace Switchyard.Pipeline;

/// <summary>
/// Rewrites the application's <c>deps.json</c> so that the synthetic
/// <c>{Package}.Switchyard.{version}</c> assemblies — which are produced by
/// the weaver and copied into the output directory but are NOT part of any
/// NuGet package — appear in the runtime's Trusted Platform Assemblies list.
/// </summary>
/// <remarks>
/// <para>
/// .NET Core's default <c>AssemblyLoadContext</c> resolves assemblies almost
/// exclusively from the TPA list that <c>hostpolicy</c> builds out of
/// <c>deps.json</c>. Files sitting in the application base directory are only
/// probed when they are listed there. The renamed/routed assemblies would
/// therefore throw <c>FileNotFoundException</c> at runtime even though the DLL
/// is physically next to the executable.
/// </para>
/// <para>
/// The patcher adds each routed assembly as a synthetic <c>"type": "project"</c>
/// library (mirroring the way MSBuild records project-reference outputs such as
/// <c>PaymentModule</c>) so the host adds the on-disk file to the TPA list and
/// the binder can locate it by simple name.
/// </para>
/// <para>
/// Version normalisation: the <c>assemblyVersion</c>/<c>fileVersion</c> written
/// into the runtime entry is padded to a full four-component version (e.g.
/// <c>1.0.0</c> → <c>1.0.0.0</c>). Leaving a field unspecified would serialise
/// the 0xFFFF sentinel, which the CLR reads back as 65535 and then fails to
/// bind against the routed assembly's real <c>AssemblyVersion</c>.
/// </para>
/// <para>
/// Publish safety: the original (un-routed) package's <c>runtime</c> member is
/// stripped from <c>targets</c> so that the SDK's publish flow — which copies
/// every runtime asset listed in deps.json — does not resurrect the original
/// <c>{PackageId}.dll</c> from the NuGet cache. The library/target node itself
/// is kept so dependency edges remain valid.
/// </para>
/// </remarks>
public static class DepsJsonPatcher
{
    /// <summary>
    /// Adds a synthetic project-library entry for every routed assembly in
    /// <paramref name="routedAssemblies"/> to the deps file at
    /// <paramref name="depsFilePath"/>. The file is rewritten in place.
    /// Returns the number of entries added, or <c>-1</c> when the file could
    /// not be read or parsed.
    /// </summary>
    public static int AddRoutedAssemblies(string depsFilePath, IEnumerable<PreparedAssembly> routedAssemblies, IReadOnlyCollection<string>? stripOriginalRuntimePackageIds = null)
    {
        return AddRoutedAssemblies(depsFilePath,
            routedAssemblies.Where(a => a.IsRouted)
                            .Select(a => (RoutedName: Path.GetFileNameWithoutExtension(a.DllPath),
                                          Version: a.Version,
                                          FileName: Path.GetFileName(a.DllPath),
                                          AssemblyVersion: a.AssemblyVersion ?? a.Version)),
            stripOriginalRuntimePackageIds);
    }

    /// <summary>
    /// Adds synthetic project-library entries for the supplied routed
    /// assemblies to the deps file at <paramref name="depsFilePath"/>. The
    /// file is rewritten in place. Returns the number of entries added, or
    /// <c>-1</c> when the file could not be read or parsed.
    /// </summary>
    /// <param name="routedAssemblies">Each tuple carries the routed simple
    /// name, the NuGet package version (used as the library key), the on-disk
    /// file name, and the routed assembly's actual
    /// <c>AssemblyVersion</c> (used as <c>assemblyVersion</c>/<c>fileVersion</c>
    /// so the CLR binds the routed assembly by <c>(Name, Version)</c> — this
    /// may differ from the package version, e.g. SkiaSharp 2.88.9 has
    /// <c>AssemblyVersion</c> 2.88.0.0).</param>
    /// <param name="stripOriginalRuntimePackageIds">The original package ids whose
    /// <c>runtime</c> member in deps.json should be removed (so the SDK's publish
    /// flow does not copy the original DLL back and the original drops out of
    /// the TPA list). Pass only ids whose original DLL is genuinely unused by
    /// any caller (e.g. TargetLib when every caller is routed away from it).
    /// Omit ids whose original is still bound by unrouted callers (e.g. SkiaSharp
    /// 2.88.9 still used by Avalonia.Skia) — stripping those would break the
    /// unrouted callers' runtime binding. May be <c>null</c>.</param>
    public static int AddRoutedAssemblies(
        string depsFilePath,
        IEnumerable<(string RoutedName, string Version, string FileName, string AssemblyVersion)> routedAssemblies,
        IReadOnlyCollection<string>? stripOriginalRuntimePackageIds = null)
    {
        if (!File.Exists(depsFilePath))
            return -1;

        var routed = routedAssemblies.ToList();
        if (routed.Count == 0)
            return 0;

        string json = File.ReadAllText(depsFilePath);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch
        {
            return -1;
        }
        if (root is null)
            return -1;

        var targets = root["targets"] as JsonObject;
        var libraries = root["libraries"] as JsonObject;
        if (targets is null || libraries is null)
            return -1;

        // Detect whether the original deps.json was minified (single line) or
        // indented (multi-line). The SDK writes deps.json minified; reformatting
        // it to indented creates large spurious diffs and can break tools that
        // byte-compare the file. Preserve the original formatting.
        bool writeIndented = json.Contains('\n');

        string runtimeTargetName = root["runtimeTarget"]?["name"]?.GetValue<string>() ?? string.Empty;
        if (runtimeTargetName.Length == 0)
        {
            // Fall back to the first (only) target framework key. When the
            // deps file has exactly one target this is unambiguous; when it
            // has more than one (a multi-targeted build), picking the "first"
            // is dictionary-order-dependent and could inject routed entries
            // into the wrong frame — the actually-running TFM would then be
            // missing the TPA entries and throw FileNotFoundException at
            // runtime. Fail loudly in that case rather than guessing.
            var targetKeys = targets.AsObject().Select(k => k.Key).ToList();
            if (targetKeys.Count == 0)
                return -1;
            if (targetKeys.Count > 1)
                return -1;
            runtimeTargetName = targetKeys[0];
        }
        if (runtimeTargetName.Length == 0 || !targets.TryGetPropertyValue(runtimeTargetName, out var targetNode) || targetNode is not JsonObject targetFrame)
            return -1;

        // Collect the original package ids whose runtime entry should be
        // neutralised. This is driven by the caller (the set of ids whose
        // original DLL is genuinely unused), NOT by every routed assembly —
        // because an original may still be bound by unrouted callers (e.g.
        // SkiaSharp 2.88.9 used by Avalonia.Skia) and stripping its runtime
        // would break that binding.
        var originalPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (stripOriginalRuntimePackageIds is not null)
        {
            foreach (var id in stripOriginalRuntimePackageIds)
            {
                if (!string.IsNullOrWhiteSpace(id))
                    originalPackageIds.Add(id);
            }
        }

        foreach (var (routedName, version, fileName, assemblyVersionRaw) in routed)
        {
            string libKey = routedName + "/" + version;
            // Use the routed assembly's ACTUAL AssemblyVersion (read from the
            // DLL metadata), not the NuGet package version — the CLR binds on
            // (Name, Version) and the on-disk file carries its own
            // AssemblyVersion (e.g. SkiaSharp 2.88.9 -> 2.88.0.0). Writing the
            // package version here would make hostpolicy record a TPA entry the
            // binder cannot match.
            string assemblyVersion = NormaliseVersion(assemblyVersionRaw);

            // targets[tfm][libKey] = { "runtime": { fileName: { assemblyVersion, fileVersion } } }
            if (!targetFrame.TryGetPropertyValue(libKey, out var libTargetNode) || libTargetNode is not JsonObject libTarget)
            {
                libTarget = new JsonObject();
                targetFrame[libKey] = libTarget;
            }
            var runtime = libTarget["runtime"] as JsonObject;
            if (runtime is null)
            {
                runtime = new JsonObject();
                libTarget["runtime"] = runtime;
            }
            runtime[fileName] = new JsonObject
            {
                ["assemblyVersion"] = assemblyVersion,
                ["fileVersion"] = assemblyVersion,
            };

            // libraries[libKey] = { "type":"project", "serviceable":false, "sha512":"" }
            if (!libraries.ContainsKey(libKey))
            {
                libraries[libKey] = new JsonObject
                {
                    ["type"] = "project",
                    ["serviceable"] = false,
                    ["sha512"] = "",
                };
            }
        }

        // Neutralise the original package runtime entries so neither the build
        // TPA list nor the publish file stream resurrect the un-routed DLL.
        // Package ids are case-insensitive (NuGet treats them so), and the
        // deps.json key casing follows the nuspec's id casing which may differ
        // from the PackageReference's casing — compare case-insensitively.
        foreach (var key in targetFrame.Select(k => k.Key).ToList())
        {
            foreach (var originalId in originalPackageIds)
            {
                if (key.StartsWith(originalId + "/", StringComparison.OrdinalIgnoreCase))
                {
                    if (targetFrame.TryGetPropertyValue(key, out var origNode) && origNode is JsonObject origTarget)
                        origTarget.Remove("runtime");
                    break;
                }
            }
        }

        File.WriteAllText(depsFilePath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = writeIndented,
        }));
        return routed.Count;
    }

    /// <summary>
    /// Normalises a package version string (e.g. <c>1.0.0</c>) to a full
    /// four-component assembly version string (<c>1.0.0.0</c>) so that the
    /// host does not record the 0xFFFF "unspecified" sentinel.
    /// </summary>
    private static string NormaliseVersion(string version)
    {
        if (Version.TryParse(version, out var v))
            return new Version(
                v.Major,
                v.Minor < 0 ? 0 : v.Minor,
                v.Build < 0 ? 0 : v.Build,
                v.Revision < 0 ? 0 : v.Revision).ToString();
        return version + ".0.0.0";
    }

}