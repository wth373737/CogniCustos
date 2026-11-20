using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using CogniCustos.Ui.ViewModels;

namespace CogniCustos.Ui.Views;

public sealed partial class ShellView : UserControl
{
    public ShellView()
    {
        AvaloniaXamlLoader.Load(this);

        // The VS Code XAML previewer instantiates the view without a host window,
        // so give the designer a view-model to bind against.
        if (Design.IsDesignMode) DataContext = new ShellViewModel();
    }

    /// <summary>Click outside the drawer or the help panel closes it.</summary>
    private void OnBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ShellViewModel vm) vm.CloseOverlays();
        e.Handled = true;
    }
}
