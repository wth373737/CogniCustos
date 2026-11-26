using Avalonia;

namespace CogniCustos.Sample;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--mail-peek"))
        {
            return MailPeek.Run();
        }

        if (args.Contains("--fetch-now"))
        {
            return FetchNow.RunAsync().GetAwaiter().GetResult();
        }

        if (args.Contains("--model-check"))
        {
            return ModelCheck.RunAsync(args.Contains("--gpu")).GetAwaiter().GetResult();
        }

        if (args.Contains("--check"))
        {
            return SelfCheck.Run();
        }

        var readerIndex = Array.IndexOf(args, "--reader-check");
        if (readerIndex >= 0)
        {
            var target = readerIndex + 1 < args.Length ? args[readerIndex + 1] : string.Empty;
            return ReaderCheck.Run(target);
        }

        if (Array.IndexOf(args, "--model-status") >= 0) return ModelStatus.Run(args);

        if (Array.IndexOf(args, "--voice-check") >= 0) return VoiceCheck.Run(args.Skip(Array.IndexOf(args, "--voice-check")).ToArray());

        var askIndex = Array.IndexOf(args, "--reader-ask");
        if (askIndex >= 0)
        {
            return ReaderAsk.Run(args.Skip(askIndex).ToArray());
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
