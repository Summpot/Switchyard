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
public static class ReferenceRedirector
{
    /// <summary>
    /// Reads <paramref name="assemblyPath"/>, applies every redirection in
    /// <paramref name="redirections"/> (original name -> routed name), and
    /// writes the modified assembly to <paramref name="outputPath"/>. The
    /// matching <c>.pdb</c> is copied alongside so debugging keeps working.
    /// </summary>
    public static void RedirectReferences(
        string assemblyPath,
        IReadOnlyDictionary<string, string> redirections,
        string outputPath)
    {
        if (redirections.Count == 0)
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
