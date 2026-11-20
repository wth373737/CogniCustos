using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CogniCustos.Ui;
using CogniCustos.Ui.ViewModels;

namespace CogniCustos.Ui.Shots;

/// <summary>
/// Renders every UI state to a PNG. Assertions live in tools/CogniCustos.Ui.Tests;
/// this tool only draws.
/// </summary>
internal static class Program
{
    private static readonly string[] States =
    [
        "home", "drawer", "expanded", "settings", "help",
        "collapsed", "toast", "settings-zh", "settings-deep",
    ];

    [STAThread]
    public static int Main(string[] args)
    {
        var outDir = args.Length > 0 ? args[0] : "artifacts/ui";
        var width = args.Length > 1 ? int.Parse(args[1]) : 1280;
        var height = args.Length > 2 ? int.Parse(args[2]) : 800;

        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .WithInterFont()
            .SetupWithoutStarting();

        Directory.CreateDirectory(outDir);
        var failures = 0;

        foreach (var state in States)
        {
            var vm = new ShellViewModel();
            vm.ShowState(state);

            var window = new ShellWindow(vm) { Width = width, Height = height };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var frame = window.CaptureRenderedFrame();
            if (frame is null)
            {
                Console.WriteLine($"FAIL {state}: renderer returned no frame");
                failures++;
            }
            else
            {
                var path = Path.Combine(outDir, $"{state}-{width}x{height}.png");
                frame.Save(path, new PngBitmapEncoderOptions());
                Console.WriteLine($"OK   {state} -> {path}");
            }

            window.Close();
            Dispatcher.UIThread.RunJobs();
        }

        Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }
}
