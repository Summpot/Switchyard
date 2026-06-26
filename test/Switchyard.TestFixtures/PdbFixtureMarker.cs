namespace Switchyard.TestFixtures;

/// <summary>
/// Marker type whose sole purpose is to produce a compiled DLL with a
/// portable PDB so that <c>AssemblyWeaver</c> PDB-handling logic can be
/// exercised by the unit tests. The assembly location of this type is used
/// to locate the physical DLL + PDB pair on disk.
/// </summary>
public static class PdbFixtureMarker
{
    /// <summary>
    /// Returns the current assembly's version string. Used to prove the
    /// fixture assembly is loadable after weaving.
    /// </summary>
    public static string GetAssemblyVersion()
        => typeof(PdbFixtureMarker).Assembly.GetName().Version?.ToString() ?? "unknown";

    /// <summary>
    /// A trivial method with a local variable so the PDB contains at least
    /// one sequence point.
    /// </summary>
    public static int ComputeAnswer()
    {
        int x = 21;
        int y = x * 2;
        return y;
    }
}
