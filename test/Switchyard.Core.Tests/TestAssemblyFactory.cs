using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Switchyard.Core.Tests;

/// <summary>
/// Generates small in-memory .NET assemblies using AsmResolver so the unit
/// tests can exercise the weaver without depending on external NuGet packages
/// or the C# compiler. Every method writes the generated assembly to a temp
/// directory and returns the file path.
/// </summary>
internal static class TestAssemblyFactory
{
    /// <summary>
    /// A dummy 128-byte public key used to strong-name test assemblies.
    /// The actual cryptographic validity is irrelevant — the weaver only
    /// needs to observe that the key is present before stripping and absent
    /// afterwards.
    /// </summary>
    public static readonly byte[] DummyPublicKey = GenerateDummyKey();

    /// <summary>
    /// Creates a strong-named <c>TargetLib.dll</c> that contains a single
    /// public type <c>TargetLib.VersionReport</c> with a static method.
    /// </summary>
    public static string CreateStrongNamedTargetLib(string outputPath)
    {
        var assembly = new AssemblyDefinition("TargetLib", new Version(1, 0, 0, 0))
        {
            PublicKey = DummyPublicKey,
            HasPublicKey = true,
            HashAlgorithm = AssemblyHashAlgorithm.Sha1
        };

        var module = new ModuleDefinition("TargetLib.dll", KnownCorLibs.SystemRuntime_v8_0_0_0);
        assembly.Modules.Add(module);

        var type = new TypeDefinition(
            "TargetLib",
            "VersionReport",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed,
            module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(type);

        assembly.Write(outputPath);
        return outputPath;
    }

    /// <summary>
    /// Creates a <c>MainApp.dll</c> that holds an <see cref="AssemblyReference"/>
    /// to <c>TargetLib</c> (with a public key token). A dummy field that
    /// references a type in the target assembly is added so that AsmResolver
    /// does not prune the reference during serialization.
    /// </summary>
    public static string CreateCallerReferencingTargetLib(string outputPath, string targetLibName = "TargetLib")
    {
        var assembly = new AssemblyDefinition("MainApp", new Version(1, 0, 0, 0));
        var module = new ModuleDefinition("MainApp.dll", KnownCorLibs.SystemRuntime_v8_0_0_0);
        assembly.Modules.Add(module);

        var targetLibRef = new AssemblyReference(targetLibName, new Version(1, 0, 0, 0));
        targetLibRef.PublicKeyOrToken = ComputeToken(DummyPublicKey);
        module.AssemblyReferences.Add(targetLibRef);

        // Force the reference to be serialized by creating a type with a field
        // that references a type from the target assembly. AsmResolver only
        // serializes assembly references that are actually used.
        var targetTypeRef = new TypeReference(targetLibRef, "TargetLib", "VersionReport");
        var callerType = new TypeDefinition(
            null, "CallerType",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed,
            module.CorLibTypeFactory.Object.Type);
        callerType.Fields.Add(new FieldDefinition(
            "TargetField",
            FieldAttributes.Private | FieldAttributes.Static,
            targetTypeRef.ToTypeSignature(false)));
        module.TopLevelTypes.Add(callerType);

        assembly.Write(outputPath);
        return outputPath;
    }

    /// <summary>
    /// Creates a <c>RouteGroupTarget.dll</c> that references both
    /// <c>TargetLib</c> and <c>CommonUtils</c>, simulating a package that
    /// participates in a route group and internally depends on two other
    /// routed packages. Each reference is anchored by a field so AsmResolver
    /// does not prune unused references.
    /// </summary>
    public static string CreateAssemblyWithMultipleReferences(
        string outputPath,
        string assemblyName,
        params (string Name, Version Version)[] references)
    {
        var assembly = new AssemblyDefinition(assemblyName, new Version(1, 0, 0, 0));
        var module = new ModuleDefinition(assemblyName + ".dll", KnownCorLibs.SystemRuntime_v8_0_0_0);
        assembly.Modules.Add(module);

        var anchorType = new TypeDefinition(
            null, assemblyName + ".Anchor",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed,
            module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(anchorType);

        foreach (var (name, version) in references)
        {
            var libRef = new AssemblyReference(name, version);
            module.AssemblyReferences.Add(libRef);

            var typeRef = new TypeReference(libRef, name, "Placeholder");
            anchorType.Fields.Add(new FieldDefinition(
                name + "Field",
                FieldAttributes.Private | FieldAttributes.Static,
                typeRef.ToTypeSignature(false)));
        }

        assembly.Write(outputPath);
        return outputPath;
    }

    /// <summary>
    /// Computes the 8-byte public key token from a full public key using
    /// SHA-1 (same algorithm the CLR uses).
    /// </summary>
    public static byte[] ComputeToken(byte[] publicKey)
    {
        using var sha1 = System.Security.Cryptography.SHA1.Create();
        var hash = sha1.ComputeHash(publicKey);
        var token = new byte[8];
        for (int i = 0; i < 8; i++)
            token[i] = hash[hash.Length - 1 - i];
        return token;
    }

    private static byte[] GenerateDummyKey()
    {
        var key = new byte[128];
        for (int i = 0; i < key.Length; i++)
            key[i] = (byte)(i * 7 + 13);
        return key;
    }
}
