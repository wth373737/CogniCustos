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
            Check(vm.Today.Items[^1].Text == "typed reminder", "wrong reminder text");
            Check(vm.InputText.Length == 0, "input was not cleared");
            Check(vm.HasToast, "no status line after submit");
            Check(vm.Cards[0].Items.All(item => item.Title != "typed reminder"),
                "the calendar card must not receive today's entries");
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
            Check(calendar.Items.All(item => item.HasWhen),
                "the calendar card holds plan entries with a date/time line, not today's reminders");
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
            ShellScenarios.Apply(vm, "today-list-long");
            Pump();

            var drawer = w.GetVisualDescendants().OfType<Border>().First(b => b.Name == "ReminderDrawer");
            var list = w.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "ReminderList");

            Check(Math.Abs(drawer.MaxHeight - 274) < 0.1, $"drawer cap should be 274, got {drawer.MaxHeight}");
            Check(drawer.Bounds.Height <= 274.5, $"drawer taller than its cap: {drawer.Bounds.Height}");
            Check(list.ItemsPanelRoot is VirtualizingStackPanel, "the reminder list must virtualise");
            Check(vm.Today.Items.Count == 12, "the long fixture should have 12 rows");
            // Virtualised: the panel must be the virtualising one (how many rows are
            // realised depends on when the scroll host measures, so that is not asserted).
            var realised = list.GetVisualDescendants().OfType<ListBoxItem>().Count();
            Check(realised > 0, "no reminder rows were realised at all");
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

        Test("card_chevrons_page_the_body_instead_of_collapsing", (vm, w) =>
        {
            // the arrows must not be a collapse control at all
            Check(typeof(CardViewModel).GetProperty("IsCollapsed") is null, "cards can still collapse");

            // make the third card overflow, then page it with the two chevrons
            var card = vm.Cards[2];
            for (var i = 0; i < 14; i++) card.AddCommand.Execute(null);
            Pump();
            for (var i = 0; i < 8; i++)
            {
                Dispatcher.UIThread.RunJobs();
                System.Threading.Thread.Sleep(16);
            }

            var scroll = w.GetVisualDescendants().OfType<ScrollViewer>()
                .Last(sv => sv.Name == "CardScrollViewer");
            Check(scroll.Extent.Height > scroll.Viewport.Height,
                $"the fixture did not overflow ({scroll.Extent.Height} <= {scroll.Viewport.Height})");

            var root = scroll.GetVisualAncestors().OfType<Control>()
                .First(control => control.Name == "CardRoot");
            var buttons = root.GetVisualDescendants().OfType<Button>().ToList();
            var up = buttons.First(button => button.Name == "CardScrollUp");
            var down = buttons.First(button => button.Name == "CardScrollDown");

            Check(!up.IsEnabled, "the up arrow must be dead at the top");
            Check(down.IsEnabled, "the down arrow must be live while there is more below");

            down.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Pump();
            Check(scroll.Offset.Y > 0, "the down chevron did not scroll the card");
            Check(up.IsEnabled, "the up arrow did not come alive after scrolling");

            var afterDown = scroll.Offset.Y;
            up.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Pump();
            Check(scroll.Offset.Y < afterDown, "the up chevron did not scroll back");
        });

        Test("added_rows_use_current_language_and_are_not_retranslated", (vm, _) =>
        {
            var card = vm.Cards[2];
            card.AddCommand.Execute(null);
            var englishLabel = Localizer.Get("Card.Row.New");
            Check(card.Items[^1].Title == englishLabel, "label must come from resources");
            Check(card.Items.Count == 2, $"unexpected seed count: {card.Items.Count}");

            vm.ToggleLanguageCommand.Execute(null);
            Pump();
            // Seed rows are re-localised; a row the user added is user data and keeps its text.
            Check(card.Items.Count == 2, $"row count changed on language switch: {card.Items.Count}");
            Check(card.Items[^1].Title == englishLabel, "user row must not be retranslated");

            card.AddCommand.Execute(null);
            var chineseLabel = Localizer.Get("Card.Row.New");
            Check(chineseLabel != englishLabel, "resources did not switch");
            Check(card.Items[^1].Title == chineseLabel, "new row must use the current language");
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
            Check(vm.Today.Items.Any(i => i.Text == "kept row"), "user reminder disappeared");
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

        // ---- settings: the Model Configuration form ----
        Test("model_section_shows_the_form_instead_of_the_placeholder", (vm, _) =>
        {
            vm.Settings.Selected = vm.Settings.Sections.First(section => section.Key == "Section.Model");
            Pump();
            Check(vm.Settings.ShowModelConfiguration, "the model form is not shown");
            Check(!vm.Settings.ShowPlaceholder, "the placeholder still shows over the form");
            Check(!vm.Settings.ShowPreviewGallery, "the appearance gallery shows on the model section");

            vm.Settings.Selected = vm.Settings.Sections.First(section => section.Key == "Section.General");
            Pump();
            Check(!vm.Settings.ShowModelConfiguration, "the model form leaked into General");
            Check(vm.Settings.ShowPlaceholder, "General lost its placeholder");
        });

        Test("model_field_edits_by_double_click_and_commits", (vm, _) =>
        {
            var field = vm.Settings.Model.ModelName;
            Check(field.IsReadOnly, "the field must start read-only");

            field.BeginEdit();
            Pump();
            Check(field.IsEditing, "double-click did not enter edit mode");
            Check(field.EditText == "deepseek-flash", "the editor did not start from the current value");

            field.EditText = "deepseek-reasoner";
            field.Commit();
            Pump();
            Check(!field.IsEditing, "the field stayed in edit mode");
            Check(field.Value == "deepseek-reasoner", "the new value was not committed");
            Check(field.DisplayValue == "deepseek-reasoner", "the row does not show the new value");
        });

        Test("model_field_escape_restores_the_old_value", (vm, _) =>
        {
            var field = vm.Settings.Model.ModelName;
            field.BeginEdit();
            field.EditText = "thrown away";
            field.Cancel();
            Pump();
            Check(!field.IsEditing, "the field stayed in edit mode");
            Check(field.Value == "deepseek-flash", "cancel changed the value");
            Check(field.EditText == "deepseek-flash", "the editor kept the abandoned text");
        });

        Test("model_api_is_masked_and_never_shown", (vm, _) =>
        {
            var api = vm.Settings.Model.ApiKey;
            Check(api.DisplayValue != api.Value, "the API key is shown in clear text");
            Check(api.DisplayValue.All(c => c == '\u2022'), "the API row is not a run of dots");
        });

        Test("model_actions_open_dialogs", (vm, _) =>
        {
            // "Advanced settings" now opens a real panel instead of a placeholder dialog.
            vm.Settings.Model.ToggleAdvancedCommand.Execute(null);
            Pump();
            Check(vm.Advanced.IsOpen, "advanced settings did not open the panel");
            Check(!vm.Dialog.IsOpen, "advanced settings must not open a dialog any more");
            vm.Advanced.CloseCommand.Execute(null);
            Pump();

            vm.Settings.Model.DeleteCommand.Execute(null);
            Pump();
            Check(vm.Dialog.Kind == DialogKind.LocalModelDelete, "delete did not ask for confirmation");

            vm.Dialog.Buttons.First(button => button.Primary).Command.Execute(null);
            Pump();
            Check(!vm.Dialog.IsOpen, "the dialog stayed open after deleting");
            Check(vm.Settings.Model.LocalStatus.Length > 0, "the row lost its status after deleting");
            Check(vm.Toast.Length > 0, "deleting did not report anything");
        });

        Test("model_form_labels_follow_the_language", (vm, _) =>
        {
            var englishLabel = vm.Settings.Model.ModelName.Label;

            vm.ToggleLanguageCommand.Execute(null);
            Pump();

            Check(vm.Settings.Model.ModelName.Label != englishLabel, "field labels did not switch language");
            Check(vm.Settings.Model.PrimaryHeader.Length > 0, "the section header went blank");
        });

        // ---- settings: the Ollama switch ----
        Test("ollama_window_carries_the_model_name_to_the_store", (_, _) =>
        {
            var source = new RecordingSource();
            var shell = new ShellViewModel(source);
            Pump();

            shell.Settings.Model.OpenOllamaCommand.Execute(null);
            Pump();
            Check(shell.Settings.Model.IsOllamaWindowOpen, "the Ollama window did not open");
            Check(shell.Settings.Model.OllamaAddress == "http://127.0.0.1:11434",
                "the address must be fixed");
            Check(shell.Settings.Model.OllamaModelText.Length > 0, "the model name box started empty");

            shell.Settings.Model.OllamaModelText = "llama3.1:8b";
            shell.Settings.Model.EnableOllamaCommand.Execute(null);
            Pump();

            Check(source.OllamaSwitches.Count == 1 && source.OllamaSwitches[0] == "llama3.1:8b",
                "the model name did not reach the store");
            Check(!shell.Settings.Model.IsOllamaWindowOpen, "the window stayed open after switching");
            Check(shell.Settings.Model.LocalStatus.Contains("qwen2.5:7b"),
                $"the row does not show the new status: '{shell.Settings.Model.LocalStatus}'");
        });

        Test("ollama_switch_failure_keeps_the_window_open", (_, _) =>
        {
            var source = new RefusingSource();
            var shell = new ShellViewModel(source);
            Pump();

            shell.Settings.Model.OpenOllamaCommand.Execute(null);
            Pump();
            shell.Settings.Model.OllamaModelText = "nope";
            shell.Settings.Model.EnableOllamaCommand.Execute(null);
            Pump();

            Check(shell.Settings.Model.IsOllamaWindowOpen, "a refused switch closed the window");
            Check(shell.Toast.Contains("refused"), $"the refusal was not shown: '{shell.Toast}'");
        });

        Test("check_downloads_the_model_and_updates_the_row", (_, _) =>
        {
            var source = new RecordingSource();
            var shell = new ShellViewModel(source);
            Pump();

            shell.Settings.Model.CheckCommand.Execute(null);
            for (var i = 0; i < 12; i++)
            {
                Dispatcher.UIThread.RunJobs();
                System.Threading.Thread.Sleep(16);
            }

            Check(source.InstallCalls == 1, $"Check did not ask the store to install (calls={source.InstallCalls})");
            Check(!shell.Settings.Model.IsBusy, "the row stayed busy after installing");
            Check(!shell.Settings.Model.HasInstallNote,
                $"progress text was left behind: '{shell.Settings.Model.InstallNote}'");
            Check(shell.Settings.Model.LocalStatus.Contains("qwen2.5:7b"),
                $"the row does not show the installed model: '{shell.Settings.Model.LocalStatus}'");
        });

        Test("gpu_toggle_reaches_the_store_and_updates_the_row", (_, _) =>
        {
            var source = new RecordingSource();
            var shell = new ShellViewModel(source);
            Pump();

            Check(!shell.Settings.Model.GpuOn, "hardware acceleration started enabled");

            shell.Settings.Model.ToggleGpuCommand.Execute(null);
            Pump();

            Check(source.Gpu, "the switch did not reach the store");
            Check(shell.Settings.Model.GpuOn, "the row did not follow the store");
            Check(shell.Settings.Model.GpuStateLabel.Length > 0, "the row lost its state label");
        });

        Test("committing_a_primary_field_saves_the_model", (_, _) =>
        {
            var source = new RecordingSource();
            var shell = new ShellViewModel(source);
            Pump();

            shell.Settings.Model.ModelName.BeginEditCommand.Execute(null);
            Pump();
            shell.Settings.Model.ModelName.EditText = "qwen3-max";
            shell.Settings.Model.ModelName.Commit();
            Pump();

            // editing is not saving: the form says so, and nothing reaches the store yet
            Check(shell.Settings.Model.HasUnsavedChanges, "editing did not mark the form as unsaved");
            Check(source.CloudModel != "qwen3-max", "the edit was written before Save was pressed");
            Check(shell.Settings.Model.SaveCommand.CanExecute(null), "Save is not available for a changed form");

            shell.Settings.Model.SaveCommand.Execute(null);
            Pump();

            Check(source.CloudModel == "qwen3-max", $"Save did not reach the store: '{source.CloudModel}'");
            Check(!shell.Settings.Model.HasUnsavedChanges, "the form still claims unsaved changes after saving");
            Check(shell.Settings.Model.SaveStatus.Contains("qwen3-max"), "the inline save line is empty");
            Check(shell.Settings.Model.CloudStatus.Length > 0, "the status line stayed empty");
        });

        Test("email_account_add_and_remove_reach_the_store", (_, _) =>
        {
            var source = new RecordingSource();
            var shell = new ShellViewModel(source);
            Pump();

            shell.Settings.EmailRetrieval.Address = "me@usm.my";
            shell.Settings.EmailRetrieval.Host = "imap.usm.my";
            shell.Settings.EmailRetrieval.Port = "993";
            shell.Settings.EmailRetrieval.Secret = "app-password";
            shell.Settings.EmailRetrieval.AddCommand.Execute(null);
            Pump();

            Check(source.Mail.Count == 1, $"the account did not reach the store ({source.Mail.Count})");
            Check(shell.Settings.EmailRetrieval.Accounts.Count == 1, "the list did not refresh");
            Check(shell.Settings.EmailRetrieval.Secret.Length == 0, "the password box was not cleared");

            shell.Settings.EmailRetrieval.Accounts[0].DeleteCommand.Execute(null);
            Pump();
            Check(source.Mail.Count == 0, "removing the account did not reach the store");
        });

        Test("web_source_add_and_remove_reach_the_store", (_, _) =>
        {
            var source = new RecordingSource();
            var shell = new ShellViewModel(source);
            Pump();

            shell.Settings.WebBrowsing.NewUrl = "https://elearning.usm.my/notices";
            shell.Settings.WebBrowsing.AddCommand.Execute(null);
            Pump();

            Check(source.Pages.Count == 1, $"the page did not reach the store ({source.Pages.Count})");
            Check(shell.Settings.WebBrowsing.Sources.Count == 1, "the list did not refresh");

            shell.Settings.WebBrowsing.Sources[0].DeleteCommand.Execute(null);
            Pump();
            Check(source.Pages.Count == 0, "removing the page did not reach the store");
        });

        Test("thunderbird_card_status_and_rescan_reach_the_store", (_, _) =>
        {
            var source = new RecordingSource();
            var shell = new ShellViewModel(source);
            Pump();

            Check(shell.Settings.EmailRetrieval.ThunderbirdStatus.Length > 0, "the status line is empty");
            Check(shell.Settings.EmailRetrieval.UseThunderbird, "Thunderbird started disabled");

            shell.Settings.EmailRetrieval.RescanCommand.Execute(null);
            Pump();
            Check(source.Rescans >= 2, "rescan did not ask the store");

            shell.Settings.EmailRetrieval.ToggleThunderbirdCommand.Execute(null);
            Pump();
            Check(!source.ThunderbirdEnabled, "the toggle did not reach the store");
            Check(!shell.Settings.EmailRetrieval.UseThunderbird, "the card did not follow the store");
        });

        Test("mail_folder_switch_reaches_the_store", (_, _) =>
        {
            var source = new RecordingSource();
            var shell = new ShellViewModel(source);
            Pump();

            Check(shell.Settings.EmailRetrieval.MailFolders.Count == 2, "the folder list did not load");

            var gmail = shell.Settings.EmailRetrieval.MailFolders[1];
            Check(gmail.Enabled, "the folder started off");

            gmail.Enabled = false;
            Pump();

            Check(source.Folders[1].Enabled == false, "the switch did not reach the store");
            Check(!shell.Settings.EmailRetrieval.MailFolders[1].Enabled, "the row came back on");
        });

        Test("only_one_mailbox_keeps_it_and_drops_the_rest", (_, _) =>
        {
            var source = new RecordingSource();
            source.Folders.Add(new("imap.gmail-1.com", "INBOX", "wutianhao373737@gmail.com", "imap.gmail.com", 107.6, true));

            var shell = new ShellViewModel(source);
            Pump();

            Check(shell.Settings.EmailRetrieval.Mailboxes.Count == 2, "the mailbox groups did not build");

            var target = shell.Settings.EmailRetrieval.Mailboxes.First(m => m.Mailbox == "wutianhao@student.usm.my");
            target.OnlyCommand.Execute(null);
            Pump();

            Check(source.OnlyMailbox == "wutianhao@student.usm.my", $"only-mailbox did not reach the store: '{source.OnlyMailbox}'");
            Check(shell.Settings.EmailRetrieval.Mailboxes.Count(m => m.Enabled) == 1,
                "more than one mailbox stayed on");
        });

        Test("drawer_mail_button_reads_and_relists", (_, _) =>
        {
            var source = new RecordingSource();
            var shell = new ShellViewModel(source);
            Pump();

            shell.CheckMailCommand.Execute(null);
            for (var i = 0; i < 10; i++)
            {
                Dispatcher.UIThread.RunJobs();
                System.Threading.Thread.Sleep(16);
            }

            Check(source.FetchCalls == 1, $"the mail button did not fetch (calls={source.FetchCalls})");
            Check(shell.Toast.Contains("fetched"), $"the result was not reported: '{shell.Toast}'");
        });

        // ---- reasoning record list and thought trace (two independent entries) ----
        Test("reasoning_row_opens_the_record_list", (vm, _) =>
        {
            Check(!vm.Reasoning.IsAnyOpen, "a panel is open before anything was clicked");

            vm.Reasoning.OpenListCommand.Execute(null);
            Pump();

            Check(vm.Reasoning.IsListOpen, "the record list did not open");
            Check(!vm.Reasoning.IsTrajectoryOpen, "the trace opened as well");
            Check(vm.Reasoning.Groups.Count >= 3, "the list has no day groups");
            Check(vm.Reasoning.Groups[^1].IsExpanded, "today's group must start expanded");
            Check(vm.Reasoning.Groups[^1].Records.Count > 0, "today's group has no records");
            Check(vm.Reasoning.Groups[0].Chevron == "\uE70D", "a collapsed group must show a down chevron");
            Check(vm.Reasoning.Groups[^1].Chevron == "\uE70E", "an expanded group must show an up chevron");
        });

        Test("reasoning_groups_collapse_and_expand", (vm, _) =>
        {
            var group = vm.Reasoning.Groups[0];
            Check(!group.IsExpanded, "the oldest group should start collapsed");

            group.ToggleCommand.Execute(null);
            Pump();
            Check(group.IsExpanded, "the group did not expand");

            group.ToggleCommand.Execute(null);
            Pump();
            Check(!group.IsExpanded, "the group did not collapse again");
        });

        Test("the_bottom_chevron_opens_the_thought_trace", (vm, _) =>
        {
            vm.Reasoning.ToggleTrajectoryCommand.Execute(null);
            Pump();

            Check(vm.Reasoning.IsTrajectoryOpen, "the trace did not open");
            Check(!vm.Reasoning.IsListOpen, "the record list should not be open at the same time");
            Check(vm.Reasoning.TrajectoryText.Length > 0, "the trace is empty");
            Check(vm.Reasoning.TrajectoryText.Contains("think:"), "the trace does not look like a thought stream");
            Check(vm.Reasoning.TrajectoryRecordTitle.Length > 0, "the trace has no record title");

            vm.Reasoning.ToggleTrajectoryCommand.Execute(null);
            Pump();
            Check(!vm.Reasoning.IsTrajectoryOpen, "the trace did not close");
        });

        Test("the_two_reasoning_panels_are_exclusive", (vm, _) =>
        {
            vm.Reasoning.OpenListCommand.Execute(null);
            Pump();
            Check(vm.Reasoning.IsListOpen, "the list did not open");

            vm.Reasoning.ToggleTrajectoryCommand.Execute(null);
            Pump();
            Check(vm.Reasoning.IsTrajectoryOpen && !vm.Reasoning.IsListOpen,
                "opening the trace must close the list");

            vm.Reasoning.OpenListCommand.Execute(null);
            Pump();
            Check(vm.Reasoning.IsListOpen && !vm.Reasoning.IsTrajectoryOpen,
                "opening the list must close the trace");

            vm.Reasoning.CloseCommand.Execute(null);
            Pump();
            Check(!vm.Reasoning.IsAnyOpen, "closing did not hide both panels");
        });

        Test("reasoning_titles_follow_the_language", (vm, _) =>
        {
            var englishTitle = vm.Reasoning.ListTitle;
            var englishToday = vm.Reasoning.Groups[^1].Header;

            vm.ToggleLanguageCommand.Execute(null);
            Pump();

            Check(vm.Reasoning.ListTitle != englishTitle, "the list title did not switch language");
            Check(vm.Reasoning.Groups[^1].Header != englishToday, "today's header did not switch language");
        });

        // ---- fixture states live outside the shell ----
        Test("scenarios_drive_the_shell_from_the_outside", (vm, _) =>
        {
            ShellScenarios.Apply(vm, "expanded");
            Pump();
            Check(vm.MenuOpen && vm.FutureOpen, "the expanded drawer fixture did not apply");

            ShellScenarios.Apply(vm, "reasoning");
            Pump();
            Check(vm.Reasoning.IsListOpen, "the reasoning fixture did not apply");
            Check(!vm.MenuOpen, "the previous fixture was not reset");

            ShellScenarios.Apply(vm, "settings-model");
            Pump();
            Check(vm.SettingsOpen, "the settings fixture did not open the page");
            Check(vm.Settings.ShowModelConfiguration, "the settings fixture did not select the model section");

            ShellScenarios.Apply(vm, "dialog-incoming");
            Pump();
            Check(vm.Dialog.IsOpen && vm.Dialog.Kind == DialogKind.IncomingDecision,
                "the dialog fixture did not open the decision dialog");
        });

        Test("dialog_origin_decides_what_confirm_means", (_, _) =>
        {
            var source = new RecordingSource();
            var shell = new ShellViewModel(source);
            Pump();

            // from a note: "let AI handle it" must report an edit, not a confirmation
            var note = shell.Cards[2].Items[0];
            note.Command!.Execute(note);
            Pump();
            shell.Dialog.Buttons.First(button => button.Text == Localizer.Get("Dialog.AiHandle"))
                .Command.Execute(null);
            Pump();
            shell.Dialog.ComposerText = "turn this into a plan";
            shell.Dialog.Buttons.First(button => button.Primary).Command.Execute(null);
            Pump();

            Check(source.Confirmed.Count == 0, "a note flow was reported as an incoming confirmation");
            Check(source.Edited.Count == 1, "the note flow did not report an edit");
            Check(!shell.Dialog.IsOpen, "the dialog stayed open");

            // and cancelling leaves the source untouched
            var incoming = shell.Cards[1].Items[0];
            incoming.Command!.Execute(incoming);
            Pump();
            shell.Dialog.CancelCommand.Execute(null);
            Pump();
            Check(!shell.Dialog.IsOpen, "cancel did not close the dialog");
            Check(source.Confirmed.Count == 0 && source.Ignored.Count == 0,
                "cancel reported a decision to the data source");
        });

        Test("a_refused_confirmation_leaves_the_board_untouched", (_, _) =>
        {
            var source = new RefusingSource();
            var shell = new ShellViewModel(source);
            Pump();

            var planBefore = shell.Cards[0].Items.Count;
            var lineBefore = shell.Today.Items.Count;
            var pending = shell.Cards[1].Items[0];

            pending.Command!.Execute(pending);
            Pump();
            shell.Dialog.Buttons.First(button => button.Primary).Command.Execute(null);
            Pump();

            Check(!shell.Dialog.IsOpen, "the dialog stayed open after a refused write");
            Check(shell.Cards[0].Items.Count == planBefore, "a refused write still created a plan row");
            Check(shell.Today.Items.Count == lineBefore, "a refused write still added a reminder");
            Check(shell.Cards[1].Items.Count == 1, "a refused write removed the incoming item");
            Check(shell.Toast.Contains("refused"), $"the refusal was not surfaced: '{shell.Toast}'");
        });

        // ---- the data-source seam (what the runnable sample plugs into) ----
        Test("shell_actions_reach_the_data_source", (_, _) =>
        {
            var source = new RecordingSource();
            var shell = new ShellViewModel(source);
            Pump();

            Check(shell.Cards[1].Items.Count == 1, "the pending card did not come from the source");
            var item = shell.Cards[1].Items[0];

            item.Command!.Execute(item);
            Pump();
            shell.Dialog.Buttons.First(button => button.Primary).Command.Execute(null);
            Pump();
            Check(source.Confirmed.Count == 1, "confirm did not reach the data source");
            Check(source.Confirmed[0].Ref == "p1", "the wrong item was confirmed");

            var note = shell.Cards[2].Items[0];
            note.Command!.Execute(note);
            Pump();
            shell.Dialog.Buttons.First(button => button.Text == Localizer.Get("Dialog.Edit"))
                .Command.Execute(null);
            Pump();
            shell.Dialog.EditorText = "edited";
            shell.Dialog.Buttons.First(button => button.Primary).Command.Execute(null);
            Pump();
            Check(source.Edited.Count == 1 && source.Edited[0].Text == "edited",
                "editing did not reach the data source");
        });

        // ---- reminder line: normal vs urgent ----
        Test("reminder_line_marks_todays_urgent_items", (vm, _) =>
        {
            Check(vm.Today.Items.Any(item => item.Urgent), "no urgent reminder in the seeds");
            Check(vm.Today.Items.Any(item => !item.Urgent), "no ordinary reminder in the seeds");
        });

        // ---- incoming item flow: confirm / ignore / more options ----
        Test("incoming_item_confirm_moves_it_into_the_plan_and_the_line", (vm, _) =>
        {
            var pending = vm.Cards[1];
            var item = pending.Items[0];
            var planBefore = vm.Cards[0].Items.Count;
            var lineBefore = vm.Today.Items.Count;

            item.Command!.Execute(item);
            Pump();
            Check(vm.Dialog.IsOpen && vm.Dialog.Kind == DialogKind.IncomingDecision,
                "clicking an incoming item did not open the decision dialog");

            var confirm = vm.Dialog.Buttons.First(button => button.Primary);
            confirm.Command.Execute(null);
            Pump();

            Check(!vm.Dialog.IsOpen, "the dialog stayed open after confirming");
            Check(!pending.Items.Contains(item), "the confirmed item stayed in the pending card");
            Check(vm.Cards[0].Items.Count == planBefore + 1, "the plan card did not receive the item");
            Check(vm.Cards[0].Items[^1].Title == item.Title, "the plan entry has the wrong title");
            Check(vm.Cards[0].Items[^1].When == item.When, "the plan entry lost its date/time line");
            Check(vm.Today.Items.Count == lineBefore + 1, "the reminder line did not receive the item");
        });

        Test("incoming_item_ignore_asks_first", (vm, _) =>
        {
            var pending = vm.Cards[1];
            var item = pending.Items[0];

            item.Command!.Execute(item);
            Pump();
            var ignore = vm.Dialog.Buttons.First(button => !button.Primary
                && button.Text == Localizer.Get("Dialog.Ignore"));
            ignore.Command.Execute(null);
            Pump();
            Check(vm.Dialog.IsOpen && vm.Dialog.Kind == DialogKind.IgnoreConfirm,
                "ignore must ask for confirmation first");
            Check(pending.Items.Contains(item), "the item was removed before the confirmation");

            var yes = vm.Dialog.Buttons.First(button => button.Primary);
            yes.Command.Execute(null);
            Pump();
            Check(!vm.Dialog.IsOpen, "the dialog stayed open after ignoring");
            Check(!pending.Items.Contains(item), "the ignored item is still in the pending card");
        });

        Test("incoming_item_more_options_shows_the_composer_and_uses_the_typed_text", (vm, _) =>
        {
            var pending = vm.Cards[1];
            var item = pending.Items[0];

            item.Command!.Execute(item);
            Pump();
            var more = vm.Dialog.Buttons.First(button => !button.Primary
                && button.Text == Localizer.Get("Dialog.More"));
            more.Command.Execute(null);
            Pump();
            Check(vm.Dialog.Kind == DialogKind.Composer, "more options did not open the composer");
            Check(vm.Dialog.ShowComposer, "the composer box is not visible");

            vm.Dialog.ComposerText = "move it to next Tuesday";
            var send = vm.Dialog.Buttons.First(button => button.Primary);
            send.Command.Execute(null);
            Pump();

            Check(!vm.Dialog.IsOpen, "the composer stayed open after sending");
            Check(vm.Cards[0].Items[^1].Title == "move it to next Tuesday",
                "the typed instruction did not become the plan entry");
            Check(!pending.Items.Contains(item), "the source item was not consumed");
        });

        // ---- note flow: edit or let AI handle it ----
        Test("note_click_offers_edit_or_ai", (vm, _) =>
        {
            var note = vm.Cards[2];
            var item = note.Items[0];

            item.Command!.Execute(item);
            Pump();
            Check(vm.Dialog.Kind == DialogKind.NoteChoice, "clicking the note did not open the choice dialog");
            Check(vm.Dialog.Buttons.Any(button => button.Text == Localizer.Get("Dialog.Edit")), "no edit button");
            Check(vm.Dialog.Buttons.Any(button => button.Text == Localizer.Get("Dialog.AiHandle")), "no AI button");
        });

        Test("note_edit_saves_the_new_text", (vm, _) =>
        {
            var note = vm.Cards[2];
            var item = note.Items[0];

            item.Command!.Execute(item);
            Pump();
            vm.Dialog.Buttons.First(button => button.Text == Localizer.Get("Dialog.Edit"))
                .Command.Execute(null);
            Pump();
            Check(vm.Dialog.Kind == DialogKind.NoteEdit, "edit did not open the editor");
            Check(vm.Dialog.ShowEditor, "the editor is not visible");

            vm.Dialog.EditorText = "rewritten note";
            vm.Dialog.Buttons.First(button => button.Primary).Command.Execute(null);
            Pump();
            Check(!vm.Dialog.IsOpen, "the editor stayed open after saving");
            Check(item.Title == "rewritten note", "the note text was not updated");
        });

        Test("note_ai_handle_opens_the_composer", (vm, _) =>
        {
            var item = vm.Cards[2].Items[0];
            item.Command!.Execute(item);
            Pump();
            vm.Dialog.Buttons.First(button => button.Text == Localizer.Get("Dialog.AiHandle"))
                .Command.Execute(null);
            Pump();
            Check(vm.Dialog.Kind == DialogKind.Composer, "AI handle did not open the composer");
            Check(vm.Dialog.ComposerText == item.Title, "the composer should start from the note text");
        });

        // ---- calendar window ----
        Test("calendar_opens_from_the_card_date", (vm, w) =>
        {
            Check(!vm.Calendar.IsVisible, "the calendar starts closed");

            var dateButton = w.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(b => b.Classes.Contains("cardDate"));
            Check(dateButton is not null, "the first card has no clickable date");

            dateButton!.Command!.Execute(dateButton.CommandParameter);
            Pump();
            Check(vm.Calendar.IsVisible, "the date did not open the calendar");

            var today = DateTime.Today;
            Check(vm.Calendar.Year == today.Year && vm.Calendar.Month == today.Month,
                $"the calendar opened on {vm.Calendar.Year}-{vm.Calendar.Month}, expected {today.Year}-{today.Month}");
            Check(vm.Calendar.Weeks.Count == 6, $"expected 6 week rows, got {vm.Calendar.Weeks.Count}");
            Check(vm.Calendar.Weeks.All(week => week.Days.Count == 7), "every week row must hold 7 cells");
            Check(vm.Calendar.Weeks.SelectMany(week => week.Days).Any(day => day.IsToday),
                "today must be marked in the grid");
            Check(vm.Calendar.Weeks.SelectMany(week => week.Days).Count(day => day.InMonth) is >= 28 and <= 31,
                "the shown month must contribute 28..31 days");
        });

        Test("calendar_minimise_restore_and_close", (vm, _) =>
        {
            vm.Calendar.Open();
            Pump();
            var expanded = vm.Calendar.WindowHeight;
            Check(expanded > 200, $"expanded height looks wrong: {expanded}");
            Check(vm.Calendar.BodyVisible, "the body should be visible when open");

            vm.Calendar.ToggleMinimizeCommand.Execute(null);
            Pump();
            Check(vm.Calendar.IsMinimized && !vm.Calendar.BodyVisible, "minimise did not hide the body");
            Check(vm.Calendar.WindowHeight < expanded, "the minimised window must be shorter");

            vm.Calendar.ToggleMinimizeCommand.Execute(null);
            Pump();
            Check(!vm.Calendar.IsMinimized && vm.Calendar.BodyVisible, "restore did not bring the body back");

            vm.Calendar.CloseCommand.Execute(null);
            Pump();
            Check(!vm.Calendar.IsVisible, "close did not hide the window");
        });

        Test("calendar_month_and_year_navigation", (vm, _) =>
        {
            vm.Calendar.Open();
            Pump();
            var year = vm.Calendar.Year;
            var month = vm.Calendar.Month;

            vm.Calendar.NextMonthCommand.Execute(null);
            Pump();
            Check(vm.Calendar.Month == (month == 12 ? 1 : month + 1), "next month is wrong");
            Check(vm.Calendar.Year == (month == 12 ? year + 1 : year), "the year must roll over in December");

            vm.Calendar.PreviousMonthCommand.Execute(null);
            Pump();
            Check(vm.Calendar.Year == year && vm.Calendar.Month == month, "previous month did not come back");

            vm.Calendar.NextYearCommand.Execute(null);
            Pump();
            Check(vm.Calendar.Year == year + 1, "next year is wrong");
            vm.Calendar.PreviousYearCommand.Execute(null);
            Pump();
            Check(vm.Calendar.Year == year, "previous year did not come back");

            // the grid follows the month: the first in-month cell is day 1
            var firstInMonth = vm.Calendar.Weeks.SelectMany(week => week.Days).First(day => day.InMonth);
            Check(firstInMonth.Number == "1", $"first in-month cell should be 1, got {firstInMonth.Number}");
        });

        Test("calendar_drags_within_the_canvas", (vm, w) =>
        {
            vm.Calendar.Open();
            Pump();
            var startX = vm.Calendar.X;
            var startY = vm.Calendar.Y;

            var bar = w.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "CalendarTitleBar");
            var origin = bar.TranslatePoint(new Point(60, 26), w);
            Check(origin is not null, "the title bar is not in the visual tree");

            w.MouseDown(origin!.Value, MouseButton.Left);
            w.MouseMove(new Point(origin.Value.X + 90, origin.Value.Y + 60));
            w.MouseUp(new Point(origin.Value.X + 90, origin.Value.Y + 60), MouseButton.Left);
            Pump();

            Check(vm.Calendar.X > startX + 40, $"dragging did not move X: {startX} -> {vm.Calendar.X}");
            Check(vm.Calendar.Y > startY + 20, $"dragging did not move Y: {startY} -> {vm.Calendar.Y}");

            // and it cannot leave the canvas
            vm.Calendar.MoveTo(-500, -500);
            Pump();
            Check(Math.Abs(vm.Calendar.X) < 0.01 && Math.Abs(vm.Calendar.Y) < 0.01, "the window escaped the top-left corner");
            vm.Calendar.MoveTo(5000, 5000);
            Pump();
            Check(vm.Calendar.X <= 1280 - vm.Calendar.WindowWidth + 0.01, "the window escaped the right edge");
        });

        Test("settings_has_an_appearance_gallery", (vm, w) =>
        {
            ShellScenarios.Apply(vm, "appearance");
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

        // ---- point read ----
        Test("reader_opens_and_fills_summary_and_page_index", (_, _) =>
        {
            var source = new RecordingSource();
            var shell = new ShellViewModel(source);
            Pump();

            shell.Reader.OpenPreview("lecture.pdf");
            Pump();

            Check(shell.Reader.IsOpen, "the reader did not open");
            Check(shell.Reader.Title == "stub.pdf", $"wrong title '{shell.Reader.Title}'");
            Check(shell.Reader.PageCount == 5, $"wrong page count {shell.Reader.PageCount}");
            Check(shell.Reader.HasPage, "the first page was not rendered");
            Check(shell.Reader.Summary == "summary text", "the summary did not arrive");
            Check(shell.Reader.Contents.Count == 2, $"wrong index length {shell.Reader.Contents.Count}");
            Check(shell.Reader.Contents[1].Page == 3, "the index rows lost their page numbers");
        });

        Test("reader_selection_is_ordered_and_cleared_by_a_page_turn", (_, _) =>
        {
            var source = new RecordingSource();
            var shell = new ShellViewModel(source);
            Pump();

            shell.Reader.OpenPreview("lecture.pdf");
            Pump();

            // dragged bottom-right to top-left: the box must still be a box
            shell.Reader.SelectRectangle(0.7, 0.6, 0.2, 0.1);
            Pump();

            Check(shell.Reader.HasSelection, "the box did not stick");
            var (x0, y0, x1, y1) = shell.Reader.Selection;
            Check(Math.Abs(x0 - 0.2) < 1e-9 && Math.Abs(y0 - 0.1) < 1e-9
                  && Math.Abs(x1 - 0.7) < 1e-9 && Math.Abs(y1 - 0.6) < 1e-9,
                $"the box was not normalised: {x0},{y0},{x1},{y1}");
            Check(source.Reader.Selections.Count == 1, "the service was not asked for the words");
            Check(!string.IsNullOrWhiteSpace(shell.Reader.SelectionText), "no words under the box");

            shell.Reader.GoToPage(2);
            Pump();
            Check(!shell.Reader.HasSelection, "a page turn must drop the box");
            Check(shell.Reader.CurrentPage == 2, "the page did not change");
        });

        Test("reader_ask_logs_an_exchange_and_clears_the_input", (_, _) =>
        {
            var source = new RecordingSource();
            var shell = new ShellViewModel(source);
            Pump();

            shell.Reader.OpenPreview("lecture.pdf");
            Pump();
            shell.Reader.SelectRectangle(0.1, 0.1, 0.5, 0.3);
            shell.Reader.Question = "why does this matter?";
            shell.Reader.AskAsync().GetAwaiter().GetResult();
            Pump();

            Check(shell.Reader.Exchanges.Count == 1, "the answer was not logged");
            Check(shell.Reader.Exchanges[0].Question == "why does this matter?", "the question was lost");
            Check(shell.Reader.Exchanges[0].Answer == "answer for page 1", "wrong answer text");
            Check(shell.Reader.Exchanges[0].PageTag == "#1", "the log lost the page tag");
            Check(shell.Reader.Question.Length == 0, "the input box was not cleared");
            Check(source.Reader.Asked.Count == 1 && source.Reader.Asked[0].Page == 1,
                "the service was not asked about this page");
        });

        Test("reader_close_resets_and_releases_the_document", (_, _) =>
        {
            var source = new RecordingSource();
            var shell = new ShellViewModel(source);
            Pump();

            shell.Reader.OpenPreview("lecture.pdf");
            Pump();
            shell.Reader.Close();
            Pump();

            Check(!shell.Reader.IsOpen, "the reader stayed open");
            Check(source.Reader.Closed, "the document was not released");
            Check(shell.Reader.PageBitmap is null, "the page bitmap was kept");
            Check(!shell.Reader.HasSelection, "the box survived the close");
            Check(shell.Reader.Title.Length == 0, "the title survived the close");
        });

        Test("reader_open_failure_keeps_the_window_closed", (_, _) =>
        {
            var source = new RecordingSource();
            source.Reader.OpenFailure = "cannot read that file";
            var shell = new ShellViewModel(source);
            Pump();

            var failure = shell.Reader.OpenAsync("broken.pdf").GetAwaiter().GetResult();
            Pump();

            Check(failure == "cannot read that file", $"wrong failure '{failure}'");
            Check(!shell.Reader.IsOpen, "a failed open must not show the window");
        });

        Test("drawer_has_one_extra_row_for_the_reader", (_, w) =>
        {
            var tile = w.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(b => b.Name == "ReadFileTile");
            Check(tile is not null, "the reader tile is missing from the drawer");
        });

        // The page is letterboxed inside its host, so pointer coordinates must be mapped
        // against the *drawn page*, not against the host. Getting this wrong shifts every
        // selection to the left and makes the right edge unreachable (reported from a real
        // run: "the box jumps left and the right side cannot be selected").
        Test("reader_selection_uses_the_page_rect_not_the_host", (vm, w) =>
        {
            vm.ClosePanels();
            vm.Reader.OpenPreview("demo.pdf");
            Pump();

            var host = w.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PageHost");
            var image = w.GetVisualDescendants().OfType<Image>().First(i => i.Name == "PageImage");
            var hostOrigin = host.TranslatePoint(new Point(0, 0), w)!.Value;
            var pageOrigin = image.TranslatePoint(new Point(0, 0), host)!.Value;
            var page = new Rect(new Point(hostOrigin.X + pageOrigin.X, hostOrigin.Y + pageOrigin.Y),
                image.Bounds.Size);

            Check(page.Width < host.Bounds.Width - 20,
                "the page is not letterboxed here, so this test would not catch the bug it covers");

            var from = new Point(page.X + page.Width * 0.25, page.Y + page.Height * 0.30);
            var to = new Point(page.X + page.Width * 0.95, page.Y + page.Height * 0.80);

            w.MouseDown(from, MouseButton.Left);
            w.MouseMove(new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2));
            w.MouseMove(to);
            w.MouseUp(to, MouseButton.Left);
            Pump();

            Check(vm.Reader.HasSelection, "the drag produced no selection");

            var (x0, y0, x1, y1) = vm.Reader.Selection;
            Check(Math.Abs(x0 - 0.25) < 0.02, $"left edge mapped to {x0:F3}, expected 0.250");
            Check(Math.Abs(y0 - 0.30) < 0.02, $"top edge mapped to {y0:F3}, expected 0.300");
            Check(Math.Abs(x1 - 0.95) < 0.02, $"right edge mapped to {x1:F3}, expected 0.950 — the far right must be reachable");
            Check(Math.Abs(y1 - 0.80) < 0.02, $"bottom edge mapped to {y1:F3}, expected 0.800");

            // the box has to be drawn where the page is, not somewhere to the left
            var band = w.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Rectangle>().First(r => r.Name == "SelectionBand");
            var drawnLeft = Canvas.GetLeft(band) + hostOrigin.X;
            var expectedLeft = page.X + page.Width * 0.25;
            Check(Math.Abs(drawnLeft - expectedLeft) < 1.5,
                $"the box was drawn at x={drawnLeft:F1}, expected {expectedLeft:F1}");
        });

        // ---- advanced settings: prompts and parameters, read-only in P0 ----
        Test("advanced_panel_lists_prompts_and_marks_the_locked_parts", (_, _) =>
        {
            // the panel renders whatever the backing store reports, so drive it with the stub
            var source = new RecordingSource();
            var shell = new ShellViewModel(source);
            Pump();

            Check(!shell.Advanced.IsOpen, "the panel must start closed");

            shell.Settings.Model.ToggleAdvancedCommand.Execute(null);
            Pump();

            Check(shell.Advanced.IsOpen, "the advanced panel did not open");
            Check(shell.Advanced.Items.Count >= 3, $"only {shell.Advanced.Items.Count} entries");
            Check(shell.Advanced.Selected is not null, "nothing selected after opening");

            var chat = shell.Advanced.Items.First(i => i.Title == "Main chat system prompt");
            Check(chat.Rows.Count >= 2, "the chat prompt lost its segments");
            Check(chat.Rows.Any(r => r.Locked), "the protocol segment must be marked locked");
            Check(chat.Rows.Any(r => !r.Locked), "the persona segment must stay tunable");
            Check(chat.CallSite.Length > 0, "the call site is not shown");
            Check(chat.Rows.Any(r => r.Text.Contains("{\"action\":\"tool\"")),
                "the panel is not showing the text the source reports");
        });

        Test("advanced_panel_closes_and_puts_the_chevron_back", (vm, _) =>
        {
            vm.Settings.Model.ToggleAdvancedCommand.Execute(null);
            Pump();
            Check(vm.Advanced.IsOpen, "the advanced panel did not open");

            vm.Advanced.CloseCommand.Execute(null);
            Pump();

            Check(!vm.Advanced.IsOpen, "the panel did not close");
            Check(vm.Settings.Model.AdvancedChevron == "\uE70D", "the chevron stayed open");
        });

        Test("advanced_panel_edits_a_segment_and_restores_the_default", (_, _) =>
        {
            // the demo source owns a small in-memory store, so the edit flow is real
            var source = new DemoShellDataSource();
            var shell = new ShellViewModel(source);
            Pump();

            shell.Settings.Model.ToggleAdvancedCommand.Execute(null);
            Pump();

            var item = shell.Advanced.Items.First(i => i.Title == "Point read: persona");
            var row = item.Rows.First();
            Check(row.Editable, "the persona segment must be editable");
            Check(!row.Overridden, "a fresh build must not report an override");

            row.EditText = "你是一位耐心的助教，回答问题前先复述对方的疑问。";
            row.SaveCommand.Execute(null);
            Pump();

            Check(row.Status.Length > 0, "saving reported nothing");
            Check(row.Text.Contains("耐心的助教"), "the text in force did not change");
            Check(row.Overridden, "the row does not show that it was edited");

            row.ResetCommand.Execute(null);
            Pump();

            Check(!row.Overridden, "reset did not clear the override badge");
            Check(!row.Text.Contains("耐心的助教"), "reset did not bring the default back");
        });

        Test("advanced_panel_refuses_editing_a_locked_segment", (_, _) =>
        {
            var source = new DemoShellDataSource();
            var shell = new ShellViewModel(source);
            Pump();

            shell.Settings.Model.ToggleAdvancedCommand.Execute(null);
            Pump();

            var chat = shell.Advanced.Items.First(i => i.Title == "Main chat system prompt");
            var protocol = chat.Rows.First(r => r.Locked);

            Check(!protocol.Editable, "a locked segment must not offer an edit box");

            // even if a caller asks anyway, the store refuses and says why
            var result = source.SavePromptOverride(protocol.PromptKey, protocol.SegmentId, "改一下");
            Check(result.Failure is not null, "the store accepted an edit to a locked segment");
        });

        Test("cloud_save_shows_the_reason_inline_instead_of_only_a_toast", (_, _) =>
        {
            // RefusingSource fails every save; the reason must end up next to the fields
            var shell = new ShellViewModel(new RefusingSource());
            Pump();

            shell.Settings.Model.ModelName.SetValue("qwen3-max");
            shell.Settings.Model.SaveCommand.Execute(null);
            Pump();

            Check(shell.Settings.Model.HasSaveStatus, "the save failure was not shown inline");
            Check(shell.Settings.Model.SaveStatus.Contains("refused"),
                $"the inline line hides the reason: {shell.Settings.Model.SaveStatus}");
            Check(shell.Settings.Model.SaveStatusBrush.Contains("8E1B1B"), "a failure is not drawn as a failure");
        });

        Test("cloud_test_button_reports_the_round_trip", (vm, _) =>
        {
            var model = vm.Settings.Model;
            model.Url.SetValue("https://api.example.com");
            model.ModelName.SetValue("gpt-test");

            string? sent = null;
            model.CloudTestRequested += (m, u, k) => sent = m + " | " + u;

            model.TestCommand.Execute(null);

            Check(sent is not null && sent.Contains("gpt-test"), "the form values did not reach the seam");
            Check(model.IsTesting, "the row does not show that a test is running");
            Check(!model.TestCommand.CanExecute(null), "the button stays clickable while testing");

            // the host answers later; the line must show success and timing
            model.ShowTestResult(new CloudTestResult(true, "connected deepseek", 42));
            Check(!model.IsTesting, "the row is still marked as testing");
            Check(model.HasTestStatus, "no result line");
            Check(model.TestStatus.Contains("42 ms"), $"the line lost the timing: {model.TestStatus}");
            Check(model.TestStatus.Contains("connected deepseek"), "the line lost the endpoint's words");

            // and a failure must show the provider's own error, not a generic message
            model.BeginTest();
            model.ShowTestResult(new CloudTestResult(false, "云端 HTTP 401：invalid api key", 12));
            Check(model.TestStatus.Contains("401"), $"the failure line hides the reason: {model.TestStatus}");
        });

        Test("switching_language_tells_the_host_so_prompts_follow", (_, _) =>
        {
            var source = new RecordingSource();
            var shell = new ShellViewModel(source);
            Pump();

            var first = Localizer.Current;
            shell.ToggleLanguageCommand.Execute(null);
            Pump();
            Check(source.Languages.Count == 1, "the host was not told about the language change");
            Check(source.Languages[0] != first, "the host was told the language did not change");

            shell.ToggleLanguageCommand.Execute(null);
            Pump();
            Check(source.Languages.Count == 2 && source.Languages[1] == first,
                "the second change did not reach the host");

            // and the mail section must re-render, not keep the labels of the old language
            Check(shell.Settings.EmailRetrieval.AdvancedLabel.Length > 0,
                "the mail section lost its label after the switch");
        });

        Test("microphone_puts_the_spoken_words_in_the_input_box", (_, _) =>
        {
            var source = new RecordingSource { VoiceText = "明天下午三点开会" };
            var shell = new ShellViewModel(source);
            Pump();

            Check(shell.InputText.Length == 0, "the box was not empty to begin with");
            shell.MicCommand.Execute(null);
            Pump();
            Pump();

            Check(shell.InputText == "明天下午三点开会", $"the words did not land: '{shell.InputText}'");
            Check(shell.HasVoiceHint, "the press left no trace of what happened");
            Check(!shell.IsListening, "the button is still capturing after the result arrived");
        });

        Test("microphone_explains_itself_when_there_is_no_device", (_, _) =>
        {
            var source = new RecordingSource { VoiceOk = false };
            var shell = new ShellViewModel(source);
            Pump();

            shell.MicCommand.Execute(null);
            Pump();

            Check(!shell.IsListening, "it claims to be listening without a recogniser");
            Check(shell.HasVoiceHint, "no reason was given");
            Check(shell.VoiceHint.Contains("麦克风"), $"the reason is not the device: {shell.VoiceHint}");
            Check(shell.InputText.Length == 0, "the box changed although nothing was heard");
        });

        Test("microphone_is_a_toggle_and_the_second_press_stops_it", (_, _) =>
        {
            var source = new RecordingSource { VoiceBlocks = true, VoiceText = "半句话" };
            var shell = new ShellViewModel(source);
            Pump();

            // first press: capture starts and the button says so
            shell.MicCommand.Execute(null);
            Pump();
            Check(shell.IsListening, "the first press did not start listening");
            Check(shell.MicGlyph == "\uE71A", "the button does not look like a stop button");
            Check(shell.MicBackground.Contains("D9534F"), "the button did not change colour");
            Check(shell.MicCommand.CanExecute(null), "a listening button must stay clickable to stop");
            Check(SaysStop(shell.VoiceHint), $"the hint does not say how to stop: {shell.VoiceHint}");

            // second press: capture stops, and the recogniser is really cancelled
            shell.MicCommand.Execute(null);
            Thread.Sleep(120);
            Pump();

            Check(!shell.IsListening, "the second press did not stop listening");
            Check(source.VoiceCancelled, "the recogniser was never cancelled");
            Check(shell.MicGlyph == "\uE720", "the button did not go back to the microphone");
            Check(shell.MicBackground.Contains("CFCFD8"), "the colour did not go back");
            Check(SaysStop(shell.VoiceHint), $"stopping left no trace: {shell.VoiceHint}");
            Check(shell.InputText.Length == 0, "a stopped capture added text anyway");
        });

        Test("web_section_approves_a_domain_and_reads_once", (_, _) =>
        {
            var source = new RecordingSource();
            source.Pages.Add("https://elearning.usm.my/notices");
            var shell = new ShellViewModel(source);
            Pump();

            var row = shell.Settings.WebBrowsing.Sources.First();
            Check(!row.Approved, "a new page must start unapproved");
            Check(row.ApprovedLabel.Length > 0, "the row does not say whether it is allowed");

            // approving reaches the store and flips the row
            row.AllowCommand.Execute(null);
            Pump();
            Check(source.WebDomains.Contains("elearning.usm.my"),
                $"the approval did not reach the store: {string.Join(",", source.WebDomains)}");
            Check(shell.Settings.WebBrowsing.Sources.First().Approved, "the row still says not allowed");

            // reading once puts the verdict on the row
            row = shell.Settings.WebBrowsing.Sources.First();
            row.ReadOnceCommand.Execute(null);
            Pump();
            Pump();
            Check(row.HasVerdict, "reading once left no verdict");
            Check(row.Verdict.Contains("12"), $"the verdict lost the result: {row.Verdict}");
        });

        Test("web_cookie_is_held_for_the_run_and_cleared_on_demand", (_, _) =>
        {
            var source = new RecordingSource();
            source.Pages.Add("https://elearning.usm.my/notices");
            var shell = new ShellViewModel(source);
            Pump();

            shell.Settings.WebBrowsing.Sources.First().AllowCommand.Execute(null);
            Pump();

            // approving rebuilds the rows; work with the live instance from here on
            var row = shell.Settings.WebBrowsing.Sources.First();
            Check(!row.HasCookie, "a fresh domain claims to hold a cookie");

            row.CookieText = "MoodleSession=abc123";
            row.SaveCookieCommand.Execute(null);
            Pump();

            Check(source.CookieHosts.Contains("elearning.usm.my"), "the cookie did not reach the host");
            Check(shell.Settings.WebBrowsing.Sources.First().HasCookie, "the row does not show a cookie");
            Check(shell.Settings.WebBrowsing.Sources.First().CookieText.Length == 0,
                "the credential stayed on screen after saving");

            shell.Settings.WebBrowsing.Sources.First().ClearCookieCommand.Execute(null);
            Pump();
            Check(!shell.Settings.WebBrowsing.Sources.First().HasCookie, "clearing did not take effect");
            Check(!source.CookieHosts.Contains("elearning.usm.my"), "the host still holds the cookie");
        });
        Test("drawer_web_tile_fetches_and_reports_like_check_mail", (_, _) =>
        {
            var source = new RecordingSource();
            var shell = new ShellViewModel(source);
            Pump();

            shell.ToggleDrawerCommand.Execute(null);
            Pump();
            Check(shell.MenuOpen, "the drawer did not open");

            shell.CheckWebCommand.Execute(null);
            Thread.Sleep(80);
            Pump();
            Pump();

            Check(source.WebFetches == 1, $"the tile fetched {source.WebFetches} times");
            Check(!shell.MenuOpen, "the drawer stayed open over the fetch");
            Check(shell.Toast.Contains("read 2 pages"), $"the result was not reported: {shell.Toast}");
            Check(!shell.SettingsOpen, "the tile opened the settings page instead of fetching");
        });
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

    /// <summary>The hint is localised; assert on the meaning, not one language.</summary>
    private static bool SaysStop(string text) =>
        text.Contains("停止", StringComparison.Ordinal) ||
        text.Contains("stop", StringComparison.OrdinalIgnoreCase);
    private static TextBox InputBox(ShellWindow window)
        => window.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "InputBox");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

/// <summary>Records what the shell asks for, so the seam can be asserted without a runtime.</summary>
internal sealed class RecordingSource : IShellDataSource
{
    public List<CardItem> Confirmed { get; } = [];
    public List<CardItem> Ignored { get; } = [];
    public List<(CardItem Item, string Text)> Edited { get; } = [];

    public IReadOnlyList<ReminderItem> Today() => [new("ordinary", false), new("urgent", true)];

    public (string Title, string Empty, IReadOnlyList<CardItem> Items) CalendarCard() =>
        ("plan", "", [new CardItem("Planned item", "11/27 3pm – 5pm")]);

    public (string Title, string Empty, IReadOnlyList<CardItem> Items) PendingCard() =>
        ("Incoming", "", [new CardItem("Incoming item", "11/28 3pm – 5pm", "From example.org") { Ref = "p1" }]);

    public (string Title, string Empty, IReadOnlyList<CardItem> Items) NoteCard() =>
        ("Notes", "", [new CardItem("A note")]);

    public string? IncomingConfirmed(CardItem item)
    {
        Confirmed.Add(item);
        return null;
    }

    public string? IncomingIgnored(CardItem item)
    {
        Ignored.Add(item);
        return null;
    }

    public void NoteEdited(CardItem item, string text) => Edited.Add((item, text));

    public Task<string?> AskAsync(string instruction) => Task.FromResult<string?>(null);

    public string LocalModelStatus() => "qwen2.5:7b（内置）";

    public int InstallCalls { get; private set; }

    public string? InstallBuiltIn(IProgress<double> progress)
    {
        InstallCalls++;
        progress.Report(0.5);
        return null;
    }

    public string? DeleteBuiltIn(out string message)
    {
        message = "已删除";
        return null;
    }

    public string? SwitchToBuiltIn() => null;

    public bool Gpu { get; private set; }

    public (string Model, string Url, bool Configured) CloudSettings() =>
        (CloudModel ?? "deepseek-flash", CloudUrl ?? "https://api.deepseek.com", CloudModel is not null);

    public string? CloudModel { get; private set; }
    public string? CloudUrl { get; private set; }

    public string? SaveCloud(string model, string url, string apiKey)
    {
        CloudModel = model;
        CloudUrl = url;
        return null;
    }

    public List<EmailAccountInfo> Mail { get; } = [];

    public (string Status, bool Available, bool Enabled) ThunderbirdMail()
    {
        Rescans++;
        return ($"{Rescans} 个文件夹", true, ThunderbirdEnabled);
    }

    public int Rescans { get; private set; }
    public bool ThunderbirdEnabled { get; private set; } = true;

    public bool SetUseThunderbird(bool enabled)
    {
        ThunderbirdEnabled = enabled;
        return true;
    }

    public List<MailFolderInfo> Folders { get; } =
    [
        new("outlook.office365.com", "INBOX", "wutianhao@student.usm.my", "outlook.office365.com", 282.3, true),
        new("imap.gmail-1.com", "INBOX", "wutianhao373737@gmail.com", "imap.gmail.com", 107.6, true),
    ];

    public IReadOnlyList<MailFolderInfo> MailFolders() => Folders;

    public bool SetMailFolderEnabled(string account, string folder, bool enabled)
    {
        var index = Folders.FindIndex(f => f.Account == account && f.Folder == folder);
        if (index >= 0) Folders[index] = Folders[index] with { Enabled = enabled };
        return true;
    }

    public int FetchCalls { get; private set; }

    public string FetchNow()
    {
        FetchCalls++;
        return "fetched 1, proposed 1";
    }

    public string? OnlyMailbox { get; private set; }

    public bool SetOnlyMailbox(string mailbox)
    {
        OnlyMailbox = mailbox;
        for (var i = 0; i < Folders.Count; i++)
        {
            Folders[i] = Folders[i] with { Enabled = Folders[i].Mailbox == mailbox };
        }

        return true;
    }

    public void RescanMail()
    {
    }

    public IReadOnlyList<EmailAccountInfo> EmailAccounts() => Mail;

    public string? AddEmailAccount(string address, string host, int port, string secret, int lookbackDays,
        bool unreadOnly)
    {
        Mail.Add(new EmailAccountInfo(address, host, port, lookbackDays, unreadOnly));
        return null;
    }

    public bool RemoveEmailAccount(string address)
    {
        Mail.RemoveAll(a => a.Address == address);
        return true;
    }

    public (bool Ok, string Message) TestEmailAccount(string address) => (true, "connected (fake)");

    public string ImportEmailAccounts() => "imported 1";

    public List<string> Pages { get; } = [];

    public IReadOnlyList<string> WebSources() => Pages;

    public string? AddWebSource(string url)
    {
        Pages.Add(url);
        return null;
    }

    public bool RemoveWebSource(string url)
    {
        Pages.Remove(url);
        return true;
    }

    public bool GpuEnabled() => Gpu;

    public string? SetGpu(bool enabled)
    {
        Gpu = enabled;
        return null;
    }

    public List<string> OllamaSwitches { get; } = [];

    public string? EnableOllama(string model)
    {
        OllamaSwitches.Add(model);
        return null;
    }

    public IReaderService CreateReader() => Reader;

    public PromptEditResult SavePromptOverride(string promptKey, string segmentId, string text) =>
        new(null, text, true);

    public PromptEditResult ResetPromptOverride(string promptKey, string segmentId) =>
        new(null, "default text", false);

    public List<string> Languages { get; } = [];

    public void LanguageChanged(string culture) => Languages.Add(culture);
    public IReadOnlyList<string> WebAllowedDomains() => WebDomains;

    public int WebFetches { get; private set; }

    public string FetchWebNow() { WebFetches++; return "read 2 pages, 1 pending"; }
    public List<string> WebDomains { get; } = [];
    public string? AllowWebDomain(string domain) { WebDomains.Add(domain); return null; }
    public string? RevokeWebDomain(string domain) { WebDomains.Remove(domain); return null; }
    public Task<string> ReadWebOnceAsync(string url) => Task.FromResult("read 12 chars");

    public HashSet<string> CookieHosts { get; } = [];
    public string? SetWebCookie(string host, string cookieHeader) { CookieHosts.Add(host); return null; }
    public string? ClearWebCookie(string host) { CookieHosts.Remove(host); return null; }
    public bool WebCookieSet(string host) => CookieHosts.Contains(host);

    public VoiceInfo SpeechStatus() => new(VoiceOk, "system", VoiceOk ? "ok" : "没有可用的麦克风");

    public bool VoiceOk { get; set; } = true;
    public string VoiceText { get; set; } = "recorded words";

    /// <summary>When set, listening only ends when the caller cancels — as a real microphone does.</summary>
    public bool VoiceBlocks { get; set; }
    public bool VoiceCancelled { get; private set; }

    public Task<VoiceResult> ListenAsync(CancellationToken ct = default)
    {
        if (!VoiceBlocks) return Task.FromResult(new VoiceResult(true, VoiceText, "", 8, "system"));

        return BlockAsync(ct);
    }

    private async Task<VoiceResult> BlockAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            VoiceCancelled = true;
            throw;
        }

        return new VoiceResult(true, VoiceText, "", 8, "system");
    }

    public string? UseModelSpeech(bool useModel) => null;

    public Task<CloudTestResult> TestCloudAsync(string model, string url, string apiKey) =>
        Task.FromResult(string.IsNullOrWhiteSpace(url)
            ? new CloudTestResult(false, "no url", 1)
            : new CloudTestResult(true, "connected " + model, 7));
    public Task<PromptTryResult> TryPromptAsync(string promptKey) =>
        Task.FromResult(new PromptTryResult(true, "prompt text", "reply text", 5, string.Empty));
    public AdvancedSnapshot AdvancedSnapshot() => new(
    [
        new AdvancedItemData("prompt", "Prompt.Chat.Title", "Prompt.Chat.Purpose", "test",
            [
                new AdvancedRowData("Prompt.Chat.Persona", "", "persona text", false),
                new AdvancedRowData("Prompt.Chat.Protocol", "", "{\"action\":\"tool\"}", true),
            ]),
        new AdvancedItemData("parameter", "Advanced.Llama.Title", "Advanced.Llama.Purpose", "test",
            [new AdvancedRowData(null, "contextSize", "2048", false)]),
        new AdvancedItemData("prompt", "Prompt.LecturePersona.Title", "Prompt.LecturePersona.Purpose", "test",
            [new AdvancedRowData("Prompt.LecturePersona.Persona", "", "senior voice", false)]),
    ]);

    /// <summary>The stub the reader tests drive; the shell keeps the same instance.</summary>
    public StubReader Reader { get; } = new();
}

