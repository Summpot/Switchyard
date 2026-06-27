using System.IO;
using SkiaSharp;

namespace WebpAnimationDemo;

/// <summary>
/// Builds a spinning-pinwheel animated WebP using <c>SKWebpEncoder.EncodeAnimated</c>,
/// an API available in SkiaSharp 4.148.0 and later. This project declares
/// SkiaSharp 4.148.0 so it compiles against that version and sees SKWebpEncoder;
/// Switchyard routes Avalonia (and any other non-app caller) DOWN to 2.88.9 at
/// build time, so Avalonia keeps its older SkiaSharp while this code uses 4.148.0.
/// </summary>
public static class WebpEncoder
{
    private const int Width = 160;
    private const int Height = 120;
    private const int FrameCount = 36;
    private static readonly TimeSpan FrameDuration = TimeSpan.FromMilliseconds(55);

    private static readonly SKColor[] BladeColors =
    [
        new(239, 71, 111),
        new(255, 209, 102),
        new(6, 214, 160),
        new(17, 138, 178),
        new(131, 56, 236),
        new(255, 127, 80),
    ];

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
                canvas.Clear(new SKColor(17, 24, 39));

                float turn = (float)i / FrameCount;
                float centerX = Width / 2f;
                float centerY = Height / 2f;
                float outerRadius = Math.Min(Width, Height) * 0.44f;
                var bounds = SKRect.Create(
                    centerX - outerRadius,
                    centerY - outerRadius,
                    outerRadius * 2,
                    outerRadius * 2);

                using var bladePaint = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Fill,
                };

                for (int blade = 0; blade < 12; blade++)
                {
                    float startAngle = turn * 360f + blade * 30f;
                    bladePaint.Color = BladeColors[blade % BladeColors.Length];
                    canvas.DrawArc(bounds, startAngle, 20f, true, bladePaint);
                }

                using var ringPaint = new SKPaint
                {
                    Color = SKColors.White,
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 3,
                };
                canvas.DrawCircle(centerX, centerY, outerRadius + 3, ringPaint);

                float markerAngle = (turn * MathF.PI * 2f) - (MathF.PI / 2f);
                float markerX = centerX + MathF.Cos(markerAngle) * (outerRadius + 3);
                float markerY = centerY + MathF.Sin(markerAngle) * (outerRadius + 3);
                using var markerPaint = new SKPaint
                {
                    Color = SKColors.White,
                    IsAntialias = true,
                    Style = SKPaintStyle.Fill,
                };
                canvas.DrawCircle(markerX, markerY, 6, markerPaint);
                canvas.DrawCircle(centerX, centerY, 11, markerPaint);

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
