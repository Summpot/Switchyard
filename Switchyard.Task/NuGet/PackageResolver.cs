using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace Switchyard.NuGet;

/// <summary>
/// Locates or downloads a specific NuGet package version and exposes the
/// restored package directory on disk. The original version declared on the
/// <c>&lt;PackageReference&gt;</c> is expected to already be present in the
/// global packages folder after restore; any additional routed version is
/// fetched on demand using <see cref="DownloadResource"/>.
/// </summary>
public sealed class PackageResolver
{
    private static readonly ILogger NuGetLog = NullLogger.Instance;
    private readonly string _globalPackagesFolder;
    private readonly ISettings _settings;
    private readonly SourceRepository[] _repositories;

    private PackageResolver(string globalPackagesFolder, ISettings settings, SourceRepository[] repositories)
    {
        _globalPackagesFolder = globalPackagesFolder;
        _settings = settings;
        _repositories = repositories;
    }

    /// <summary>
    /// Creates a resolver using the supplied global packages folder and the
    /// package sources declared in <c>nuget.config</c>.
    /// </summary>
    public static PackageResolver Create(string globalPackagesFolder, string? nugetConfigPath)
    {
        var settings = nugetConfigPath is not null && File.Exists(nugetConfigPath)
            ? Settings.LoadSpecificSettings(Path.GetDirectoryName(nugetConfigPath)!, Path.GetFileName(nugetConfigPath))
            : Settings.LoadDefaultSettings(Directory.GetCurrentDirectory());

        var providers = Repository.Provider.GetCoreV3();
        var sources = SettingsUtility.GetEnabledSources(settings).ToList();
        if (sources.Count == 0)
        {
            sources.Add(new PackageSource("https://api.nuget.org/v3/index.json"));
        }

        var repos = sources.Select(s => Repository.CreateSource(providers, s)).ToArray();
        return new PackageResolver(globalPackagesFolder, settings, repos);
    }

    /// <summary>
    /// The global packages folder used by this resolver.
    /// </summary>
    public string GlobalPackagesFolder => _globalPackagesFolder;

    /// <summary>
    /// Returns the on-disk package directory for <paramref name="packageId"/>/
    /// <paramref name="version"/>. When the package is not yet present in the
    /// global packages folder it is downloaded silently.
    /// </summary>
    public async Task<string> EnsurePackageAvailableAsync(
        string packageId,
        string version,
        CancellationToken cancellationToken = default)
    {
        if (!NuGetVersion.TryParse(version, out var nugetVersion))
            throw new InvalidOperationException($"'{version}' is not a valid NuGet version.");

        var identity = new PackageIdentity(packageId, nugetVersion);
        var pathResolver = new VersionFolderPathResolver(_globalPackagesFolder);
        var installPath = pathResolver.GetInstallPath(packageId, nugetVersion);

        if (Directory.Exists(installPath) && IsPackageExtracted(installPath))
            return installPath;

        var nupkgPath = pathResolver.GetPackageFilePath(packageId, nugetVersion);
        if (File.Exists(nupkgPath) && Directory.Exists(installPath))
            return installPath;

        await DownloadAndExtractAsync(identity, installPath, cancellationToken);
        return installPath;
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

    private async Task DownloadAndExtractAsync(
        PackageIdentity identity,
        string installPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_globalPackagesFolder);

        Exception? lastError = null;
        foreach (var repo in _repositories)
        {
            try
            {
                var downloadResource = await repo.GetResourceAsync<DownloadResource>(cancellationToken);
                var context = new PackageDownloadContext(new SourceCacheContext());
                var result = await downloadResource.GetDownloadResourceResultAsync(
                    identity, context, _globalPackagesFolder, NuGetLog, cancellationToken);

                if (result.Status == DownloadResourceResultStatus.Available && result.PackageStream is not null)
                {
                    await ExtractToGlobalPackagesAsync(identity, result.PackageStream, installPath, cancellationToken);
                    return;
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException(
            $"Failed to download package '{identity.Id}' version '{identity.Version}' from any configured source.",
            lastError);
    }

    private async Task ExtractToGlobalPackagesAsync(
        PackageIdentity identity,
        Stream packageStream,
        string installPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(installPath);

        var nupkgPath = Path.Combine(installPath, identity.Id.ToLowerInvariant() + "." +
            identity.Version.ToNormalizedString().ToLowerInvariant() + ".nupkg");

        using (var fs = File.Create(nupkgPath))
        {
            packageStream.Seek(0, SeekOrigin.Begin);
            await packageStream.CopyToAsync(fs, 8192, cancellationToken);
        }

        using var reader = new PackageArchiveReader(nupkgPath);
        var files = reader.GetFiles().ToList();
        reader.CopyFiles(installPath, files, (name, target, s) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var targetStream = File.Create(target);
            s.CopyTo(targetStream);
            return name;
        }, NuGetLog, cancellationToken);
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
