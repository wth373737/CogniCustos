using System.Collections.ObjectModel;
using CogniCustos.Ui.Localization;
using CogniCustos.Ui.Mvvm;

namespace CogniCustos.Ui.ViewModels;

/// <summary>One line inside an advanced-settings entry: a prompt segment or one parameter.</summary>
/// <param name="LabelKey">A resource key when the label is localised, else null.</param>
/// <param name="Label">A literal label (parameter names, code constants).</param>
/// <param name="Text">The value: prompt text, or "value (source)".</param>
/// <param name="Locked">True when the code owns this text (JSON contracts, the privacy gate).</param>
public sealed record AdvancedRowData(string? LabelKey, string Label, string Text, bool Locked);

/// <summary>One entry in the advanced panel.</summary>
public sealed record AdvancedItemData(
    string GroupKey,
    string TitleKey,
    string PurposeKey,
    string CallSite,
    IReadOnlyList<AdvancedRowData> Rows);

/// <summary>Everything the advanced panel shows, as the backing store hands it over.</summary>
public sealed record AdvancedSnapshot(IReadOnlyList<AdvancedItemData> Items)
{
    public static readonly AdvancedSnapshot Empty = new([]);
}

/// <summary>A row, ready to bind: labels resolved for the current language.</summary>
public sealed class AdvancedRowViewModel
{
    public AdvancedRowViewModel(AdvancedRowData data)
    {
        Label = data.LabelKey is null ? data.Label : Localizer.Get(data.LabelKey);
        Text = data.Text;
        Locked = data.Locked;
    }

    public string Label { get; }
    public string Text { get; }
    public bool Locked { get; }

    /// <summary>Shown next to locked rows so "why can't I change this?" is answered in place.</summary>
    public bool ShowLockedBadge => Locked;
}

/// <summary>An entry, ready to bind.</summary>
public sealed class AdvancedItemViewModel
{
    public AdvancedItemViewModel(AdvancedItemData data)
    {
        Title = Localizer.Get(data.TitleKey);
        Purpose = Localizer.Get(data.PurposeKey);
        Group = Localizer.Get("Advanced.Group." + data.GroupKey);
        CallSite = data.CallSite;
        Rows = new ObservableCollection<AdvancedRowViewModel>(data.Rows.Select(r => new AdvancedRowViewModel(r)));
        Locked = data.Rows.Any(r => r.Locked);
    }

    public string Title { get; }
    public string Purpose { get; }
    public string Group { get; }
    public string CallSite { get; }
    public bool Locked { get; }
    public ObservableCollection<AdvancedRowViewModel> Rows { get; }

    /// <summary>What the list shows on one line: group · title.</summary>
    public string ListLabel => Group + " · " + Title;
}

/// <summary>
/// The advanced-settings panel behind "高级设置".
///
/// P0 is deliberately read-only: it exists so the prompts and parameters that used to be
/// invisible literals can be *seen*, compared and talked about. The point is that it renders the
/// same catalogue the call sites use — a panel that shows a copy would be worse than no panel.
/// Editing (P1) will write overrides into settings.json and keep these defaults as the fallback.
/// </summary>
public sealed class AdvancedViewModel : ObservableObject, ILocalizedViewModel
{
    private bool _isOpen;
    private AdvancedItemViewModel? _selected;
    private AdvancedSnapshot _snapshot = AdvancedSnapshot.Empty;

    public AdvancedViewModel()
    {
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

    /// <summary>Take a fresh snapshot from the backing store; keeps the selection when possible.</summary>
    public void Load(AdvancedSnapshot snapshot)
    {
        var previousTitle = _selected?.Title;
        _snapshot = snapshot;

        Items.Clear();
        foreach (var item in snapshot.Items) Items.Add(new AdvancedItemViewModel(item));
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

    public void RefreshText()
    {
        Load(_snapshot);
        OnAllPropertiesChanged();
    }
}
