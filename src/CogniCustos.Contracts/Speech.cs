namespace CogniCustos.Contracts;

/// <summary>Which speech recogniser would answer, and why it cannot when it cannot.</summary>
/// <param name="Available">True when a recogniser and an input device are both usable.</param>
/// <param name="Engine">"system" (SAPI), "model" (a local ASR model), or "none".</param>
/// <param name="Detail">What it is, or the concrete reason it is unavailable.</param>
public sealed record SpeechInfo(bool Available, string Engine, string Detail)
{
    public static readonly SpeechInfo None = new(false, "none", "no recogniser");
}

/// <summary>One spoken utterance turned into text (or the reason it was not).</summary>
public sealed record SpeechResult(bool Ok, string Text, string Error, long ElapsedMs, string Engine)
{
    public static SpeechResult Fail(string error, string engine = "none") =>
        new(false, string.Empty, error, 0, engine);

    public static SpeechResult Said(string text, long ms, string engine) =>
        new(true, text, string.Empty, ms, engine);
}
