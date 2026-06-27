using NativeBindingLib;

namespace NativeConsumerModule;

public static class Reporter
{
    public static void Report()
    {
        Console.WriteLine("[NATIVE_CONSUMER] Managed version : " + NativeBinding.GetVersion());
        Console.WriteLine("[NATIVE_CONSUMER] Native version : " + NativeBinding.GetNativeVersion());
    }
}