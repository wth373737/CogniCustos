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
///
/// Capture ends for exactly three reasons, and the caller can tell which: the person stopped it
/// (cancellation), the person stopped speaking (the recogniser finished), or the time box ran out.
/// </summary>
public sealed class SystemSpeechRecognizer : ISpeechRecognizer
{
    private string? _detail;
    private bool _probed;
    private bool _available;

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
            _detail = "没有可用的麦克风或语音识别引擎：" + ex.Message + NonAsciiPathHint();
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
        return Task.Run(async () =>
        {
            var clock = Stopwatch.StartNew();
            SpeechRecognitionEngine? engine = null;

            try
            {
                engine = new SpeechRecognitionEngine(Pick(culture));
                engine.SetInputToDefaultAudioDevice();
                engine.LoadGrammar(new DictationGrammar());

                var completion = new TaskCompletionSource<RecognitionResult?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                // keep the newest hypothesis so "press again to stop" can still return words that
                // were already spoken but not yet finalised
                string? hypothesis = null;
                engine.SpeechHypothesized += (_, e) => hypothesis = e.Result?.Text;
                engine.SpeechRecognized += (_, e) => hypothesis = e.Result?.Text ?? hypothesis;
                engine.RecognizeCompleted += (_, e) => completion.TrySetResult(e.Result);

                engine.RecognizeAsync(RecognizeMode.Single);

                using var deadline = new CancellationTokenSource(timeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
                using var stop = linked.Token.Register(() =>
                {
                    try { engine.RecognizeAsyncCancel(); } catch { /* already gone */ }
                });

                var result = await completion.Task.ConfigureAwait(false);
                var elapsed = clock.ElapsedMilliseconds;
                var text = (result?.Text ?? hypothesis ?? string.Empty).Trim();
                var stoppedByUser = ct.IsCancellationRequested;
                var timedOut = deadline.IsCancellationRequested;

                if (text.Length == 0)
                {
                    var reason = stoppedByUser
                        ? "已停止，没有听到内容。"
                        : timedOut
                            ? $"没有听到内容（已等 {(int)timeout.TotalSeconds} 秒）。"
                            : "没有听到内容。";
                    return new SpeechResult(false, string.Empty, reason, elapsed, Id);
                }

                return SpeechResult.Said(text, elapsed, Id);
            }
            catch (Exception ex)
            {
                return new SpeechResult(false, string.Empty,
                    "语音识别失败：" + ex.Message + NonAsciiPathHint(), clock.ElapsedMilliseconds, Id);
            }
            finally
            {
                engine?.Dispose();
            }
        });
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
