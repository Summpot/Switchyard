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
    /// assemblies that do not reference any routed package.
    /// </summary>
    public static bool ReferencesAnyPackage(string assemblyPath, IReadOnlyCollection<string> packageNames)
    {
        if (packageNames.Count == 0)
            return false;

        var module = ReadModule(assemblyPath);
        foreach (var reference in module.AssemblyReferences)
        {
            var name = reference.Name?.Value;
            if (name is not null && packageNames.Contains(name))
                return true;
        }
        return false;
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
