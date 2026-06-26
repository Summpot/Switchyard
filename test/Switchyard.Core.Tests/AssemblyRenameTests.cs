using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Switchyard.Weaver;
using Xunit;

namespace Switchyard.Core.Tests;

/// <summary>
/// Level 1 unit tests for <see cref="AssemblyWeaver.PrepareAndRename"/> and
/// <see cref="AssemblyWeaver.StripStrongNameInPlace"/>.
/// Verifies that the assembly name is rewritten, the strong-name signature is
/// fully stripped (public key + hash algorithm), and the output DLL is a valid
/// .NET assembly that can be re-read by AsmResolver.
/// </summary>
public class AssemblyRenameTests : IDisposable
{
    private readonly string _tempDir;

    public AssemblyRenameTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "switchyard-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    [Fact]
    public void PrepareAndRename_SetsAssemblyName_ToRoutedName()
    {
        string source = TestAssemblyFactory.CreateStrongNamedTargetLib(Path.Combine(_tempDir, "TargetLib.dll"));
        string outputPath = Path.Combine(_tempDir, "TargetLib.Switchyard.1.0.0.dll");

        AssemblyWeaver.PrepareAndRename(source, "TargetLib.Switchyard.1.0.0", outputPath);

        var module = ModuleDefinition.FromFile(outputPath);
        Assert.NotNull(module.Assembly);
        Assert.Equal("TargetLib.Switchyard.1.0.0", module.Assembly.Name);
    }

    [Fact]
    public void PrepareAndRename_StripsPublicKey_AndResetsHashAlgorithm()
    {
        string source = TestAssemblyFactory.CreateStrongNamedTargetLib(Path.Combine(_tempDir, "TargetLib.dll"));
        string outputPath = Path.Combine(_tempDir, "TargetLib.Switchyard.1.0.0.dll");

        AssemblyWeaver.PrepareAndRename(source, "TargetLib.Switchyard.1.0.0", outputPath);

        var module = ModuleDefinition.FromFile(outputPath);
        Assert.NotNull(module.Assembly);
        Assert.Null(module.Assembly.PublicKey);
        Assert.False(module.Assembly.HasPublicKey);
        Assert.Equal(AssemblyHashAlgorithm.None, module.Assembly.HashAlgorithm);
    }

    [Fact]
    public void PrepareAndRename_PreservesTypes_AndModuleIsReadable()
    {
        string source = TestAssemblyFactory.CreateStrongNamedTargetLib(Path.Combine(_tempDir, "TargetLib.dll"));
        string outputPath = Path.Combine(_tempDir, "TargetLib.Switchyard.1.0.0.dll");

        AssemblyWeaver.PrepareAndRename(source, "TargetLib.Switchyard.1.0.0", outputPath);

        var module = ModuleDefinition.FromFile(outputPath);
        var type = module.TopLevelTypes.FirstOrDefault(t => t.Name == "VersionReport");
        Assert.NotNull(type);
        Assert.Equal("TargetLib", type.Namespace);
    }

    [Fact]
    public void StripStrongNameInPlace_RemovesPublicKey_KeepsAssemblyName()
    {
        string source = TestAssemblyFactory.CreateStrongNamedTargetLib(Path.Combine(_tempDir, "TargetLib.dll"));

        AssemblyWeaver.StripStrongNameInPlace(source);

        var module = ModuleDefinition.FromFile(source);
        Assert.NotNull(module.Assembly);
        Assert.Equal("TargetLib", module.Assembly.Name);
        Assert.Null(module.Assembly.PublicKey);
        Assert.False(module.Assembly.HasPublicKey);
        Assert.Equal(AssemblyHashAlgorithm.None, module.Assembly.HashAlgorithm);
    }

    [Fact]
    public void PrepareAndRename_OnAssemblyWithoutManifest_Throws()
    {
        // A netmodule has no assembly manifest.
        var module = new ModuleDefinition("Module.netmodule", KnownCorLibs.SystemRuntime_v8_0_0_0);
        string path = Path.Combine(_tempDir, "Module.netmodule");
        module.Write(path);

        Assert.Throws<InvalidOperationException>(() =>
            AssemblyWeaver.PrepareAndRename(path, "Renamed", Path.Combine(_tempDir, "Renamed.dll")));
    }
}
