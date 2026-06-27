using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SkiaSharp;

namespace WebpAnimationDemo;

public partial class MainWindow : Window
{
    private Image _preview = null!;
    private TextBlock _status = null!;
    private readonly List<Bitmap> _previewFrames = [];
    private readonly List<TimeSpan> _previewFrameDurations = [];
    private DispatcherTimer? _previewTimer;
    private int _previewFrameIndex;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _preview = this.FindControl<Image>("Preview")!;
        _status = this.FindControl<TextBlock>("Status")!;
    }

    private void OnEncodeClick(object? sender, RoutedEventArgs e)
    {
        // Re-encode on demand via the app's SkiaSharp 4.148.0, then play the frames in Avalonia.
        string outPath = Path.Combine(AppContext.BaseDirectory, "spinning-pinwheel.webp");
        var bytes = WebpEncoder.EncodeAnimation(outPath);
        if (bytes is null)
        {
            _status.Text = "Encode failed";
            return;
        }

        int frameCount = ShowAnimatedPreview(bytes);
        _status.Text = $"Wrote spinning-pinwheel.webp ({bytes.Length} bytes), {frameCount} frames";
    }

    private int ShowAnimatedPreview(byte[] bytes)
    {
        StopPreviewAnimation();

        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        var decodeInfo = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        int frameCount = Math.Max(1, codec.FrameCount);

        for (int i = 0; i < frameCount; i++)
        {
            using var bmp = new SKBitmap(decodeInfo);
            codec.GetPixels(bmp.Info, bmp.GetPixels(out _), new SKCodecOptions(i));

            using var skImage = SKImage.FromBitmap(bmp);
            using var pngStream = new MemoryStream();
            skImage.Encode(SKEncodedImageFormat.Png, 100).SaveTo(pngStream);
            pngStream.Position = 0;
            _previewFrames.Add(new Bitmap(pngStream));

            int durationMs = codec.FrameInfo.Length > i ? codec.FrameInfo[i].Duration : 0;
            _previewFrameDurations.Add(TimeSpan.FromMilliseconds(durationMs > 0 ? durationMs : 55));
        }

        _previewFrameIndex = 0;
        _preview.Source = _previewFrames[0];
        if (_previewFrames.Count > 1)
        {
            _previewTimer = new DispatcherTimer { Interval = _previewFrameDurations[0] };
            _previewTimer.Tick += OnPreviewTimerTick;
            _previewTimer.Start();
        }

        return frameCount;
    }

    private void OnPreviewTimerTick(object? sender, EventArgs e)
    {
        if (_previewFrames.Count == 0 || _previewTimer is null)
            return;

        _previewFrameIndex = (_previewFrameIndex + 1) % _previewFrames.Count;
        _preview.Source = _previewFrames[_previewFrameIndex];
        _previewTimer.Interval = _previewFrameDurations[_previewFrameIndex];
    }

    private void StopPreviewAnimation()
    {
        if (_previewTimer is not null)
        {
            _previewTimer.Stop();
            _previewTimer.Tick -= OnPreviewTimerTick;
            _previewTimer = null;
        }

        _preview.Source = null;
        foreach (var frame in _previewFrames)
            frame.Dispose();

        _previewFrames.Clear();
        _previewFrameDurations.Clear();
        _previewFrameIndex = 0;
    }

    private void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        var dir = AppContext.BaseDirectory;
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        else if (OperatingSystem.IsMacOS())
            Process.Start("open", dir);
        else
            Process.Start("xdg-open", dir);
    }
}