/// <summary>Refuses every decision, so the "write failed" path can be asserted.</summary>
internal sealed class RefusingSource : IShellDataSource
{
    public IReadOnlyList<ReminderItem> Today() => [new("ordinary", false)];

    public (string Title, string Empty, IReadOnlyList<CardItem> Items) CalendarCard() =>
        ("plan", "", [new CardItem("Planned item", "11/27 3pm – 5pm")]);

    public (string Title, string Empty, IReadOnlyList<CardItem> Items) PendingCard() =>
        ("Incoming", "", [new CardItem("Incoming item", "11/28 3pm – 5pm") { Ref = "p1" }]);

    public (string Title, string Empty, IReadOnlyList<CardItem> Items) NoteCard() =>
        ("Notes", "", [new CardItem("A note")]);

    public string? IncomingConfirmed(CardItem item) => "refused by the store";

    public string? IncomingIgnored(CardItem item) => "refused by the store";

    public void NoteEdited(CardItem item, string text)
    {
    }

    public Task<string?> AskAsync(string instruction) => Task.FromResult<string?>(null);

    public string LocalModelStatus() => "未安装本地模型";

    public string? EnableOllama(string model) => "refused by the store";

    public string? InstallBuiltIn(IProgress<double> progress) => "download refused";

    public string? DeleteBuiltIn(out string message)
    {
        message = string.Empty;
        return "delete refused";
    }

