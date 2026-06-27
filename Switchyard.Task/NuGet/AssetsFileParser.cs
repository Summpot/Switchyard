using NuGet.Frameworks;
using NuGet.ProjectModel;
using NuGet.Versioning;

namespace Switchyard.NuGet;

/// <summary>
/// Wraps the NuGet <see cref="LockFileFormat"/> parser to provide typed access
/// to the bits of <c>project.assets.json</c> that Switchyard cares about:
/// the per-target dependency graph and the location of restored package files.
/// </summary>
public sealed class AssetsFileParser
{
    private readonly LockFile _lockFile;
    private readonly string _packageFolder;

    private AssetsFileParser(LockFile lockFile, string packageFolder)
    {
        _lockFile = lockFile;
        _packageFolder = packageFolder;
    }

    /// <summary>
    /// Loads and parses the assets file at <paramref name="path"/>.
    /// </summary>
    public static AssetsFileParser? Load(string path)
    {
        if (!File.Exists(path))
            return null;

        var format = new LockFileFormat();
        var lockFile = format.Read(path);

        var packageFolder = ResolvePackageFolder(lockFile, Path.GetDirectoryName(path));
        if (packageFolder is null)
            return null;

        return new AssetsFileParser(lockFile, packageFolder);
    }

    /// <summary>
    /// The absolute path to the global packages folder used during restore.
    /// </summary>
    public string GlobalPackagesFolder => _packageFolder;

    /// <summary>
    /// Returns the <see cref="LockFileTarget"/> matching the supplied target
    /// framework string (e.g. <c>net10.0</c>).
    /// </summary>
    public LockFileTarget? GetTarget(string targetFramework)
    {
        var framework = NuGetFramework.Parse(targetFramework);
        foreach (var target in _lockFile.Targets)
        {
            if (NuGetFramework.Comparer.Equals(target.TargetFramework, framework))
                return target;
        }
        return null;
    }

    /// <summary>
    /// Returns the target library entry for the given package id and version,
    /// or <c>null</c> when the combination is not present in the resolved graph.
    /// </summary>
    public LockFileTargetLibrary? GetLibrary(LockFileTarget target, string packageId, string version)
    {
        if (!NuGetVersion.TryParse(version, out var nv))
            return null;

        foreach (var lib in target.Libraries)
        {
            if (string.Equals(lib.Name, packageId, StringComparison.OrdinalIgnoreCase)
                && lib.Version is not null
                && lib.Version.Equals(nv))
                return lib;
        }
        return null;
    }

    /// <summary>
    /// Returns the target library for the package id regardless of version,
    /// picking the first match. Useful for inspecting the original dependency.
    /// </summary>
    public LockFileTargetLibrary? GetLibraryById(LockFileTarget target, string packageId)
    {
        foreach (var lib in target.Libraries)
        {
            if (string.Equals(lib.Name, packageId, StringComparison.OrdinalIgnoreCase))
                return lib;
        }
        return null;
    }

    /// <summary>
    /// Returns the absolute path to the folder that contains the restored
    /// package files for <paramref name="packageId"/>/<paramref name="version"/>.
    /// </summary>
    public string GetPackageDirectory(string packageId, string version)
    {
        var lib = FindLibraryInfo(packageId, version);
        var relative = lib?.Path ?? Path.Combine(packageId.ToLowerInvariant(), version.ToLowerInvariant());
        return Path.Combine(_packageFolder, relative);
    }

    /// <summary>
    /// Walks the transitive dependency graph starting at <paramref name="packageId"/>/
    /// <paramref name="version"/> and returns every (id, version) pair reachable.
    /// The seed pair itself is included.
    /// </summary>
    public IEnumerable<(string Id, string Version)> GetTransitiveClosure(
        LockFileTarget target,
        string packageId,
        string version)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Id, string Version)>();
        queue.Enqueue((packageId, version));

        while (queue.Count > 0)
        {
            var (id, ver) = queue.Dequeue();
            var key = $"{id}/{ver}";
            if (!visited.Add(key))
                continue;

            yield return (id, ver);

            var lib = GetLibrary(target, id, ver);
            if (lib is null)
                continue;

            foreach (var dep in lib.Dependencies)
            {
                if (dep.VersionRange is null || dep.VersionRange.MinVersion is null)
                    continue;
                queue.Enqueue((dep.Id, dep.VersionRange.MinVersion.ToNormalizedString()));
            }
        }
    }

    /// <summary>
    /// Returns the best-matching managed runtime assembly inside the supplied
    /// package directory for the given target framework.
    /// </summary>
    public string? FindManagedAssembly(string packageDirectory, string targetFramework)
    {
        if (!Directory.Exists(packageDirectory))
            return null;

        var framework = NuGetFramework.Parse(targetFramework);
        var libDir = Path.Combine(packageDirectory, "lib");
        if (!Directory.Exists(libDir))
        {
            return FindSingleDll(packageDirectory);
        }

        var candidates = new List<(NuGetFramework Framework, string Path)>();
        foreach (var dir in Directory.GetDirectories(libDir))
        {
            var name = Path.GetFileName(dir);
            var parsed = NuGetFramework.Parse(name);
            candidates.Add((parsed, dir));
        }

        var reducer = new FrameworkReducer();
        var best = reducer.GetNearest(framework, candidates.Select(c => c.Framework));
        if (best is null)
            return FindSingleDll(packageDirectory);

        var bestDir = candidates.First(c => NuGetFramework.Comparer.Equals(c.Framework, best)).Path;
        return FindSingleDll(bestDir);
    }

    private static string? FindSingleDll(string directory)
    {
        var dlls = Directory.GetFiles(directory, "*.dll");
        string? best = null;
        foreach (var dll in dlls)
        {
            var name = Path.GetFileNameWithoutExtension(dll);
            if (name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
                continue;
            best = dll;
            break;
        }
        return best;
    }

    private LockFileLibrary? FindLibraryInfo(string packageId, string version)
    {
        if (!NuGetVersion.TryParse(version, out var nv))
            return null;

        foreach (var lib in _lockFile.Libraries)
        {
            if (string.Equals(lib.Name, packageId, StringComparison.OrdinalIgnoreCase)
                && lib.Version is not null
                && lib.Version.Equals(nv))
                return lib;
        }
        return null;
    }

    private static string ResolvePackageFolder(LockFile lockFile, string? projectDir)
    {
        if (lockFile.PackageFolders.Count > 0)
        {
            var first = lockFile.PackageFolders[0].Path;
            if (Path.IsPathRooted(first))
                return first;
            if (projectDir is not null)
                return Path.GetFullPath(Path.Combine(projectDir, first));
        }

        var env = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrWhiteSpace(env))
            return env;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".nuget", "packages");
    }
}
