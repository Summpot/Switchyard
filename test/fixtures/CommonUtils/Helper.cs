namespace CommonUtils;

/// <summary>
/// Reports the assembly version of CommonUtils. Like TargetLib, the version
/// is baked in at pack time.
/// </summary>
public static class Helper
{
    public static string GetVersion()
    {
        return typeof(Helper).Assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
