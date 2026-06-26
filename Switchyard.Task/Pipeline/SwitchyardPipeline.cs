using Switchyard.Configuration;
using Switchyard.NuGet;
using Switchyard.Weaver;

namespace Switchyard.Pipeline;

/// <summary>
/// Describes a single prepared (renamed) target assembly that should be added
/// to the MSBuild copy-local output.
/// </summary>
public sealed class PreparedAssembly
{
    public PreparedAssembly(string dllPath, string? pdbPath, string packageId, string version, bool isRouted)
    {
        DllPath = dllPath;
        PdbPath = pdbPath;
        PackageId = packageId;
        Version = version;
        IsRouted = isRouted;
    }

    public string DllPath { get; }
    public string? PdbPath { get; }
    public string PackageId { get; }
    public string Version { get; }
    public bool IsRouted { get; }
}

/// <summary>
/// Describes a caller assembly whose references were redirected. The modified
/// copy lives in the intermediate directory and should replace the original
/// entry in the copy-local output.
/// </summary>
public sealed class RedirectedCaller
{
    public RedirectedCaller(string originalPath, string modifiedPath, string? modifiedPdbPath)
    {
        OriginalPath = originalPath;
        ModifiedPath = modifiedPath;
        ModifiedPdbPath = modifiedPdbPath;
    }

    public string OriginalPath { get; }
    public string ModifiedPath { get; }
    public string? ModifiedPdbPath { get; }
}

/// <summary>
/// The complete result of a Switchyard pipeline run.
/// </summary>
public sealed class SwitchyardResult
{
    public SwitchyardResult(
        IReadOnlyList<PreparedAssembly> preparedAssemblies,
        IReadOnlyList<RedirectedCaller> redirectedCallers,
        IReadOnlyList<string> removedOriginalPaths)
    {
        PreparedAssemblies = preparedAssemblies;
        RedirectedCallers = redirectedCallers;
        RemovedOriginalPaths = removedOriginalPaths;
    }

    public IReadOnlyList<PreparedAssembly> PreparedAssemblies { get; }
    public IReadOnlyList<RedirectedCaller> RedirectedCallers { get; }
    public IReadOnlyList<string> RemovedOriginalPaths { get; }
}

/// <summary>
/// Orchestrates the full Switchyard weaving pipeline:
/// <list type="number">
/// <item>Resolve package directories for every routed version.</item>
/// <item>Rename target assemblies to their <c>.Switchyard.{version}</c> identity.</item>
/// <item>For explicit route groups, redirect the renamed target's own references
/// to the other group members' routed names, closing the sandbox.</item>
/// <item>Redirect every caller assembly's references to the appropriate routed
/// name.</item>
/// </list>
/// </summary>
public sealed class SwitchyardPipeline
{
    private readonly string _intermediateDir;
    private readonly string _targetFramework;
    private readonly AssetsFileParser? _assets;
    private readonly PackageResolver _resolver;
    private readonly string _assemblyName;

    private SwitchyardPipeline(
        string intermediateDir,
        string targetFramework,
        AssetsFileParser? assets,
        PackageResolver resolver,
        string assemblyName)
    {
        _intermediateDir = intermediateDir;
        _targetFramework = targetFramework;
        _assets = assets;
        _resolver = resolver;
        _assemblyName = assemblyName;
    }

    /// <summary>
    /// Creates a pipeline using the supplied configuration.
    /// </summary>
    public static SwitchyardPipeline Create(
        string intermediateOutputPath,
        string targetFramework,
        string? projectAssetsFile,
        string assemblyName)
    {
        var intermediateDir = Path.Combine(intermediateOutputPath, "switchyard");
        Directory.CreateDirectory(intermediateDir);

        var assets = projectAssetsFile is not null ? AssetsFileParser.Load(projectAssetsFile) : null;

        var globalPackagesFolder = assets?.GlobalPackagesFolder
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget", "packages");

        var nugetConfig = FindNuGetConfig(Path.GetDirectoryName(projectAssetsFile) ?? intermediateOutputPath);
        var resolver = PackageResolver.Create(globalPackagesFolder, nugetConfig);

        return new SwitchyardPipeline(intermediateDir, targetFramework, assets, resolver, assemblyName);
    }

