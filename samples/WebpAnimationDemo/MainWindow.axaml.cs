using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace WebpAnimationDemo;

public partial class MainWindow : Window
{
    private Image _preview = null!;
    private TextBlock _status = null!;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _preview = this.FindControl<Image>("Preview")!;
        _status = this.FindControl<TextBlock>("Status")!;
    }

    private void OnEncodeClick(object? sender, RoutedEventArgs e)
    {
        // Re-encode on demand via WebpEncoder.dll (routed to SkiaSharp
        // 4.147.0-preview by Switchyard). Then show the first frame in the
        // Avalonia Image, which renders through Avalonia's own 2.88.9 SkiaSharp.
        string outPath = Path.Combine(AppContext.BaseDirectory, "bouncing-ball.webp");
        var bytes = WebpEncoder.EncodeAnimation(outPath);
        if (bytes is null)
        {
            _status.Text = "Encode failed";
            return;
        }

        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bmp = new SKBitmap(info);
        codec.GetPixels(bmp.Info, bmp.GetPixels(out _));
        using var skImage = SKImage.FromBitmap(bmp);
        using var pngStream = new MemoryStream();
        skImage.Encode(SKEncodedImageFormat.Png, 100).SaveTo(pngStream);
        pngStream.Position = 0;

        var avBmp = new Bitmap(pngStream);
        _preview.Source = avBmp;
        _status.Text = $"Wrote bouncing-ball.webp ({bytes.Length} bytes), {codec.FrameCount} frames";
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