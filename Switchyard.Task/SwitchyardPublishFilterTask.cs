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
/// package id that Phase 1 proved no caller still binds to, while keeping the renamed
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
    /// Package ids whose original assemblies Phase 1 removed from the build
    /// copy-local stream. Packages whose original version is still used by at
    /// least one caller must not be listed here.
    /// </summary>
    [Required]
    public ITaskItem[]? RoutedAssemblies { get; set; }

    /// <summary>
    /// Optional set of native library file names (e.g.
    /// <c>libskiasharp.dll</c>) to drop from the copy-local stream because a
    /// renamed, routed native copy is shipped instead.
    /// </summary>
    public ITaskItem[]? BlockedNativeFileNames { get; set; }

    /// <summary>
    /// The filtered items: every input item whose file name (without
    /// extension) is NOT one of the removable original package ids, and whose
    /// file name is NOT a blocked native file name, with all metadata preserved.
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
        if ((RoutedAssemblies is null || RoutedAssemblies.Length == 0)
            && (BlockedNativeFileNames is null || BlockedNativeFileNames.Length == 0))
        {
            FilteredItems = CopyLocalItems;
            return true;
        }

        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (RoutedAssemblies is not null)
        {
            foreach (var item in RoutedAssemblies)
            {
                var id = item.ItemSpec;
                if (!string.IsNullOrWhiteSpace(id))
                    blocked.Add(id);
            }
        }

        var blockedNativeFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (BlockedNativeFileNames is not null)
        {
            foreach (var item in BlockedNativeFileNames)
            {
                var name = item.ItemSpec;
                if (!string.IsNullOrWhiteSpace(name))
                    blockedNativeFiles.Add(Path.GetFileName(name));
            }
        }

        var kept = new List<ITaskItem>();
        foreach (var item in CopyLocalItems)
        {
            string fileName = Path.GetFileName(item.ItemSpec);
            string baseName = Path.GetFileNameWithoutExtension(fileName);

            if (blocked.Contains(baseName))
                continue;
            if (blockedNativeFiles.Contains(fileName))
                continue;
            kept.Add(item);
        }

        FilteredItems = kept.ToArray();
        return true;
    }
}