    /// <summary>
    /// Executes the pipeline for the supplied configurations and caller paths.
    /// </summary>
    /// <param name="configurations">Parsed route configurations.</param>
    /// <param name="callerPaths">Absolute paths to every caller assembly that
    /// may need reference redirection. This typically includes
    /// <c>ReferenceCopyLocalPaths</c> and the main project's intermediate
    /// assembly.</param>
    public SwitchyardResult Execute(
        IReadOnlyList<RouteConfiguration> configurations,
        IReadOnlyList<string> callerPaths)
    {
        if (configurations.Count == 0)
            return new SwitchyardResult(Array.Empty<PreparedAssembly>(), Array.Empty<RedirectedCaller>(), Array.Empty<string>());

        var groups = ConfigurationParser.BuildGroups(configurations);
        var prepared = new List<PreparedAssembly>();
        var removedOriginals = new List<string>();

        var packageToRoutedName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var cfg in configurations)
        {
            foreach (var version in cfg.GetAllTargetVersions())
            {
                var isOriginal = cfg.IsOriginalVersion(version);
                var routedName = isOriginal ? cfg.PackageId : cfg.GetRoutedName(version);
                packageToRoutedName[RoutedKey(cfg.PackageId, version)] = routedName;

                if (isOriginal)
                    continue;

                var sourceDll = ResolvePackageDll(cfg.PackageId, version);
                if (sourceDll is null)
                    continue;

                var outDll = Path.Combine(_intermediateDir, routedName + ".dll");
                AssemblyWeaver.PrepareAndRename(sourceDll, routedName, outDll);

                var outPdb = Path.ChangeExtension(outDll, ".pdb");
                prepared.Add(new PreparedAssembly(
                    outDll,
                    File.Exists(outPdb) ? outPdb : null,
                    cfg.PackageId,
                    version,
                    true));

                var originalDll = FindOriginalCopyLocalDll(callerPaths, cfg.PackageId);
                if (originalDll is not null)
                    removedOriginals.Add(originalDll);
            }
        }

        foreach (var group in groups)
        {
            if (!group.IsExplicit)
                continue;

            foreach (var cfg in group.Members)
            {
                foreach (var version in cfg.GetAllTargetVersions())
                {
                    if (cfg.IsOriginalVersion(version))
                        continue;

                    var routedName = cfg.GetRoutedName(version);
                    var dllPath = Path.Combine(_intermediateDir, routedName + ".dll");
                    if (!File.Exists(dllPath))
                        continue;

                    var redirections = BuildGroupRedirections(group, version);
                    if (redirections.Count == 0)
                        continue;

                    var tempPath = Path.Combine(_intermediateDir, routedName + ".tmp.dll");
                    ReferenceRedirector.RedirectReferences(dllPath, redirections, tempPath);
                    if (File.Exists(tempPath))
                    {
                        File.Delete(dllPath);
                        File.Move(tempPath, dllPath);
                    }
                }
            }
        }

        var redirectedCallers = new List<RedirectedCaller>();
        var allPackageNames = new HashSet<string>(
            configurations.Select(c => c.PackageId),
            StringComparer.OrdinalIgnoreCase);

        foreach (var callerPath in callerPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(callerPath))
                continue;

            var callerName = DetermineCallerName(callerPath);
            if (callerName is null)
                continue;

            var redirections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cfg in configurations)
            {
                var version = cfg.ResolveVersionForCaller(callerName);
                if (version is null)
                    continue;

                if (!packageToRoutedName.TryGetValue(RoutedKey(cfg.PackageId, version), out var routedName))
                    continue;

                if (routedName != cfg.PackageId)
                    redirections[cfg.PackageId] = routedName;
            }

            if (redirections.Count == 0)
                continue;

            if (!ReferenceRedirector.ReferencesAnyPackage(callerPath, allPackageNames))
                continue;

            var callerFileName = Path.GetFileName(callerPath);
            var outPath = Path.Combine(_intermediateDir, callerFileName);
            ReferenceRedirector.RedirectReferences(callerPath, redirections, outPath);

            var outPdb = Path.ChangeExtension(outPath, ".pdb");
            redirectedCallers.Add(new RedirectedCaller(
                callerPath,
                outPath,
                File.Exists(outPdb) ? outPdb : null));
        }

        return new SwitchyardResult(
            prepared,
            redirectedCallers,
            removedOriginals.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private string? ResolvePackageDll(string packageId, string version)
    {
        var packageDir = _resolver.EnsurePackageAvailableAsync(packageId, version).GetAwaiter().GetResult();
        return _resolver.FindManagedAssembly(packageDir, _targetFramework);
    }

    private Dictionary<string, string> BuildGroupRedirections(RouteGroup group, string version)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in group.Members)
        {
            var routedName = member.IsOriginalVersion(version) ? member.PackageId : member.GetRoutedName(version);
            if (routedName != member.PackageId)
                map[member.PackageId] = routedName;
        }
        return map;
    }

    private string? DetermineCallerName(string callerPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(callerPath);
        if (string.Equals(fileName, _assemblyName, StringComparison.OrdinalIgnoreCase))
            return _assemblyName;
        return fileName;
    }

    private static string? FindOriginalCopyLocalDll(IReadOnlyList<string> callerPaths, string packageId)
    {
        var expected = packageId + ".dll";
        foreach (var path in callerPaths)
        {
            if (string.Equals(Path.GetFileName(path), expected, StringComparison.OrdinalIgnoreCase))
                return path;
        }
        return null;
    }

    private static string? FindNuGetConfig(string? startDir)
    {
        var dir = startDir;
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, "nuget.config");
            if (File.Exists(candidate))
                return candidate;
            candidate = Path.Combine(dir, "NuGet.Config");
            if (File.Exists(candidate))
                return candidate;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir)
                break;
            dir = parent;
        }
        return null;
    }

    private static string RoutedKey(string packageId, string version)
        => packageId + "/" + version;
}
