using System.Collections.ObjectModel;
using System.Windows.Input;
using CogniCustos.Ui.Localization;
using CogniCustos.Ui.Mvvm;

namespace CogniCustos.Ui.ViewModels;

/// <summary>One line inside an advanced-settings entry: a prompt segment or one parameter.</summary>
/// <param name="LabelKey">A resource key when the label is localised, else null.</param>
/// <param name="Label">A literal label (parameter names, code constants).</param>
/// <param name="Text">The value in force: prompt text, or "value (source)".</param>
/// <param name="Locked">True when the code owns this text (JSON contracts, the privacy gate).</param>
/// <param name="PromptKey">Which prompt this segment belongs to (empty for parameters).</param>
/// <param name="SegmentId">Which segment of that prompt (empty for parameters).</param>
/// <param name="Overridden">True when the value in force is a user override, not the default.</param>
public sealed record AdvancedRowData(
    string? LabelKey,
    string Label,
    string Text,
    bool Locked,
    string PromptKey = "",
    string SegmentId = "",
    bool Overridden = false);

/// <summary>One entry in the advanced panel.</summary>
public sealed record AdvancedItemData(
    string GroupKey,
    string TitleKey,
    string PurposeKey,
    string CallSite,
    IReadOnlyList<AdvancedRowData> Rows,
    bool CanTry = false);

/// <summary>Everything the advanced panel shows, as the backing store hands it over.</summary>
public sealed record AdvancedSnapshot(IReadOnlyList<AdvancedItemData> Items)
{
    public static readonly AdvancedSnapshot Empty = new([]);
}

/// <summary>The outcome of one edit: what happened, and the text now in force.</summary>
public sealed record PromptEditResult(string? Failure, string Text, bool Overridden);

/// <summary>What a "try it" run produced.</summary>
public sealed record PromptTryResult(bool Ok, string Prompt, string Reply, long ElapsedMs, string Message);

/// <summary>A row, ready to bind: labels resolved, edit box seeded, save/reset wired.</summary>
public sealed class AdvancedRowViewModel : ObservableObject
{
    private readonly Func<AdvancedRowViewModel, string, PromptEditResult> _save;
    private readonly Func<AdvancedRowViewModel, PromptEditResult> _reset;

    private string _editText;
    private string _status = string.Empty;
    private string _text;

    public AdvancedRowViewModel(AdvancedRowData data,
        Func<AdvancedRowViewModel, string, PromptEditResult> save,
        Func<AdvancedRowViewModel, PromptEditResult> reset)
    {
        _save = save;
        _reset = reset;

        Label = data.LabelKey is null ? data.Label : Localizer.Get(data.LabelKey);
        _text = data.Text;
        Locked = data.Locked;
        PromptKey = data.PromptKey;
        SegmentId = data.SegmentId;
        Overridden = data.Overridden;
        _editText = data.Text;

        SaveCommand = new RelayCommand(() => Save(), () => Editable);
        ResetCommand = new RelayCommand(() => Reset(), () => Overridden);
    }

    public string Label { get; }

    /// <summary>The text currently in force (default, or the user's override).</summary>
    public string Text => _text;

    public bool Locked { get; }
    public string PromptKey { get; }
    public string SegmentId { get; }

    /// <summary>A segment with a prompt/segment id and no lock is editable in P1.</summary>
    public bool Editable => !Locked && PromptKey.Length > 0 && SegmentId.Length > 0;

    public bool ShowLockedBadge => Locked;
    public bool ShowOverrideBadge => Overridden;

    public bool Overridden { get; private set; }

