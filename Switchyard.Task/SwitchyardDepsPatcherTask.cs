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
                FileName: i.GetMetadata("RoutedFileName")))
            .ToList();

        if (tuples.Count == 0)
            return true;

        int result = DepsJsonPatcher.AddRoutedAssemblies(DepsFilePath!, tuples);
        if (!Silent)
            Log.LogMessage(MessageImportance.High,
                $"Switchyard: deps.json patch result={result} (path={DepsFilePath}, routed={tuples.Count}).");
        return true;
    }
}