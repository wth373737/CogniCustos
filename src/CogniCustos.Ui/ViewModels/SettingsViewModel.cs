using System.Collections.ObjectModel;
using CogniCustos.Ui.Localization;
using CogniCustos.Ui.Mvvm;

namespace CogniCustos.Ui.ViewModels;

/// <summary>
/// Settings page state: a stable list of sections plus the current selection.
/// Sections keep their identity when the language changes, so the selection survives.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private SettingsSectionViewModel? _selected;

    public SettingsViewModel(Strings strings)
    {
        Sections = [];
        for (var i = 0; i < Strings.SectionCount; i++)
        {
            var index = i;
            Sections.Add(new SettingsSectionViewModel($"section-{index}", () => strings.SectionTitle(index)));
        }

        _selected = Sections[0];
    }

    public ObservableCollection<SettingsSectionViewModel> Sections { get; }

    /// <summary>Two-way with the ListBox. A null write (Avalonia resets selection) is ignored on purpose.</summary>
    public SettingsSectionViewModel? Selected
    {
        get => _selected;
        set
        {
            if (value is null) return;
            if (Set(ref _selected, value)) OnPropertyChanged(nameof(SelectedTitle));
        }
    }

    public string SelectedTitle => _selected?.Title ?? string.Empty;

    public void RefreshTitles()
    {
        foreach (var section in Sections) section.Refresh();
        OnPropertyChanged(nameof(SelectedTitle));
    }
}
