using System.Collections.ObjectModel;
using CogniCustos.Ui.Localization;
using CogniCustos.Ui.Mvvm;

namespace CogniCustos.Ui.ViewModels;

/// <summary>
/// The reminder line at the top of the window: today's entries, played as a
/// marquee, expandable into a list. This — not the first card — is "today".
/// Each entry knows whether it is urgent (needs attention today) so the line can
/// draw it in dark red.
/// </summary>
public sealed class TodayViewModel : ObservableObject
{
    private readonly List<ReminderItem> _seed = [];
    private bool _listOpen;

    public TodayViewModel()
    {
        ToggleListCommand = new RelayCommand(ToggleList);
    }

    public ObservableCollection<ReminderItem> Items { get; } = [];

    public RelayCommand ToggleListCommand { get; }

    public bool IsEmpty => Items.Count == 0;

    public bool IsListOpen
    {
        get => _listOpen;
        private set
        {
            if (!Set(ref _listOpen, value)) return;
            OnPropertyChanged(nameof(ListChevron));
        }
    }

    /// <summary>Chevron in the reminder pill: down when closed, up when the list is open.</summary>
    public string ListChevron => _listOpen ? "\uE70E" : "\uE70D";

    public string Title => Localizer.Get("Today.Title");

    public void ToggleList() => IsListOpen = !_listOpen;

    public void CloseList() => IsListOpen = false;

    /// <summary>Adds a user entry (from the input box or a confirmed incoming item).</summary>
    public void Add(string text, bool urgent = false)
    {
        Items.Add(new ReminderItem(text, urgent));
        NotifyContent();
    }

    /// <summary>
    /// Replaces the localised seed entries but keeps whatever the user added,
    /// the same rule the board cards use.
    /// </summary>
    public void ResetSeeds(IReadOnlyList<ReminderItem> seeds)
    {
        for (var i = 0; i < _seed.Count && Items.Count > 0; i++) Items.RemoveAt(0);
        for (var i = 0; i < seeds.Count; i++) Items.Insert(i, seeds[i]);

        _seed.Clear();
        _seed.AddRange(seeds);
        NotifyContent();
        OnPropertyChanged(nameof(Title));
    }

    private void NotifyContent()
    {
        OnPropertyChanged(nameof(IsEmpty));
    }
}
