namespace TargetLib;

/// <summary>
/// Reports the assembly version of the TargetLib that was loaded at runtime.
/// Because the assembly version is set at pack time, different NuGet package
/// versions produce DLLs that report different version strings.
/// </summary>
/// <remarks>
/// This class is intentionally non-static (while keeping a static
/// <see cref="GetVersion"/> for convenience) so that a caller routed to a
/// different version can <c>new VersionReport()</c> across the route boundary
/// and observe the <c>InvalidCastException</c> the CLR throws for two
/// unrelated type systems. A static-only class would not allow that instance
/// to cross the boundary.
/// </remarks>
public class VersionReport
{
    public static string GetVersion()
    {
        return typeof(VersionReport).Assembly.GetName().Version?.ToString() ?? "unknown";
    }

    public static string GetCommonUtilsVersion()
    {
        try
        {
            return CommonUtils.Helper.GetVersion();
        }
        catch (Exception ex)
        {
            return "CommonUtils unavailable: " + ex.GetType().Name;
        }
    }
}
