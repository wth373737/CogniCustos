using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using CogniCustos.Core;
using CogniCustos.Ui.ViewModels;

namespace CogniCustos.Sample;

public sealed partial class SampleWindow : Window
{
    private readonly RuntimeShellDataSource _source;

    public SampleWindow()
    {
        AvaloniaXamlLoader.Load(this);

        var root = AppRuntime.Home();

        // one assembly point, shared with the CLI checks and --model-status
        var (runtime, vision) = AppRuntime.Build(root);
        _source = new RuntimeShellDataSource(runtime, vision);

        // today's fetch runs before the shell is built, so freshly proposed items are
        // already on the incoming card when the window opens
        if (runtime.Poller.IsDue(DateTime.Today))
        {
            try
            {
                runtime.Poller.RunAsync(DateTime.Today).GetAwaiter().GetResult();
            }
            catch
            {
                // a failing source must not stop the app from opening
            }
        }

        DataContext = new ShellViewModel(_source);
        Title = $"CogniCustos — Sample · {_source.DataRoot}";
    }

}
