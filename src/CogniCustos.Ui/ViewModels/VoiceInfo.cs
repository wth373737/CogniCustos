namespace CogniCustos.Ui.ViewModels;

/// <summary>Which speech recogniser the composer's microphone would use, and why not when not.</summary>
public sealed record VoiceInfo(bool Available, string Engine, string Detail)
{
    public static readonly VoiceInfo None = new(false, "none", "");
}

/// <summary>One utterance turned into text (or the reason it was not).</summary>
public sealed record VoiceResult(bool Ok, string Text, string Error, long ElapsedMs, string Engine);
