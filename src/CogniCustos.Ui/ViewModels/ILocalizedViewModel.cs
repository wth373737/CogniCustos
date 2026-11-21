namespace CogniCustos.Ui.ViewModels;

/// <summary>
/// Implemented by every window view-model that holds localised text.
/// On a language change the window calls <see cref="RefreshText"/> so text that lives
/// in the view-model (card titles, section names, dates) is re-read from the catalog.
/// XAML text does not need this — it is bound with DynamicResource.
/// </summary>
public interface ILocalizedViewModel
{
    void RefreshText();
}
