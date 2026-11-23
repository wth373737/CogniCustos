using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CogniCustos.Ui.ViewModels;

namespace CogniCustos.Ui.Views;

public sealed partial class SettingsPage : UserControl
{
    public SettingsPage() => AvaloniaXamlLoader.Load(this);

    private static ModelField? FieldOf(object? sender) => (sender as Control)?.DataContext as ModelField;

    /// <summary>Double-clicking a value row starts editing it and focuses the box.</summary>
    private void OnFieldDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (FieldOf(sender) is not { } field) return;

        field.BeginEdit();
        Dispatcher.UIThread.Post(() =>
        {
            if (sender is Control control)
            {
                foreach (var box in control.GetVisualDescendants().OfType<TextBox>())
                {
                    box.Focus();
                    box.CaretIndex = box.Text?.Length ?? 0;
                }
            }
        });
        e.Handled = true;
    }

    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ModelField field) return;

        if (e.Key == Key.Enter)
        {
            field.Commit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            field.Cancel();
            e.Handled = true;
        }
    }

    private void OnFieldLostFocus(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ModelField field) field.Commit();
    }
}
