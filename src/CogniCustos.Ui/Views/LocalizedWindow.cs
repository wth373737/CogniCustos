using Avalonia.Controls;
using CogniCustos.Ui.Localization;
using CogniCustos.Ui.ViewModels;

namespace CogniCustos.Ui.Views;

/// <summary>
/// Base window that keeps itself in sync with the app language:
///   * the OS window title is re-read from the catalog;
///   * any <see cref="ILocalizedViewModel"/> gets <see cref="ILocalizedViewModel.RefreshText"/>.
/// Subscribing here (and unsubscribing on close) is what keeps a static event from
/// pinning closed windows — with several windows that matters.
/// </summary>
public abstract class LocalizedWindow : Window
{
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Localizer.LanguageChanged += OnLanguageChanged;
        ApplyLocalizedText();
    }

    protected override void OnClosed(EventArgs e)
    {
        Localizer.LanguageChanged -= OnLanguageChanged;
        base.OnClosed(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        ApplyLocalizedText();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => ApplyLocalizedText();

    /// <summary>Called on open, on language change, and when the data context is set.</summary>
    protected virtual void ApplyLocalizedText()
    {
        Title = Localizer.WindowTitle;

        if (DataContext is ILocalizedViewModel vm) vm.RefreshText();
    }
}
