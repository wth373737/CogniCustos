using System.Collections.ObjectModel;
using CogniCustos.Ui.Localization;
using CogniCustos.Ui.Mvvm;

namespace CogniCustos.Ui.ViewModels;

/// <summary>
/// State of the window shell: which drawer / page is open, plus the mock board.
/// No services, no persistence — this is the UI sandbox.
/// </summary>
public sealed class ShellViewModel : ObservableObject
{
    private const double MenuWidth = 208;
    private const double ExpandedWidth = 560;

    private bool _drawerOpen;
    private bool _drawerExpanded;
    private bool _settingsOpen;
    private bool _helpOpen;
    private int _selectedSectionIndex;
    private string _inputText = string.Empty;
    private string _toast = string.Empty;

    public ShellViewModel()
    {
        ToggleDrawerCommand = new RelayCommand(ToggleDrawer);
        ExpandDrawerCommand = new RelayCommand(ExpandDrawer);
        ToggleSettingsCommand = new RelayCommand(ToggleSettings);
        ToggleHelpCommand = new RelayCommand(ToggleHelp);
        ToggleLanguageCommand = new RelayCommand(ToggleLanguage);
        GoHomeCommand = new RelayCommand(GoHome);
        SubmitInputCommand = new RelayCommand(SubmitInput);
        CloseOverlaysCommand = new RelayCommand(CloseOverlays);

        Cards =
        [
            new CardViewModel("\uE787", "today", isPink: false, isOlive: false, BuildToday),
            new CardViewModel("\uE9D5", "pending", isPink: true, isOlive: false, BuildPending),
            new CardViewModel("\uE8FD", "note", isPink: false, isOlive: true, BuildNote),
        ];
    }

    public Strings S { get; } = new();

    public ObservableCollection<CardViewModel> Cards { get; }

    public RelayCommand ToggleDrawerCommand { get; }
    public RelayCommand ExpandDrawerCommand { get; }
    public RelayCommand ToggleSettingsCommand { get; }
    public RelayCommand ToggleHelpCommand { get; }
    public RelayCommand ToggleLanguageCommand { get; }
    public RelayCommand GoHomeCommand { get; }
    public RelayCommand SubmitInputCommand { get; }
    public RelayCommand CloseOverlaysCommand { get; }

    public string InputText
    {
        get => _inputText;
        set => Set(ref _inputText, value);
    }

    /// <summary>One-line status shown in the reasoning panel (placeholder for real events).</summary>
    public string Toast
    {
        get => _toast;
        private set
        {
            if (!Set(ref _toast, value)) return;
            OnPropertyChanged(nameof(HasToast));
        }
    }

    public bool HasToast => !string.IsNullOrWhiteSpace(_toast);

    public bool DrawerOpen
    {
        get => _drawerOpen;
        private set
        {
            if (!Set(ref _drawerOpen, value)) return;
            OnPropertyChanged(nameof(DrawerOffset));
            OnPropertyChanged(nameof(DrawerOpacity));
            OnPropertyChanged(nameof(DrawerHitTest));
            OnPropertyChanged(nameof(IsMenuActive));
            OnPropertyChanged(nameof(IsBackdropVisible));
        }
    }

    /// <summary>Drawer grows leftwards to reveal the reserved "future features" area.</summary>
    public bool DrawerExpanded
    {
        get => _drawerExpanded;
        private set
        {
            if (!Set(ref _drawerExpanded, value)) return;
            OnPropertyChanged(nameof(DrawerWidth));
            OnPropertyChanged(nameof(DrawerOffset));
            OnPropertyChanged(nameof(ExpandGlyph));
        }
    }

    public bool SettingsOpen
    {
        get => _settingsOpen;
        private set
        {
            if (!Set(ref _settingsOpen, value)) return;
            OnPropertyChanged(nameof(IsMenuActive));
            OnPropertyChanged(nameof(IsSettingsActive));
            OnPropertyChanged(nameof(IsBackdropVisible));
        }
    }

    public bool HelpOpen
    {
        get => _helpOpen;
        private set
        {
            if (!Set(ref _helpOpen, value)) return;
            OnPropertyChanged(nameof(IsHelpActive));
            OnPropertyChanged(nameof(IsBackdropVisible));
        }
    }

    public bool IsMenuActive => _drawerOpen && !_settingsOpen;
    public bool IsHelpActive => _helpOpen;
    public bool IsSettingsActive => _settingsOpen;

    /// <summary>Transparent catcher behind the drawer / help panel: click outside to close.</summary>
    public bool IsBackdropVisible => (_drawerOpen || _helpOpen) && !_settingsOpen;

