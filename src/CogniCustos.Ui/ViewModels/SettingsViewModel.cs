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
        "Section.General", "Section.Appearance", "Section.Model", "Section.EmailRetrieval", "Section.ChatRetrieval",
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
        Model = new ModelConfigurationViewModel();
        EmailRetrieval = new EmailRetrievalViewModel();
        WebBrowsing = new WebBrowsingViewModel();
    }

    /// <summary>The content of 邮件抓取配置.</summary>
    public EmailRetrievalViewModel EmailRetrieval { get; }

    /// <summary>The content of 网页浏览配置.</summary>
    public WebBrowsingViewModel WebBrowsing { get; }

    /// <summary>The content of the Model Configuration section.</summary>
    public ModelConfigurationViewModel Model { get; }

    public static IReadOnlyList<string> Keys => SectionKeys;

    public ObservableCollection<SettingsSectionViewModel> Sections { get; }

    /// <summary>Two-way with the ListBox. A null write (Avalonia resets selection) is ignored on purpose.</summary>
    public SettingsSectionViewModel? Selected
    {
        get => _selected;
        set
        {
            if (value is null) return;
            if (!Set(ref _selected, value)) return;
            OnPropertyChanged(nameof(SelectedTitle));
            OnPropertyChanged(nameof(ShowPreviewGallery));
            OnPropertyChanged(nameof(ShowModelConfiguration));
            OnPropertyChanged(nameof(ShowEmailRetrieval));
            OnPropertyChanged(nameof(ShowWebBrowsing));
            OnPropertyChanged(nameof(ShowPlaceholder));
        }
    }

    public string SelectedTitle => _selected?.Title ?? string.Empty;

    /// <summary>The interface gallery replaces the placeholder for 界面配置 only.</summary>
    public bool ShowPreviewGallery => _selected?.Key == "Section.Appearance";

    /// <summary>Model Configuration owns the only real form so far.</summary>
    public bool ShowModelConfiguration => _selected?.Key == "Section.Model";

    /// <summary>邮件抓取配置 owns the account list.</summary>
    public bool ShowEmailRetrieval => _selected?.Key == "Section.EmailRetrieval";

    /// <summary>网页浏览配置 owns the page list.</summary>
    public bool ShowWebBrowsing => _selected?.Key == "Section.WebsiteBrowsing";

    /// <summary>The generic placeholder only shows for sections with no form at all.</summary>
    public bool ShowPlaceholder =>
        !ShowPreviewGallery && !ShowModelConfiguration && !ShowEmailRetrieval && !ShowWebBrowsing;

    public void RefreshTitles()
    {
        foreach (var section in Sections) section.Refresh();
        OnPropertyChanged(nameof(SelectedTitle));
        OnPropertyChanged(nameof(ShowPreviewGallery));
        OnPropertyChanged(nameof(ShowModelConfiguration));
        OnPropertyChanged(nameof(ShowEmailRetrieval));
        OnPropertyChanged(nameof(ShowWebBrowsing));
        OnPropertyChanged(nameof(ShowPlaceholder));
        Model.RefreshText();
        EmailRetrieval.RefreshText();
        WebBrowsing.RefreshText();
    }
}