    public string? SwitchToBuiltIn() => "switch refused";

    public bool GpuEnabled() => false;

    public string? SetGpu(bool enabled) => "gpu refused";

    public (string Model, string Url, bool Configured) CloudSettings() => ("", "", false);

    public string? SaveCloud(string model, string url, string apiKey) => "cloud refused";

    public (string Status, bool Available, bool Enabled) ThunderbirdMail() => ("", false, false);

    public bool SetUseThunderbird(bool enabled) => false;

    public IReadOnlyList<MailFolderInfo> MailFolders() => [];

    public bool SetMailFolderEnabled(string account, string folder, bool enabled) => false;

    public bool SetOnlyMailbox(string mailbox) => false;

    public string FetchNow() => "fetch refused";

    public void RescanMail()
    {
    }

    public IReadOnlyList<EmailAccountInfo> EmailAccounts() => [];

    public string? AddEmailAccount(string address, string host, int port, string secret, int lookbackDays,
        bool unreadOnly) => "mail refused";

    public bool RemoveEmailAccount(string address) => false;

    public (bool Ok, string Message) TestEmailAccount(string address) => (false, "refused");

    public string ImportEmailAccounts() => "refused";

    public IReadOnlyList<string> WebSources() => [];

    public string? AddWebSource(string url) => "page refused";

