using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Switchyard.Pipeline;
using Task = Microsoft.Build.Utilities.Task;

namespace Switchyard;

/// <summary>
/// Rewrites the application's <c>deps.json</c> so that the routed (renamed)
/// assemblies produced by <see cref="SwitchyardTask"/> appear in the
/// runtime's Trusted Platform Assemblies list. Runs as a separate MSBuild
/// target <c>PatchSwitchyardDepsJson</c> after the SDK's
/// <c>GenerateBuildDependencyFile</c> target, because the deps file is only
/// written to the output directory at that late stage.
/// </summary>
public sealed class SwitchyardDepsPatcherTask : Task
{
    /// <summary>
    /// The <c>$(ProjectDepsFilePath)</c> — the application's
    /// <c>deps.json</c> in the output directory.
    /// </summary>
    [Required]
    public string? DepsFilePath { get; set; }

    /// <summary>
    /// The routed assemblies emitted by <see cref="SwitchyardTask"/>, each
    /// carrying <c>RoutedName</c>, <c>Version</c> and <c>FileName</c>
    /// metadata.
    /// </summary>
    [Required]
    public ITaskItem[]? RoutedAssemblies { get; set; }

    /// <summary>
    /// The package ids whose original DLL was removed from the copy-local
    /// output (because every caller is routed away from it). The deps.json
    /// patcher strips those packages' runtime entries. Omit ids whose original
    /// is still bound by unrouted callers (e.g. SkiaSharp 2.88.9 used by
    /// Avalonia.Skia) — stripping those would break the unrouted callers'
    /// runtime binding.
    /// </summary>
    public ITaskItem[]? StrippedOriginalPackageIds { get; set; }

    public bool Silent { get; set; }

    public override bool Execute()
    {
        if (string.IsNullOrWhiteSpace(DepsFilePath) || RoutedAssemblies is null || RoutedAssemblies.Length == 0)
            return true;

        var tuples = RoutedAssemblies
            .Where(i => !string.IsNullOrWhiteSpace(i.GetMetadata("RoutedName")))
            .Select(i => (
                RoutedName: i.GetMetadata("RoutedName"),
                Version: i.GetMetadata("RoutedVersion"),
                FileName: i.GetMetadata("RoutedFileName"),
                AssemblyVersion: i.GetMetadata("RoutedAssemblyVersion")))
            .ToList();

        if (tuples.Count == 0)
            return true;

        var stripIds = StrippedOriginalPackageIds?
            .Select(i => i.ItemSpec)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        int result = DepsJsonPatcher.AddRoutedAssemblies(DepsFilePath!, tuples, stripIds);
        if (!Silent)
            Log.LogMessage(MessageImportance.High,
                $"Switchyard: deps.json patch result={result} (path={DepsFilePath}, routed={tuples.Count}).");
        return true;
    }
}