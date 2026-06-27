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
    public static void RedirectReferences(
        string assemblyPath,
        IReadOnlyDictionary<string, string> redirections,
        string outputPath,
        IReadOnlyDictionary<string, string>? nativeRedirections = null)
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
            // The routed assembly carries the version of the package it was
            // built from (e.g. TargetLib.Switchyard.1.0.0 has assembly version
            // 1.0.0.0), not the original version the caller referenced. The
            // CLR binds on (Name, Version, PublicKey), so the reference version
            // must be updated to match — otherwise the loader fails with a
            // FileNotFoundException for the old version number.
            var routedVersion = ExtractRoutedVersion(routedName);
            if (routedVersion is not null)
                reference.Version = routedVersion;
            reference.PublicKeyOrToken = null;
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
    /// <c>ModuleRef</c> rows backing the module's P/Invoke entries. Used to
    /// detect which native libraries a managed assembly binds to.
    /// </summary>
    public static IReadOnlyCollection<string> GetPInvokeModuleNames(string assemblyPath)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (!IsManagedAssembly(assemblyPath))
            return result;

        try
        {
            var module = ReadModule(assemblyPath);
            foreach (var moduleRef in module.ModuleReferences)
            {
                var name = moduleRef.Name?.Value;
                if (name is not null)
                    result.Add(name);
            }
        }
        catch (BadImageFormatException)
        {
        }
        return result;
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
        int major = v.Major;
        int minor = v.Minor < 0 ? 0 : v.Minor;
        int build = v.Build < 0 ? 0 : v.Build;
        int revision = v.Revision < 0 ? 0 : v.Revision;
        return new Version(major, minor, build, revision);
    }

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
