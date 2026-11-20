using System.Collections.ObjectModel;
using Avalonia.Threading;
using CogniCustos.Ui.Localization;
using CogniCustos.Ui.Mvvm;

namespace CogniCustos.Ui.ViewModels;

/// <summary>
/// State of the window shell: which drawer / page is open, plus the mock board.
/// No services, no persistence — this is the UI sandbox.
/// All text comes from the language resources via <see cref="Localizer"/>.
/// </summary>
public sealed class ShellViewModel : ObservableObject
{
    private const double MenuWidth = 208;
    private const double ExpandedWidth = 560;

    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(6) };

    private bool _drawerOpen;
    private bool _drawerExpanded;
    private bool _settingsOpen;
    private bool _helpOpen;
    private string _inputText = string.Empty;
    private string _toast = string.Empty;

    public ShellViewModel()
    {
        Settings = new SettingsViewModel();

        ToggleDrawerCommand = new RelayCommand(ToggleDrawer);
        ExpandDrawerCommand = new RelayCommand(ExpandDrawer);
        ToggleSettingsCommand = new RelayCommand(ToggleSettings);
        ToggleHelpCommand = new RelayCommand(ToggleHelp);
        ToggleLanguageCommand = new RelayCommand(() => ApplyLanguage(Localizer.NextLanguage()));
        GoHomeCommand = new RelayCommand(GoHome);
        SubmitInputCommand = new RelayCommand(SubmitInput);

        Cards =
        [
            new CardViewModel("\uE787", CardVariant.Plain, BuildToday, () => Localizer.Get("Card.Row.New")),
            new CardViewModel("\uE9D5", CardVariant.Pink, BuildPending, () => Localizer.Get("Card.Row.New")),
            new CardViewModel("\uE8FD", CardVariant.Olive, BuildNote, () => Localizer.Get("Card.Row.New")),
        ];

        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast = string.Empty;
        };
    }

    public SettingsViewModel Settings { get; }

    public ObservableCollection<CardViewModel> Cards { get; }

    public RelayCommand ToggleDrawerCommand { get; }
    public RelayCommand ExpandDrawerCommand { get; }
    public RelayCommand ToggleSettingsCommand { get; }
    public RelayCommand ToggleHelpCommand { get; }
    public RelayCommand ToggleLanguageCommand { get; }
    public RelayCommand GoHomeCommand { get; }
    public RelayCommand SubmitInputCommand { get; }

    /// <summary>Exposed for the headless snapshot tool and for diagnostics.</summary>
    public string LanguageCode => Localizer.Current;

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
            OnPropertyChanged(nameof(FutureAreaOpacity));
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

    /// <summary>Fades the reserved area in while the drawer grows.</summary>
    public double FutureAreaOpacity => _drawerExpanded ? 1 : 0;

    /// <summary>Chevron in the drawer footer: "&lt;" expands, "&gt;" collapses (MDL2 glyphs).</summary>
    public string ExpandGlyph => _drawerExpanded ? "\uE76C" : "\uE76B";

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

    /// <summary>
    /// Single path for language changes: swap the resource dictionary, then let the
    /// view-model-held strings re-read it. XAML text updates by itself (DynamicResource).
    /// </summary>
    public void ApplyLanguage(string culture)
    {
        Localizer.SetLanguage(culture);
        Settings.RefreshTitles();
        foreach (var card in Cards) card.Refresh();
        OnPropertyChanged(nameof(LanguageCode));
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
        SetToast(Localizer.Get("Toast.AddedToToday"));
    }

    private void SetToast(string text)
    {
        Toast = text;
        _toastTimer.Stop();
        if (HasToast) _toastTimer.Start();
    }

    public void CloseOverlays()
    {
        DrawerOpen = false;
        SettingsOpen = false;
        HelpOpen = false;
    }

    // ---------- mock data (localised through the resources) ----------

    private (string, string, IReadOnlyList<CardItem>) BuildToday() => (
        DateTime.Today.ToString("yy/MM/dd"),
        Localizer.Get("Card.Empty.Today"),
        [
            new CardItem(Localizer.Get("Demo.Today1"), "09:00"),
            new CardItem(Localizer.Get("Demo.Today2"), "14:30"),
        ]);

    private (string, string, IReadOnlyList<CardItem>) BuildPending() => (
        Localizer.Get("Card.Pending"),
        Localizer.Get("Card.Empty.Pending"),
        [
            new CardItem(Localizer.Get("Demo.Pending1")),
            new CardItem(Localizer.Get("Demo.Pending2")),
        ]);

    private (string, string, IReadOnlyList<CardItem>) BuildNote() => (
        Localizer.Get("Card.Note"),
        Localizer.Get("Card.Empty.Note"),
        [
            new CardItem(Localizer.Get("Demo.Note1")),
            new CardItem(Localizer.Get("Demo.Note2")),
        ]);

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
                ApplyLanguage(Localizer.Chinese);
                SettingsOpen = true;
                break;
            case "settings-deep":
                // Select first, switch language after: this used to clear the selection.
                SettingsOpen = true;
                Settings.Selected = Settings.Sections[5];
                ApplyLanguage(Localizer.Chinese);
                break;
            case "help":
                HelpOpen = true;
                break;
            case "collapsed":
                foreach (var card in Cards) card.ToggleCollapseCommand.Execute(null);
                break;
            case "toast":
                ApplyLanguage(Localizer.Chinese);
                InputText = Localizer.Get("Demo.Note1");
                SubmitInput();
                break;
        }
    }
}
