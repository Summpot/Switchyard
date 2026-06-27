using System.IO;
using SkiaSharp;

namespace WebpAnimationDemo;

/// <summary>
/// Builds a bouncing-ball animated WebP using <c>SKWebpEncoder.EncodeAnimated</c>,
/// an API available in SkiaSharp 4.148.0 and later. This project declares
/// SkiaSharp 4.148.0 so it compiles against that version and sees SKWebpEncoder;
/// Switchyard routes Avalonia (and any other non-app caller) DOWN to 2.88.9 at
/// build time, so Avalonia keeps its older SkiaSharp while this code uses 4.148.0.
/// </summary>
public static class WebpEncoder
{
    private const int Width = 128;
    private const int Height = 96;
    private const int FrameCount = 24;
    private static readonly TimeSpan FrameDuration = TimeSpan.FromMilliseconds(80);

    /// <summary>
    /// Encodes the animation to <paramref name="outputPath"/> and returns the
    /// raw WebP bytes (or <c>null</c> on failure).
    /// </summary>
    public static byte[]? EncodeAnimation(string outputPath)
    {
        var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var frames = new SKWebpEncoderFrame[FrameCount];
        try
        {
            for (int i = 0; i < FrameCount; i++)
            {
                var bitmap = new SKBitmap(info);
                using var canvas = new SKCanvas(bitmap);
                canvas.Clear(SKColors.White);

                float t = (float)i / (FrameCount - 1);
                float ballX = Width * t;
                float ballY = (float)(Height * 0.5 + Height * 0.35 * Math.Sin(t * Math.PI * 2));
                using var paint = new SKPaint
                {
                    Color = new SKColor(30, 115, 190, 255),
                    IsAntialias = true,
                };
                canvas.DrawCircle(ballX, ballY, 10, paint);
                frames[i] = new SKWebpEncoderFrame(bitmap, FrameDuration);
            }

            var options = new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, 90);
            using var data = SKWebpEncoder.EncodeAnimated(frames, options);
            if (data is null)
                return null;

            using var fs = File.OpenWrite(outputPath);
            data.SaveTo(fs);
            return data.ToArray();
        }
        finally
        {
            foreach (var f in frames)
                f.Pixmap?.Dispose();
        }
    }
}