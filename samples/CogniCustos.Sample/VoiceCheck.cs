using CogniCustos.Adapters;
using CogniCustos.Core;

namespace CogniCustos.Sample;

/// <summary>
/// The microphone path without a window:
///
///   CogniCustos.Sample.exe --voice-check            what the recognisers report
///   CogniCustos.Sample.exe --voice-check 4          listen for 4 seconds and print what it heard
///
/// It exists because "the mic button does nothing" has four different causes — no recogniser, no
/// device, Windows denying access, and silence — and only a status line tells them apart.
/// </summary>
public static class VoiceCheck
{
    public static int Run(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        var system = new SystemSpeechRecognizer();
        var model = new ModelSpeechRecognizer();
        var voice = new VoiceInput(system, model);
        var culture = Environment.GetEnvironmentVariable("COGNI_LANG") ?? "zh";

        Console.WriteLine($"exe     : {AppContext.BaseDirectory}");
        Console.WriteLine($"culture : {culture}");

        foreach (var recogniser in voice.Recognizers)
        {
            Console.WriteLine($"[{recogniser.Id}] available={recogniser.IsAvailable}");
            Console.WriteLine($"    {recogniser.Detail}");
        }

        var status = voice.Status();
        Console.WriteLine($"active  : {status.Engine} available={status.Available}");
        Console.WriteLine($"model switch: {voice.UseModel(true) ?? "ok (switched to the model engine)"}");

        var seconds = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 0;
        if (seconds <= 0)
        {
            Console.WriteLine("(add a number of seconds to really record, e.g. --voice-check 4)");
            return status.Available ? 0 : 2;
        }

        if (!status.Available)
        {
            Console.WriteLine("skip: the active recogniser is not available (see above).");
            return 2;
        }

        Console.WriteLine($"listening for {seconds}s...");
        var result = voice.ListenAsync(culture, TimeSpan.FromSeconds(seconds)).GetAwaiter().GetResult();
        Console.WriteLine(result.Ok
            ? $"heard ({result.ElapsedMs} ms, {result.Engine}): {result.Text}"
            : $"failed ({result.ElapsedMs} ms, {result.Engine}): {result.Error}");

        return result.Ok ? 0 : 3;
    }
}