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
    private int _seedCount;
    private bool _collapsed;
    private string _title = string.Empty;
    private string _emptyText = string.Empty;

    public CardViewModel(CardKind kind, string glyph,
        Func<(string Title, string Empty, IReadOnlyList<CardItem> Items)> source,
        Func<string> newRowText)
    {
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

    /// <summary>True for cards that carry a list of rows (pending / note).</summary>
    public bool HasRows => Kind != CardKind.Calendar;

    /// <summary>The sketch shows "+" on the calendar and note cards, but not on pending.</summary>
    public bool ShowAdd => Kind != CardKind.Pending;

    public ObservableCollection<CardItem> Items { get; } = [];

    public RelayCommand ToggleCollapseCommand { get; }
    public RelayCommand AddCommand { get; }

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

        OnPropertyChanged(nameof(IsEmpty));
    }

    private void ToggleCollapse() => IsCollapsed = !_collapsed;

    private void AddRow()
    {
        if (!CanAdd) return;
        Items.Add(new CardItem(_newRowText(), DateTime.Now.ToString("HH:mm")));
        OnPropertyChanged(nameof(IsEmpty));
    }
}
