using System.Net.Http.Json;
using System.Text.Json;
using CogniCustos.Contracts;

namespace CogniCustos.Adapters;

/// <summary>
/// Ollama's vision endpoint, same REST style as <see cref="OllamaModelProvider"/>.
///
/// The point-read reader uses this only when the selected rectangle has no text layer
/// (a scanned page, a chart, a photo of a slide). Everything else stays text: it is cheaper,
/// it keeps the image on this machine either way, and a 4B vision model is a better
/// describer than an explainer.
/// </summary>
public sealed class OllamaVisionModel : IVisionModel
{
    private readonly HttpClient _http;
    private readonly string _model;
    private bool? _available;

    public OllamaVisionModel(string baseUrl, string model = "qwen3-vl:4b")
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _model = model;
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl + "/"), Timeout = TimeSpan.FromMinutes(3) };
    }

    public string BaseUrl { get; }
    public string Id => $"ollama-vision:{_model}";
    public string Model => _model;

    public bool IsAvailable
    {
        get
        {
            _available ??= Probe();
            return _available.Value;
        }
    }

    public void ResetAvailability() => _available = null;

    private bool Probe()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var resp = _http.GetAsync("api/tags", cts.Token).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode) return false;

            var raw = resp.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("models", out var models)) return false;

            // Only claim availability when the configured model is really installed.
            var wanted = _model.Split(':')[0];
            return models.EnumerateArray().Any(m =>
                m.TryGetProperty("name", out var n) &&
                (n.GetString() ?? string.Empty).StartsWith(wanted, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> DescribeAsync(string prompt, byte[] png, CancellationToken ct = default)
    {
        if (png.Length == 0) throw new ArgumentException("empty image", nameof(png));

        // Ollama takes base64 in the "images" array of /api/generate.
        var body = new
        {
            model = _model,
            prompt,
            stream = false,
            images = new[] { Convert.ToBase64String(png) },
            options = new { temperature = 0.1, num_predict = 500 }
        };

        using var resp = await _http.PostAsJsonAsync("api/generate", body, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Ollama vision HTTP {(int)resp.StatusCode}: {Trim(text)}");

        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.TryGetProperty("response", out var response))
            return (response.GetString() ?? string.Empty).Trim();

        throw new InvalidOperationException("Ollama vision response carried no 'response'");
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300];
}
