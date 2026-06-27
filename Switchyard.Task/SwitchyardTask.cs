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
/// <c>CoreCompile</c> and before <c>CopyFilesToOutputDirectory</c>.
/// </summary>
/// <remarks>
/// This is Phase 1 of the pipeline. It MUST run after <c>CoreCompile</c> (not
/// <c>ResolveAssemblyReferences</c>) so that <c>@(IntermediateAssembly)</c> —
/// the just-built main project — exists and can itself be redirected. The
/// downstream <c>deps.json</c> shaping and publish copy-local cleanup are
/// performed by <see cref="SwitchyardDepsPatcherTask"/> and
/// <see cref="SwitchyardPublishFilterTask"/> in later targets.
/// </remarks>
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
    /// Controls NativeAOT direct P/Invoke handling for routed native libraries.
    /// <c>PrefixSymbols</c> attempts to prefix native export symbols and rewrite
    /// managed DllImport entry points so duplicate native symbols can be linked
    /// directly. Any other value keeps the safe default lazy/direct filtering.
    /// </summary>
    public string? NativeAotDirectPInvokeMode { get; set; }

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

    /// <summary>
    /// The routed (renamed) target assemblies produced by the pipeline, each
    /// carrying <c>RoutedName</c>, <c>Version</c> and <c>FileName</c>
    /// metadata. Consumed by the separate <c>PatchSwitchyardDepsJson</c>
    /// target which rewrites <c>deps.json</c> after the SDK generates it.
    /// </summary>
    [Output]
    public ITaskItem[]? NewRoutedAssemblies { get; set; }

    /// <summary>
    /// The original (un-routed) native library paths that should be stripped
    /// from the copy-local output because a renamed native copy is shipped
    /// instead. Consumed by the publish-cleanup target so the SDK's publish
    /// flow does not resurrect the original native libraries.
    /// </summary>
    [Output]
    public ITaskItem[]? NewRemovedOriginalNativePaths { get; set; }

    /// <summary>
    /// The routed native libraries produced by the pipeline, each carrying the
    /// routed <c>DllImport</c> module name as metadata. NativeAOT direct
    /// P/Invoke consumes these names so it binds the version-specific native
    /// libraries that Switchyard already wrote into managed metadata.
    /// </summary>
    [Output]
    public ITaskItem[]? NewRoutedNativeLibraries { get; set; }

    /// <summary>
    /// The package ids whose original (un-routed) DLL was removed from the
    /// copy-local output because every caller is routed away from it. Consumed
    /// by the deps.json patcher so it strips those packages' runtime entries
    /// (and only those — packages whose original is still bound by unrouted
    /// callers, e.g. SkiaSharp 2.88.9 used by Avalonia.Skia, keep their runtime
    /// entry so the unrouted callers can still bind at runtime).
    /// </summary>
    [Output]
    public ITaskItem[]? NewStrippedOriginalPackageIds { get; set; }

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
        {
            callerPaths.Add(IntermediateAssembly.ItemSpec);
            LogMessage($"Switchyard: added IntermediateAssembly {IntermediateAssembly.ItemSpec}");
        }

        LogMessage($"Switchyard: {callerPaths.Count} caller path(s) received.");

        try
        {
            var pipeline = SwitchyardPipeline.Create(
                IntermediateOutputPath!,
                targetFramework,
                ProjectAssetsFile,
                assemblyName,
                NativeAotDirectPInvokeMode);

            var result = pipeline.Execute(configurations, callerPaths);

            LogMessage($"Switchyard: prepared {result.PreparedAssemblies.Count} target assembly(ies), " +
                       $"redirected {result.RedirectedCallers.Count} caller(s).");

            NewReferenceCopyLocalPaths = BuildOutputItems(result, ReferenceCopyLocalPaths);
            NewIntermediateAssembly = BuildIntermediateAssembly(result, IntermediateAssembly);
            NewRoutedAssemblies = BuildRoutedAssemblyItems(result);
            NewRemovedOriginalNativePaths = result.RemovedOriginalNativePaths
                .Select(p => new TaskItem(p))
                .ToArray();
            NewRoutedNativeLibraries = BuildRoutedNativeLibraryItems(result);
            // Derive the package ids whose original DLL was removed — those are
            // the ones the deps.json patcher may strip the runtime entry for.
            NewStrippedOriginalPackageIds = result.RemovedOriginalPaths
                .Select(p => Path.GetFileNameWithoutExtension(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(id => new TaskItem(id))
                .ToArray();

            // deps.json is patched by the separate PatchSwitchyardDepsJson target,
            // which runs AFTER the SDK's GenerateBuildDependencyFile target: the
            // deps file only lands in the output directory at that late stage.
            // Without that TPA injection, framework-dependent apps throw
            // FileNotFoundException for the renamed assemblies even though the
            // DLLs sit next to the executable.
            return true;
        }
        catch (Exception ex)
        {
            Log.LogError("Switchyard pipeline failed: " + ex.Message);
            LogMessage("Switchyard full exception details: " + ex);
            for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
                LogMessage("  Inner: " + inner.GetType().Name + ": " + inner.Message);
            return false;
        }
    }

    private ITaskItem[] BuildOutputItems(SwitchyardResult result, ITaskItem[]? originalItems)
    {
        var output = new List<ITaskItem>();
        var removed = new HashSet<string>(result.RemovedOriginalPaths, StringComparer.OrdinalIgnoreCase);
        var removedNative = new HashSet<string>(result.RemovedOriginalNativePaths, StringComparer.OrdinalIgnoreCase);

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

                // Strip the original (un-routed) native libraries that MSBuild
                // resolved under runtimes/{rid}/native/ — a renamed copy is
                // shipped instead so each routed version binds its own native.
                if (removedNative.Contains(item.ItemSpec))
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

            foreach (var nativePath in prepared.NativeLibraryPaths)
            {
                if (File.Exists(nativePath))
                    output.Add(new TaskItem(nativePath));
            }
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

    private static ITaskItem[] BuildRoutedAssemblyItems(SwitchyardResult result)
    {
        var items = new List<ITaskItem>();
        foreach (var prepared in result.PreparedAssemblies)
        {
            if (!prepared.IsRouted)
                continue;
            var item = new TaskItem(prepared.PackageId);
            item.SetMetadata("RoutedName", Path.GetFileNameWithoutExtension(prepared.DllPath));
            item.SetMetadata("RoutedVersion", prepared.Version);
            item.SetMetadata("RoutedFileName", Path.GetFileName(prepared.DllPath));
            // The actual AssemblyVersion (may differ from the package version).
            // Used by the deps.json patcher so the CLR binds the routed assembly
            // by (Name, Version) correctly.
            item.SetMetadata("RoutedAssemblyVersion", prepared.AssemblyVersion ?? prepared.Version);
            items.Add(item);
        }
        return items.ToArray();
    }

    private static ITaskItem[] BuildRoutedNativeLibraryItems(SwitchyardResult result)
    {
        var items = new List<ITaskItem>();
        foreach (var prepared in result.PreparedAssemblies)
        {
            if (!prepared.IsRouted)
                continue;

            foreach (var native in prepared.NativeLibraries)
            {
                var item = new TaskItem(native.RuntimePath);
                item.SetMetadata("ModuleName", native.ModuleName);
                item.SetMetadata("PackageId", prepared.PackageId);
                item.SetMetadata("RoutedVersion", prepared.Version);
                item.SetMetadata("RuntimeFileName", Path.GetFileName(native.RuntimePath));
                item.SetMetadata("EntryPointNames", string.Join(";", native.EntryPointNames));
                if (!string.IsNullOrWhiteSpace(native.LinkerPath))
                    item.SetMetadata("LinkerPath", native.LinkerPath);
                items.Add(item);
            }
        }
        return items.ToArray();
    }

    private void LogMessage(string message)
    {
        if (!Silent)
            Log.LogMessage(MessageImportance.High, message);
    }
}
