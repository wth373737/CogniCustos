using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CogniCustos.Ui.Localization;
using CogniCustos.Ui.Mvvm;

namespace CogniCustos.Ui.ViewModels;

/// <summary>
/// State of the window shell: which drawer / page / panel is open, the reminder line,
/// the board, the settings page and the one dialog surface. Content and actions go
/// through <see cref="IShellDataSource"/>, which the demo fills and the sample binds to
/// the runtime; nothing here touches storage directly.
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

    private readonly IShellDataSource _source;
    private DialogOrigin _dialogOrigin;

    /// <summary>Where the open dialog came from: it decides what "confirm" means.</summary>
    private enum DialogOrigin { None, Pending, Note }

    public ShellViewModel(IShellDataSource? source = null)
    {
        _source = source ?? new DemoShellDataSource();
        Settings = new SettingsViewModel();
        WireModelSection();
        Today = new TodayViewModel();
        Calendar = new CalendarViewModel();
        Reasoning = new ReasoningViewModel();
        Dialog = new DialogViewModel();
        Dialog.RequestClose += () => FinishDialog();

        ToggleDrawerCommand = new RelayCommand(ToggleMenu);
        ExpandDrawerCommand = new RelayCommand(ToggleFuture);
        PreviewCommand = new RelayCommand(parameter =>
        {
            if (parameter is string state) ShellScenarios.Apply(this, state);
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
            // 1: compressed calendar — the date title plus the confirmed plan rows.
            new CardViewModel(CardKind.Calendar, "\uE787", BuildCalendar, () => Localizer.Get("Card.Row.New"),
                onDateClick: Calendar.Open),
            // 2: pending items — pink, no "+" in the sketch.
            new CardViewModel(CardKind.Pending, "\uE9D5", BuildPending, () => Localizer.Get("Card.Row.New"),
                onItemClick: item => BeginIncoming(item, Cards[1])),
            // 3: note — pale yellow, with "+".
            new CardViewModel(CardKind.Note, "\uE70F", BuildNote, () => Localizer.Get("Card.Row.New"),
                onItemClick: item => BeginNote(item, Cards[2])),
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

    /// <summary>The floating calendar window (opened from the first card's date).</summary>
    public CalendarViewModel Calendar { get; }

    /// <summary>The record list and the thought trace; two independent panels.</summary>
    public ReasoningViewModel Reasoning { get; }

    /// <summary>One dialog surface for the incoming decision, ignore confirmation,
    /// composer and note flows.</summary>
    public DialogViewModel Dialog { get; }

    private CardItem? _dialogItem;
    private CardViewModel? _dialogCard;

    public ObservableCollection<CardViewModel> Cards { get; }

    public RelayCommand ToggleDrawerCommand { get; }
    public RelayCommand ExpandDrawerCommand { get; }

    /// <summary>Developer gallery in the settings page: parameter is a <see cref="ShellScenarios"/> state.</summary>
    public RelayCommand PreviewCommand { get; }
    public RelayCommand ToggleSettingsCommand { get; }
    public RelayCommand ToggleHelpCommand { get; }
    public RelayCommand ToggleLanguageCommand { get; }

    /// <summary>Picks a language from the menu; parameter is the culture code.</summary>
    public RelayCommand SetLanguageCommand { get; }

    public RelayCommand ToggleLanguageMenuCommand { get; }
    public RelayCommand GoHomeCommand { get; }
    public RelayCommand SubmitInputCommand { get; }

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
    public const double BlurWhenDrawerOpen = 6;

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
        Settings.Model.SetLocalStatus(_source.LocalModelStatus());
        Settings.Model.SetGpuState(_source.GpuEnabled(), string.Empty);

        var thunderbird = _source.ThunderbirdMail();
        Settings.EmailRetrieval.SetThunderbirdStatus(thunderbird.Status, thunderbird.Available, thunderbird.Enabled);

        Settings.EmailRetrieval.SetAccounts(_source.EmailAccounts());
        Settings.WebBrowsing.SetSources(_source.WebSources());

        var cloud = _source.CloudSettings();
        Settings.Model.SetCloudValues(cloud.Model, cloud.Url, cloud.Configured);
        Settings.Model.SetCloudStatus(cloud.Configured
            ? Localizer.Get("Settings.Model.CloudSaved") + " " + cloud.Model
            : Localizer.Get("Settings.Model.CloudNotConfigured"));
        Calendar.RefreshText();
        Dialog.RefreshText(BuildDialogButtons);
        Reasoning.RefreshText();
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

        _ = AskAndShowAsync(text);
    }

    /// <summary>Free text goes to the backing service; a non-empty answer lands on the line.</summary>
    private async Task AskAndShowAsync(string text)
    {
        try
        {
            var answer = await _source.AskAsync(text).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(answer)) return;

            var line = answer.Trim();
            if (Dispatcher.UIThread.CheckAccess())
            {
                ShowAnswer(line);
            }
            else
            {
                // A backing service may answer from a worker thread; the board is UI state.
                Dispatcher.UIThread.Post(() => ShowAnswer(line));
            }
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => SetToast(ex.Message));
        }
    }

    private void ShowAnswer(string line)
    {
        Today.Add(line, urgent: false);
        SetToast(line);
    }

    /// <summary>
    /// The Ollama window asks the backing store to switch; the mention that switching
    /// frees the bundled model is part of the window's own note.
    /// </summary>
    /// <summary>
    /// Hardware acceleration: the store owns the flag; the row only shows it. Switching it
    /// re-registers the provider, so it takes effect on the next model call.
    /// </summary>
    /// <summary>
    /// The primary model: saving it registers the provider immediately, so the next answer
    /// comes from the endpoint the fields describe.
    /// </summary>
    // ---------- retrieval: email accounts ----------

    private void AddEmailAccount(string address, string host, int port, string secret, int days, bool unread)
    {
        if (_source.AddEmailAccount(address, host, port, secret, days, unread) is { } failure)
        {
            Settings.EmailRetrieval.ShowStatus(failure);
            return;
        }

        Settings.EmailRetrieval.ClearForm();
        Settings.EmailRetrieval.ShowStatus($"已添加 {address}");
        RefreshEmailAccounts();
    }

    private void RemoveEmailAccount(string address)
    {
        _source.RemoveEmailAccount(address);
        Settings.EmailRetrieval.ShowStatus($"已删除 {address}");
        RefreshEmailAccounts();
    }

    private void TestEmailAccount(string address)
    {
        var (ok, message) = _source.TestEmailAccount(address);
        Settings.EmailRetrieval.ShowStatus((ok ? "✓ " : "✗ ") + message);

        var row = Settings.EmailRetrieval.Accounts.FirstOrDefault(a => a.Address == address);
        if (row is not null) row.Status = message;
    }

    /// <summary>
    /// "Import from Thunderbird" reads the accounts the user already has, so nobody has to
    /// type a server twice. It reports what it found instead of silently doing nothing.
    /// </summary>
    private void ImportEmailAccounts()
    {
        var imported = _source.ImportEmailAccounts();
        Settings.EmailRetrieval.ShowStatus(imported);
        RefreshEmailAccounts();
    }

    /// <summary>Re-scan Thunderbird's profiles after the user adds an account there.</summary>
    private void RefreshThunderbird()
    {
        _source.RescanMail();
        var (status, available, enabled) = _source.ThunderbirdMail();
        Settings.EmailRetrieval.SetThunderbirdStatus(status, available, enabled);
        SetToast(status);
    }

    private void ToggleThunderbird(bool enabled)
    {
        _source.SetUseThunderbird(enabled);
        var (status, available, _) = _source.ThunderbirdMail();
        Settings.EmailRetrieval.SetThunderbirdStatus(status, available, enabled);
        SetToast(enabled ? "已开启：从 Thunderbird 读取邮件" : "已关闭 Thunderbird 读取");
    }

    private void RefreshEmailAccounts() =>
        Settings.EmailRetrieval.SetAccounts(_source.EmailAccounts());

    // ---------- retrieval: web pages ----------

    private void AddWebSource(string url)
    {
        if (_source.AddWebSource(url) is { } failure)
        {
            Settings.WebBrowsing.ShowStatus(failure);
            return;
        }

        Settings.WebBrowsing.NewUrl = string.Empty;
        Settings.WebBrowsing.ShowStatus("已添加");
        Settings.WebBrowsing.SetSources(_source.WebSources());
    }

    private void RemoveWebSource(string url)
    {
        _source.RemoveWebSource(url);
        Settings.WebBrowsing.ShowStatus("已删除");
        Settings.WebBrowsing.SetSources(_source.WebSources());
    }

    private void SaveCloud(string model, string url, string apiKey)
    {
        if (_source.SaveCloud(model, url, apiKey) is { } failure)
        {
            SetToast(failure);
            return;
        }

        Settings.Model.SetCloudStatus(_source.CloudSettings().Configured
            ? Localizer.Get("Settings.Model.CloudSaved") + " " + model
            : Localizer.Get("Settings.Model.CloudNotConfigured"));
        SetToast(Localizer.Get("Settings.Model.CloudSavedShort"));
    }

    private void ToggleGpu(bool enabled)
    {
        if (_source.SetGpu(enabled) is { } failure)
        {
            SetToast(failure);
            return;
        }

        Settings.Model.SetGpuState(_source.GpuEnabled(), string.Empty);
        SetToast(Settings.Model.GpuStateLabel);
    }

    private void EnableOllama(string model)
    {
        if (_source.EnableOllama(model) is { } failure)
        {
            SetToast(failure);
            return;
        }

        Settings.Model.CompleteOllamaSwitch(true, _source.LocalModelStatus());
        SetToast(_source.LocalModelStatus());
    }

    /// <summary>The Model Configuration form reports intent through events; the shell answers.</summary>
    private void WireModelSection()
    {
        Settings.Model.AdvancedRequested += () =>
            ShowInfoDialog("Settings.Model.AdvancedTitle", "Settings.Model.AdvancedPending");
        Settings.Model.CheckRequested += () => _ = CheckLocalModelAsync();
        Settings.Model.AddRequested += () =>
            ShowInfoDialog("Settings.Model.AddTitle", "Settings.Model.AddPending");
        Settings.Model.DeleteRequested += AskRemoveLocalModel;
        Settings.Model.OllamaEnableRequested += EnableOllama;
        Settings.Model.GpuToggleRequested += ToggleGpu;
        Settings.Model.CloudSaveRequested += SaveCloud;

        Settings.EmailRetrieval.AddRequested += AddEmailAccount;
        Settings.EmailRetrieval.RemoveRequested += RemoveEmailAccount;
        Settings.EmailRetrieval.TestRequested += TestEmailAccount;
        Settings.EmailRetrieval.ImportRequested += ImportEmailAccounts;
        Settings.EmailRetrieval.RescanRequested += RefreshThunderbird;
        Settings.EmailRetrieval.UseThunderbirdRequested += ToggleThunderbird;
        Settings.WebBrowsing.AddRequested += AddWebSource;
        Settings.WebBrowsing.RemoveRequested += RemoveWebSource;
    }

    private void SetToast(string text)
    {
        Toast = text;
        _toastTimer.Stop();
        if (HasToast) _toastTimer.Start();
    }

    public void CloseOverlays()
    {
        Reasoning.Close();
        MenuOpen = false;
        SettingsOpen = false;
        HelpOpen = false;
        IsLanguageMenuOpen = false;
        Today.CloseList();
    }

    // ---------- mock data (localised through the resources) ----------

    private IReadOnlyList<ReminderItem> BuildTodaySeeds() => _source.Today();

    private (string, string, IReadOnlyList<CardItem>) BuildCalendar() => _source.CalendarCard();

    private (string, string, IReadOnlyList<CardItem>) BuildPending() => _source.PendingCard();

    private (string, string, IReadOnlyList<CardItem>) BuildNote() => _source.NoteCard();

    // ---------- dialogs (incoming decision / ignore / composer / note) ----------

    private void BeginIncoming(CardItem item, CardViewModel card)
    {
        _dialogItem = item;
        _dialogCard = card;
        _dialogOrigin = DialogOrigin.Pending;
        OpenDialog(DialogKind.IncomingDecision, Localizer.Get("Dialog.Incoming.Title"), item.Summary);
    }

    private void BeginNote(CardItem item, CardViewModel card)
    {
        _dialogItem = item;
        _dialogCard = card;
        _dialogOrigin = DialogOrigin.Note;
        OpenDialog(DialogKind.NoteChoice, Localizer.Get("Dialog.Note.Title"), item.Title);
    }

    private void OpenDialog(DialogKind kind, string title, string body, bool composer = false,
        bool editor = false, string editorText = "", string composerText = "")
    {
        Dialog.Open(kind, title, body, BuildDialogButtons(kind), composer, editor, editorText, composerText);
    }

    /// <summary>Buttons for a kind; also used to re-label them after a language switch.</summary>
    private IEnumerable<DialogButton> BuildDialogButtons(DialogKind kind) => kind switch
    {
        DialogKind.IncomingDecision =>
        [
            new(Localizer.Get("Dialog.Confirm"), new RelayCommand(ConfirmIncoming), true),
            new(Localizer.Get("Dialog.Ignore"), new RelayCommand(AskIgnore)),
            new(Localizer.Get("Dialog.More"), new RelayCommand(ComposeFromIncoming)),
        ],
        DialogKind.IgnoreConfirm =>
        [
            new(Localizer.Get("Dialog.IgnoreYes"), new RelayCommand(ConfirmIgnore), true),
            new(Localizer.Get("Dialog.Cancel"), Dialog.CancelCommand),
        ],
        DialogKind.Composer =>
        [
            new(Localizer.Get("Dialog.Send"), new RelayCommand(SendComposer), true),
            new(Localizer.Get("Dialog.Cancel"), Dialog.CancelCommand),
        ],
        DialogKind.NoteChoice =>
        [
            new(Localizer.Get("Dialog.Edit"), new RelayCommand(EditNote), true),
            new(Localizer.Get("Dialog.AiHandle"), new RelayCommand(ComposeFromNote)),
            new(Localizer.Get("Dialog.Cancel"), Dialog.CancelCommand),
        ],
        DialogKind.Info =>
        [
            new(Localizer.Get("Dialog.Ok"), Dialog.CancelCommand, true),
        ],
        DialogKind.LocalModelDelete =>
        [
            new(Localizer.Get("Dialog.Delete"), new RelayCommand(ConfirmRemoveLocalModel), true),
            new(Localizer.Get("Dialog.Cancel"), Dialog.CancelCommand),
        ],
        DialogKind.NoteEdit =>
        [
            new(Localizer.Get("Dialog.Save"), new RelayCommand(SaveNote), true),
            new(Localizer.Get("Dialog.Cancel"), Dialog.CancelCommand),
        ],
        _ => [],
    };

    private void ShowInfoDialog(string titleKey, string bodyKey) =>
        Dialog.Open(DialogKind.Info, Localizer.Get(titleKey), Localizer.Get(bodyKey),
            BuildDialogButtons(DialogKind.Info));

    private void AskRemoveLocalModel() =>
        Dialog.Open(DialogKind.LocalModelDelete, Localizer.Get("Settings.Model.DeleteTitle"),
            Localizer.Get("Settings.Model.DeleteBody"),
            BuildDialogButtons(DialogKind.LocalModelDelete));

    private void ConfirmRemoveLocalModel()
    {
        if (_source.DeleteBuiltIn(out var message) is { } failure)
        {
            SetToast(failure);
            FinishDialog();
            return;
        }

        Settings.Model.SetLocalStatus(_source.LocalModelStatus());
        SetToast(message);
        FinishDialog();
    }

    /// <summary>
    /// Check: install the bundled model when it is missing (with progress), then switch to
    /// it. Downloading is I/O, so it runs off the UI thread; every completion comes back
    /// through the dispatcher.
    /// </summary>
    private async Task CheckLocalModelAsync()
    {
        Settings.Model.BeginInstall();

        var progress = new Progress<double>(fraction =>
            Dispatcher.UIThread.Post(() => Settings.Model.ReportInstallProgress(fraction)));

        string? failure;
        try
        {
            failure = await Task.Run(() => _source.InstallBuiltIn(progress));
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }

        Settings.Model.EndInstall();

        if (failure is not null)
        {
            Settings.Model.EndInstallBusy();
            SetToast(failure);
            return;
        }

        var switchFailure = _source.SwitchToBuiltIn();
        Settings.Model.EndInstallBusy();

        if (switchFailure is not null)
        {
            SetToast(switchFailure);
            return;
        }

        Settings.Model.SetLocalStatus(_source.LocalModelStatus());
        SetToast(_source.LocalModelStatus());
    }

    private void ConfirmIncoming() => AcceptPlan(_dialogItem?.When ?? string.Empty, _dialogItem?.Title ?? string.Empty);

    private void AskIgnore() =>
        OpenDialog(DialogKind.IgnoreConfirm, Localizer.Get("Dialog.IgnoreTitle"), _dialogItem?.Summary ?? string.Empty);

    private void ConfirmIgnore()
    {
        if (_dialogItem is { } ignored)
        {
            if (_source.IncomingIgnored(ignored) is { } failure)
            {
                SetToast(failure);
                FinishDialog();
                return;
            }
        }

        FinishDialog(removeSource: true);
    }

    private void ComposeFromIncoming() =>
        OpenDialog(DialogKind.Composer, Localizer.Get("Dialog.Composer.Title"),
            _dialogItem?.Summary ?? string.Empty, composer: true);

    private void ComposeFromNote() =>
        OpenDialog(DialogKind.Composer, Localizer.Get("Dialog.Composer.Title"),
            _dialogItem?.Title ?? string.Empty, composer: true, composerText: _dialogItem?.Title ?? string.Empty);

    private void EditNote() =>
        OpenDialog(DialogKind.NoteEdit, Localizer.Get("Dialog.EditTitle"), string.Empty,
            editor: true, editorText: _dialogItem?.Title ?? string.Empty);

    private void SaveNote()
    {
        if (_dialogItem is not null && !string.Equals(_dialogItem.Title, Dialog.EditorText, StringComparison.Ordinal))
        {
            _dialogItem.Title = Dialog.EditorText;
            _dialogCard?.NotifyItemsChanged();
            _source.NoteEdited(_dialogItem, Dialog.EditorText);
        }

        FinishDialog();
    }

    /// <summary>The composer's Send: whatever was typed (or the item itself) becomes the plan.</summary>
    private void SendComposer()
    {
        var typed = Dialog.ComposerText.Trim();
        AcceptPlan(_dialogItem?.When ?? string.Empty,
            typed.Length > 0 ? typed : _dialogItem?.Title ?? string.Empty);
    }

    /// <summary>Confirmation path: the item becomes a plan entry and a reminder.</summary>
    private void AcceptPlan(string when, string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            FinishDialog();
            return;
        }

        if (_dialogItem is { } confirmed)
        {
            switch (_dialogOrigin)
            {
                case DialogOrigin.Pending:
                    // The write can be refused by the backing store (a change set that does
                    // not apply). In that case nothing is added and the item stays in place.
                    if (_source.IncomingConfirmed(confirmed) is { } failure)
                    {
                        SetToast(failure);
                        FinishDialog();
                        return;
                    }

                    break;
                case DialogOrigin.Note:
                    _source.NoteEdited(confirmed, title);
                    break;
            }
        }

        Cards[0].Items.Add(new CardItem(title, when));
        Today.Add(title, urgent: false);
        FinishDialog(removeSource: true);
    }

    /// <summary>The one way a dialog ends: forget the item, forget the origin, close the surface.</summary>
    private void FinishDialog(bool removeSource = false)
    {
        if (removeSource && _dialogItem is not null) _dialogCard?.Remove(_dialogItem);
        _dialogItem = null;
        _dialogCard = null;
        _dialogOrigin = DialogOrigin.None;
        Dialog.Close();
    }
}
