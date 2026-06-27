using Switchyard.Configuration;
using Switchyard.NuGet;
using Switchyard.Weaver;
using System.Runtime.InteropServices;

namespace Switchyard.Pipeline;

/// <summary>
/// Describes a single prepared (renamed) target assembly that should be added
/// to the MSBuild copy-local output.
/// </summary>
public sealed class PreparedAssembly
{
    public PreparedAssembly(
        string dllPath,
        string? pdbPath,
        string packageId,
        string version,
        bool isRouted,
        IReadOnlyList<string>? nativeLibraryPaths = null)
    {
        DllPath = dllPath;
        PdbPath = pdbPath;
        PackageId = packageId;
        Version = version;
        IsRouted = isRouted;
        NativeLibraryPaths = nativeLibraryPaths ?? Array.Empty<string>();
    }

    public string DllPath { get; }
    public string? PdbPath { get; }
    public string PackageId { get; }
    public string Version { get; }
    public bool IsRouted { get; }

    /// <summary>
    /// Paths to the renamed native libraries that accompany this routed
    /// assembly (e.g. <c>libskiasharp.Switchyard.1.0.0.dll</c>). Empty for
    /// packages without a native dependency. Each path should be added to the
    /// MSBuild copy-local output next to the managed DLL.
    /// </summary>
    public IReadOnlyList<string> NativeLibraryPaths { get; }
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
        IReadOnlyList<string> removedOriginalPaths,
        IReadOnlyList<string> removedOriginalNativePaths)
    {
        PreparedAssemblies = preparedAssemblies;
        RedirectedCallers = redirectedCallers;
        RemovedOriginalPaths = removedOriginalPaths;
        RemovedOriginalNativePaths = removedOriginalNativePaths;
    }

    public IReadOnlyList<PreparedAssembly> PreparedAssemblies { get; }
    public IReadOnlyList<RedirectedCaller> RedirectedCallers { get; }
    public IReadOnlyList<string> RemovedOriginalPaths { get; }

    /// <summary>
    /// Paths to the original (un-routed) native libraries that should be
    /// stripped from the copy-local output because a renamed copy is shipped
    /// instead.
    /// </summary>
    public IReadOnlyList<string> RemovedOriginalNativePaths { get; }
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
        // MSBuild frequently passes relative paths (e.g. "obj\project.assets.json").
        // Normalise everything against the current working directory (which MSBuild
        // sets to the project directory) so downstream path traversal is correct.
        intermediateOutputPath = Path.GetFullPath(intermediateOutputPath);
        projectAssetsFile = projectAssetsFile is not null
            ? Path.GetFullPath(projectAssetsFile)
            : null;

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
            return new SwitchyardResult(Array.Empty<PreparedAssembly>(), Array.Empty<RedirectedCaller>(), Array.Empty<string>(), Array.Empty<string>());

        var groups = ConfigurationParser.BuildGroups(configurations);
        var prepared = new List<PreparedAssembly>();
        var removedOriginals = new List<string>();
        var removedOriginalNatives = new List<string>();

        var packageToRoutedName = new Dictionary<string, string>(StringComparer.Ordinal);
        // original native lib name -> routed native lib name, for the version a
        // given caller binds. Keyed by (packageId, version) so each routed
        // managed version rewrites its DllImports to its own native lib name.
        var nativeRedirectionsByRoutedName = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var cfg in configurations)
        {
            foreach (var version in cfg.GetAllTargetVersions())
            {
                var isOriginal = cfg.IsOriginalVersion(version);
                var routedName = isOriginal ? cfg.PackageId : cfg.GetRoutedName(version);
                packageToRoutedName[RoutedKey(cfg.PackageId, version)] = routedName;

                if (isOriginal)
                    continue;

                var packageDir = _resolver.EnsurePackageAvailableAsync(cfg.PackageId, version).GetAwaiter().GetResult();
                var sourceDll = _resolver.FindManagedAssembly(packageDir, _targetFramework);
                if (sourceDll is null)
                    continue;

                var outDll = Path.Combine(_intermediateDir, routedName + ".dll");
                AssemblyWeaver.PrepareAndRename(sourceDll, routedName, outDll);

                // Discover and rename the native libraries this package ships
                // (under runtimes/{rid}/native/). Each routed managed version
                // gets its own renamed native file, and the routed managed
                // assembly's own DllImport entries are repointed at that
                // renamed native name so P/Invoke binds to the version-specific
                // native lib instead of a single shared one.
                var nativePaths = PrepareNativeLibraries(
                    packageDir, routedName, version, outDll,
                    nativeRedirectionsByRoutedName, removedOriginalNatives, callerPaths);

                var outPdb = Path.ChangeExtension(outDll, ".pdb");
                prepared.Add(new PreparedAssembly(
                    outDll,
                    File.Exists(outPdb) ? outPdb : null,
                    cfg.PackageId,
                    version,
                    true,
                    nativePaths));

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

                    var nativeRedirs = nativeRedirectionsByRoutedName.TryGetValue(routedName, out var nd) ? nd : null;
                    var tempPath = Path.Combine(_intermediateDir, routedName + ".tmp.dll");
                    ReferenceRedirector.RedirectReferences(dllPath, redirections, tempPath, nativeRedirs);
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
            {
                continue;
            }

            // Skip native DLLs, XML documentation files, and other non-PE
            // entries that MSBuild may have placed in ReferenceCopyLocalPaths.
            if (!IsManagedAssembly(callerPath))
                continue;

            var callerName = DetermineCallerName(callerPath);
            if (callerName is null)
                continue;

            System.Diagnostics.Debug.WriteLine($"Switchyard: examining caller {callerName} at {callerPath}");

            var redirections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Aggregate native redirections across every package this caller is
            // routed to, so a caller that P/Invokes several routed native libs
            // gets all of them rewritten.
            var callerNativeRedirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cfg in configurations)
            {
                var version = cfg.ResolveVersionForCaller(callerName);
                if (version is null)
                    continue;

                if (!packageToRoutedName.TryGetValue(RoutedKey(cfg.PackageId, version), out var routedName))
                    continue;

                if (routedName != cfg.PackageId)
                    redirections[cfg.PackageId] = routedName;

                if (nativeRedirectionsByRoutedName.TryGetValue(routedName, out var nativeMap))
                {
                    foreach (var kv in nativeMap)
                        callerNativeRedirs[kv.Key] = kv.Value;
                }
            }

            if (redirections.Count == 0 && callerNativeRedirs.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine($"Switchyard: no redirections for {callerName}");
                continue;
            }

            if (redirections.Count > 0 && !ReferenceRedirector.ReferencesAnyPackage(callerPath, allPackageNames))
            {
                System.Diagnostics.Debug.WriteLine($"Switchyard: {callerName} does not reference any routed package");
                continue;
            }

            var callerFileName = Path.GetFileName(callerPath);
            var outPath = Path.Combine(_intermediateDir, callerFileName);
            ReferenceRedirector.RedirectReferences(callerPath, redirections, outPath, callerNativeRedirs);

            var outPdb = Path.ChangeExtension(outPath, ".pdb");
            redirectedCallers.Add(new RedirectedCaller(
                callerPath,
                outPath,
                File.Exists(outPdb) ? outPdb : null));
        }

        return new SwitchyardResult(
            prepared,
            redirectedCallers,
            removedOriginals.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            removedOriginalNatives.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private string? ResolvePackageDll(string packageId, string version)
    {
        var packageDir = _resolver.EnsurePackageAvailableAsync(packageId, version).GetAwaiter().GetResult();
        return _resolver.FindManagedAssembly(packageDir, _targetFramework);
    }

    /// <summary>
    /// Locates, renames and copies the native libraries a routed package ships
    /// into the intermediate directory so each routed managed version binds
    /// its own native library. Also rewrites the routed managed assembly's own
    /// <c>DllImport</c> entries to point at the renamed native names. Records the
    /// original->routed native-name map for the given routed managed assembly and
    /// notes the original native paths that should be stripped from the
    /// copy-local output.
    /// </summary>
    /// <remarks>
    /// Native libraries are discovered under <c>runtimes/{rid}/native/</c>.
    /// The runtime identifier is derived from the current process so the build
    /// picks the native file that will actually be loaded on the target OS/arch.
    /// The DllImport module name is the bare name without extension and without
    /// the <c>lib</c> prefix used on Linux/macOS; the renamed native file
    /// preserves the platform prefix/suffix so the OS loader still finds it
    /// (e.g. <c>libskiasharp.so</c> -> <c>libskiasharp.Switchyard.2.88.0.so</c>,
    /// DllImport <c>skiasharp</c> -> <c>skiasharp.Switchyard.2.88.0</c>).
    /// </remarks>
    private List<string> PrepareNativeLibraries(
        string packageDir,
        string routedManagedName,
        string version,
        string managedDllPath,
        Dictionary<string, Dictionary<string, string>> nativeRedirectionsByRoutedName,
        List<string> removedOriginalNatives,
        IReadOnlyList<string> callerPaths)
    {
        var nativePaths = new List<string>();
        var rid = GetCurrentRuntimeIdentifier();
        var nativeDir = Path.Combine(packageDir, "runtimes", rid, "native");
        if (!Directory.Exists(nativeDir))
            return nativePaths;

        // Only rewrite native libs actually referenced via DllImport by the
        // managed assembly, to avoid renaming unrelated native assets.
        var pinvokeNames = ReferenceRedirector.GetPInvokeModuleNames(managedDllPath);
        var nativeMap = new Dictionary<string, string>(StringComparer.Ordinal);
        bool isWindows = OperatingSystem.IsWindows();

        foreach (var nativeFile in Directory.EnumerateFiles(nativeDir))
        {
            var fileName = Path.GetFileName(nativeFile);
            var ext = Path.GetExtension(fileName);
            var fileNameNoExt = Path.GetFileNameWithoutExtension(fileName);

            // The DllImport module name is the bare name: no extension, and on
            // non-Windows no leading "lib" prefix (the loader adds it).
            bool libPrefixed = !isWindows && fileNameNoExt.StartsWith("lib", StringComparison.Ordinal);
            string pinvokeName = libPrefixed ? fileNameNoExt.Substring(3) : fileNameNoExt;

            if (!pinvokeNames.Contains(pinvokeName))
                continue;

            // Routed DllImport target name (bare) and the matching on-disk file
            // name (with platform prefix + extension preserved).
            string routedBase = pinvokeName + ".Switchyard." + version;
            string routedFileName = (libPrefixed ? "lib" : "") + routedBase + ext;
            var outNative = Path.Combine(_intermediateDir, routedFileName);
            File.Copy(nativeFile, outNative, overwrite: true);
            nativePaths.Add(outNative);

            nativeMap[pinvokeName] = routedBase;

            // Remember the original native path (as MSBuild would copy it
            // under runtimes/{rid}/native/) so the publish cleanup can strip it.
            var originalCopy = FindOriginalCopyLocalNative(callerPaths, fileName);
            if (originalCopy is not null)
                removedOriginalNatives.Add(originalCopy);
        }

        if (nativeMap.Count == 0)
            return nativePaths;

        nativeRedirectionsByRoutedName[routedManagedName] = nativeMap;

        // Rewrite the routed managed assembly's OWN DllImport entries to point
        // at the renamed native names. This is the actual native-isolation step:
        // the managed assembly is what carries the P/Invoke declaration, so it
        // (not its callers) must be rewritten.
        RewritePInvokeInPlace(managedDllPath, nativeMap);

        return nativePaths;
    }

    /// <summary>
    /// Rewrites the <c>ModuleRef</c> names backing the P/Invoke entries of the
    /// assembly at <paramref name="assemblyPath"/> according to
    /// <paramref name="nativeRedirections"/>, writing the result back over the
    /// same file (via a temp file + move so the input can be the destination).
    /// </summary>
    private static void RewritePInvokeInPlace(
        string assemblyPath,
        IReadOnlyDictionary<string, string> nativeRedirections)
    {
        var tempPath = assemblyPath + ".pinvoke.tmp";
        ReferenceRedirector.RedirectReferences(assemblyPath, new Dictionary<string, string>(), tempPath, nativeRedirections);
        if (!File.Exists(tempPath))
            return;
        File.Delete(assemblyPath);
        File.Move(tempPath, assemblyPath);

        // The pdb stays valid: only the metadata ModuleRef names changed, not
        // the MVID or method bodies, so the existing .pdb still binds.
    }

    private static string GetCurrentRuntimeIdentifier()
    {
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => "x64",
        };

        if (OperatingSystem.IsWindows())
            return "win-" + arch;
        if (OperatingSystem.IsLinux())
            return "linux-" + arch;
        if (OperatingSystem.IsMacOS())
            return "osx-" + arch;
        return "win-" + arch;
    }

    private static string? FindOriginalCopyLocalNative(IReadOnlyList<string> callerPaths, string nativeFileName)
    {
        foreach (var path in callerPaths)
        {
            if (string.Equals(Path.GetFileName(path), nativeFileName, StringComparison.OrdinalIgnoreCase))
                return path;
        }
        return null;
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

    private static bool IsManagedAssembly(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            if (fs.Length < 0x40)
                return false;
            fs.Position = 0x3C;
            int peOffset = br.ReadInt32();
            if (peOffset < 0 || peOffset + 4 > fs.Length)
                return false;
            fs.Position = peOffset;
            uint peHeader = br.ReadUInt32();
            if (peHeader != 0x00004550) // "PE\0\0"
                return false;
            fs.Position = peOffset + 24;
            ushort magic = br.ReadUInt16();
            int dataDirectoryOffset = magic == 0x10b
                ? peOffset + 24 + 96
                : peOffset + 24 + 112;
            if (dataDirectoryOffset + 8 > fs.Length)
                return false;
            fs.Position = dataDirectoryOffset + 14 * 8;
            int clrRva = br.ReadInt32();
            return clrRva != 0;
        }
        catch
        {
            return false;
        }
    }
}
