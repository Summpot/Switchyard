using NativeBindingLib;

Console.WriteLine("[NATIVE_AOT_APP] Managed version : " + NativeBinding.GetVersion());
Console.WriteLine("[NATIVE_AOT_APP] Native version : " + NativeBinding.GetNativeVersion());

NativeConsumerModule.Reporter.Report();

Console.WriteLine("[NATIVE_AOT_APP] Done.");
