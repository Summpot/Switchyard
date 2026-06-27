using Switchyard.Configuration;
using Switchyard.NuGet;
using Switchyard.Weaver;
using System.Runtime.InteropServices;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Serialized;

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
        IReadOnlyList<string>? nativeLibraryPaths = null,
        string? assemblyVersion = null)
    {
        DllPath = dllPath;
        PdbPath = pdbPath;
        PackageId = packageId;
        Version = version;
        IsRouted = isRouted;
        NativeLibraryPaths = nativeLibraryPaths ?? Array.Empty<string>();
        AssemblyVersion = assemblyVersion;
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

    /// <summary>
    /// The actual <c>AssemblyVersion</c> of the prepared assembly (read from
    /// the source DLL's metadata). May differ from <see cref="Version"/> (the
    /// NuGet package version): e.g. SkiaSharp 2.88.9 has
    /// <c>AssemblyVersion</c> 2.88.0.0. Used to write the correct
    /// <c>assemblyVersion</c> into <c>deps.json</c> so the CLR's TPA binder
    /// matches the routed assembly by <c>(Name, Version)</c>.
    /// </summary>
    public string? AssemblyVersion { get; }
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
        // routed managed name -> the routed assembly's ACTUAL AssemblyVersion
        // (read from the DLL metadata, may differ from the package version, e.g.
        // SkiaSharp 2.88.9 -> 2.88.0.0). Used to sync the caller's
        // AssemblyReference.Version so the CLR binds by (Name, Version).
        var routedNameToAssemblyVersion = new Dictionary<string, string>(StringComparer.Ordinal);
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

                // The routed assembly keeps its original AssemblyVersion (e.g.
                // SkiaSharp 2.88.9 -> AssemblyVersion 2.88.0.0). Capture it from
                // the source DLL so the deps.json patcher writes the version the
                // CLR will actually bind against, not the NuGet package version.
                var assemblyVersion = ReadAssemblyVersion(sourceDll);
                if (assemblyVersion is not null)
                    routedNameToAssemblyVersion[routedName] = assemblyVersion;

                // Discover and rename the native libraries for this routed
                // managed version, and rewrite its own DllImport entries to point
                // at the renamed native names so P/Invoke binds to the
                // version-specific native lib instead of a single shared one.
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
                    nativePaths,
                    assemblyVersion));
            }
        }

        // Decide whether the original (un-routed) package DLL must be kept. It
        // is the binding target for any NON-original-DLL caller that REFERENCES
        // the routed package but has no matching rule (or matches the original
        // version) — e.g. a transitive dependency like Avalonia.Skia that
        // references SkiaSharp but was never added to the route table.
        //
        // Callers that are themselves the original DLL of ANY routed package
        // are EXCLUDED from this analysis: those original assemblies are either
        // removed (no caller uses them) or routed, so they never load in their
        // original form and their references to other routed packages must not
        // keep those packages' originals alive (otherwise RouteGroup cascade
        // graphs could never strip originals).
        var routedPackageIds = new HashSet<string>(
            configurations.Select(c => c.PackageId),
            StringComparer.OrdinalIgnoreCase);
        foreach (var cfg in configurations)
        {
            bool anyUsesOriginal = false;
            foreach (var callerPath in callerPaths)
            {
                if (!File.Exists(callerPath) || !IsManagedAssembly(callerPath))
                    continue;

                // Skip callers that are the original DLL of any routed package —
                // they are removed or routed, never loaded as the original.
                if (routedPackageIds.Contains(
                        Path.GetFileNameWithoutExtension(callerPath)))
                    continue;

                var callerName = DetermineCallerName(callerPath);
                if (callerName is null)
                    continue;

                // Skip callers that don't reference this routed package at all —
                // they cannot bind to either the original or a routed version.
                if (!ReferenceRedirector.ReferencesAnyPackage(callerPath, new[] { cfg.PackageId }))
                    continue;

                var resolved = cfg.ResolveVersionForCaller(callerName);
                // resolved == null means "no rule matched, no wildcard" -> the
                // caller falls back to the original version. resolved ==
                // OriginalVersion also uses it.
                if (resolved is null || cfg.IsOriginalVersion(resolved))
                {
                    anyUsesOriginal = true;
                    break;
                }
            }

            if (!anyUsesOriginal)
            {
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

                    // Sync the routed target's internal references to the actual
                    // AssemblyVersion of the other group members (read from their
                    // DLL metadata) so the CLR binds them by (Name, Version).
                    var versionOverrides = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in redirections)
                    {
                        if (routedNameToAssemblyVersion.TryGetValue(kv.Value, out var asmVer)
                            && Version.TryParse(asmVer, out var actualVer))
                        {
                            versionOverrides[kv.Key] = actualVer;
                        }
                    }

                    var tempPath = Path.Combine(_intermediateDir, routedName + ".tmp.dll");
                    ReferenceRedirector.RedirectReferences(dllPath, redirections, tempPath, nativeRedirs, versionOverrides);
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
            // original package name -> routed assembly's actual AssemblyVersion,
            // so RedirectReferences syncs the caller's reference version to the
            // version the routed DLL carries (which may differ from the package
            // version the routed-name suffix encodes).
            var callerVersionOverrides = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
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

                // Prefer the routed assembly's actual AssemblyVersion (read from
                // the DLL metadata). Fall back to the version parsed from the
                // routed-name suffix (package version) when it was not captured.
                if (routedNameToAssemblyVersion.TryGetValue(routedName, out var asmVer)
                    && Version.TryParse(asmVer, out var actualVer))
                {
                    callerVersionOverrides[cfg.PackageId] = actualVer;
                }

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
            ReferenceRedirector.RedirectReferences(
                callerPath, redirections, outPath, callerNativeRedirs, callerVersionOverrides);

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
    /// Reads the <c>AssemblyVersion</c> of the assembly at
    /// <paramref name="assemblyPath"/> without executing any of its code, so
    /// the deps.json patcher can write the version the CLR will actually bind
    /// against (which may differ from the NuGet package version — e.g. SkiaSharp
    /// 2.88.9 has AssemblyVersion 2.88.0.0).
    /// </summary>
    private static string? ReadAssemblyVersion(string assemblyPath)
    {
        try
        {
            var module = ModuleDefinition.FromFile(assemblyPath, new ModuleReaderParameters
            {
                PEReaderParameters = new AsmResolver.PE.PEReaderParameters(),
            });
            return module.Assembly?.Version?.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Locates, renames and copies the native libraries for a routed managed
    /// version so each routed version binds its own native library. Also
    /// rewrites the routed managed assembly's own <c>DllImport</c> entries to
    /// point at the renamed native names. Records the original->routed
    /// native-name map for the given routed managed assembly and notes the
    /// original native paths that should be stripped from the copy-local output.
    /// </summary>
    /// <remarks>
    /// Native libraries are searched in two places, because real packages
    /// split the native dependency across a separate "native assets" package
    /// (e.g. <c>SkiaSharp</c> ships its <c>libskiasharp</c> via the transitive
    /// <c>SkiaSharp.NativeAssets.Win32</c> / <c>.Linux</c> / <c>.macOS</c>
    /// packages, not inside the <c>SkiaSharp</c> package itself):
    /// <list type="bullet">
    /// <item>The routed package's own <c>runtimes/{rid}/native/</c> (covers
    /// packages that bundle their native lib).</item>
    /// <item>The routed package's native-asset dependency packages, discovered
    /// by parsing the routed package's <c>.nuspec</c> and resolving each
    /// declared dependency at its declared version. A dependency is treated as a
    /// native-asset provider when it actually contains a
    /// <c>runtimes/{rid}/native/</c> folder.</item>
    /// </list>
    /// The .NET runtime does NOT add a <c>lib</c> prefix when resolving a
    /// DllImport, so the import name must already match the on-disk file basename
    /// (minus extension). The DllImport name is matched case-insensitively
    /// against the native file basenames (SkiaSharp declares
    /// <c>libskiasharp</c> but ships <c>libSkiaSharp.dll</c>; the Windows loader
    /// is case-insensitive, so the binding works either way). The renamed file
    /// preserves the on-disk casing so it still resolves on case-sensitive OSes;
    /// the DllImport is rewritten to the metadata name's routed form.
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

        // Only rewrite native libs actually referenced via DllImport by the
        // managed assembly, to avoid renaming unrelated native assets. Compared
        // case-insensitively: the DllImport name in metadata and the on-disk
        // native file name may differ in casing.
        var pinvokeNames = new HashSet<string>(
            ReferenceRedirector.GetPInvokeModuleNames(managedDllPath),
            StringComparer.OrdinalIgnoreCase);
        if (pinvokeNames.Count == 0)
            return nativePaths;

        // Collect every directory that might hold the routed version's native
        // libraries: the routed package's own runtimes folder, plus the native
        // runtimes folders of its native-asset dependency packages.
        var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nativeDirs = new List<string>();
        var ownNativeDir = Path.Combine(packageDir, "runtimes", rid, "native");
        if (Directory.Exists(ownNativeDir))
        {
            nativeDirs.Add(ownNativeDir);
            seenDirs.Add(ownNativeDir);
        }

        foreach (var depDir in ResolveNativeAssetDirs(packageDir, rid))
        {
            if (seenDirs.Add(depDir))
                nativeDirs.Add(depDir);
        }

        var nativeMap = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var nativeDir in nativeDirs)
        {
            if (!Directory.Exists(nativeDir))
                continue;

            foreach (var nativeFile in Directory.EnumerateFiles(nativeDir))
            {
                var fileName = Path.GetFileName(nativeFile);
                var ext = Path.GetExtension(fileName);
                string fileBase = Path.GetFileNameWithoutExtension(fileName);

                if (!pinvokeNames.Contains(fileBase))
                    continue;

                // Resolve the exact DllImport name the metadata uses (may differ
                // in casing from the on-disk file, e.g. "libskiasharp" vs
                // "libSkiaSharp"). The rewrite must target the metadata name so
                // RewritePInvokeModules can match the ModuleRef.
                string dllImportName = pinvokeNames.First(p =>
                    string.Equals(p, fileBase, StringComparison.OrdinalIgnoreCase));

                // Routed DllImport target name (metadata casing) and the matching
                // on-disk file name (on-disk casing, version-suffixed).
                string routedBase = dllImportName + ".Switchyard." + version;
                string routedFileName = fileBase + ".Switchyard." + version + ext;
                var outNative = Path.Combine(_intermediateDir, routedFileName);
                File.Copy(nativeFile, outNative, overwrite: true);
                nativePaths.Add(outNative);

                nativeMap[dllImportName] = routedBase;

                // Remember the original native path (as MSBuild would copy it
                // under runtimes/{rid}/native/) so the publish cleanup can strip
                // it. Match by filename so it works whether the native came from
                // the routed package itself or a native-asset dependency.
                var originalCopy = FindOriginalCopyLocalNative(callerPaths, fileName);
                if (originalCopy is not null)
                    removedOriginalNatives.Add(originalCopy);
            }
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
    /// Parses the <c>.nuspec</c> of the routed package at <paramref name="packageDir"/>
    /// and returns the on-disk <c>runtimes/{rid}/native/</c> directories of every
    /// declared dependency that actually ships a native runtime folder for the
    /// host RID. This is how Switchyard follows the full dependency chain to
    /// find native libraries that live in a separate "native assets" package
    /// (e.g. <c>SkiaSharp.NativeAssets.Win32</c> for <c>SkiaSharp</c>), instead of
    /// forcing the user to route the native-assets package explicitly.
    /// </summary>
    private IEnumerable<string> ResolveNativeAssetDirs(string packageDir, string rid)
    {
        string packageId = Path.GetFileName(packageDir); // global cache folder is the id
        string nuspecPath = Path.Combine(packageDir, packageId.ToLowerInvariant() + ".nuspec");
        if (!File.Exists(nuspecPath))
        {
            // Fallback: some layouts name the nuspec by the exact id casing.
            string? found = Directory.EnumerateFiles(packageDir, "*.nuspec").FirstOrDefault();
            if (found is null)
                yield break;
            nuspecPath = found;
        }

        IEnumerable<(string Id, string Version)> deps;
        try
        {
            deps = ReadNuspecDependencies(nuspecPath);
        }
        catch
        {
            yield break;
        }

        foreach (var (depId, depVersion) in deps)
        {
            if (string.IsNullOrWhiteSpace(depId) || string.IsNullOrWhiteSpace(depVersion))
                continue;

            string depDir;
            try
            {
                depDir = _resolver.EnsurePackageAvailableAsync(depId, depVersion).GetAwaiter().GetResult();
            }
            catch
            {
                // A transitive native-assets package that can't be resolved is
                // skipped rather than failing the whole build.
                continue;
            }

            var depNativeDir = Path.Combine(depDir, "runtimes", rid, "native");
            if (Directory.Exists(depNativeDir))
                yield return depNativeDir;
        }
    }

    /// <summary>
    /// Extracts every <c>&lt;dependency id=... version=.../&gt;</c> entry from a
    /// nuspec, across all dependency groups. Returns distinct (id, version)
    /// pairs.
    /// </summary>
    private static IEnumerable<(string Id, string Version)> ReadNuspecDependencies(string nuspecPath)
    {
        var doc = System.Xml.Linq.XDocument.Load(nuspecPath);
        // nuspec uses a default namespace; match dependency elements by local name.
        foreach (var dep in doc.Descendants().Where(e => e.Name.LocalName == "dependency"))
        {
            var id = dep.Attribute("id")?.Value;
            var ver = dep.Attribute("version")?.Value;
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(ver))
                yield return (id!, ver!);
        }
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

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "win-" + arch;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return "linux-" + arch;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
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
