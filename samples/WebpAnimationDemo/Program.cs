using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SkiaSharp;

namespace WebpAnimationDemo;

public static class Program
{
    // Initialization order matters: build the Avalonia app, show the window,
    // then demonstrate the routed (4.147.0-preview) SkiaSharp's animated WebP
    // encoder — an API that is NOT in the 2.88.9 SkiaSharp Avalonia uses. The
    // WebP code lives in WebpEncoder.dll, which Switchyard routes to
    // 4.147.0-preview at build time; this main app compiles only against 2.88.9
    // (for Avalonia) and the WebpEncoder API surface, never touching
    // SKWebpEncoder directly.
    [STAThread]
    public static void Main(string[] args)
    {
        Console.WriteLine("[DEMO] Avalonia's SkiaSharp (rendering): "
            + typeof(SKBitmap).Assembly.GetName().Version);

        // Build the WebP animation up front so it exists on disk when the
        // window opens. The call crosses into WebpEncoder.dll, which at runtime
        // binds SkiaSharp.Switchyard.4.147.0-preview (routed by Switchyard).
        string outPath = Path.Combine(AppContext.BaseDirectory, "bouncing-ball.webp");
        var bytes = WebpEncoder.EncodeAnimation(outPath);
        Console.WriteLine("[DEMO] Animated WebP written: bouncing-ball.webp ("
            + (bytes?.Length ?? 0) + " bytes)");

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}

public class App : Avalonia.Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                Title = "Switchyard WebP Animation Demo",
                Width = 520,
                Height = 360,
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}