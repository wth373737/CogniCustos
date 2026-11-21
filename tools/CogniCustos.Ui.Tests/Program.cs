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
using Avalonia.Media;
using Avalonia.LogicalTree;
using System.Text.RegularExpressions;

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

            Check(SettingsViewModel.Keys.Count == 17, $"expected 17 sections, got {SettingsViewModel.Keys.Count}");
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
        Test("enter_adds_to_today_and_clears_input", (vm, w) =>
        {
            var input = InputBox(w);
            var before = vm.Today.Items.Count;
            input.Focus();
            input.Text = "typed reminder";
            Pump();
            w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Pump();
            Check(vm.Today.Items.Count == before + 1, "reminder was not added");
            Check(vm.Today.Items[^1] == "typed reminder", "wrong reminder text");
            Check(vm.InputText.Length == 0, "input was not cleared");
            Check(vm.HasToast, "no status line after submit");
            Check(vm.Cards[0].Items.Count == 0, "the calendar card must not receive today's entries");
        });

        Test("empty_enter_does_nothing", (vm, w) =>
        {
            var before = vm.Today.Items.Count;
            var input = InputBox(w);
            input.Focus();
            input.Text = "   ";
            Pump();
            w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Pump();
            Check(vm.Today.Items.Count == before, "blank submit added a reminder");
        });

        // ---- semantic model: today lives at the top, the first card is a calendar ----
        Test("calendar_card_is_not_today", (vm, _) =>
        {
            var calendar = vm.Cards[0];
            Check(calendar.Kind == CardKind.Calendar, "first card must be the calendar card");
            Check(calendar.Items.Count == 0, "calendar card must not hold today's entries");
            Check(!calendar.CanAdd, "calendar card has no working add button yet");
            Check(vm.Today.Items.Count > 0, "today's reminders must live in the top line");
        });

        Test("pending_card_has_no_add_button", (vm, _) =>
        {
            Check(!vm.Cards[1].ShowAdd, "the sketch has no + on the pending card");
            Check(vm.Cards[2].ShowAdd && vm.Cards[2].CanAdd, "the note card keeps its +");
        });

        Test("today_list_toggles", (vm, w) =>
        {
            Check(!vm.Today.IsListOpen, "list starts closed");
            Check(vm.Today.ListChevron == "\uE70D", "closed chevron points down");
            vm.Today.ToggleListCommand.Execute(null);
            Pump();
            Check(vm.Today.IsListOpen, "list did not open");
            Check(vm.Today.ListChevron == "\uE70E", "open chevron points up");
            Check(vm.IsBackdropVisible, "the list needs a click-outside catcher");
            var backdrop = w.GetVisualDescendants().OfType<Border>()
                .First(b => b.Name == "OverlayBackdrop");
            Check(backdrop.IsVisible, "the click-outside catcher did not become visible");
            vm.CloseOverlays();
            Pump();
            Check(!vm.Today.IsListOpen, "close did not close the list");
        });

        // ---- cards ----
        Test("reminder_drawer_virtualises_and_caps_its_height", (vm, w) =>
        {
            vm.ShowState("today-list-long");
            Pump();

            var drawer = w.GetVisualDescendants().OfType<Border>().First(b => b.Name == "ReminderDrawer");
            var list = w.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "ReminderList");

            Check(Math.Abs(drawer.MaxHeight - 274) < 0.1, $"drawer cap should be 274, got {drawer.MaxHeight}");
            Check(drawer.Bounds.Height <= 274.5, $"drawer taller than its cap: {drawer.Bounds.Height}");
            Check(list.ItemsPanelRoot is VirtualizingStackPanel, "the reminder list must virtualise");
            Check(vm.Today.Items.Count == 12, "the long fixture should have 12 rows");
            // Virtualised: only the rows that fit are realised.
            var realised = list.GetVisualDescendants().OfType<ListBoxItem>().Count();
            Check(realised < vm.Today.Items.Count, $"expected fewer realised rows than items, got {realised}");
        });

        Test("clicking_outside_the_drawer_closes_it", (vm, w) =>
        {
            vm.Today.ToggleListCommand.Execute(null);
            Pump();
            Check(vm.Today.IsListOpen, "drawer did not open");

            // a click on the card area lands on the backdrop and closes the drawer
            w.MouseDown(new Point(300, 560), MouseButton.Left);
            w.MouseUp(new Point(300, 560), MouseButton.Left);
            Pump();
            Check(!vm.Today.IsListOpen, "clicking outside the drawer did not close it");
        });

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
            vm.Settings.Selected = vm.Settings.Sections.First(section => section.Key == "Section.StudentNetwork");
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
            var before = vm.Today.Items.Count;

            vm.ToggleLanguageCommand.Execute(null);
            Pump();
            Check(vm.Today.Items.Count == before, $"rows lost: {before} -> {vm.Today.Items.Count}");
            Check(vm.Today.Items.Any(i => i == "kept row"), "user reminder disappeared");
        });

        // ---- DynamicResource plumbing ----
        Test("dynamic_resource_text_updates_on_language_switch", (vm, w) =>
        {
            var homeButton = w.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("link"));
            Check((string?)homeButton.Content == "Home", $"expected Home, got {homeButton.Content}");

            vm.ToggleLanguageCommand.Execute(null);
            Pump();
            Check((string?)homeButton.Content == "主页", $"dynamic resource did not refresh: {homeButton.Content}");
            Check(vm.Cards[1].Title == Localizer.Get("Card.Pending"), $"card title did not refresh: {vm.Cards[1].Title}");
            Check(vm.Cards[1].Title == "待办事项", $"unexpected pending title: {vm.Cards[1].Title}");
        });

        // ---- language picker: the button always shows the language the UI is in ----
        Test("language_picker_shows_current_language", (vm, w) =>
        {
            var picker = w.GetVisualDescendants().OfType<LanguagePicker>().First();
            var label = picker.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => t.Text is "EN" or "中");
            Check(label is not null, "picker label not found");
            Check(label!.Text == "EN", $"expected EN, got {label.Text}");

            vm.SetLanguageCommand.Execute("zh");
            Pump();
            Check(label.Text == "中", $"picker did not follow the UI language: {label.Text}");
            Check(vm.CurrentLanguageCode == "中", $"view-model code stale: {vm.CurrentLanguageCode}");
        });

        Test("language_menu_lists_both_languages_and_switches", (vm, w) =>
        {
            vm.ToggleLanguageMenuCommand.Execute(null);
            Pump();
            Check(vm.IsLanguageMenuOpen, "menu did not open");

            var picker = w.GetVisualDescendants().OfType<LanguagePicker>().First();
            var items = picker.GetLogicalDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("menuItem")).ToList();
            Check(items.Count == 2, $"expected 2 language menu items, got {items.Count}");

            var options = items.Select(b => b.DataContext).OfType<LanguageOption>().ToList();
            Check(options.Count == 2, "menu items are not bound to language options");
            Check(options[0].Code == "en" && options[1].Code == "zh", "menu order is wrong");
            Check(options[0].IsActive && !options[1].IsActive, "the active marker is wrong for English");

            options[1].SelectCommand.Execute(null);
            Pump();
            Check(Localizer.Current == Localizer.Chinese, "menu item did not switch the language");
            Check(!vm.IsLanguageMenuOpen, "menu stayed open after a pick");
            Check(vm.Languages.First(o => o.Code == "zh").IsActive, "active marker did not refresh");
        });

        Test("window_title_follows_language", (vm, w) =>
        {
            Check(w.Title == Localizer.Get("Window.Main.Title"), $"title not applied on open: {w.Title}");
            vm.SetLanguageCommand.Execute("zh");
            Pump();
            Check(w.Title?.Contains("沙箱") == true, $"title did not follow the language: {w.Title}");
        });

        // ---- every resource key bound in XAML must exist, and exist in both catalogs ----
        Test("xaml_binds_only_existing_resource_keys", (_, _) =>
        {
            var en = ReadLanguageFile("en");
            var tokens = ReadResourceKeys("src/CogniCustos.Ui/Styles/Tokens.axaml");
            var root = Path.GetDirectoryName(FindSourceFile("src/CogniCustos.Ui/Styles/Tokens.axaml"))!;
            var uiRoot = Path.GetFullPath(Path.Combine(root, ".."));

            var used = new HashSet<string>();
            foreach (var file in Directory.EnumerateFiles(uiRoot, "*.axaml", SearchOption.AllDirectories))
            {
                foreach (Match match in Regex.Matches(File.ReadAllText(file), @"\{DynamicResource\s+([A-Za-z0-9_.]+)\s*\}"))
                {
                    used.Add(match.Groups[1].Value);
                }
            }

            Check(used.Count > 20, $"suspiciously few DynamicResource keys found: {used.Count}");
            foreach (var key in used)
            {
                var inTokens = tokens.Contains(key);
                var inEn = en.ContainsKey(key);
                Check(inTokens || inEn, $"'{key}' is bound in XAML but defined nowhere");
                if (inEn) Check(Localizer.HasKey(key), $"'{key}' is bound but missing in the current catalog");
            }
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
            Check(!vm.MenuOpen && vm.SettingsOpen, "settings did not replace the drawer");
            Check(!vm.IsScrimVisible, "the scrim must be hidden when settings is open");
        });

        Test("drawer_stack_dims_home", (vm, _) =>
        {
            Check(Math.Abs(vm.HomeBlurRadius) < 0.01, "home must be sharp with nothing open");
            vm.ToggleDrawerCommand.Execute(null);
            Pump();
            Check(vm.IsScrimVisible, "the scrim did not appear");
            Check(vm.HomeDimOpacity > 0, "home was not dimmed");
            Check(Math.Abs(vm.HomeBlurRadius - ShellViewModel.BlurWhenDrawerOpen) < 0.01,
                "the rendered blur must follow the configured value");
            Check(!vm.IsBackdropVisible, "the reminder backdrop must not double up");
        });

        Test("drawer_stack_adds_a_second_layer_without_moving_the_first", (vm, w) =>
        {
            vm.ToggleDrawerCommand.Execute(null);
            Pump();

            var layer1 = w.GetVisualDescendants().OfType<Border>().First(b => b.Name == "MenuDrawer");
            Check(layer1.Transitions is { Count: >= 1 }, "layer 1 needs a transition");
            Check(Math.Abs(layer1.Bounds.Width - 390) < 1, $"layer 1 should be 390 wide, got {layer1.Bounds.Width}");

            vm.ExpandDrawerCommand.Execute(null);
            Pump();
            Check(vm.MenuOpen, "opening layer 2 must not close layer 1");
            Check(vm.FutureOpen, "layer 2 did not open");
            Check(Math.Abs(vm.FutureWidth - 290) < 0.1, "layer 2 should be 290 wide");

            var layer2 = w.GetVisualDescendants().OfType<Border>().First(b => b.Name == "FutureDrawer");
            Check(layer2.IsVisible, "layer 2 is not visible");
            Check(layer2.Bounds.Right <= layer1.Bounds.Left + 1, "layer 2 must sit left of layer 1");

            vm.CloseTopDrawer();
            Pump();
            Check(!vm.FutureOpen && vm.MenuOpen, "CloseTopDrawer should close only layer 2");

            vm.CloseTopDrawer();
            Pump();
            Check(!vm.MenuOpen, "a second CloseTopDrawer should close layer 1");
            Check(Math.Abs(vm.HomeBlurRadius) < 0.01, "home must be sharp again");
        });

        Test("menu_button_toggles_the_drawer_open_and_closed", (vm, w) =>
        {
            // the rail sits outside the scrim, so these are real clicks on ≡
            var rail = w.GetVisualDescendants().OfType<StackPanel>()
                .First(panel => panel.Children.OfType<Button>().Any(b =>
                    b.Classes.Contains("icon") && b.Command == vm.ToggleDrawerCommand));
            var button = rail.Children.OfType<Button>().First(b => b.Command == vm.ToggleDrawerCommand);
            var centre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), w);
            Check(centre is not null, "could not locate the menu button");

            void Click()
            {
                w.MouseDown(centre!.Value, MouseButton.Left);
                w.MouseUp(centre.Value, MouseButton.Left);
                Pump();
            }

            void Settle()
            {
                for (var i = 0; i < 20; i++)
                {
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    Thread.Sleep(16);
                }
            }

            Click();
            Check(vm.MenuOpen, "clicking ≡ did not open the drawer");

            // the fast case: right after opening, the drawer is still sliding in over the
            // rail. The rail sits above the stack (ZIndex 15) so this click must still land.
            Click();
            Check(!vm.MenuOpen, "a quick second click on ≡ did not close the drawer");

            Click();
            Check(vm.MenuOpen, "≡ did not reopen the drawer");
            Settle();
            Click();
            Settle();
            Check(!vm.MenuOpen, "clicking ≡ again did not close the drawer");
            Check(Math.Abs(vm.HomeBlurRadius) < 0.01, "home should be sharp again");

            var blurred = w.GetVisualDescendants().OfType<Grid>()
                .Select(grid => grid.Effect)
                .OfType<BlurEffect>()
                .FirstOrDefault();
            Check(blurred is not null, "the home grid lost its blur effect");
            Check(Math.Abs(blurred!.Radius) < 0.01, $"the rendered blur stayed at {blurred.Radius}");
        });

        Test("settings_has_an_appearance_gallery", (vm, w) =>
        {
            vm.ShowState("appearance");
            Pump();
            Check(vm.Settings.Selected?.Key == "Section.Appearance", "the appearance section was not selected");
            Check(vm.Settings.ShowPreviewGallery, "the gallery did not replace the placeholder");

            var buttons = w.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("preview")).ToList();
            Check(buttons.Count >= 8, $"expected the gallery buttons, got {buttons.Count}");

            var drawerButton = buttons.First(b => (string?)b.CommandParameter == "drawer");
            drawerButton.Command!.Execute(drawerButton.CommandParameter);
            Pump();
            Check(vm.MenuOpen, "a gallery button did not open its interface piece");
            Check(!vm.SettingsOpen, "the gallery should leave the settings page");
        });

        // ---- proportional scaling ----
        Test("canvas_scales_uniformly_at_1024x576", (_, w) => AssertScale(w, 0.8), 1024, 576);
        Test("canvas_scales_uniformly_at_1280x720", (_, w) => AssertScale(w, 1.0), 1280, 720);
        Test("canvas_scales_uniformly_at_1600x900", (_, w) => AssertScale(w, 1.25), 1600, 900);
        Test("canvas_scales_uniformly_at_1920x1080", (_, w) => AssertScale(w, 1.5), 1920, 1080);
        Test("canvas_scales_uniformly_at_2560x1440", (_, w) => AssertScale(w, 2.0), 2560, 1440);
        Test("canvas_letterboxes_at_4x3", (_, w) => AssertScale(w, 0.8), 1024, 768);

        Console.WriteLine(_failures == 0 ? "ALL PASS" : $"{_failures} FAILURE(S)");
        return _failures == 0 ? 0 : 1;
    }

    // ---------- assertions ----------

    /// <summary>The shell always lays out on the fixed 1280x720 canvas and is scaled by the Viewbox.</summary>
    private static void AssertScale(ShellWindow window, double expected)
    {
        var view = window.GetVisualDescendants().OfType<ShellView>().First();
        Check(Math.Abs(view.Bounds.Width - 1280) < 0.5, $"canvas width changed: {view.Bounds.Width}");
        Check(Math.Abs(view.Bounds.Height - 720) < 0.5, $"canvas height changed: {view.Bounds.Height}");

        var matrix = view.TransformToVisual(window);
        Check(matrix is not null, "no transform to the window");
        var m = matrix!.Value;
        Check(Math.Abs(m.M11 - expected) < 0.002, $"scale should be {expected}, got {m.M11:F4}");
        Check(Math.Abs(m.M22 - expected) < 0.002, $"vertical scale should be {expected}, got {m.M22:F4}");
    }

    // ---------- plumbing ----------

    private static void Test(string name, Action<ShellViewModel, ShellWindow> body,
        int width = 1280, int height = 720)
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

    /// <summary>Keys declared in any resource dictionary file (x:Key).</summary>
    private static HashSet<string> ReadResourceKeys(string relativePath)
    {
        var text = File.ReadAllText(FindSourceFile(relativePath));
        return Regex.Matches(text, "x:Key=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
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
