using SkiaSharp;

// SkiaSharpIsolationApp — the real-world scenario Switchyard was built for:
// using Avalonia (which pins an older SkiaSharp) alongside a newer SkiaSharp
// elsewhere, with both native libskiasharp copies coexisting in one process.
//
// The app routes SkiaSharp to 2.88.9 for itself and 3.116.1 for the
// SkiaSharpConsumerModule project reference. Switchyard must:
//   * rename both managed versions (SkiaSharp.Switchyard.2.88.9 / .3.116.1),
//   * rewrite the DllImport "libskiasharp" in each to a routed native name,
//   * ship a renamed libskiasharp native lib per routed version,
// so each managed version binds its OWN native library. Creating an SKBitmap
// forces the native library to load; if the wrong/missing native is bound the
// process throws DllNotFoundException. The assertion is that both report their
// distinct managed versions AND both native loads succeed (exit 0).

Console.WriteLine("[SKIA_APP] SkiaSharp managed version: "
    + typeof(SKBitmap).Assembly.GetName().Version);

using (new SKBitmap())
{
    Console.WriteLine("[SKIA_APP] libskiasharp native loaded OK (2.88.9)");
}

SkiaSharpConsumerModule.Reporter.Report();

Console.WriteLine("[SKIA_APP] Done.");