using CogniCustos.Ui.Mvvm;

namespace CogniCustos.Ui.ViewModels;

/// <summary>
/// One settings section. The instance is created exactly once and keeps its
/// identity across language switches; only <see cref="Title"/> changes.
/// That is what keeps the ListBox selection alive.
/// </summary>
public sealed class SettingsSectionViewModel : ObservableObject
{
    private readonly Func<string> _titleSource;
    private string _title;

    public SettingsSectionViewModel(string key, Func<string> titleSource)
    {
        Key = key;
        _titleSource = titleSource;
        _title = titleSource();
    }

    /// <summary>Stable identifier; use it to route to a section's content later.</summary>
    public string Key { get; }

    public string Title
    {
        get => _title;
        private set => Set(ref _title, value);
    }

    public void Refresh() => Title = _titleSource();
}
