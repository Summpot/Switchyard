using Microsoft.Build.Framework;

namespace Switchyard.Configuration;

/// <summary>
/// Parses the <c>&lt;SwitchyardRoutes&gt;</c> / <c>&lt;SwitchyardRouteGroup&gt;</c>
/// metadata attached to <c>&lt;PackageReference&gt;</c> items and produces
/// <see cref="RouteConfiguration"/> objects.
/// </summary>
public static class ConfigurationParser
{
    /// <summary>
    /// Parses the supplied <see cref="ITaskItem"/> array representing
    /// <c>@(PackageReference)</c> and returns the active route configurations.
    /// Items without <c>SwitchyardRoutes</c> metadata are ignored.
    /// </summary>
    public static IReadOnlyList<RouteConfiguration> Parse(ITaskItem[]? packageReferences)
    {
        if (packageReferences is null || packageReferences.Length == 0)
            return Array.Empty<RouteConfiguration>();

        var result = new List<RouteConfiguration>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in packageReferences)
        {
            var packageId = item.ItemSpec;
            if (string.IsNullOrWhiteSpace(packageId))
                continue;

            var routes = item.GetMetadata("SwitchyardRoutes");
            if (string.IsNullOrWhiteSpace(routes))
                continue;

            var version = item.GetMetadata("Version");
            if (string.IsNullOrWhiteSpace(version))
            {
                // A PackageReference without a Version is invalid for routing:
                // IsOriginalVersion compares versions as strings, and the
                // pipeline would treat every routed version as non-original
                // (since none equals "*"), then fail trying to locate version
                // "*" in the global packages folder with a misleading
                // "package not restored" error. Surface the real cause here.
                throw new InvalidOperationException(
                    $"Switchyard: PackageReference '{packageId}' has SwitchyardRoutes metadata but no Version. " +
                    "A concrete Version is required on every routed PackageReference so Switchyard can identify " +
                    "the original version and locate the routed versions in the global packages folder.");
            }

            var routeGroup = item.GetMetadata("SwitchyardRouteGroup");
            if (string.IsNullOrWhiteSpace(routeGroup))
                routeGroup = null;

            var entries = ParseRouteTable(routes);
            if (entries.Count == 0)
                continue;

            if (!seen.Add(packageId))
                continue;

            result.Add(new RouteConfiguration
            {
                PackageId = packageId,
                OriginalVersion = version,
                Routes = entries,
                RouteGroup = routeGroup
            });
        }

        return result;
    }

    /// <summary>
    /// Parses the <c>Caller=Version;Caller2=Version2;*=Fallback</c> syntax.
    /// Whitespace around entries, keys and values is trimmed and ignored.
    /// </summary>
    private static IReadOnlyList<RouteEntry> ParseRouteTable(string routes)
    {
        var entries = new List<RouteEntry>();
        foreach (var segment in routes.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = segment.Trim();
            var eq = pair.IndexOf('=');
            if (eq < 0)
                continue;

            var caller = pair.Substring(0, eq).Trim();
            var version = pair.Substring(eq + 1).Trim();

            if (caller.Length == 0 || version.Length == 0)
                continue;

            entries.Add(new RouteEntry(caller, version));
        }

        return entries;
    }

    /// <summary>
    /// Groups configurations by their <see cref="RouteConfiguration.RouteGroup"/>
    /// value. Configurations without a group are placed in singleton groups.
    /// </summary>
    public static IReadOnlyList<RouteGroup> BuildGroups(IReadOnlyList<RouteConfiguration> configurations)
    {
        var byName = new Dictionary<string, List<RouteConfiguration>>(StringComparer.Ordinal);
        var groups = new List<RouteGroup>();

        foreach (var cfg in configurations)
        {
            var key = cfg.RouteGroup ?? cfg.PackageId;
            if (!byName.TryGetValue(key, out var bucket))
            {
                bucket = new List<RouteConfiguration>();
                byName[key] = bucket;
            }
            bucket.Add(cfg);
        }

        foreach (var kv in byName)
        {
            var isExplicit = kv.Value[0].RouteGroup is not null;
            groups.Add(new RouteGroup(kv.Key, kv.Value, isExplicit));
        }

        return groups;
    }
}

/// <summary>
/// A set of <see cref="RouteConfiguration"/> objects that share the same
/// <c>&lt;SwitchyardRouteGroup&gt;</c> name and therefore form a closed
/// dependency sandbox.
/// </summary>
public sealed class RouteGroup
{
    public RouteGroup(string name, IReadOnlyList<RouteConfiguration> members, bool isExplicit)
    {
        Name = name;
        Members = members;
        IsExplicit = isExplicit;
    }

    /// <summary>
    /// The group name (either the explicit <c>SwitchyardRouteGroup</c> value or
    /// the package id when the package is not part of an explicit group).
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The package configurations belonging to this group.
    /// </summary>
    public IReadOnlyList<RouteConfiguration> Members { get; }

    /// <summary>
    /// <c>true</c> when the group was declared explicitly via
    /// <c>&lt;SwitchyardRouteGroup&gt;</c>; <c>false</c> when it is an implicit
    /// single-member group for an ungrouped package.
    /// </summary>
    public bool IsExplicit { get; }

    /// <summary>
    /// Looks up the <see cref="RouteConfiguration"/> for a package id within this
    /// group, or <c>null</c> when the package is not part of the group.
    /// </summary>
    public RouteConfiguration? Find(string packageId)
    {
        foreach (var m in Members)
        {
            if (string.Equals(m.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
                return m;
        }
        return null;
    }
}