    public bool RemoveWebSource(string url) => false;

    public IReaderService CreateReader() => new StubReader();

    public PromptEditResult SavePromptOverride(string promptKey, string segmentId, string text) =>
        new(null, text, true);

    public PromptEditResult ResetPromptOverride(string promptKey, string segmentId) =>
        new(null, "default text", false);

    public List<string> Languages { get; } = [];

    public void LanguageChanged(string culture) => Languages.Add(culture);
    public IReadOnlyList<string> WebAllowedDomains() => WebDomains;

    public int WebFetches { get; private set; }

    public string FetchWebNow() { WebFetches++; return "read 2 pages, 1 pending"; }
    public List<string> WebDomains { get; } = [];
    public string? AllowWebDomain(string domain) { WebDomains.Add(domain); return null; }
    public string? RevokeWebDomain(string domain) { WebDomains.Remove(domain); return null; }
    public Task<string> ReadWebOnceAsync(string url) => Task.FromResult("read 12 chars");

    public HashSet<string> CookieHosts { get; } = [];
    public string? SetWebCookie(string host, string cookieHeader) { CookieHosts.Add(host); return null; }
    public string? ClearWebCookie(string host) { CookieHosts.Remove(host); return null; }
    public bool WebCookieSet(string host) => CookieHosts.Contains(host);

