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
    private static int _failures;

    [STAThread]
    public static int Main()
    {
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .WithInterFont()
            .SetupWithoutStarting();

        // ---- strings ----
        Test("sections_have_titles_in_both_languages", (_, _) =>
        {
            var s = new Strings();
            Check(Strings.SectionCount == 16, "expected 16 sections");
            for (var i = 0; i < Strings.SectionCount; i++)
            {
                var en = s.SectionTitle(i);
                s.IsChinese = true;
                var zh = s.SectionTitle(i);
                Check(en.Length > 0 && zh.Length > 0, $"section {i} has an empty title");
                Check(en != zh, $"section {i} is not translated");
                s.IsChinese = false;
            }

            Check(s.SectionTitle(-1) == string.Empty && s.SectionTitle(99) == string.Empty, "out-of-range must be empty");
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
            Check(card.Items[^1].Text == "New entry", "english label expected");
            Check(card.Items.Count == 3, $"unexpected seed count: {card.Items.Count}");

            vm.ToggleLanguageCommand.Execute(null);
            Pump();
            // Seed rows are re-localised; a row the user added is user data and keeps its text.
            Check(card.Items.Count == 3, $"row count changed on language switch: {card.Items.Count}");
            Check(card.Items[^1].Text == "New entry", "user row must not be retranslated");

            card.AddCommand.Execute(null);
            Check(card.Items[^1].Text == "新条目", "new row must use the current language");
        });

        // ---- regression A: settings selection ----
        Test("language_switch_keeps_settings_selection", (vm, _) =>
        {
            vm.ToggleSettingsCommand.Execute(null);
            Pump();
            vm.Settings.Selected = vm.Settings.Sections[5];
            Pump();
            Check(vm.Settings.SelectedTitle == vm.S.SectionTitle(5), "precondition: selection not applied");

            vm.ToggleLanguageCommand.Execute(null);
            Pump();
            Check(vm.Settings.Selected?.Key == "section-5", "selection was reset by the language switch");
            Check(vm.Settings.SelectedTitle == vm.S.SectionTitle(5), "selection title went stale");
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

        Console.WriteLine(_failures == 0 ? "ALL PASS" : $"{_failures} FAILURE(S)");
        return _failures == 0 ? 0 : 1;
    }

    // ---------- plumbing ----------

    private static void Test(string name, Action<ShellViewModel, ShellWindow> body)
    {
        var vm = new ShellViewModel();
        var window = new ShellWindow(vm) { Width = 1280, Height = 800 };
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

    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private static TextBox InputBox(ShellWindow window)
        => window.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "InputBox");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
