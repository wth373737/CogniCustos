using Avalonia.Input;
using Avalonia.Markup.Xaml;
using CogniCustos.Ui.ViewModels;
using CogniCustos.Ui.Views;

namespace CogniCustos.Ui;

public sealed partial class ShellWindow : LocalizedWindow
{
    public ShellWindow()
    {
        InitializeComponent();
        DataContext = new ShellViewModel();
    }

    public ShellWindow(ShellViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Escape is handled at the window level on purpose: right after launch nothing
    /// has focus, so a key handler on the view would never see the key.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is ShellViewModel vm)
        {
            // The calendar is its own window: close it before touching the shell overlays.
            if (vm.Calendar.IsVisible) vm.Calendar.Close();
            else vm.CloseOverlays();

            e.Handled = true;
        }

        base.OnKeyDown(e);
    }
}