    public VoiceInfo SpeechStatus() => new(VoiceOk, "system", VoiceOk ? "ok" : "没有可用的麦克风");

    public bool VoiceOk { get; set; } = true;
    public string VoiceText { get; set; } = "recorded words";

    /// <summary>When set, listening only ends when the caller cancels — as a real microphone does.</summary>
    public bool VoiceBlocks { get; set; }
    public bool VoiceCancelled { get; private set; }

    public Task<VoiceResult> ListenAsync(CancellationToken ct = default)
    {
        if (!VoiceBlocks) return Task.FromResult(new VoiceResult(true, VoiceText, "", 8, "system"));

        return BlockAsync(ct);
    }

    private async Task<VoiceResult> BlockAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            VoiceCancelled = true;
            throw;
        }

        return new VoiceResult(true, VoiceText, "", 8, "system");
    }

    public string? UseModelSpeech(bool useModel) => null;

    public Task<CloudTestResult> TestCloudAsync(string model, string url, string apiKey) =>
        Task.FromResult(string.IsNullOrWhiteSpace(url)
            ? new CloudTestResult(false, "no url", 1)
            : new CloudTestResult(true, "connected " + model, 7));
    public Task<PromptTryResult> TryPromptAsync(string promptKey) =>
        Task.FromResult(new PromptTryResult(true, "prompt text", "reply text", 5, string.Empty));
    public AdvancedSnapshot AdvancedSnapshot() => new(
    [
        new AdvancedItemData("prompt", "Prompt.Chat.Title", "Prompt.Chat.Purpose", "test",
            [
                new AdvancedRowData("Prompt.Chat.Persona", "", "persona text", false),
                new AdvancedRowData("Prompt.Chat.Protocol", "", "{\"action\":\"tool\"}", true),
            ]),
        new AdvancedItemData("parameter", "Advanced.Llama.Title", "Advanced.Llama.Purpose", "test",
            [new AdvancedRowData(null, "contextSize", "2048", false)]),
        new AdvancedItemData("prompt", "Prompt.LecturePersona.Title", "Prompt.LecturePersona.Purpose", "test",
            [new AdvancedRowData("Prompt.LecturePersona.Persona", "", "senior voice", false)]),
    ]);
}

