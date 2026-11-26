using CogniCustos.Contracts;

namespace CogniCustos.Core;

/// <summary>
/// One way of turning speech into text. Two are planned: the Windows engine that ships with the
/// system, and a local ASR model once one is installed. Keeping them behind this interface means
/// the composer's microphone button never learns which one answered.
/// </summary>
public interface ISpeechRecognizer
{
    /// <summary>Stable id: "system", or "model:&lt;name&gt;".</summary>
    string Id { get; }

    /// <summary>Short name for the settings/status line.</summary>
    string Engine { get; }

    /// <summary>True when this recogniser can actually capture right now.</summary>
    bool IsAvailable { get; }

    /// <summary>What it is, or the concrete reason it cannot be used (never a generic "error").</summary>
    string Detail { get; }

    /// <summary>
    /// Capture one utterance and return the text. <paramref name="culture"/> is the interface
    /// language, so the recogniser can pick a matching acoustic model.
    /// </summary>
    Task<SpeechResult> ListenAsync(string culture, TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>
/// Chooses between the installed recognisers: the local ASR model when the person asked for it
/// and it is installed, otherwise the system engine.
///
/// The model engine is deliberately a hole with a shape: an implementation exists, it reports
/// itself unavailable until a model is actually installed, and switching to it is a flag rather
/// than a rewrite.
/// </summary>
public sealed class VoiceInput
{
    private readonly ISpeechRecognizer _system;
    private readonly ISpeechRecognizer? _model;

    public VoiceInput(ISpeechRecognizer system, ISpeechRecognizer? model = null, bool preferModel = false)
    {
        _system = system;
        _model = model;
        PreferModel = preferModel && model is { IsAvailable: true };
    }

    /// <summary>True when the person asked for the model engine (only honoured if installed).</summary>
    public bool PreferModel { get; private set; }

    public IReadOnlyList<ISpeechRecognizer> Recognizers =>
        _model is null ? [_system] : [_system, _model];

    /// <summary>The recogniser that would answer, model first when it is both requested and installed.</summary>
    public ISpeechRecognizer Active =>
        PreferModel && _model is { IsAvailable: true } ? _model : _system;

    public SpeechInfo Status() =>
        new(Active.IsAvailable, Active.Id, Active.Detail);

    /// <summary>
    /// Ask for the model engine (or the system one). Returns the reason when the request cannot
    /// be honoured — switching to something that is not installed must say so, not fail later.
    /// </summary>
    public string? UseModel(bool useModel)
    {
        if (!useModel)
        {
            PreferModel = false;
            return null;
        }

        if (_model is null) return "这个构建没有接入语音识别模型。";
        if (!_model.IsAvailable) return _model.Detail;

        PreferModel = true;
        return null;
    }

    /// <summary>Capture one utterance with whichever recogniser is active.</summary>
    public Task<SpeechResult> ListenAsync(string culture, TimeSpan timeout, CancellationToken ct = default)
    {
        var active = Active;
        if (!active.IsAvailable) return Task.FromResult(SpeechResult.Fail(active.Detail, active.Id));

        return active.ListenAsync(culture, timeout, ct);
    }
}
