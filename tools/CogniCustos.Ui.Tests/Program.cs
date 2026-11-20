using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CogniCustos.Ui;
using CogniCustos.Ui.Localization;
using CogniCustos.Ui.ViewModels;
using CogniCustos.Ui.Views;

namespace CogniCustos.Ui.Tests;

/// <summary>
/// Headless regression suite. Every assertion here exists because it once broke
/// or because a reviewer would reasonably expect it to hold.
/// </summary>
internal static class Program
{
    private const string XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static int _failures;

    [STAThread]
    public static int Main()
    {
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .WithInterFont()
            .SetupWithoutStarting();

        // ---- language resources ----
        Test("language_files_have_identical_non_empty_keys", (_, _) =>
        {
            var en = ReadLanguageFile("en");
            var zh = ReadLanguageFile("zh");

            Check(en.Count > 0, "english resource file is empty");
            Check(en.Keys.OrderBy(k => k).SequenceEqual(zh.Keys.OrderBy(k => k)),
                $"key sets differ: only-en=[{string.Join(", ", en.Keys.Except(zh.Keys))}] " +
                $"only-zh=[{string.Join(", ", zh.Keys.Except(en.Keys))}]");

            foreach (var (key, value) in en.Concat(zh))
            {
                Check(!string.IsNullOrWhiteSpace(value), $"empty value for key '{key}'");
            }
        });

        Test("settings_sections_all_have_resources", (_, _) =>
        {
            var en = ReadLanguageFile("en");
            foreach (var key in SettingsViewModel.Keys)
            {
                Check(en.ContainsKey(key), $"missing settings resource '{key}'");
            }

            Check(SettingsViewModel.Keys.Count == 16, $"expected 16 sections, got {SettingsViewModel.Keys.Count}");
        });

        Test("every_key_resolves_in_both_languages", (_, _) =>
        {
            var keys = ReadLanguageFile("en").Keys.ToList();
            foreach (var language in Localizer.Languages)
            {
                Localizer.SetLanguage(language);
                foreach (var key in keys)
                {
                    var text = Localizer.Get(key);
                    Check(text != $"!{key}!" && text.Length > 0, $"key '{key}' missing in '{language}'");
                }
            }
        });

        // ---- input box ----
        Test("enter_adds_row_and_clears_input", (vm, w) =>
        {
            var input = InputBox(w);
            var before = vm.Cards[0].Items.Count;
            input.Focus();
            input.Text = "typed row";
            Pump();
            w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Pump();
            Check(vm.Cards[0].Items.Count == before + 1, "row was not added");
            Check(vm.InputText.Length == 0, "input was not cleared");
            Check(vm.HasToast, "no status line after submit");
        });

        Test("empty_enter_does_nothing", (vm, w) =>
        {
            var before = vm.Cards[0].Items.Count;
            var input = InputBox(w);
            input.Focus();
            input.Text = "   ";
            Pump();
            w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Pump();
            Check(vm.Cards[0].Items.Count == before, "blank submit added a row");
        });

        // ---- cards ----
        Test("card_collapse_and_expand", (vm, _) =>
        {
            var card = vm.Cards[1];
            card.ToggleCollapseCommand.Execute(null);
            Pump();
            Check(card.IsCollapsed && Math.Abs(card.CardHeight - 78) < 0.1, "collapse state wrong");
            card.ToggleCollapseCommand.Execute(null);
            Pump();
            Check(!card.IsCollapsed && Math.Abs(card.CardHeight - 300) < 0.1, "expand state wrong");
        });

        Test("added_rows_use_current_language_and_are_not_retranslated", (vm, _) =>
        {
            var card = vm.Cards[2];
            card.AddCommand.Execute(null);
            var englishLabel = Localizer.Get("Card.Row.New");
            Check(card.Items[^1].Text == englishLabel, "label must come from resources");
            Check(card.Items.Count == 3, $"unexpected seed count: {card.Items.Count}");

            vm.ToggleLanguageCommand.Execute(null);
            Pump();
            // Seed rows are re-localised; a row the user added is user data and keeps its text.
            Check(card.Items.Count == 3, $"row count changed on language switch: {card.Items.Count}");
            Check(card.Items[^1].Text == englishLabel, "user row must not be retranslated");

            card.AddCommand.Execute(null);
            var chineseLabel = Localizer.Get("Card.Row.New");
            Check(chineseLabel != englishLabel, "resources did not switch");
            Check(card.Items[^1].Text == chineseLabel, "new row must use the current language");
        });

        // ---- regression A: settings selection ----
        Test("language_switch_keeps_settings_selection", (vm, _) =>
        {
            vm.ToggleSettingsCommand.Execute(null);
            Pump();
            vm.Settings.Selected = vm.Settings.Sections[5];
            Pump();
            Check(vm.Settings.SelectedTitle == Localizer.Get("Section.StudentNetwork"),
                "precondition: selection not applied");

            vm.ToggleLanguageCommand.Execute(null);
            Pump();
            Check(vm.Settings.Selected?.Key == "Section.StudentNetwork", "selection was reset by the language switch");
            Check(vm.Settings.SelectedTitle == Localizer.Get("Section.StudentNetwork"), "selection title went stale");
            Check(vm.Settings.SelectedTitle == "学生网络配置", $"unexpected title: {vm.Settings.SelectedTitle}");
        });

        // ---- regression C: user rows ----
        Test("language_switch_keeps_user_rows", (vm, w) =>
        {
            var input = InputBox(w);
            input.Focus();
            input.Text = "kept row";
            Pump();
            w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Pump();
            var before = vm.Cards[0].Items.Count;

            vm.ToggleLanguageCommand.Execute(null);
            Pump();
            Check(vm.Cards[0].Items.Count == before, $"rows lost: {before} -> {vm.Cards[0].Items.Count}");
            Check(vm.Cards[0].Items.Any(i => i.Text == "kept row"), "user row disappeared");
        });

        // ---- DynamicResource plumbing ----
        Test("dynamic_resource_text_updates_on_language_switch", (vm, w) =>
        {
            var languageButton = w.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("lang"));
            var homeButton = w.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("link"));
            Check((string?)languageButton.Content == "中", $"expected 中, got {languageButton.Content}");
            Check((string?)homeButton.Content == "Home", $"expected Home, got {homeButton.Content}");

            vm.ToggleLanguageCommand.Execute(null);
            Pump();
            Check((string?)languageButton.Content == "En", $"language button did not refresh: {languageButton.Content}");
            Check((string?)homeButton.Content == "主页", $"dynamic resource did not refresh: {homeButton.Content}");
            Check(vm.Cards[1].Title == "待办任务", $"card title did not refresh: {vm.Cards[1].Title}");
        });

        // ---- regression B: escape without inner focus ----
        Test("escape_closes_drawer_without_inner_focus", (vm, w) =>
        {
            vm.ToggleDrawerCommand.Execute(null);
            Pump();
            Check(vm.IsMenuActive, "drawer did not open");

            w.Focus();
            Pump();
            w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Pump();
            Check(!vm.IsMenuActive, "escape did nothing when nothing was focused");
        });

        Test("escape_closes_settings_with_inner_focus", (vm, w) =>
        {
            vm.ToggleSettingsCommand.Execute(null);
            Pump();
            InputBox(w).Focus();
            Pump();
            w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Pump();
            Check(!vm.SettingsOpen, "escape did not close the settings page");
        });

        // ---- panel rules ----
        Test("drawer_and_settings_are_mutually_exclusive", (vm, _) =>
        {
            vm.ToggleDrawerCommand.Execute(null);
            Pump();
            vm.ToggleSettingsCommand.Execute(null);
            Pump();
            Check(!vm.DrawerOpen && vm.SettingsOpen, "settings did not replace the drawer");
            Check(!vm.IsBackdropVisible, "backdrop must be hidden when settings is open");
        });

        Test("backdrop_visibility_follows_overlays", (vm, _) =>
        {
            Check(!vm.IsBackdropVisible, "backdrop visible with nothing open");
            vm.ToggleDrawerCommand.Execute(null);
            Pump();
            Check(vm.IsBackdropVisible, "backdrop missing for the drawer");
            vm.ToggleDrawerCommand.Execute(null);
            Pump();
            Check(!vm.IsBackdropVisible, "backdrop left behind");
        });

        Test("drawer_expands_and_animates_width", (vm, w) =>
        {
            vm.ToggleDrawerCommand.Execute(null);
            Pump();
            var panel = w.GetVisualDescendants().OfType<Border>().First(b => b.Name == "DrawerPanel");
            Check(panel.Transitions is { Count: >= 2 }, "the drawer needs width + opacity transitions");

            vm.ExpandDrawerCommand.Execute(null);
            Pump();
            Check(Math.Abs(vm.DrawerWidth - 560) < 0.1, "expanded width should be 560");
            Check(Math.Abs(vm.FutureAreaOpacity - 1) < 0.01, "reserved area should be visible");

            vm.ExpandDrawerCommand.Execute(null);
            Pump();
            Check(Math.Abs(vm.DrawerWidth - 208) < 0.1, "collapsed width should be 208");
            Check(Math.Abs(vm.FutureAreaOpacity) < 0.01, "reserved area should be hidden");
        });

        // ---- proportional scaling ----
        Test("canvas_scales_uniformly_at_1024x640", (_, w) => AssertScale(w, 0.8), 1024, 640);
        Test("canvas_scales_uniformly_at_1280x800", (_, w) => AssertScale(w, 1.0), 1280, 800);
        Test("canvas_scales_uniformly_at_2560x1440", (_, w) => AssertScale(w, 1.8), 2560, 1440);
        Test("canvas_scales_uniformly_at_odd_aspect", (_, w) => AssertScale(w, 1.0), 1400, 800);

        Console.WriteLine(_failures == 0 ? "ALL PASS" : $"{_failures} FAILURE(S)");
        return _failures == 0 ? 0 : 1;
    }

    // ---------- assertions ----------

    /// <summary>The shell always lays out on the fixed 1280x800 canvas and is scaled by the Viewbox.</summary>
    private static void AssertScale(ShellWindow window, double expected)
    {
        var view = window.GetVisualDescendants().OfType<ShellView>().First();
        Check(Math.Abs(view.Bounds.Width - 1280) < 0.5, $"canvas width changed: {view.Bounds.Width}");
        Check(Math.Abs(view.Bounds.Height - 800) < 0.5, $"canvas height changed: {view.Bounds.Height}");

        var matrix = view.TransformToVisual(window);
        Check(matrix is not null, "no transform to the window");
        var m = matrix!.Value;
        Check(Math.Abs(m.M11 - expected) < 0.002, $"scale should be {expected}, got {m.M11:F4}");
        Check(Math.Abs(m.M22 - expected) < 0.002, $"vertical scale should be {expected}, got {m.M22:F4}");
    }

    // ---------- plumbing ----------

    private static void Test(string name, Action<ShellViewModel, ShellWindow> body,
        int width = 1280, int height = 800)
    {
        // Every test starts from English so the order of tests cannot matter.
        Localizer.SetLanguage(Localizer.English);

        var vm = new ShellViewModel();
        var window = new ShellWindow(vm) { Width = width, Height = height };
        window.Show();
        Pump();

        try
        {
            body(vm, window);
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine($"FAIL {name}: {ex.Message}");
        }
        finally
        {
            window.Close();
            Pump();
        }
    }

    private static Dictionary<string, string> ReadLanguageFile(string culture)
    {
        // This is a source-level parity check, so it reads the file on disk:
        // compiled Avalonia XAML is not exposed through AssetLoader as raw text.
        var path = FindSourceFile($"src/CogniCustos.Ui/Resources/Lang.{culture}.axaml");
        var document = XDocument.Load(path);
        var key = XName.Get("Key", XamlNs);

        return document.Descendants()
            .Select(element => (Key: element.Attribute(key)?.Value, Value: element.Value))
            .Where(pair => pair.Key is not null)
            .ToDictionary(pair => pair.Key!, pair => pair.Value);
    }

    private static string FindSourceFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"could not locate {relativePath} above {AppContext.BaseDirectory}");
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static TextBox InputBox(ShellWindow window)
        => window.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "InputBox");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
