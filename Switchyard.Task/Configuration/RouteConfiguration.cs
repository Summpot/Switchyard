namespace Switchyard.Configuration;

/// <summary>
/// Represents the routing configuration extracted from a single
/// <c>&lt;PackageReference&gt;</c> node decorated with <c>&lt;SwitchyardRoutes&gt;</c>.
/// </summary>
public sealed class RouteConfiguration
{
    /// <summary>
    /// The NuGet package id that is being routed (e.g. <c>TargetLib</c>).
    /// </summary>
    public string PackageId { get; init; }

    /// <summary>
    /// The original version declared on the <c>&lt;PackageReference&gt;</c> node
    /// (the version that the main project would normally use).
    /// </summary>
    public string OriginalVersion { get; init; }

    /// <summary>
    /// Ordered route table mapping caller assembly names to the target version.
    /// The special key <c>*</c> acts as the fallback rule.
    /// </summary>
    public IReadOnlyList<RouteEntry> Routes { get; init; }

    /// <summary>
    /// Optional isolation group name (<c>&lt;SwitchyardRouteGroup&gt;</c>).
    /// Packages sharing the same group form a closed dependency sandbox. For
    /// example, if <c>TargetLib</c> depends on <c>CommonUtils</c> and both
    /// carry <c>&lt;SwitchyardRouteGroup&gt;AuthIsolation&lt;/SwitchyardRouteGroup&gt;</c>
    /// with <c>AuthModule=1.0.0</c>, then the 1.0.0 copy of <c>TargetLib</c>
    /// has its internal <c>CommonUtils</c> reference forced to
    /// <c>CommonUtils.Switchyard.1.0.0</c>, forming a closed loop and keeping
    /// the sub-dependency chain on the same routed version.
    /// </summary>
    public string? RouteGroup { get; init; }

    public RouteConfiguration()
    {
        PackageId = string.Empty;
        OriginalVersion = string.Empty;
        Routes = Array.Empty<RouteEntry>();
    }

    /// <summary>
    /// Computes the routed assembly name for a given version.
    /// </summary>
    public string GetRoutedName(string version) => $"{PackageId}.Switchyard.{SanitizeVersion(version)}";

    /// <summary>
    /// Resolves the version that should be used for the given caller assembly name.
    /// Returns <c>null</c> when no rule (including the <c>*</c> fallback) matches.
    /// </summary>
    public string? ResolveVersionForCaller(string callerName)
    {
        foreach (var entry in Routes)
        {
            if (entry.IsWildcard)
                continue;
            if (string.Equals(entry.Caller, callerName, StringComparison.OrdinalIgnoreCase))
                return entry.Version;
        }

        foreach (var entry in Routes)
        {
            if (entry.IsWildcard)
                return entry.Version;
        }

        return null;
    }

    /// <summary>
    /// Returns the distinct set of versions referenced by every rule, including
    /// the wildcard fallback. This is the set of physical copies that must be
    /// prepared in the intermediate directory.
    /// </summary>
    public IEnumerable<string> GetAllTargetVersions()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Routes)
        {
            if (seen.Add(entry.Version))
                yield return entry.Version;
        }
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="version"/> is the same as the
    /// <see cref="OriginalVersion"/> declared on the package reference. The
    /// original version does not need to be renamed because it is already
    /// resolved by NuGet restore.
    /// </summary>
    public bool IsOriginalVersion(string version)
        => string.Equals(version, OriginalVersion, StringComparison.OrdinalIgnoreCase);

    private static string SanitizeVersion(string version)
    {
        var sb = new System.Text.StringBuilder(version.Length);
        foreach (var c in version)
        {
            if (char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_')
                sb.Append(c);
            else
                sb.Append('_');
        }
        return sb.ToString();
    }
}

/// <summary>
/// A single caller-to-version mapping entry inside a <see cref="RouteConfiguration"/>.
/// </summary>
public readonly struct RouteEntry
{
    public RouteEntry(string caller, string version)
    {
        Caller = caller;
        Version = version;
    }

    /// <summary>
    /// The caller assembly name, or <c>*</c> for the wildcard fallback.
    /// </summary>
    public string Caller { get; }

    /// <summary>
    /// The target version for this caller.
    /// </summary>
    public string Version { get; }

    /// <summary>
    /// <c>true</c> when this entry is the <c>*</c> wildcard fallback rule.
    /// </summary>
    public bool IsWildcard => Caller == "*";
}
