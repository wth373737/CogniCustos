using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CogniCustos.Ui;
using CogniCustos.Ui.ViewModels;
using CogniCustos.Ui.Views;

namespace CogniCustos.Ui.Shots;

internal static class Program
{
    private static readonly string[] States =
        ["home", "drawer", "expanded", "settings", "help", "collapsed", "toast", "settings-zh"];

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
            failures += Snap(outDir, state, width, height) ? 0 : 1;
        }

        // Interaction checks that need a live visual tree: real keystrokes, real bindings.
        failures += InteractionChecks() ? 0 : 1;

        Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    private static bool Snap(string outDir, string state, int width, int height)
    {
        var vm = new ShellViewModel();
        vm.ShowState(state);

        var window = new ShellWindow(vm) { Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        var ok = frame is not null;
        if (!ok)
        {
            Console.WriteLine($"FAIL {state}: renderer returned no frame");
        }
        else
        {
            var path = Path.Combine(outDir, $"{state}-{width}x{height}.png");
            frame!.Save(path, new PngBitmapEncoderOptions());
            Console.WriteLine($"OK   {state} -> {path} ({frame.PixelSize.Width}x{frame.PixelSize.Height})");
        }

        window.Close();
        Dispatcher.UIThread.RunJobs();
        return ok;
    }

    /// <summary>
    /// Drives the real controls: types into the input box, presses Enter,
    /// clicks the card chevron through the command, toggles the language.
    /// </summary>
    private static bool InteractionChecks()
    {
        var ok = true;

        var vm = new ShellViewModel();
        var window = new ShellWindow(vm) { Width = 1280, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var view = window.GetVisualDescendants().OfType<ShellView>().First();
        var input = view.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "InputBox");
        var before = vm.Cards[0].Items.Count;

        if (input is null)
        {
            Console.WriteLine("FAIL interaction: input box not found");
            return false;
        }

        input.Focus();
        input.Text = "typed from the headless test";
        Dispatcher.UIThread.RunJobs();
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();

        ok &= Check("enter-submits-input", vm.Cards[0].Items.Count == before + 1 && vm.InputText.Length == 0);
        ok &= Check("submit-shows-status", vm.HasToast);

        var card = vm.Cards[1];
        card.ToggleCollapseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        ok &= Check("card-collapses", card.IsCollapsed && Math.Abs(card.CardHeight - 78) < 0.1);

        card.ToggleCollapseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        ok &= Check("card-expands", !card.IsCollapsed && Math.Abs(card.CardHeight - 300) < 0.1);

        vm.ToggleLanguageCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        ok &= Check("language-switches", vm.S.IsChinese && vm.Cards[0].Title.Length > 0 && vm.Cards[0].EmptyText.Length > 0);

        vm.ToggleDrawerCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        ok &= Check("drawer-opens", vm.IsMenuActive && vm.IsBackdropVisible);

        window.Close();
        Dispatcher.UIThread.RunJobs();
        return ok;
    }

    private static bool Check(string name, bool condition)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
        return condition;
    }
}
