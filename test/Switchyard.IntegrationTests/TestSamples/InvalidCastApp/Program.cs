using TargetLib;

// MainApp is routed to TargetLib 1.0.0. BoundaryBreaker is routed to 3.5.0.
// BoundaryBreaker returns a TargetLib.VersionReport instance, but from the
// 3.5.0 assembly. MainApp tries to cast it to its own 1.0.0 VersionReport —
// this MUST fail with InvalidCastException because the two are distinct types
// in the eyes of the CLR.
try
{
    var holder = BoundaryBreaker.BoundaryBreaker.GetVersionHolder();
    var report = (VersionReport)holder;
    Console.WriteLine("[INVALID_CAST_APP] Unexpected success: " + VersionReport.GetVersion());
    Environment.Exit(1);
}
catch (InvalidCastException ex)
{
    Console.WriteLine("[INVALID_CAST_APP] InvalidCastException caught as expected: " + ex.Message);
    Console.WriteLine("[INVALID_CAST_APP] Type boundary successfully torn.");
    Environment.Exit(0);
}
catch (Exception ex)
{
    Console.WriteLine("[INVALID_CAST_APP] Unexpected exception type: " + ex.GetType().FullName + " — " + ex.Message);
    Environment.Exit(2);
}
