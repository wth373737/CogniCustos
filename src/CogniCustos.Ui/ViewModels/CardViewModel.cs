using System.Collections.ObjectModel;
using CogniCustos.Ui.Mvvm;

namespace CogniCustos.Ui.ViewModels;

/// <summary>
/// One board column. Text comes from a factory so switching language does not
/// destroy collapse state or the current contents.
/// </summary>
public sealed class CardViewModel : ObservableObject
{
    private readonly Func<(string Title, string Empty, IReadOnlyList<CardItem> Items)> _source;
    private bool _collapsed;
    private string _title = string.Empty;
    private string _emptyText = string.Empty;

    public CardViewModel(string glyph, string styleClass, bool isPink, bool isOlive,
        Func<(string Title, string Empty, IReadOnlyList<CardItem> Items)> source)
    {
        Glyph = glyph;
        StyleClass = styleClass;
        IsPink = isPink;
        IsOlive = isOlive;
        _source = source;

        ToggleCollapseCommand = new RelayCommand(ToggleCollapse);
        AddCommand = new RelayCommand(AddDemoRow);

        Refresh();
    }

    public string Glyph { get; }
    public string StyleClass { get; }
    public bool IsPink { get; }
    public bool IsOlive { get; }

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

    /// <summary>Re-read every localised string (called when the language changes).</summary>
    public void Refresh()
    {
        var (title, empty, items) = _source();
        Title = title;
        EmptyText = empty;

        Items.Clear();
        foreach (var item in items) Items.Add(item);

        OnPropertyChanged(nameof(IsEmpty));
    }

    private void ToggleCollapse() => IsCollapsed = !_collapsed;

    private void AddDemoRow()
    {
        Items.Add(new CardItem("New entry", DateTime.Now.ToString("HH:mm")));
        OnPropertyChanged(nameof(IsEmpty));
    }
}
