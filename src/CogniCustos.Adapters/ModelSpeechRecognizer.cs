using CogniCustos.Contracts;
using CogniCustos.Core;

namespace CogniCustos.Adapters;

/// <summary>
/// The local ASR model engine — the shape, not the wiring.
///
/// A model is "installed" when the environment points at one (COGNI_ASR_MODEL) and the directory
/// looks like a model. Until then this recogniser reports itself unavailable with that reason, so
/// the settings line can say "还没有安装语音识别模型" instead of the microphone silently failing.
///
/// When the model lands (Qwen3-ASR or similar through a local process), only ListenAsync changes:
/// the seam, the switch, and the UI stay as they are.
/// </summary>
public sealed class ModelSpeechRecognizer : ISpeechRecognizer
{
    public const string ModelPathVariable = "COGNI_ASR_MODEL";

    public string Id => "model:asr";
    public string Engine => "Local speech model";

    public string? ModelPath { get; } = Environment.GetEnvironmentVariable(ModelPathVariable);

    public bool IsAvailable =>
        !string.IsNullOrWhiteSpace(ModelPath) && Directory.Exists(ModelPath);

    public string Detail => IsAvailable
        ? "本地语音识别模型：" + ModelPath
        : string.IsNullOrWhiteSpace(ModelPath)
            ? "还没有安装语音识别模型（设置 " + ModelPathVariable + " 指向模型目录即可启用）。"
            : "语音识别模型目录不存在：" + ModelPath;

    public Task<SpeechResult> ListenAsync(string culture, TimeSpan timeout, CancellationToken ct = default)
    {
        // Deliberately not implemented: this is the seam for the model path, and pretending to
        // recognise speech would be worse than saying it is not ready.
        if (!IsAvailable)
            return Task.FromResult(SpeechResult.Fail(Detail, Id));

        return Task.FromResult(SpeechResult.Fail(
            "语音识别模型接口已预留，但还没有接上推理进程（下一步）。", Id));
    }
}