    public double DrawerWidth => _drawerExpanded ? ExpandedWidth : MenuWidth;

    /// <summary>0 when open; fully off-screen (behind the rail) when closed.</summary>
    public double DrawerOffset => _drawerOpen ? 0 : DrawerWidth + 24;

    public double DrawerOpacity => _drawerOpen ? 1 : 0;

    public bool DrawerHitTest => _drawerOpen;

    /// <summary>Chevron in the drawer footer: "&lt;" expands, "&gt;" collapses (MDL2 glyphs).</summary>
    public string ExpandGlyph => _drawerExpanded ? "\uE76C" : "\uE76B";

    public int SelectedSectionIndex
    {
        get => _selectedSectionIndex;
        set
        {
            if (!Set(ref _selectedSectionIndex, value)) return;
            OnPropertyChanged(nameof(SelectedSection));
        }
    }

    public string SelectedSection
    {
        get
        {
            var sections = S.SettingsSections;
            return _selectedSectionIndex >= 0 && _selectedSectionIndex < sections.Count
                ? sections[_selectedSectionIndex]
                : string.Empty;
        }
    }

    private void ToggleDrawer()
    {
        if (SettingsOpen) SettingsOpen = false;
        HelpOpen = false;
        DrawerOpen = !DrawerOpen;
    }

    private void ExpandDrawer()
    {
        if (!DrawerOpen) DrawerOpen = true;
        DrawerExpanded = !DrawerExpanded;
    }

    private void ToggleSettings()
    {
        DrawerOpen = false;
        HelpOpen = false;
        SettingsOpen = !SettingsOpen;
    }

    private void ToggleHelp()
    {
        DrawerOpen = false;
        HelpOpen = !HelpOpen;
    }

    private void ToggleLanguage()
    {
        S.IsChinese = !S.IsChinese;
        foreach (var card in Cards) card.Refresh();
        OnPropertyChanged(nameof(SelectedSection));
        Toast = string.Empty;
    }

    private void GoHome()
    {
        CloseOverlays();
        Toast = string.Empty;
    }

    private void SubmitInput()
    {
        var text = InputText.Trim();
        if (text.Length == 0) return;

        Cards[0].Items.Add(new CardItem(text, DateTime.Now.ToString("HH:mm")));
        InputText = string.Empty;
        Toast = S.IsChinese ? "已暂存到「今天」（沙箱假数据）" : "Added to today (sandbox data)";
    }

    public void CloseOverlays()
    {
        DrawerOpen = false;
        SettingsOpen = false;
        HelpOpen = false;
    }

    // ---------- mock data (localised) ----------

    private (string, string, IReadOnlyList<CardItem>) BuildToday() => S.IsChinese
        ? (S.CardToday, "今天还没有安排", [new CardItem("SAD 讲座", "09:00"), new CardItem("小组同步", "14:30")])
        : (S.CardToday, "Nothing planned yet", [new CardItem("SAD lecture", "09:00"), new CardItem("Group sync", "14:30")]);

    private (string, string, IReadOnlyList<CardItem>) BuildPending() => S.IsChinese
        ? (S.CardPending, "没有待办", [new CardItem("补完提案 §5.2"), new CardItem("检查门户截止时间")])
        : (S.CardPending, "Nothing pending", [new CardItem("Finish proposal §5.2"), new CardItem("Check portal deadlines")]);

    private (string, string, IReadOnlyList<CardItem>) BuildNote() => S.IsChinese
        ? (S.CardNote, "还没有便签", [new CardItem("想法：可折叠的预留区"), new CardItem("问导师邮件范围")])
        : (S.CardNote, "No notes yet", [new CardItem("Idea: collapsible future area"), new CardItem("Ask tutor about mail scope")]);

    // ---------- headless snapshot tool ----------

    /// <summary>Convenience for the headless screenshot tool.</summary>
    public void ShowState(string state)
    {
        CloseOverlays();
        Toast = string.Empty;
        switch (state)
        {
            case "drawer":
                DrawerOpen = true;
                break;
            case "expanded":
                DrawerOpen = true;
                DrawerExpanded = true;
                break;
            case "settings":
                SettingsOpen = true;
                break;
            case "settings-zh":
                S.IsChinese = true;
                foreach (var card in Cards) card.Refresh();
                SettingsOpen = true;
                break;
            case "help":
                HelpOpen = true;
                break;
            case "collapsed":
                foreach (var card in Cards) card.ToggleCollapseCommand.Execute(null);
                break;
            case "toast":
                InputText = S.IsChinese ? "把明天的评审挪到下午" : "move tomorrow's review to the afternoon";
                SubmitInput();
                break;
        }
    }
}
