using SkiaSharp;

namespace SkiaSharpConsumerModule;

public static class Reporter
{
    public static void Report()
    {
        Console.WriteLine("[SKIA_CONSUMER] SkiaSharp managed version: "
            + typeof(SKBitmap).Assembly.GetName().Version);

        using (new SKBitmap())
        {
            Console.WriteLine("[SKIA_CONSUMER] libskiasharp native loaded OK (3.116.1)");
        }
    }
}