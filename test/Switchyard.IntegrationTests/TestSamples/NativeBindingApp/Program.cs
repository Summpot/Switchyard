using NativeBindingLib;

// NativeBindingApp — exercises the Avalonia + higher-SkiaSharp-style scenario
// (a package that combines a managed assembly with a native dependency) at a
// small, self-contained scale.
//
// The app routes NativeBindingLib to 1.0.0 for itself and to 3.5.0 for the
// NativeConsumerModule project reference. Switchyard must:
//   * rename both managed versions (NativeBindingLib.Switchyard.1.0.0 / .3.5.0),
//   * rewrite the DllImport "nativebinding" in each to a routed native name,
//   * ship a renamed native lib per routed version,
// so each managed version binds its OWN native library. The assertion is that
// the two routed versions report DIFFERENT native version constants (1 vs 3),
// proving native-lib isolation rather than a single shared native load.
Console.WriteLine("[NATIVE_APP] Managed version : " + NativeBinding.GetVersion());
Console.WriteLine("[NATIVE_APP] Native version : " + NativeBinding.GetNativeVersion());

NativeConsumerModule.Reporter.Report();

Console.WriteLine("[NATIVE_APP] Done.");