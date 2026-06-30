using AsmResolver.DotNet;
using AsmResolver.DotNet.Serialized;
using AsmResolver.PE.Debug;

namespace Switchyard.Weaver;

/// <summary>
/// Rewrites the <see cref="AssemblyReference"/> table of a caller assembly so
/// that references to routed packages point at the renamed
/// <c>{Package}.Switchyard.{Version}</c> assemblies instead of the original
/// identity. Only metadata table entries are modified; AsmResolver transparently
/// updates every referencing <c>TypeRef</c> token.
/// </summary>
/// <remarks>
/// When a routed package carries a native dependency, the caller's
/// <c>DllImport</c> entries that target the package's native library are also
/// repointed at the routed native library name, so each routed managed version
/// binds its own native library and native-lib conflicts are avoided.
/// </remarks>
/// <remarks>
/// Runtime consequence: once renamed, two routed versions of a package are
/// distinct type systems to the CLR. Passing a typed object from a routed
/// package across a route boundary throws <c>InvalidCastException</c>.
/// Cross-boundary signatures must therefore use BCL primitives or a shared,
/// non-routed contract assembly resolved via DI. This is an architecture
/// contract the weaver cannot enforce at weave time.
/// </remarks>
public static class ReferenceRedirector
{
    /// <summary>
    /// Reads <paramref name="assemblyPath"/>, applies every redirection in
    /// <paramref name="redirections"/> (original name -> routed name), and
    /// writes the modified assembly to <paramref name="outputPath"/>. The
    /// matching <c>.pdb</c> is copied alongside so debugging keeps working.
    /// </summary>
    /// <param name="nativeRedirections">
    /// Optional map of native library names to rewrite in
    /// <c>DllImport</c>/<c>ImplMap</c> entries (original native name ->
    /// routed native name). May be <c>null</c> or empty for packages without
    /// a native dependency.
    /// </param>
    /// <param name="assemblyVersionOverrides">
    /// Optional map of original package name -> the routed assembly's ACTUAL
    /// <c>AssemblyVersion</c> (read from the DLL metadata). When present, the
    /// caller's <c>AssemblyReference.Version</c> is synced to this value instead
    /// of the version parsed from the routed-name suffix, because the routed
    /// DLL carries its own <c>AssemblyVersion</c> which may differ from the
    /// NuGet package version the routed name encodes (e.g. SkiaSharp 2.88.9 has
    /// <c>AssemblyVersion</c> 2.88.0.0). The CLR binds on
    /// <c>(Name, Version, PublicKey)</c>, so this sync is mandatory.
    /// </param>
    /// <param name="routedPublicKeyToken">
    /// Optional 8-byte public key token to write into every redirected
    /// <c>AssemblyReference</c>. Used only when the routed assemblies were
    /// re-signed with a user-provided <see cref="StrongNameKey"/> (opt-in
    /// strong-name re-signing). When <c>null</c>, the redirected reference's
    /// <c>PublicKeyOrToken</c> is cleared (the default behaviour, matching
    /// stripped strong names). When non-null, the caller binds against the
    /// routed assembly's new strong-name identity by
    /// <c>(Name, Version, PublicKeyToken)</c>.
    /// </param>
    public static void RedirectReferences(
        string assemblyPath,
        IReadOnlyDictionary<string, string> redirections,
        string outputPath,
        IReadOnlyDictionary<string, string>? nativeRedirections = null,
        IReadOnlyDictionary<string, Version>? assemblyVersionOverrides = null,
        byte[]? routedPublicKeyToken = null)
    {
        if (redirections.Count == 0 && (nativeRedirections is null || nativeRedirections.Count == 0))
            return;

        var module = ReadModule(assemblyPath);

        var changed = false;
        foreach (var reference in module.AssemblyReferences)
        {
            if (reference.Name is null)
                continue;

            var name = reference.Name.Value ?? string.Empty;
            if (!redirections.TryGetValue(name, out var routedName))
                continue;

            reference.Name = routedName;
            // Sync the reference version to the routed assembly's ACTUAL
            // AssemblyVersion when known (read from the DLL metadata). Fall back
            // to the version parsed from the routed-name suffix (the NuGet
            // package version) when the actual version was not captured. The CLR
            // binds on (Name, Version, PublicKey); leaving the old version makes
            // the loader fail with a FileNotFoundException for the old version
            // number. The version is padded to four components so AsmResolver
            // serialises concrete 0s rather than the 0xFFFF "unspecified"
            // sentinel (which the CLR reads back as 65535).
            Version? routedVersion = null;
            if (assemblyVersionOverrides is not null
                && assemblyVersionOverrides.TryGetValue(name, out var actualVer))
            {
                routedVersion = PadToFourComponents(actualVer);
            }
            routedVersion ??= ExtractRoutedVersion(routedName);
            if (routedVersion is not null)
                reference.Version = routedVersion;
            // When the routed assemblies were re-signed with a user-provided key,
            // stamp the new public key token onto the redirected reference so the
            // CLR binds by (Name, Version, PublicKeyToken). Otherwise clear the
            // token to match the default stripped-strong-name behaviour.
            reference.PublicKeyOrToken = routedPublicKeyToken;
            reference.HasPublicKey = false;
            changed = true;
        }

        if (nativeRedirections is not null && nativeRedirections.Count > 0)
        {
            if (RewritePInvokeModules(module, nativeRedirections))
                changed = true;
        }

        if (!changed)
            return;

        UpdateCodeViewPdbPath(module, Path.ChangeExtension(outputPath, ".pdb"));

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        module.Write(outputPath);

        CopyPdb(assemblyPath, Path.ChangeExtension(outputPath, ".pdb"));
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="assemblyPath"/> contains at
    /// least one <see cref="AssemblyReference"/> whose name appears in
    /// <paramref name="packageNames"/>. Used to short-circuit work for
    /// assemblies that do not reference any routed package. Non-.NET files
    /// (e.g. native DLLs) are silently skipped.
    /// </summary>
    public static bool ReferencesAnyPackage(string assemblyPath, IReadOnlyCollection<string> packageNames)
    {
        if (packageNames.Count == 0)
            return false;

        if (!IsManagedAssembly(assemblyPath))
            return false;

        try
        {
            var module = ReadModule(assemblyPath);
            foreach (var reference in module.AssemblyReferences)
            {
                var name = reference.Name?.Value;
                if (name is not null && packageNames.Contains(name))
                    return true;
            }
            return false;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    private static ModuleDefinition ReadModule(string sourcePath)
    {
        var readerParameters = new ModuleReaderParameters
        {
            PEReaderParameters = new AsmResolver.PE.PEReaderParameters()
        };
        return ModuleDefinition.FromFile(sourcePath, readerParameters);
    }

    /// <summary>
    /// Rewrites the <c>ModuleRef</c> names referenced by every
    /// <c>ImplMap</c> (P/Invoke / <c>DllImport</c>) entry in
    /// <paramref name="module"/> according to <paramref name="nativeRedirections"/>
    /// (original native lib name -> routed native lib name). Returns
    /// <c>true</c> when at least one entry was rewritten.
    /// </summary>
    /// <remarks>
    /// P/Invoke resolution is by the module name stored in the
    /// <c>ModuleRef</c> row, not by assembly identity. Two routed managed
    /// versions of a package that both <c>DllImport</c> the same native name
    /// would otherwise share a single native library loaded by the OS. Naming
    /// the native library per routed version (and shipping a renamed copy of
    /// the native file) gives each routed version its own native binding.
    /// </remarks>
    private static bool RewritePInvokeModules(
        ModuleDefinition module,
        IReadOnlyDictionary<string, string> nativeRedirections)
    {
        var changed = false;
        foreach (var moduleRef in module.ModuleReferences)
        {
            if (moduleRef.Name is null)
                continue;

            var name = moduleRef.Name.Value ?? string.Empty;
            if (!nativeRedirections.TryGetValue(name, out var routedName))
                continue;

            moduleRef.Name = routedName;
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Returns the set of native library names referenced by the
    /// <c>ModuleRef</c> rows that back the module's P/Invoke
    /// (<c>DllImport</c>) entries. Used to detect which native libraries a
    /// managed assembly actually binds to via P/Invoke — NOT every
    /// <c>ModuleRef</c> in the module, since a <c>ModuleRef</c> can also
    /// describe a managed netmodule or an unmanaged export scope that is never
    /// the target of a <c>DllImport</c>. Only the modules referenced by an
    /// <c>ImplementationMap</c> (<c>ImplMap</c>) row are returned, so the
    /// caller never renames a native file whose basename coincidentally
    /// collides with an unrelated <c>ModuleRef</c>.
    /// </summary>
    public static IReadOnlyCollection<string> GetPInvokeModuleNames(string assemblyPath)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (!IsManagedAssembly(assemblyPath))
            return result;

        try
        {
            var module = ReadModule(assemblyPath);
            // Walk every method's ImplementationMap (the ImplMap row that
            // backs a DllImport) and collect the Scope (ModuleRef) name. This
            // is the only reliable way to know which ModuleRef rows are
            // actually P/Invoke targets — iterating module.ModuleReferences
            // directly would also include module refs that are not DllImport
            // scopes, and a coincidental basename collision could cause
            // unrelated native files to be renamed and have their DllImports
            // rewritten, corrupting the build.
            foreach (var type in module.GetAllTypes())
            {
                foreach (var method in type.Methods)
                {
                    var moduleName = method.ImplementationMap?.Scope?.Name?.Value?.ToString();
                    if (!string.IsNullOrWhiteSpace(moduleName))
                        result.Add(moduleName!);
                }
            }
        }
        catch (BadImageFormatException)
        {
        }
        return result;
    }

    /// <summary>
    /// Returns the unmanaged entry point names imported from each
    /// <c>DllImport</c> module. NativeAOT direct P/Invoke turns these imports
    /// into native linker symbols, so Switchyard uses this to avoid enabling
    /// direct P/Invoke automatically when multiple routed native modules export
    /// the same entry point name.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyCollection<string>> GetPInvokeEntryPointNamesByModule(string assemblyPath)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (!IsManagedAssembly(assemblyPath))
            return result.ToDictionary(k => k.Key, v => (IReadOnlyCollection<string>)v.Value, StringComparer.Ordinal);

        try
        {
            var module = ReadModule(assemblyPath);
            foreach (var type in module.GetAllTypes())
            {
                foreach (var method in type.Methods)
                {
                    var map = method.ImplementationMap;
                    var rawModuleName = map?.Scope?.Name?.Value?.ToString();
                    var rawEntryPoint = map?.Name?.Value?.ToString();
                    if (string.IsNullOrWhiteSpace(rawModuleName) || string.IsNullOrWhiteSpace(rawEntryPoint))
                        continue;

                    var moduleName = rawModuleName!;
                    var entryPoint = rawEntryPoint!;

                    if (!result.TryGetValue(moduleName, out var entryPoints))
                    {
                        entryPoints = new HashSet<string>(StringComparer.Ordinal);
                        result[moduleName] = entryPoints;
                    }
                    entryPoints.Add(entryPoint);
                }
            }
        }
        catch (BadImageFormatException)
        {
        }

        return result.ToDictionary(k => k.Key, v => (IReadOnlyCollection<string>)v.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// Rewrites P/Invoke entry point names for imports from a specific module.
    /// Used by NativeAOT direct P/Invoke symbol-prefix mode: the native export
    /// symbols are prefixed first, then the managed <c>ImplMap.Name</c> values
    /// are updated to the matching prefixed names.
    /// </summary>
    public static void RewritePInvokeEntryPoints(
        string assemblyPath,
        string moduleName,
        IReadOnlyDictionary<string, string> entryPointRedirections)
    {
        if (entryPointRedirections.Count == 0)
            return;

        var module = ReadModule(assemblyPath);
        var changed = false;
        foreach (var type in module.GetAllTypes())
        {
            foreach (var method in type.Methods)
            {
                var map = method.ImplementationMap;
                var mapModuleName = map?.Scope?.Name?.Value?.ToString();
                var rawEntryPoint = map?.Name?.Value?.ToString();
                if (map is null
                    || !string.Equals(mapModuleName, moduleName, StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(rawEntryPoint))
                {
                    continue;
                }

                var entryPoint = rawEntryPoint!;
                if (!entryPointRedirections.TryGetValue(entryPoint, out var routedEntryPoint))
                {
                    continue;
                }

                map.Name = routedEntryPoint;
                changed = true;
            }
        }

        if (!changed)
            return;

        module.Write(assemblyPath);
    }

    /// <summary>
    /// Extracts the version component embedded in a routed assembly name of
    /// the form <c>{Package}.Switchyard.{version}</c> (e.g.
    /// <c>TargetLib.Switchyard.1.0.0</c> → <c>1.0.0</c>). Returns <c>null</c>
    /// when the name does not follow the routed-name convention.
    /// </summary>
    /// <remarks>
    /// The version is normalised to a full four-component
    /// <see cref="Version"/> (missing fields padded with 0) so that AsmResolver
    /// serialises concrete zeroes rather than the 0xFFFF "unspecified" sentinel
    /// that the CLR would otherwise read back as 65535 and fail to bind against
    /// the routed assembly's actual <c>AssemblyVersion</c>.
    /// </remarks>
    private static Version? ExtractRoutedVersion(string routedName)
    {
        const string suffix = ".Switchyard.";
        int idx = routedName.LastIndexOf(suffix, StringComparison.Ordinal);
        if (idx < 0)
            return null;
        string verStr = routedName.Substring(idx + suffix.Length);
        if (!Version.TryParse(verStr, out var v))
            return null;
        return PadToFourComponents(v);
    }

    /// <summary>
    /// Pads a <see cref="Version"/> to a full four-component version (missing
    /// fields become 0) so AsmResolver serialises concrete zeroes rather than
    /// the 0xFFFF "unspecified" sentinel that the CLR would otherwise read
    /// back as 65535 and fail to bind against.
    /// </summary>
    private static Version PadToFourComponents(Version v)
        => new(v.Major, v.Minor < 0 ? 0 : v.Minor, v.Build < 0 ? 0 : v.Build, v.Revision < 0 ? 0 : v.Revision);

    /// <summary>
    /// Returns <c>true</c> when the supplied file is a .NET PE that AsmResolver
    /// can load. Used to filter out native DLLs from the caller list.
    /// </summary>
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
            // Skip to the optional header to read the magic (PE32 vs PE32+)
            fs.Position = peOffset + 24;
            ushort magic = br.ReadUInt16();
            int dataDirectoryOffset = magic == 0x10b
                ? peOffset + 24 + 96
                : peOffset + 24 + 112;
            if (dataDirectoryOffset + 8 > fs.Length)
                return false;
            fs.Position = dataDirectoryOffset + 14 * 8; // 15th data directory = CLR header
            int clrRva = br.ReadInt32();
            return clrRva != 0;
        }
        catch
        {
            return false;
        }
    }

    private static void UpdateCodeViewPdbPath(ModuleDefinition module, string newPdbPath)
    {
        var fileName = Path.GetFileName(newPdbPath);
        foreach (var entry in module.DebugData)
        {
            if (entry.Contents is RsdsDataSegment rsds)
            {
                rsds.Path = fileName;
            }
        }
    }

    private static void CopyPdb(string sourceDllPath, string targetPdbPath)
    {
        var sourcePdbPath = Path.ChangeExtension(sourceDllPath, ".pdb");
        if (!File.Exists(sourcePdbPath))
            return;

        var dir = Path.GetDirectoryName(targetPdbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.Copy(sourcePdbPath, targetPdbPath, overwrite: true);
    }
}
