using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CogniCustos.Ui.Localization;
using CogniCustos.Ui.Mvvm;

namespace CogniCustos.Ui.ViewModels;

/// <summary>
/// State of the window shell: which drawer / page is open, the reminder line at
/// the top, plus the mock board. No services, no persistence — this is the UI sandbox.
/// </summary>
public sealed class ShellViewModel : ObservableObject, ILocalizedViewModel
{

    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(6) };

    private bool _menuOpen;
    private bool _futureOpen;
    private bool _settingsOpen;
    private bool _helpOpen;
    private string _inputText = string.Empty;
    private string _toast = string.Empty;
    private bool _languageMenuOpen;

    public ShellViewModel()
    {
        Settings = new SettingsViewModel();
        Today = new TodayViewModel();

        ToggleDrawerCommand = new RelayCommand(ToggleMenu);
        ExpandDrawerCommand = new RelayCommand(ToggleFuture);
        PreviewCommand = new RelayCommand(parameter =>
        {
            if (parameter is string state) ShowState(state);
        });
        ToggleSettingsCommand = new RelayCommand(ToggleSettings);
        ToggleHelpCommand = new RelayCommand(ToggleHelp);
        ToggleLanguageCommand = new RelayCommand(() => ApplyLanguage(Localizer.NextLanguage()));
        SetLanguageCommand = new RelayCommand(parameter =>
        {
            if (parameter is string culture) ApplyLanguage(culture);
        });
        ToggleLanguageMenuCommand = new RelayCommand(() => IsLanguageMenuOpen = !IsLanguageMenuOpen);
        GoHomeCommand = new RelayCommand(GoHome);
        SubmitInputCommand = new RelayCommand(SubmitInput);

        Cards =
        [
            // 1: compressed calendar — a date, no rows, no "+" yet.
            new CardViewModel(CardKind.Calendar, "\uE787", BuildCalendar, () => Localizer.Get("Card.Row.New")),
            // 2: pending items — pink, no "+" in the sketch.
            new CardViewModel(CardKind.Pending, "\uE9D5", BuildPending, () => Localizer.Get("Card.Row.New")),
            // 3: note — pale yellow, with "+".
            new CardViewModel(CardKind.Note, "\uE70F", BuildNote, () => Localizer.Get("Card.Row.New")),
        ];

        // The backdrop also covers the reminder drawer; keep its binding in sync.
        Today.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TodayViewModel.IsListOpen))
            {
                OnPropertyChanged(nameof(IsBackdropVisible));
            }
        };

        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast = string.Empty;
        };

        RefreshText();
    }

    public SettingsViewModel Settings { get; }

    /// <summary>Today's reminders: the line at the top of the window.</summary>
    public TodayViewModel Today { get; }

    public ObservableCollection<CardViewModel> Cards { get; }

    public RelayCommand ToggleDrawerCommand { get; }
    public RelayCommand ExpandDrawerCommand { get; }

    /// <summary>Settings "interface" gallery: parameter is a ShowState state name.</summary>
    public RelayCommand PreviewCommand { get; }
    public RelayCommand ToggleSettingsCommand { get; }
    public RelayCommand ToggleHelpCommand { get; }
    public RelayCommand ToggleLanguageCommand { get; }

    /// <summary>Picks a language from the menu; parameter is the culture code.</summary>
    public RelayCommand SetLanguageCommand { get; }

    public RelayCommand ToggleLanguageMenuCommand { get; }
    public RelayCommand GoHomeCommand { get; }
    public RelayCommand SubmitInputCommand { get; }

    /// <summary>Exposed for the headless snapshot tool and for diagnostics.</summary>
    public string LanguageCode => Localizer.Current;

    /// <summary>Short code shown on the language picker ("EN" / "中").</summary>
    public string CurrentLanguageCode => Localizer.Code(Localizer.Current);

    /// <summary>The picker's popup. Two-way with the Popup control.</summary>
    public bool IsLanguageMenuOpen
    {
        get => _languageMenuOpen;
        set => Set(ref _languageMenuOpen, value);
    }

    /// <summary>Menu rows, rebuilt on every language change; adding a language needs no XAML.</summary>
    public ObservableCollection<LanguageOption> Languages { get; } = [];

    public string InputText
    {
        get => _inputText;
        set => Set(ref _inputText, value);
    }

    /// <summary>One-line status shown in the sidebar's reasoning panel.</summary>
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

    /// <summary>First drawer layer: the icon grid. It covers the top bar; home blurs behind it.</summary>
    public bool MenuOpen
    {
        get => _menuOpen;
        private set
        {
            if (!Set(ref _menuOpen, value)) return;
            OnPropertyChanged(nameof(MenuOffset));
            OnPropertyChanged(nameof(MenuOpacity));
            OnPropertyChanged(nameof(MenuHitTest));
            OnPropertyChanged(nameof(IsMenuActive));
            OnPropertyChanged(nameof(IsScrimVisible));
            OnPropertyChanged(nameof(HomeBlurRadius));
            OnPropertyChanged(nameof(HomeDimOpacity));
            OnPropertyChanged(nameof(IsBackdropVisible));
            if (!value) FutureOpen = false;
        }
    }

    /// <summary>
    /// Second drawer layer: opens to the LEFT of the first one, which stays where it is.
    /// "Expanding" is another drawer, not a wider one.
    /// </summary>
    public bool FutureOpen
    {
        get => _futureOpen;
        private set
        {
            if (!Set(ref _futureOpen, value)) return;
            OnPropertyChanged(nameof(FutureOffset));
            OnPropertyChanged(nameof(FutureOpacity));
            OnPropertyChanged(nameof(FutureHitTest));
            OnPropertyChanged(nameof(ExpandGlyph));
            OnPropertyChanged(nameof(HomeDimOpacity));
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
            OnPropertyChanged(nameof(IsScrimVisible));
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

    public bool IsMenuActive => _menuOpen && !_settingsOpen;
    public bool IsHelpActive => _helpOpen;
    public bool IsSettingsActive => _settingsOpen;

    /// <summary>Transparent catcher behind the help panel / reminder list.</summary>
    public bool IsBackdropVisible => (_helpOpen || Today.IsListOpen) && !_settingsOpen && !_menuOpen;

    /// <summary>Layer 1: icon grid, 390 wide (canvas units).</summary>
    public double MenuWidth => 390;

    /// <summary>Layer 2: reserved future area, 290 wide, sits left of layer 1.</summary>
    public double FutureWidth => 290;

    /// <summary>0 when open; fully off-screen (behind the rail) when closed.</summary>
    public double MenuOffset => _menuOpen ? 0 : MenuWidth + 24;

    public double MenuOpacity => _menuOpen ? 1 : 0;

    public bool MenuHitTest => _menuOpen;

    public double FutureOffset => _futureOpen ? 0 : FutureWidth + 24;

    public double FutureOpacity => _futureOpen ? 1 : 0;

    public bool FutureHitTest => _futureOpen;

    /// <summary>
    /// How soft the page behind the drawer stack is. 0 = sharp (dim only, matches the
    /// current mock); 2-4 gives a light frosted look. 界面配置 will expose this.
    /// </summary>
    public const double BlurWhenDrawerOpen = 0;

    public double HomeBlurRadius => (_menuOpen || _futureOpen) ? BlurWhenDrawerOpen : 0;

    /// <summary>Home dim while a drawer is up; a second layer dims a little more.</summary>
    public double HomeDimOpacity => _futureOpen ? 0.1 : 0.05;

    /// <summary>The dim + click catcher behind the drawer stack.</summary>
    public bool IsScrimVisible => (_menuOpen || _futureOpen) && !_settingsOpen;

    /// <summary>Footer chevron: "&lt;" opens the second layer, "&gt;" closes it.</summary>
    public string ExpandGlyph => _futureOpen ? "\uE76C" : "\uE76B";

    private void ToggleMenu()
    {
        if (SettingsOpen) SettingsOpen = false;
        HelpOpen = false;
        Today.CloseList();
        MenuOpen = !MenuOpen;
    }

    /// <summary>Layer 2 only; layer 1 never moves.</summary>
    private void ToggleFuture()
    {
        if (!MenuOpen)
        {
            MenuOpen = true;
            FutureOpen = true;
            return;
        }

        FutureOpen = !FutureOpen;
    }

    /// <summary>The page behind the stack closes one layer at a time.</summary>
    public void CloseTopDrawer()
    {
        if (FutureOpen)
        {
            FutureOpen = false;
            return;
        }

        MenuOpen = false;
    }

    private void ToggleSettings()
    {
        MenuOpen = false;
        HelpOpen = false;
        Today.CloseList();
        SettingsOpen = !SettingsOpen;
    }

    private void ToggleHelp()
    {
        MenuOpen = false;
        Today.CloseList();
        HelpOpen = !HelpOpen;
    }

    /// <summary>
    /// Single path for language changes: swap the resource dictionary, then let the
    /// view-model-held strings re-read it. XAML text updates by itself (DynamicResource);
    /// the window's LocalizedWindow hook refreshes other windows.
    /// </summary>
    public void ApplyLanguage(string culture)
    {
        Localizer.SetLanguage(culture);
        IsLanguageMenuOpen = false;
        RefreshText();
        Toast = string.Empty;
    }

    /// <summary>
    /// Re-read every view-model string. Called by the window on open and whenever
    /// <see cref="Localizer.LanguageChanged"/> fires, so every window stays in sync.
    /// </summary>
    public void RefreshText()
    {
        Settings.RefreshTitles();
        foreach (var card in Cards) card.Refresh();
        Today.ResetSeeds(BuildTodaySeeds());
        RebuildLanguageMenu();
        OnAllPropertiesChanged();
    }

    private void RebuildLanguageMenu()
    {
        Languages.Clear();
        foreach (var code in Localizer.Languages)
        {
            var culture = code;
            Languages.Add(new LanguageOption(
                culture,
                Localizer.DisplayName(culture),
                culture == Localizer.Current,
                new RelayCommand(() => ApplyLanguage(culture))));
        }
    }

    private void GoHome()
    {
        CloseOverlays();
        Toast = string.Empty;
    }

    /// <summary>The input box adds to today's reminders (the top line), not to a card.</summary>
    private void SubmitInput()
    {
        var text = InputText.Trim();
        if (text.Length == 0) return;

        Today.Add(text);
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
        MenuOpen = false;
        SettingsOpen = false;
        HelpOpen = false;
        IsLanguageMenuOpen = false;
        Today.CloseList();
    }

    // ---------- mock data (localised through the resources) ----------

    private IReadOnlyList<string> BuildTodaySeeds() =>
    [
        Localizer.Get("Demo.Today1"),
        Localizer.Get("Demo.Today2"),
    ];

    private IReadOnlyList<string> BuildLongTodaySeeds()
    {
        var items = new List<string>(12);
        for (var i = 1; i <= 12; i++)
        {
            items.Add($"{i:00}  {Localizer.Get("Demo.Today1")} · {Localizer.Get("Demo.Today2")}");
        }

        return items;
    }

    private (string, string, IReadOnlyList<CardItem>) BuildCalendar() => (
        DateTime.Today.ToString("yy/MM/dd"),
        string.Empty,
        []);

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
            case "home":
                break;
            case "appearance":
                SettingsOpen = true;
                Settings.Selected = Settings.Sections.First(section => section.Key == "Section.Appearance");
                break;
            case "drawer":
                MenuOpen = true;
                break;
            case "expanded":
                MenuOpen = true;
                FutureOpen = true;
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
            case "today-list":
                Today.ToggleList();
                break;
            case "language-menu":
                IsLanguageMenuOpen = true;
                break;
            case "today-list-long":
                Today.ResetSeeds(BuildLongTodaySeeds());
                Today.ToggleList();
                break;
            case "toast":
                ApplyLanguage(Localizer.Chinese);
                InputText = Localizer.Get("Demo.Note1");
                SubmitInput();
                break;
        }
    }
}
