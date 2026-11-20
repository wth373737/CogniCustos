using System.Collections.ObjectModel;
using CogniCustos.Ui.Localization;
using CogniCustos.Ui.Mvvm;

namespace CogniCustos.Ui.ViewModels;

/// <summary>
/// Settings page state: a stable list of sections plus the current selection.
/// Section titles come from the language resources; the item instances themselves
/// are never replaced, so the ListBox selection survives a language switch.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    /// <summary>Resource keys, in the order of the sketch. Also asserted by the tests.</summary>
    private static readonly string[] SectionKeys =
    [
        "Section.General", "Section.Model", "Section.EmailRetrieval", "Section.ChatRetrieval",
        "Section.WebsiteBrowsing", "Section.StudentNetwork", "Section.Prompt", "Section.Hotkeys",
        "Section.Privacy", "Section.KnowledgeBase", "Section.Database", "Section.Sandbox",
        "Section.Storage", "Section.Software", "Section.Notifications", "Section.Offline",
    ];

    private SettingsSectionViewModel? _selected;

    public SettingsViewModel()
    {
        Sections = [];
        for (var i = 0; i < SectionKeys.Length; i++)
        {
            var key = SectionKeys[i];
            Sections.Add(new SettingsSectionViewModel(key, () => Localizer.Get(key)));
        }

        _selected = Sections[0];
    }

    public static IReadOnlyList<string> Keys => SectionKeys;

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
