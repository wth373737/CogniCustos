using System.Diagnostics;
using System.Globalization;
using System.Speech.Recognition;
using CogniCustos.Contracts;
using CogniCustos.Core;

namespace CogniCustos.Adapters;

/// <summary>
/// The recogniser that ships with Windows (SAPI, via System.Speech): no download, no model
/// server, a few milliseconds to start. It picks the acoustic model that matches the interface
/// language, and says plainly when there is no microphone or no recogniser installed — a button
/// that silently does nothing is worse than one that explains itself.
/// </summary>
public sealed class SystemSpeechRecognizer : ISpeechRecognizer
{
    private string? _detail;
    private bool _probed;

    public string Id => "system";
    public string Engine => "Windows speech";

    public bool IsAvailable
    {
        get
        {
            Probe();
            return _available;
        }
    }

    private bool _available;

    public string Detail
    {
        get
        {
            Probe();
            return _detail ?? string.Empty;
        }
    }

    /// <summary>
    /// Availability is decided once: the recogniser list is cheap, opening the audio device is
    /// not, and a failing device is not going to start working between two clicks.
    /// </summary>
    private void Probe()
    {
        if (_probed) return;
        _probed = true;

        try
        {
            var installed = SpeechRecognitionEngine.InstalledRecognizers();
            if (installed.Count == 0)
            {
                _available = false;
                _detail = "系统里没有安装语音识别引擎（Windows 语音识别未启用）。";
                return;
            }

            using var engine = new SpeechRecognitionEngine();
            engine.SetInputToDefaultAudioDevice();
            _available = true;
            _detail = $"系统语音识别 · {installed.Count} 个引擎 · 默认麦克风可用";
        }
        catch (Exception ex)
        {
            _available = false;
            _detail = "没有可用的麦克风或语音识别引擎：" + ex.Message
                      + NonAsciiPathHint();
        }
    }

    /// <summary>
    /// A non-ASCII install path makes SAPI's capture fail with 0x80070005 on this machine while
    /// the same executable works from an ASCII path. Saying so turns a mystery into a move.
    /// </summary>
    private static string NonAsciiPathHint()
    {
        var directory = AppContext.BaseDirectory;
        return directory.Any(c => c > 127)
            ? "\n这个程序所在的路径含非 ASCII 字符（" + directory + "）。Windows 语音识别在这里会被拒绝"
              + "（0x80070005），把它放到纯英文路径下（例如 C:\\CogniCustos）再试。"
            : string.Empty;
    }

    public Task<SpeechResult> ListenAsync(string culture, TimeSpan timeout, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var clock = Stopwatch.StartNew();
            try
            {
                using var engine = new SpeechRecognitionEngine(Pick(culture));
                engine.SetInputToDefaultAudioDevice();
                engine.LoadGrammar(new DictationGrammar());

                // Recognize() blocks until the person speaks or the timeout expires; that is the
                // whole feature: press, speak, text appears.
                var result = engine.Recognize(timeout);
                if (result is null)
                    return new SpeechResult(false, string.Empty,
                        "没有听到内容（" + (int)timeout.TotalSeconds + " 秒内静音）。", clock.ElapsedMilliseconds, Id);

                var text = result.Text?.Trim() ?? string.Empty;
                return text.Length == 0
                    ? new SpeechResult(false, string.Empty, "识别结果为空。", clock.ElapsedMilliseconds, Id)
                    : SpeechResult.Said(text, clock.ElapsedMilliseconds, Id);
            }
            catch (OperationCanceledException)
            {
                return new SpeechResult(false, string.Empty, "已取消。", clock.ElapsedMilliseconds, Id);
            }
            catch (Exception ex)
            {
                return new SpeechResult(false, string.Empty, "语音识别失败：" + ex.Message,
                    clock.ElapsedMilliseconds, Id);
            }
        }, ct);
    }

    /// <summary>The recogniser whose culture matches the interface language, else the neutral one.</summary>
    private static RecognizerInfo Pick(string culture)
    {
        var installed = SpeechRecognitionEngine.InstalledRecognizers();
        var wanted = (culture ?? string.Empty).StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? "en"
            : "zh";

        return installed.FirstOrDefault(r =>
                   r.Culture.Name.StartsWith(wanted, StringComparison.OrdinalIgnoreCase))
               ?? installed.FirstOrDefault(r => r.Culture.Equals(CultureInfo.InvariantCulture))
               ?? installed[0];
    }
}
