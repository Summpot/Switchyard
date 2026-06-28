using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace Switchyard.NuGet;

/// <summary>
/// Locates packages in the global packages folder. Read-only: Switchyard never
/// downloads — the restore-time <c>SwitchyardPackageDownloadInjectionTask</c>
/// turns every routed version (and its native-assets companions) into
/// <c>&lt;PackageDownload&gt;</c> items, so NuGet itself fetches them. The
/// weaving task then only reads what NuGet already restored.
/// </summary>
/// <remarks>
/// This keeps restore behaviour (sources, credentials, proxy, offline mode,
/// caching semantics) entirely NuGet's: there is no second, partial NuGet
/// client inside the build task. A package that is not present in the global
/// packages folder means restore did not run (or the consumer forgot to
/// reference the native-assets package for the host platform), and the caller
/// is expected to surface a clear error rather than silently fetching.
/// </remarks>
public sealed class PackageResolver
{
    private readonly string _globalPackagesFolder;

    private PackageResolver(string globalPackagesFolder)
    {
        _globalPackagesFolder = globalPackagesFolder;
    }

    /// <summary>
    /// Creates a resolver rooted at the supplied global packages folder (the
    /// one recorded in <c>project.assets.json</c>, or NuGet's default).
    /// </summary>
    public static PackageResolver Create(string globalPackagesFolder, string? nugetConfigPath)
        => new(globalPackagesFolder);

    /// <summary>
    /// The global packages folder used by this resolver.
    /// </summary>
    public string GlobalPackagesFolder => _globalPackagesFolder;

    /// <summary>
    /// Returns the on-disk package directory for <paramref name="packageId"/>/
    /// <paramref name="version"/>, or <c>null</c> when NuGet has not restored
    /// that version. Does not download: callers should surface the missing
    /// version so the consumer knows to run restore (or reference the
    /// native-assets package for the host platform) rather than having
    /// Switchyard silently fetch it.
    /// </summary>
    public string? GetPackageDirectory(string packageId, string version)
    {
        if (!NuGetVersion.TryParse(version, out var nugetVersion))
            return null;

        var pathResolver = new VersionFolderPathResolver(_globalPackagesFolder);
        var installPath = pathResolver.GetInstallPath(packageId, nugetVersion);
        return Directory.Exists(installPath) && IsPackageExtracted(installPath) ? installPath : null;
    }

    /// <summary>
    /// Returns the best-matching managed assembly for the supplied target
    /// framework inside the restored package directory.
    /// </summary>
    public string? FindManagedAssembly(string packageDirectory, string targetFramework)
    {
        var framework = NuGetFramework.Parse(targetFramework);
        var libDir = Path.Combine(packageDirectory, "lib");
        if (!Directory.Exists(libDir))
            return FindSingleManagedDll(packageDirectory);

        var candidates = new List<(NuGetFramework Framework, string Path)>();
        foreach (var dir in Directory.GetDirectories(libDir))
        {
            var name = Path.GetFileName(dir);
            candidates.Add((NuGetFramework.Parse(name), dir));
        }

        var reducer = new FrameworkReducer();
        var best = reducer.GetNearest(framework, candidates.Select(c => c.Framework));
        if (best is null)
            return FindSingleManagedDll(packageDirectory);

        var bestDir = candidates.First(c => NuGetFramework.Comparer.Equals(c.Framework, best)).Path;
        return FindSingleManagedDll(bestDir);
    }

    private static bool IsPackageExtracted(string installPath)
    {
        return Directory.GetFiles(installPath, "*.nuspec", SearchOption.TopDirectoryOnly).Length > 0
            || Directory.GetDirectories(installPath).Length > 0;
    }

    private static string? FindSingleManagedDll(string directory)
    {
        foreach (var dll in Directory.GetFiles(directory, "*.dll"))
        {
            var name = Path.GetFileNameWithoutExtension(dll);
            if (name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
                continue;
            return dll;
        }
        return null;
    }
}