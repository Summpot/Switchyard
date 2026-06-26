using AsmResolver.DotNet;
using AsmResolver.DotNet.Serialized;
using AsmResolver.PE.Debug;
using Switchyard.TestFixtures;
using Switchyard.Weaver;
using Xunit;

namespace Switchyard.Core.Tests;

/// <summary>
/// Level 1 unit tests for PDB (debug symbol) handling during assembly
/// rewriting. Uses the compiled <see cref="Switchyard.TestFixtures"/>
/// assembly which ships with a portable PDB.
/// </summary>
public class PdbAlignmentTests : IDisposable
{
    private readonly string _tempDir;

    public PdbAlignmentTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "switchyard-pdb-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    /// <summary>
    /// Returns the path to the compiled TestFixtures DLL. Because the test
    /// project has a ProjectReference to TestFixtures, the DLL and its PDB
    /// are copied into the test output directory.
    /// </summary>
    private static (string DllPath, string PdbPath) LocateFixtureAssembly()
    {
        string dllPath = typeof(PdbFixtureMarker).Assembly.Location;
        string pdbPath = Path.ChangeExtension(dllPath, ".pdb");
        Assert.True(File.Exists(dllPath), $"Fixture DLL not found at {dllPath}");
        Assert.True(File.Exists(pdbPath), $"Fixture PDB not found at {pdbPath}");
        return (dllPath, pdbPath);
    }

    [Fact]
    public void PrepareAndRename_CopiesPdb_ToOutputDirectory()
    {
        var (dllPath, _) = LocateFixtureAssembly();
        string outputPath = Path.Combine(_tempDir, "Fixture.Switchyard.1.0.0.dll");
        string expectedPdb = Path.ChangeExtension(outputPath, ".pdb");

        AssemblyWeaver.PrepareAndRename(dllPath, "Fixture.Switchyard.1.0.0", outputPath);

        Assert.True(File.Exists(outputPath), "Rewritten DLL should exist");
        Assert.True(File.Exists(expectedPdb), "PDB should be copied alongside the rewritten DLL");
    }

    [Fact]
    public void PrepareAndRename_UpdatesCodeViewPdbPath_InDebugData()
    {
        var (dllPath, _) = LocateFixtureAssembly();
        string outputPath = Path.Combine(_tempDir, "Fixture.Switchyard.1.0.0.dll");
        string expectedPdbName = "Fixture.Switchyard.1.0.0.pdb";

        AssemblyWeaver.PrepareAndRename(dllPath, "Fixture.Switchyard.1.0.0", outputPath);

        var readerParameters = new ModuleReaderParameters
        {
            PEReaderParameters = new AsmResolver.PE.PEReaderParameters()
        };
        var module = ModuleDefinition.FromFile(outputPath, readerParameters);

        bool foundRsds = false;
        foreach (var entry in module.DebugData)
        {
            if (entry.Contents is RsdsDataSegment rsds)
            {
                foundRsds = true;
                Assert.Equal(expectedPdbName, rsds.Path);
            }
        }
        Assert.True(foundRsds, "The rewritten DLL should contain a CodeView (RSDS) debug data entry pointing at the new PDB filename");
    }

    [Fact]
    public void PrepareAndRename_PreservesMvid_AcrossRewrite()
    {
        var (dllPath, _) = LocateFixtureAssembly();
        string outputPath = Path.Combine(_tempDir, "Fixture.Switchyard.1.0.0.dll");

        var readerParameters = new ModuleReaderParameters
        {
            PEReaderParameters = new AsmResolver.PE.PEReaderParameters()
        };
        var originalModule = ModuleDefinition.FromFile(dllPath, readerParameters);
        Guid originalMvid = originalModule.Mvid;

        AssemblyWeaver.PrepareAndRename(dllPath, "Fixture.Switchyard.1.0.0", outputPath);

        var rewrittenModule = ModuleDefinition.FromFile(outputPath, readerParameters);
        Guid rewrittenMvid = rewrittenModule.Mvid;

        Assert.Equal(originalMvid, rewrittenMvid);
    }

    [Fact]
    public void PrepareAndRename_OnAssemblyWithoutPdb_StillProducesValidDll()
    {
        // Generate an assembly with no PDB — the weaver should handle this gracefully.
        string source = TestAssemblyFactory.CreateStrongNamedTargetLib(Path.Combine(_tempDir, "NoPdbLib.dll"));
        string outputPath = Path.Combine(_tempDir, "NoPdbLib.Switchyard.1.0.0.dll");
        string expectedPdb = Path.ChangeExtension(outputPath, ".pdb");

        AssemblyWeaver.PrepareAndRename(source, "NoPdbLib.Switchyard.1.0.0", outputPath);

        Assert.True(File.Exists(outputPath));
        Assert.False(File.Exists(expectedPdb));

        // The output should still be a valid .NET assembly.
        var module = ModuleDefinition.FromFile(outputPath);
        Assert.Equal("NoPdbLib.Switchyard.1.0.0", module.Assembly?.Name);
    }
}
