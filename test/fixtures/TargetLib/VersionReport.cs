namespace TargetLib;

/// <summary>
/// Reports the assembly version of the TargetLib that was loaded at runtime.
/// Because the assembly version is set at pack time, different NuGet package
/// versions produce DLLs that report different version strings.
/// </summary>
/// <remarks>
/// The class is intentionally non-static so that the <c>InvalidCastApp</c>
/// boundary-tearing sample can create instances across two routed versions
/// and observe an <c>InvalidCastException</c> at runtime.
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