    /// <summary>Replace what is in force without rebuilding the panel (keeps the status line).</summary>
    public void ApplyInForce(string text, bool overridden)
    {
        _text = text;
        Overridden = overridden;
        _editText = text;

        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(EditText));
        OnPropertyChanged(nameof(Overridden));
        OnPropertyChanged(nameof(ShowOverrideBadge));
        OnPropertyChanged(nameof(IsDirty));
        ResetCommand.RaiseCanExecuteChanged();
    }

    public string EditText
    {
        get => _editText;
        set
        {
            if (!Set(ref _editText, value)) return;
            Status = string.Empty;
            SaveCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>The line under an edited row: what happened, or why it was refused.</summary>
    public string Status
    {
        get => _status;
        private set
        {
            if (!Set(ref _status, value)) return;
            OnPropertyChanged(nameof(HasStatus));
        }
    }

    public bool HasStatus => _status.Length > 0;

    /// <summary>True when the edit box differs from the value in force.</summary>
    public bool IsDirty => Editable && !string.Equals(_editText, Text, StringComparison.Ordinal);

    public RelayCommand SaveCommand { get; }
    public RelayCommand ResetCommand { get; }

    public void Save()
    {
        if (!Editable) return;

        var result = _save(this, _editText);
        if (result.Failure is null) ApplyInForce(result.Text, result.Overridden);
        Status = result.Failure ?? Localizer.Get("Advanced.Saved");
    }

    public void Reset()
    {
        var result = _reset(this);
        if (result.Failure is null) ApplyInForce(result.Text, result.Overridden);
        Status = result.Failure ?? Localizer.Get("Advanced.Reset");
    }

    /// <summary>Set by the panel when the store reported a reason (validation, disk, …).</summary>
    public void SetStatus(string message) => Status = message;
}

/// <summary>An entry, ready to bind.</summary>
public sealed class AdvancedItemViewModel : ObservableObject
{
    private readonly Func<AdvancedItemViewModel, Task>? _try;
    private bool _isTrying;
    private string _tryOutput = string.Empty;
    private string _tryPrompt = string.Empty;

    public AdvancedItemViewModel(AdvancedItemData data,
        Func<AdvancedRowViewModel, string, PromptEditResult> save,
        Func<AdvancedRowViewModel, PromptEditResult> reset,
        Func<AdvancedItemViewModel, Task>? tryRun)
    {
        _try = tryRun;

        Title = Localizer.Get(data.TitleKey);
        Purpose = Localizer.Get(data.PurposeKey);
        Group = Localizer.Get("Advanced.Group." + data.GroupKey);
        CallSite = data.CallSite;
        PromptKey = data.Rows.FirstOrDefault(r => r.PromptKey.Length > 0)?.PromptKey ?? string.Empty;
        Rows = new ObservableCollection<AdvancedRowViewModel>(
            data.Rows.Select(r => new AdvancedRowViewModel(r, save, reset)));
        Locked = data.Rows.Any(r => r.Locked);
        CanTry = data.CanTry && PromptKey.Length > 0;

        TryCommand = new RelayCommand(() => _ = TryAsync(), () => CanTry && !_isTrying);
    }

    public string Title { get; }
    public string Purpose { get; }
    public string Group { get; }
    public string CallSite { get; }
    public bool Locked { get; }
    public string PromptKey { get; }
    public ObservableCollection<AdvancedRowViewModel> Rows { get; }

    public bool CanTry { get; }
    public RelayCommand TryCommand { get; }

    public bool IsTrying
    {
        get => _isTrying;
        private set
        {
            if (!Set(ref _isTrying, value)) return;
            TryCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(TryLabel));
        }
    }

    public string TryLabel => Localizer.Get(_isTrying ? "Advanced.Trying" : "Advanced.Try");

    /// <summary>The exact text the try-run sent (so an unexpected answer can be traced).</summary>
    public string TryPrompt
    {
        get => _tryPrompt;
        private set
        {
            if (!Set(ref _tryPrompt, value)) return;
            OnPropertyChanged(nameof(HasTryPrompt));
        }
    }

    public bool HasTryPrompt => _tryPrompt.Length > 0;

    public string TryOutput
    {
        get => _tryOutput;
        private set
        {
            if (!Set(ref _tryOutput, value)) return;
            OnPropertyChanged(nameof(HasTryOutput));
        }
    }

    public bool HasTryOutput => _tryOutput.Length > 0;

    public async Task TryAsync()
    {
        if (_try is null || IsTrying) return;

        IsTrying = true;
        TryOutput = string.Empty;
        TryPrompt = string.Empty;
        try
        {
            await _try(this).ConfigureAwait(true);
        }
        finally
        {
            IsTrying = false;
        }
    }

    public void ShowTry(PromptTryResult result)
    {
        TryPrompt = result.Prompt;
        TryOutput = result.Ok
            ? Localizer.Get("Advanced.TryOk") + " · " + result.ElapsedMs + " ms\n\n" + result.Reply
            : result.Message;
    }
}

