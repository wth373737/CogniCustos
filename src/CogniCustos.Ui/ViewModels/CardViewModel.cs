using System.Collections.ObjectModel;
using CogniCustos.Ui.Mvvm;

namespace CogniCustos.Ui.ViewModels;

/// <summary>
/// What a board card stands for. The first card is a compressed calendar, not a
/// list of today's entries — today lives in the reminder pill at the top.
/// </summary>
public enum CardKind
{
    Calendar,
    Pending,
    Note,
}

/// <summary>
/// One board column. Text comes from a factory so switching language does not
/// destroy collapse state, and the seed rows are kept separate from the rows the
/// user added at runtime.
/// </summary>
public sealed class CardViewModel : ObservableObject
{
    private readonly Func<(string Title, string Empty, IReadOnlyList<CardItem> Items)> _source;
    private readonly Func<string> _newRowText;
    private readonly Action? _onDateClick;
    private readonly Action<CardItem>? _onItemClick;
    private int _seedCount;
    private bool _collapsed;
    private string _title = string.Empty;
    private string _emptyText = string.Empty;

    public CardViewModel(CardKind kind, string glyph,
        Func<(string Title, string Empty, IReadOnlyList<CardItem> Items)> source,
        Func<string> newRowText,
        Action? onDateClick = null,
        Action<CardItem>? onItemClick = null)
    {
        _onDateClick = onDateClick;
        _onItemClick = onItemClick;
        Kind = kind;
        Glyph = glyph;
        _source = source;
        _newRowText = newRowText;

        ToggleCollapseCommand = new RelayCommand(ToggleCollapse);
        AddCommand = new RelayCommand(AddRow);

        Refresh();
    }

    public CardKind Kind { get; }

    /// <summary>MDL2 glyph shown in the card header.</summary>
    public string Glyph { get; }

    // Style-class bindings (Classes.pink / Classes.olive in the template).
    public bool IsPink => Kind == CardKind.Pending;
    public bool IsOlive => Kind == CardKind.Note;

    /// <summary>The calendar card has no rows and no "+" (the sketch has none either... it has one, but no action yet).</summary>
    public bool CanAdd => Kind != CardKind.Calendar;

    /// <summary>Every card carries rows now: the calendar card holds the plan.</summary>
    public bool HasRows => true;

    /// <summary>The calendar card's date opens the calendar window.</summary>
    public bool ShowDateButton => _onDateClick is not null;

    public RelayCommand OpenDateCommand => _openDate ??= new RelayCommand(() => _onDateClick?.Invoke());

    private RelayCommand? _openDate;

    /// <summary>The sketch shows "+" on the calendar and note cards, but not on pending.</summary>
    public bool ShowAdd => Kind != CardKind.Pending;

    public ObservableCollection<CardItem> Items { get; } = [];

    public RelayCommand ToggleCollapseCommand { get; }
    public RelayCommand AddCommand { get; }

    /// <summary>Rows are clickable when the card has a handler (incoming → decision,
    /// note → edit / let AI handle); the same command is stamped onto every row.</summary>
    private RelayCommand ItemCommand => _itemCommand ??= new RelayCommand(parameter =>
    {
        if (parameter is CardItem item) _onItemClick?.Invoke(item);
    });

    private RelayCommand? _itemCommand;

    public string Title
    {
        get => _title;
        private set => Set(ref _title, value);
    }

    public string EmptyText
    {
        get => _emptyText;
        private set => Set(ref _emptyText, value);
    }

    public bool IsCollapsed
    {
        get => _collapsed;
        private set
        {
            if (!Set(ref _collapsed, value)) return;
            OnPropertyChanged(nameof(IsExpanded));
            OnPropertyChanged(nameof(CardHeight));
            OnPropertyChanged(nameof(CollapseGlyph));
        }
    }

    public bool IsExpanded => !_collapsed;

    public double CardHeight => _collapsed ? 78 : 300;

    /// <summary>MDL2 chevron for the header: up while expanded (collapse), down while collapsed.</summary>
    public string CollapseGlyph => _collapsed ? "\uE70D" : "\uE70E";

    public bool IsEmpty => Items.Count == 0;

    /// <summary>
    /// Re-read every localised string (called when the language changes).
    /// Only the seed rows are replaced; rows added at runtime stay where they are.
    /// </summary>
    public void Refresh()
    {
        var (title, empty, items) = _source();
        Title = title;
        EmptyText = empty;

        for (var i = 0; i < _seedCount && Items.Count > 0; i++) Items.RemoveAt(0);
        for (var i = 0; i < items.Count; i++) Items.Insert(i, items[i]);
        _seedCount = items.Count;
        StampCommands();

        OnPropertyChanged(nameof(IsEmpty));
    }

    private void StampCommands()
    {
        var command = _onItemClick is null ? null : ItemCommand;
        foreach (var item in Items) item.Command = command;
    }

    /// <summary>Removes a row (used when a decision resolves it).</summary>
    public void Remove(CardItem item)
    {
        if (!Items.Remove(item)) return;
        if (Items.Count < _seedCount) _seedCount = Items.Count;
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Rows were edited in place (the note editor).</summary>
    public void NotifyItemsChanged() => OnPropertyChanged(nameof(IsEmpty));

    private void ToggleCollapse() => IsCollapsed = !_collapsed;

    private void AddRow()
    {
        if (!CanAdd) return;
        var row = new CardItem(_newRowText(), DateTime.Now.ToString("HH:mm"));
        row.Command = _onItemClick is null ? null : ItemCommand;
        Items.Add(row);
        OnPropertyChanged(nameof(IsEmpty));
    }
}
