using CogniCustos.Ui.Localization;

namespace CogniCustos.Ui.ViewModels;

/// <summary>
/// The named UI states the screenshot tool and the settings "interface" gallery render.
/// It lives outside the shell on purpose: the shell keeps its production surface, and
/// this file is the only place that knows about fixture states.
/// </summary>
public static class ShellScenarios
{
    public static void Apply(ShellViewModel vm, string state)
    {
        // Every state starts from home with no status line.
        vm.GoHomeCommand.Execute(null);

        switch (state)
        {
            case "home":
                break;
            case "appearance":
                vm.ToggleSettingsCommand.Execute(null);
                Select(vm, "Section.Appearance");
                break;
            case "drawer":
                vm.ToggleDrawerCommand.Execute(null);
                break;
            case "expanded":
                vm.ToggleDrawerCommand.Execute(null);
                vm.ExpandDrawerCommand.Execute(null);
                break;
            case "settings":
                vm.ToggleSettingsCommand.Execute(null);
                break;
            case "settings-zh":
                vm.SetLanguageCommand.Execute(Localizer.Chinese);
                vm.ToggleSettingsCommand.Execute(null);
                break;
            case "settings-deep":
                vm.ToggleSettingsCommand.Execute(null);
                vm.Settings.Selected = vm.Settings.Sections[5];
                vm.SetLanguageCommand.Execute(Localizer.Chinese);
                break;
            case "settings-model":
                OpenModelSection(vm);
                break;
            case "settings-email":
                OpenSettingsAt(vm, "Section.EmailRetrieval");
                break;
            case "settings-web":
                OpenSettingsAt(vm, "Section.WebsiteBrowsing");
                break;
            case "settings-ollama":
                OpenModelSection(vm);
                vm.Settings.Model.OpenOllamaCommand.Execute(null);
                break;
            case "settings-model-zh":
                OpenModelSection(vm);
                vm.SetLanguageCommand.Execute(Localizer.Chinese);
                break;
            case "help":
                vm.ToggleHelpCommand.Execute(null);
                break;
            case "cards-full":
                for (var i = 0; i < 12; i++) vm.Cards[2].AddCommand.Execute(null);
                break;
            case "today-list":
                vm.Today.ToggleList();
                break;
            case "today-list-long":
                vm.Today.ResetSeeds(LongSeeds());
                vm.Today.ToggleList();
                break;
            case "language-menu":
                vm.IsLanguageMenuOpen = true;
                break;
            case "calendar":
                vm.Calendar.Open();
                break;
            case "reasoning":
                vm.Reasoning.OpenList();
                break;
            case "trajectory":
                vm.Reasoning.ToggleTrajectory();
                break;
            case "dialog-incoming":
                OpenFirstRow(vm.Cards[1]);
                break;
            case "dialog-note":
                OpenFirstRow(vm.Cards[2]);
                break;
            case "dialog-compose":
                OpenFirstRow(vm.Cards[1]);
                Click(vm, primary: false, Localizer.Get("Dialog.More"));
                break;
            case "reader":
                OpenReader(vm);
                break;
            case "reader-ask":
                OpenReader(vm);
                vm.Reader.SelectRectangle(0.10, 0.18, 0.78, 0.36);
                vm.Reader.Question = Localizer.Get("Reader.DefaultQuestion");
                vm.Reader.AskAsync().GetAwaiter().GetResult();
                break;
            case "toast":
                vm.SetLanguageCommand.Execute(Localizer.Chinese);
                vm.InputText = Localizer.Get("Demo.Note1");
                vm.SubmitInputCommand.Execute(null);
                break;
        }
    }

    private static void OpenSettingsAt(ShellViewModel vm, string key)
    {
        vm.ToggleSettingsCommand.Execute(null);
        Select(vm, key);
    }

    /// <summary>Open the point-read window on the demo document, box and all.</summary>
    private static void OpenReader(ShellViewModel vm)
    {
        vm.ClosePanels();
        vm.Reader.OpenPreview("demo-lecture.pdf");
        vm.Reader.SelectRectangle(0.10, 0.18, 0.78, 0.36);
    }

    private static void OpenModelSection(ShellViewModel vm)
    {
        vm.ToggleSettingsCommand.Execute(null);
        Select(vm, "Section.Model");
    }

    private static void Select(ShellViewModel vm, string key)
    {
        var section = vm.Settings.Sections.FirstOrDefault(s => s.Key == key);
        if (section is not null) vm.Settings.Selected = section;
    }

    /// <summary>A row click is what the user does, so the fixture does exactly that.</summary>
    private static void OpenFirstRow(CardViewModel card)
    {
        if (card.Items.Count == 0) return;
        var item = card.Items[0];
        item.Command?.Execute(item);
    }

    private static void Click(ShellViewModel vm, bool primary, string? text = null)
    {
        var button = vm.Dialog.Buttons.FirstOrDefault(b =>
            b.Primary == primary && (text is null || b.Text == text));
        button?.Command.Execute(null);
    }

    private static IReadOnlyList<ReminderItem> LongSeeds()
    {
        var items = new List<ReminderItem>(12);
        for (var i = 1; i <= 12; i++)
        {
            items.Add(new ReminderItem($"{i:00}  {Localizer.Get("Demo.TodayNormal")}", i % 4 == 0));
        }

        return items;
    }
}
