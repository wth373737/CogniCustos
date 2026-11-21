using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CogniCustos.Ui;
using CogniCustos.Ui.Localization;
using CogniCustos.Ui.ViewModels;

namespace CogniCustos.Ui.Shots;

/// <summary>
/// Renders every UI state to a PNG. Assertions live in tools/CogniCustos.Ui.Tests;
/// this tool only draws.
///
/// Usage: CogniCustos.Ui.Shots &lt;outDir&gt; [width] [height] [sketchPath] [crop]
///   crop = "x,y,w,h" of the sketch panel to compare against (optional).
/// When a sketch is given, a side-by-side compare.png is written next to the states.
/// </summary>
internal static class Program
{
    private static readonly string[] States =
    [
        "home", "drawer", "expanded", "settings", "help",
        "collapsed", "toast", "settings-zh", "settings-deep", "today-list", "today-list-long", "language-menu",
    ];

    [STAThread]
    public static int Main(string[] args)
    {
        var outDir = args.Length > 0 ? args[0] : "artifacts/ui";
        var width = args.Length > 1 ? int.Parse(args[1]) : 1280;
        var height = args.Length > 2 ? int.Parse(args[2]) : 720;
        var sketchPath = args.Length > 3 ? args[3] : string.Empty;
        var crop = args.Length > 4 ? args[4] : string.Empty;

        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .WithInterFont()
            .SetupWithoutStarting();

        Directory.CreateDirectory(outDir);
        var failures = 0;

        foreach (var state in States)
        {
            failures += Snap(outDir, state, width, height) ? 0 : 1;
        }

        if (sketchPath.Length > 0)
        {
            failures += Compare(outDir, sketchPath, crop, width, height) ? 0 : 1;
        }

        Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    private static bool Snap(string outDir, string state, int width, int height)
    {
        var frame = RenderState(state, width, height);
        if (frame is null)
        {
            Console.WriteLine($"FAIL {state}: renderer returned no frame");
            return false;
        }

        var path = Path.Combine(outDir, $"{state}-{width}x{height}.png");
        frame.Save(path, new PngBitmapEncoderOptions());
        Console.WriteLine($"OK   {state} -> {path}");
        frame.Dispose();
        return true;
    }

    private static WriteableBitmap? RenderState(string state, int width, int height)
    {
        var vm = new ShellViewModel();
        // Every state starts from English; the zh states switch inside ShowState.
        vm.ApplyLanguage(Localizer.English);

        var window = new ShellWindow(vm) { Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // State goes on after the window opened: opening calls RefreshText, which
        // would otherwise reset the fixture (that is what the app wants, not the shots).
        vm.ShowState(state);
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        window.Close();
        Dispatcher.UIThread.RunJobs();
        return frame;
    }

    /// <summary>Stack the sketch (top) and the render (bottom) into one PNG.</summary>
    private static bool Compare(string outDir, string sketchPath, string crop, int width, int height)
    {
        if (!File.Exists(sketchPath))
        {
            Console.WriteLine($"FAIL compare: sketch not found at {sketchPath}");
            return false;
        }

        using var sketch = new Bitmap(sketchPath);
        using var frame = RenderState("home", width, height);
        if (frame is null)
        {
            Console.WriteLine("FAIL compare: no frame");
            return false;
        }

        var source = ParseCrop(crop, sketch.PixelSize);
        using var target = new RenderTargetBitmap(new PixelSize(width, height * 2 + 24), new Vector(96, 96));

        using (var context = target.CreateDrawingContext())
        {
            context.DrawImage(sketch, source, new Rect(0, 0, width, height));
            context.DrawImage(frame, new Rect(0, height + 24, width, height));
        }

        var path = Path.Combine(outDir, "compare.png");
        target.Save(path, new PngBitmapEncoderOptions());
        Console.WriteLine($"OK   compare -> {path}");
        return true;
    }

    private static Rect ParseCrop(string crop, PixelSize size)
    {
        if (crop.Length == 0) return new Rect(0, 0, size.Width, size.Height);

        var parts = crop.Split(',');
        if (parts.Length != 4) return new Rect(0, 0, size.Width, size.Height);

        return new Rect(
            double.Parse(parts[0]), double.Parse(parts[1]),
            double.Parse(parts[2]), double.Parse(parts[3]));
    }
}
