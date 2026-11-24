using Avalonia;

namespace CogniCustos.Sample;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--model-check"))
        {
            return ModelCheck.RunAsync().GetAwaiter().GetResult();
        }

        if (args.Contains("--check"))
        {
            return SelfCheck.Run();
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