/// <summary>
/// A reader that fills the seam without a document, so the window's own behaviour (open,
/// page, selection, ask, close) can be asserted without a PDF engine or a model.
/// </summary>
internal sealed class StubReader : IReaderService
{
    public string? OpenFailure { get; set; }

    public List<(int Page, string Question)> Asked { get; } = [];

    public List<(double X0, double Y0, double X1, double Y1)> Selections { get; } = [];

    public bool Closed { get; private set; }

    public string Title => "stub.pdf";

    public int PageCount => 5;

    public string? Open(string path) => OpenFailure;

    public ReaderPageImage Render(int page, int targetWidth) => new(new byte[40 * 40 * 4], 40, 40);

    public string PageText(int page) => "page " + page;

    public string TextIn(int page, double x0, double y0, double x1, double y1)
    {
        Selections.Add((x0, y0, x1, y1));
        return "selected words";
    }

    public Task<ReaderOutline> OutlineAsync(CancellationToken ct = default) =>
        Task.FromResult(new ReaderOutline("summary text",
            [new ReaderSection(1, "first"), new ReaderSection(3, "third")]));

    public Task<string> AskAsync(int page, double x0, double y0, double x1, double y1,
        string question, CancellationToken ct = default)
    {
        Asked.Add((page, question));
        return Task.FromResult("answer for page " + page);
    }

    public void Close() => Closed = true;
}
