using System.Collections.ObjectModel;
using CogniCustos.Ui.Mvvm;

namespace CogniCustos.Ui.ViewModels;

/// <summary>Visual variant of a board card; each value maps to a style class in Controls.axaml.</summary>
public enum CardVariant
{
    Plain,
    Pink,
    Olive,
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

    public CardViewModel(string glyph, CardVariant variant,
        Func<(string Title, string Empty, IReadOnlyList<CardItem> Items)> source,
        Func<string> newRowText)
    {
        Glyph = glyph;
        Variant = variant;
        _source = source;
        _newRowText = newRowText;

        ToggleCollapseCommand = new RelayCommand(ToggleCollapse);
        AddCommand = new RelayCommand(AddRow);

        Refresh();
    }

    /// <summary>MDL2 glyph shown in the card header.</summary>
    public string Glyph { get; }

    public CardVariant Variant { get; }

    // Style-class bindings (Classes.pink / Classes.olive in the template).
    public bool IsPink => Variant == CardVariant.Pink;
    public bool IsOlive => Variant == CardVariant.Olive;

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

    /// <summary>MDL2 chevron: down when expanded, up when collapsed.</summary>
    public string CollapseGlyph => _collapsed ? "\uE70E" : "\uE70D";

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
        Items.Add(new CardItem(_newRowText(), DateTime.Now.ToString("HH:mm")));
        OnPropertyChanged(nameof(IsEmpty));
    }
}
