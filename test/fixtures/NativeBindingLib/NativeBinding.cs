using System.Runtime.InteropServices;

namespace NativeBindingLib;

/// <summary>
/// Managed facade over a small native library. Mirrors the structure of a
/// package like SkiaSharp: a managed assembly that P/Invokes a native
/// dependency shipped under <c>runtimes/{rid}/native/</c>. The native function
/// returns a version constant baked at native-build time, so a routed copy of
/// this package reports the native version it actually binds — proving each
/// routed managed version binds its own renamed native library.
/// </summary>
public static class NativeBinding
{
    // The DllImport target is the bare native module name. Switchyard rewrites
    // this to "nativebinding.Switchyard.{version}" and ships a renamed native
    // file alongside, so each routed managed version binds its own native lib.
    [DllImport("nativebinding", CallingConvention = CallingConvention.Cdecl)]
    private static extern int native_get_version();

    public static string GetVersion()
    {
        return typeof(NativeBinding).Assembly.GetName().Version?.ToString() ?? "unknown";
    }

    /// <summary>
    /// Returns the version baked into the native library this managed
    /// assembly is bound to. When Switchyard isolates native libs, two routed
    /// versions report different native versions; without isolation they
    /// would share a single native lib and report the same value.
    /// </summary>
    public static int GetNativeVersion()
    {
        return native_get_version();
    }
}