using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Switchyard.Configuration;
using Switchyard.Pipeline;
using Task = Microsoft.Build.Utilities.Task;

namespace Switchyard;

/// <summary>
/// MSBuild task that performs compile-time IL weaving and reference
/// redirection so that multiple versions of the same NuGet package can coexist
/// in a single process. Invoked from <c>Switchyard.targets</c> after
/// <c>ResolveAssemblyReferences</c> and before <c>CopyFilesToOutputDirectory</c>.
/// </summary>
public sealed class SwitchyardTask : Task
{
    /// <summary>
    /// The <c>@(PackageReference)</c> items, optionally decorated with
    /// <c>SwitchyardRoutes</c> / <c>SwitchyardRouteGroup</c> metadata.
    /// </summary>
    [Required]
    public ITaskItem[]? PackageReferences { get; set; }

    /// <summary>
    /// The <c>@(ReferenceCopyLocalPaths)</c> items that MSBuild is about to
    /// copy to the output directory.
    /// </summary>
    [Required]
    public ITaskItem[]? ReferenceCopyLocalPaths { get; set; }

    /// <summary>
    /// Absolute path to <c>project.assets.json</c>.
    /// </summary>
    public string? ProjectAssetsFile { get; set; }

    /// <summary>
    /// The <c>$(IntermediateOutputPath)</c> directory. Weaver output is placed
    /// in a <c>switchyard/</c> subfolder.
    /// </summary>
    [Required]
    public string? IntermediateOutputPath { get; set; }

    /// <summary>
    /// The <c>$(TargetFramework)</c> of the project being built, used to
    /// select the best-matching managed assembly inside each NuGet package.
    /// </summary>
    public string? TargetFramework { get; set; }

    /// <summary>
    /// The <c>$(AssemblyName)</c> of the project being built. Used to match
    /// the main project's own assembly against the route table.
    /// </summary>
    public string? AssemblyName { get; set; }

    /// <summary>
    /// The <c>@(IntermediateAssembly)</c> item. When supplied, the main
    /// project's own assembly is also processed for reference redirection.
    /// </summary>
    public ITaskItem? IntermediateAssembly { get; set; }

    /// <summary>
    /// When set to <c>true</c>, suppresses all diagnostic output.
    /// </summary>
    public bool Silent { get; set; }

    /// <summary>
    /// The updated copy-local list. Replaces <c>@(ReferenceCopyLocalPaths)</c>
    /// in the calling target via an <c>&lt;Output&gt;</c> element.
    /// </summary>
    [Output]
    public ITaskItem[]? NewReferenceCopyLocalPaths { get; set; }

    /// <summary>
    /// The updated intermediate assembly path. When the main project's own
    /// assembly was modified, this points at the rewritten file in the
    /// switchyard intermediate directory.
    /// </summary>
    [Output]
    public ITaskItem? NewIntermediateAssembly { get; set; }

    public override bool Execute()
    {
        var configurations = ConfigurationParser.Parse(PackageReferences);
        if (configurations.Count == 0)
        {
            NewReferenceCopyLocalPaths = ReferenceCopyLocalPaths;
            return true;
        }

        if (string.IsNullOrWhiteSpace(IntermediateOutputPath))
        {
            Log.LogError("Switchyard requires IntermediateOutputPath to be set.");
            return false;
        }

        var targetFramework = string.IsNullOrWhiteSpace(TargetFramework) ? "net8.0" : TargetFramework!;
        var assemblyName = AssemblyName ?? string.Empty;

        LogMessage($"Switchyard: processing {configurations.Count} route configuration(s).");
        foreach (var cfg in configurations)
        {
            LogMessage($"  Package '{cfg.PackageId}' original={cfg.OriginalVersion}" +
                       (cfg.RouteGroup is null ? "" : $" group={cfg.RouteGroup}"));
            foreach (var entry in cfg.Routes)
                LogMessage($"    {entry.Caller} => {entry.Version}");
        }

        var callerPaths = new List<string>();
        if (ReferenceCopyLocalPaths is not null)
        {
            foreach (var item in ReferenceCopyLocalPaths)
            {
                if (!string.IsNullOrWhiteSpace(item.ItemSpec))
                    callerPaths.Add(item.ItemSpec);
            }
        }
        if (IntermediateAssembly is not null && !string.IsNullOrWhiteSpace(IntermediateAssembly.ItemSpec))
            callerPaths.Add(IntermediateAssembly.ItemSpec);

        try
        {
            var pipeline = SwitchyardPipeline.Create(
                IntermediateOutputPath!,
                targetFramework,
                ProjectAssetsFile,
                assemblyName);

            var result = pipeline.Execute(configurations, callerPaths);

            LogMessage($"Switchyard: prepared {result.PreparedAssemblies.Count} target assembly(ies), " +
                       $"redirected {result.RedirectedCallers.Count} caller(s).");

            NewReferenceCopyLocalPaths = BuildOutputItems(result, ReferenceCopyLocalPaths);
            NewIntermediateAssembly = BuildIntermediateAssembly(result, IntermediateAssembly);

            return true;
        }
        catch (Exception ex)
        {
            Log.LogError("Switchyard pipeline failed: " + ex.Message);
            LogMessage(ex.ToString());
            return false;
        }
    }

    private ITaskItem[] BuildOutputItems(SwitchyardResult result, ITaskItem[]? originalItems)
    {
        var output = new List<ITaskItem>();
        var removed = new HashSet<string>(result.RemovedOriginalPaths, StringComparer.OrdinalIgnoreCase);

        if (originalItems is not null)
        {
            foreach (var item in originalItems)
            {
                var replaced = false;
                foreach (var redirected in result.RedirectedCallers)
                {
                    if (string.Equals(item.ItemSpec, redirected.OriginalPath, StringComparison.OrdinalIgnoreCase))
                    {
                        var replacement = new TaskItem(redirected.ModifiedPath);
                        item.CopyMetadataTo(replacement);
                        output.Add(replacement);
                        replaced = true;
                        break;
                    }
                }
                if (replaced)
                    continue;

                if (removed.Contains(item.ItemSpec))
                    continue;

                output.Add(item);
            }
        }

        foreach (var prepared in result.PreparedAssemblies)
        {
            var item = new TaskItem(prepared.DllPath);
            output.Add(item);

            if (prepared.PdbPath is not null && File.Exists(prepared.PdbPath))
                output.Add(new TaskItem(prepared.PdbPath));
        }

        return output.ToArray();
    }

    private static ITaskItem? BuildIntermediateAssembly(SwitchyardResult result, ITaskItem? original)
    {
        if (original is null)
            return null;

        foreach (var redirected in result.RedirectedCallers)
        {
            if (string.Equals(original.ItemSpec, redirected.OriginalPath, StringComparison.OrdinalIgnoreCase))
            {
                var replacement = new TaskItem(redirected.ModifiedPath);
                original.CopyMetadataTo(replacement);
                return replacement;
            }
        }
        return original;
    }

    private void LogMessage(string message)
    {
        if (!Silent)
            Log.LogMessage(MessageImportance.Normal, message);
    }
}
