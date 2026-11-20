using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using CogniCustos.Ui.ViewModels;

namespace CogniCustos.Ui;

public sealed partial class ShellWindow : Window
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
}
