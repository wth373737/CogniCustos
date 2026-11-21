using CogniCustos.Ui.Mvvm;

namespace CogniCustos.Ui.ViewModels;

/// <summary>
/// One row in the language menu. The menu is generated from
/// <see cref="CogniCustos.Ui.Localization.Localizer.Languages"/>, so adding a language
/// needs no XAML change.
/// </summary>
public sealed class LanguageOption
{
    public LanguageOption(string code, string name, bool isActive, RelayCommand selectCommand)
    {
        Code = code;
        Name = name;
        IsActive = isActive;
        SelectCommand = selectCommand;
    }

    public string Code { get; }
    public string Name { get; }
    public bool IsActive { get; }
    public RelayCommand SelectCommand { get; }
}