/// <summary>
/// The advanced-settings panel behind "高级设置".
///
/// P0 made the prompts visible; P1 makes the segments the code does not parse editable, keeps the
/// shipped text as the fallback, and adds a "try it" button that sends one real request so an edit
/// can be judged instead of guessed at.
/// </summary>
public sealed class AdvancedViewModel : ObservableObject, ILocalizedViewModel
{
    private readonly Func<AdvancedRowViewModel, string, PromptEditResult> _save;
    private readonly Func<AdvancedRowViewModel, PromptEditResult> _reset;
    private readonly Func<AdvancedItemViewModel, Task> _try;

    private bool _isOpen;
    private AdvancedItemViewModel? _selected;
    private AdvancedSnapshot _snapshot = AdvancedSnapshot.Empty;

    public AdvancedViewModel(
        Func<AdvancedRowViewModel, string, PromptEditResult>? save = null,
        Func<AdvancedRowViewModel, PromptEditResult>? reset = null,
        Func<AdvancedItemViewModel, Task>? tryRun = null)
    {
        // no store (the demo, a fixture): saving is refused with a reason instead of throwing
        _save = save ?? ((row, _) => new PromptEditResult(
            Localizer.Get("Advanced.NoStore"), row.Text, row.Overridden));
        _reset = reset ?? (row => new PromptEditResult(
            Localizer.Get("Advanced.NoStore"), row.Text, row.Overridden));
        _try = tryRun ?? (item =>
        {
            item.ShowTry(new PromptTryResult(false, string.Empty, string.Empty, 0,
                Localizer.Get("Advanced.NoStore")));
            return Task.CompletedTask;
        });

        CloseCommand = new RelayCommand(Close);
    }

    public ObservableCollection<AdvancedItemViewModel> Items { get; } = [];

    public bool IsOpen
    {
        get => _isOpen;
        private set => Set(ref _isOpen, value);
    }

    public AdvancedItemViewModel? Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    public bool HasItems => Items.Count > 0;

    public RelayCommand CloseCommand { get; }

    public string Title => Localizer.Get("Advanced.Title");
    public string Note => Localizer.Get("Advanced.Note");
    public string Footer => Localizer.Get("Advanced.Footer");
    public string CloseLabel => Localizer.Get("Advanced.Close");
    public string CallSitePrefix => Localizer.Get("Advanced.CallSite");
    public string LockedLabel => Localizer.Get("Advanced.Locked");
    public string OverrideLabel => Localizer.Get("Advanced.Overridden");
    public string SaveLabel => Localizer.Get("Advanced.Save");
    public string ResetLabel => Localizer.Get("Advanced.ResetDefault");

    /// <summary>Take a fresh snapshot from the backing store; keeps the selection when possible.</summary>
    public void Load(AdvancedSnapshot snapshot)
    {
        var previousTitle = _selected?.Title;
        _snapshot = snapshot;

        Items.Clear();
        foreach (var item in snapshot.Items)
            Items.Add(new AdvancedItemViewModel(item, _save, _reset, _try));
        OnPropertyChanged(nameof(HasItems));

        Selected = previousTitle is null
            ? Items.FirstOrDefault()
            : Items.FirstOrDefault(i => i.Title == previousTitle) ?? Items.FirstOrDefault();
    }

    public void Open(AdvancedSnapshot snapshot)
    {
        Load(snapshot);
        IsOpen = true;
    }

    public void Close() => IsOpen = false;

    /// <summary>Re-read after an edit so badges and "in force" text match the store again.</summary>
    public void Reload(AdvancedSnapshot snapshot)
    {
        var selected = _selected?.Title;
        Load(snapshot);
        Selected = Items.FirstOrDefault(i => i.Title == selected) ?? Items.FirstOrDefault();
    }

    public void RefreshText()
    {
        Load(_snapshot);
        OnAllPropertiesChanged();
    }
}
