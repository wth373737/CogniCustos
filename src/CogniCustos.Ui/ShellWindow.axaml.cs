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
            // Top-most first: dialog, calendar, reader, advanced panel, shell overlays.
            if (vm.Dialog.IsOpen) vm.Dialog.CancelCommand.Execute(null);
            else if (vm.Calendar.IsVisible) vm.Calendar.Close();
            else if (vm.Reader.IsOpen) vm.Reader.Close();
            else if (vm.Advanced.IsOpen) vm.Advanced.Close();
            else if (vm.Reasoning.IsAnyOpen) vm.Reasoning.Close();
            else vm.CloseOverlays();

            e.Handled = true;
        }

        base.OnKeyDown(e);
    }
}
