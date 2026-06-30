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
        // Validate required inputs before any work. A null/empty RoutedAssemblies
        // with a valid deps file is a legitimate no-op (nothing to inject), so
        // that path returns true. But a null DepsFilePath while routed
        // assemblies exist is a wiring error — surface it rather than silently
        // skipping TPA injection, which would produce a "successful" build that
        // throws FileNotFoundException at runtime.
        if (RoutedAssemblies is null || RoutedAssemblies.Length == 0)
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

        if (string.IsNullOrWhiteSpace(DepsFilePath))
        {
            Log.LogError(
                "Switchyard: {0} routed assembly(ies) require deps.json TPA injection but DepsFilePath is not set. " +
                "The build would succeed but the app would throw FileNotFoundException at runtime for the routed assemblies.",
                tuples.Count);
            return false;
        }

        var stripIds = StrippedOriginalPackageIds?
            .Select(i => i.ItemSpec)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        int result = DepsJsonPatcher.AddRoutedAssemblies(DepsFilePath!, tuples, stripIds);
        // AddRoutedAssemblies returns -1 when the deps file is missing or
        // cannot be parsed. That is a real failure: the routed assemblies will
        // not appear in the TPA list and the app will throw
        // FileNotFoundException at runtime. Surface it as a build error rather
        // than logging a message and returning success.
        if (result < 0)
        {
            Log.LogError(
                "Switchyard: failed to patch deps.json at '{0}' (result={1}). " +
                "The file is missing or could not be parsed; the routed assemblies would not be in the TPA list " +
                "and the app would throw FileNotFoundException at runtime.",
                DepsFilePath, result);
            return false;
        }

        if (!Silent)
            Log.LogMessage(MessageImportance.High,
                $"Switchyard: deps.json patch added {result} routed assembly entr(ies) (path={DepsFilePath}, requested={tuples.Count}).");
        return true;
    }
}