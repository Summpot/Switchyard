using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Task = Microsoft.Build.Utilities.Task;

namespace Switchyard;

/// <summary>
/// Removes the original (un-routed) package DLLs from a copy-local item
/// stream during publish. The publish pipeline re-resolves copy-local assets
/// from the lock file (<c>_ResolvedCopyLocalBuildAssets</c>), which
/// resurrects the original <c>{PackageId}.dll</c> entries even after the
/// build phase stripped them from <c>@(ReferenceCopyLocalPaths)</c>. This
/// task drops any item whose simple file name (without extension) equals a
/// routed package id, while keeping the renamed
/// <c>{PackageId}.Switchyard.{version}.dll</c> assemblies.
/// </summary>
public sealed class SwitchyardPublishFilterTask : Task
{
    /// <summary>
    /// The copy-local items to filter (e.g.
    /// <c>@(ReferenceCopyLocalPaths)</c> or
    /// <c>@(_ResolvedCopyLocalPublishAssets)</c>).
    /// </summary>
    [Required]
    public ITaskItem[]? CopyLocalItems { get; set; }

    /// <summary>
    /// The routed assemblies emitted by <see cref="SwitchyardTask"/>, whose
    /// item specs are the original package ids (e.g. <c>TargetLib</c>).
    /// </summary>
    [Required]
    public ITaskItem[]? RoutedAssemblies { get; set; }

    /// <summary>
    /// The filtered items: every input item whose file name (without
    /// extension) is NOT one of the routed package ids, with all metadata
    /// preserved.
    /// </summary>
    [Output]
    public ITaskItem[]? FilteredItems { get; set; }

    public override bool Execute()
    {
        if (CopyLocalItems is null || CopyLocalItems.Length == 0)
        {
            FilteredItems = CopyLocalItems;
            return true;
        }
        if (RoutedAssemblies is null || RoutedAssemblies.Length == 0)
        {
            FilteredItems = CopyLocalItems;
            return true;
        }

        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in RoutedAssemblies)
        {
            var id = item.ItemSpec;
            if (!string.IsNullOrWhiteSpace(id))
                blocked.Add(id);
        }

        var kept = new List<ITaskItem>();
        foreach (var item in CopyLocalItems)
        {
            string fileName = Path.GetFileNameWithoutExtension(item.ItemSpec);
            if (blocked.Contains(fileName))
                continue;
            kept.Add(item);
        }

        FilteredItems = kept.ToArray();
        return true;
    }
}