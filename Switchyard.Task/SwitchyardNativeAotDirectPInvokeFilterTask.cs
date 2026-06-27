using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Task = Microsoft.Build.Utilities.Task;

namespace Switchyard;

/// <summary>
/// Filters routed native libraries down to the modules that can safely be
/// enabled for NativeAOT direct P/Invoke.
/// </summary>
/// <remarks>
/// Direct P/Invoke turns each imported native entry point into a linker symbol.
/// If two routed native libraries import the same entry point name (the common
/// case for two versions of the same package), the native linker cannot infer
/// the intended module from the symbol alone. Those modules must stay on
/// NativeAOT's lazy P/Invoke path unless a future symbol-renaming shim is used.
/// </remarks>
public sealed class SwitchyardNativeAotDirectPInvokeFilterTask : Task
{
    /// <summary>
    /// Routed native library items produced by <see cref="SwitchyardTask"/>.
    /// </summary>
    public ITaskItem[]? RoutedNativeLibraries { get; set; }

    /// <summary>
    /// Routed native library items whose imported entry point names are unique
    /// across the routed set and can therefore be passed to <c>DirectPInvoke</c>.
    /// </summary>
    [Output]
    public ITaskItem[]? SafeDirectPInvokeLibraries { get; set; }

    public override bool Execute()
    {
        if (RoutedNativeLibraries is null || RoutedNativeLibraries.Length == 0)
        {
            SafeDirectPInvokeLibraries = Array.Empty<ITaskItem>();
            return true;
        }

        var entryPointCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in RoutedNativeLibraries)
        {
            foreach (var entryPoint in GetEntryPoints(item))
            {
                entryPointCounts.TryGetValue(entryPoint, out var count);
                entryPointCounts[entryPoint] = count + 1;
            }
        }

        var safe = new List<ITaskItem>();
        foreach (var item in RoutedNativeLibraries)
        {
            var entryPoints = GetEntryPoints(item).ToList();
            if (entryPoints.Count == 0)
                continue;

            if (entryPoints.All(e => entryPointCounts.TryGetValue(e, out var count) && count == 1))
                safe.Add(item);
        }

        SafeDirectPInvokeLibraries = safe.ToArray();
        return true;
    }

    private static IEnumerable<string> GetEntryPoints(ITaskItem item)
    {
        var raw = item.GetMetadata("EntryPointNames");
        if (string.IsNullOrWhiteSpace(raw))
            yield break;

        foreach (var part in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var entryPoint = part.Trim();
            if (entryPoint.Length > 0)
                yield return entryPoint;
        }
    }
}
