using TargetLib;

namespace BoundaryBreaker;

public static class BoundaryBreaker
{
    /// <summary>
    /// Returns a <c>TargetLib.VersionReport</c> instance. Because
    /// BoundaryBreaker is routed to TargetLib 3.5.0, the returned object is
    /// <c>TargetLib.Switchyard.3.5.0.VersionReport</c>, which is a completely
    /// different type from the <c>TargetLib.Switchyard.1.0.0.VersionReport</c>
    /// that the caller (InvalidCastApp) expects.
    /// </summary>
    public static object GetVersionHolder()
    {
        return new VersionReport();
    }
}
