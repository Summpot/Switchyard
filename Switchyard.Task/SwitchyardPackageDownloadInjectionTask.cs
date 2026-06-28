using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Switchyard.Configuration;
using Task = Microsoft.Build.Utilities.Task;

namespace Switchyard;

/// <summary>
/// Restore-time task that turns Switchyard's route table into
/// <c>&lt;PackageDownload&gt;</c> items so NuGet itself fetches every routed
/// version of each routed package into the global packages folder. Switchyard's
/// weaving task then only reads already-restored packages, never downloads —
/// restore behaviour (sources, credentials, proxy, offline mode, caching) is
/// entirely NuGet's.
/// </summary>
/// <remarks>
/// This task runs from <c>Sdk.targets</c>, which as a project-SDK targets file
/// is imported even during the restore-traversal evaluation
/// (<c>ExcludeRestorePackageImports=true</c> suppresses <c>build/</c> package
/// imports, not SDK imports). That is what makes routed-version restore
/// possible from a package at all.
/// </remarks>
/// <remarks>
/// Scope: the task only asks NuGet to restore the exact routed versions the
/// consumer declared in <c>SwitchyardRoutes</c>. It deliberately does NOT infer
/// or expand anything else — in particular it knows nothing about any package's
/// native-assets naming convention. A routed package's declared
/// native-asset dependencies are restored transitively by NuGet (because the
/// routed package's own <c>.nuspec</c> declares them). When a routed package's
/// <c>.nuspec</c> omits a platform's native-asset dependency, the consumer is
/// responsible for restoring that platform's routed native versions, typically
/// by adding the corresponding multi-version <c>&lt;PackageDownload&gt;</c>
/// entry alongside the <c>&lt;PackageReference&gt;</c>. That keeps all package
/// knowledge on the consumer side; Switchyard itself stays free of any
/// package-specific naming assumptions.
/// </remarks>
public sealed class SwitchyardPackageDownloadInjectionTask : Task
{
    /// <summary>
    /// The <c>@(PackageReference)</c> items, optionally decorated with
    /// <c>SwitchyardRoutes</c> metadata.
    /// </summary>
    [Required]
    public ITaskItem[]? PackageReferences { get; set; }

    /// <summary>
    /// The <c>PackageDownload</c> items to add so NuGet restores every routed
    /// version. Each item's <c>Identity</c> is the package id and its
    /// <c>Version</c> metadata is a semicolon-separated list of exact
    /// <c>[version]</c> ranges (NuGet's multi-version PackageDownload syntax).
    /// </summary>
    [Output]
    public ITaskItem[]? RoutedPackageDownloads { get; set; }

    public override bool Execute()
    {
        var configurations = ConfigurationParser.Parse(PackageReferences);
        if (configurations.Count == 0)
        {
            RoutedPackageDownloads = Array.Empty<ITaskItem>();
            return true;
        }

        // routed package id -> the routed (non-original) versions to restore.
        var routedVersionsByPackage = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var cfg in configurations)
        {
            foreach (var version in cfg.GetAllTargetVersions())
            {
                if (cfg.IsOriginalVersion(version))
                    continue;
                if (!routedVersionsByPackage.TryGetValue(cfg.PackageId, out var list))
                {
                    list = new List<string>();
                    routedVersionsByPackage[cfg.PackageId] = list;
                }
                list.Add(version);
            }
        }

        if (routedVersionsByPackage.Count == 0)
        {
            RoutedPackageDownloads = Array.Empty<ITaskItem>();
            return true;
        }

        // One PackageDownload item per routed package id, Version = the
        // multi-version list of its routed versions. No companion/native-assets
        // expansion: that would require encoding a package-naming convention
        // into the tool, which is out of scope (see the class remarks).
        var output = new List<ITaskItem>();
        foreach (var kv in routedVersionsByPackage)
        {
            var item = new TaskItem(kv.Key);
            item.SetMetadata("Version", BuildMultiVersionRange(kv.Value));
            output.Add(item);
        }

        RoutedPackageDownloads = output.ToArray();
        return true;
    }

    /// <summary>
    /// Builds NuGet's multi-version PackageDownload range list, e.g.
    /// <c>"[2.88.9];[3.116.1]"</c>. Each version is pinned to an exact range so
    /// NuGet restores precisely that version rather than a floating resolution.
    /// </summary>
    private static string BuildMultiVersionRange(IReadOnlyList<string> versions)
    {
        var parts = new string[versions.Count];
        for (int i = 0; i < versions.Count; i++)
            parts[i] = "[" + versions[i] + "]";
        return string.Join(";", parts);
    }
}