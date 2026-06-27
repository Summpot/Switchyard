using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SkiaSharp;

namespace WebpAnimationDemo;

public static class Program
{
    // This single project uses TWO SkiaSharp versions at once, via Switchyard:
    //   * It declares SkiaSharp 4.148.0 so it compiles against it and can call
    //     SKWebpEncoder.EncodeAnimated (animated WebP) — an API absent from
    //     the 2.88.9 SkiaSharp Avalonia pins.
    //   * Switchyard routes every other caller (Avalonia.Skia etc.) DOWN to
    //     2.88.9, so Avalonia keeps its validated SkiaSharp.
    [STAThread]
    public static void Main(string[] args)
    {
        Console.WriteLine("[DEMO] App's SkiaSharp (compile + own code): "
            + typeof(SKWebpEncoder).Assembly.GetName().Version);

        string outPath = Path.Combine(AppContext.BaseDirectory, "spinning-pinwheel.webp");
        var bytes = WebpEncoder.EncodeAnimation(outPath);
        Console.WriteLine("[DEMO] Animated WebP written: spinning-pinwheel.webp ("
            + (bytes?.Length ?? 0) + " bytes) via SKWebpEncoder (4.148.0 only)");

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
