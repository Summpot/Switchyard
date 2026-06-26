using AsmResolver.DotNet;
using AsmResolver.DotNet.Serialized;
using AsmResolver.PE.Debug;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Switchyard.Weaver;

/// <summary>
/// Performs the metadata-level rewriting of a target assembly:
/// <list type="bullet">
/// <item>Renames the <see cref="AssemblyDefinition.Name"/> to the routed name.</item>
/// <item>Strips the strong-name signature (public key + hash algorithm).</item>
/// <item>Updates the CodeView debug data path so the rewritten DLL keeps
/// referencing its sibling <c>.pdb</c> file.</item>
/// <item>Copies the original <c>.pdb</c> alongside the rewritten DLL so the
/// MVID-based binding stays intact.</item>
/// </list>
/// Only metadata tables are touched; method bodies are left untouched, which
/// allows AsmResolver to transparently fix up every downstream type/member
/// token reference.
/// </summary>
public static class AssemblyWeaver
{
    /// <summary>
    /// Reads <paramref name="sourcePath"/>, renames the assembly to
    /// <paramref name="routedName"/>, strips its strong name, rewrites the
    /// DLL to <paramref name="outputPath"/> and copies the matching
    /// <c>.pdb</c> next to it.
    /// </summary>
    /// <remarks>
    /// Only metadata tables are touched — method bodies are left untouched,
    /// which lets AsmResolver transparently fix up every downstream
    /// type/member token reference. The strong-name signature is fully
    /// removed (<c>PublicKey</c> = null, <c>HashAlgorithm</c> = None), so the
    /// renamed assembly loses its original strong-name identity; environments
    /// that require strong-name verification or GAC deployment must re-sign
    /// the output with a custom key after weaving.
    /// </remarks>
    public static void PrepareAndRename(string sourcePath, string routedName, string outputPath)
    {
        var module = ReadModule(sourcePath);
        if (module.Assembly is null)
            throw new InvalidOperationException($"'{sourcePath}' does not contain an assembly manifest.");

        module.Assembly.Name = routedName;
        module.Assembly.PublicKey = null;
        module.Assembly.HasPublicKey = false;
        module.Assembly.HashAlgorithm = AssemblyHashAlgorithm.None;

        UpdateCodeViewPdbPath(module, Path.ChangeExtension(outputPath, ".pdb"));

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        module.Write(outputPath);

        CopyPdb(sourcePath, Path.ChangeExtension(outputPath, ".pdb"));
    }

    /// <summary>
    /// Reads <paramref name="sourcePath"/>, strips its strong name and rewrites
    /// the DLL in place. Used for the original version of a routed package when
    /// only the strong name needs to be removed (e.g. when it participates in a
    /// route group but keeps its identity).
    /// </summary>
    public static void StripStrongNameInPlace(string assemblyPath)
    {
        var module = ReadModule(assemblyPath);
        if (module.Assembly is null)
            return;

        module.Assembly.PublicKey = null;
        module.Assembly.HasPublicKey = false;
        module.Assembly.HashAlgorithm = AssemblyHashAlgorithm.None;

        module.Write(assemblyPath);
    }

    private static ModuleDefinition ReadModule(string sourcePath)
    {
        var readerParameters = new ModuleReaderParameters
        {
            PEReaderParameters = new AsmResolver.PE.PEReaderParameters()
        };
        return ModuleDefinition.FromFile(sourcePath, readerParameters);
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
