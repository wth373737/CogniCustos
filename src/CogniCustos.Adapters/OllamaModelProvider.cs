using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CogniCustos.Contracts;

namespace CogniCustos.Adapters;

/// <summary>
/// Talks the Ollama REST contract directly (127.0.0.1:11434). This is the interface
/// that dsh-plugin-ollama-bridge also speaks, so the two can coexist and either can
/// be swapped: the design is reused, not the JavaScript.
/// </summary>
public sealed class OllamaModelProvider : IModelProvider
{
    private readonly HttpClient _http;
    private readonly string _model;
    private bool? _available;

    public OllamaModelProvider(string baseUrl, string model, double temperature = 0.2, int numPredict = 700)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _model = model;
        Temperature = temperature;
        NumPredict = numPredict;
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(120) };
    }

    public string BaseUrl { get; }

    /// <summary>Sampling temperature this instance sends (was a literal in the request body).</summary>
    public double Temperature { get; }

    /// <summary>Hard cap on generated tokens (Ollama's num_predict).</summary>
    public int NumPredict { get; }
    public string Id => $"ollama:{_model}";
    public ModelRole Role => ModelRole.Reasoning;
    public bool IsAvailable => _available ??= Probe();

    private bool Probe()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var resp = _http.GetAsync("api/tags", cts.Token).GetAwaiter().GetResult();
            _available = resp.IsSuccessStatusCode;
        }
        catch
        {
            _available = false;
        }
        return _available.Value;
    }

    /// <summary>Re-probe after the user changes settings.</summary>
    public void ResetAvailability() => _available = null;

    public async Task<string> CompleteAsync(string system, string user, CancellationToken ct = default)
    {
        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(system)) messages.Add(new { role = "system", content = system });
        messages.Add(new { role = "user", content = user });

        var body = new
        {
            model = _model,
            stream = false,
            messages,
            options = new { temperature = Temperature, num_predict = NumPredict }
        };

        using var resp = await _http.PostAsJsonAsync("api/chat", body, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Ollama HTTP {(int)resp.StatusCode}: {Trim(text)}");

        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content))
            return content.GetString() ?? "";
        throw new InvalidOperationException("Ollama response carried no message.content");
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var body = new { model = _model, prompt = text };
        using var resp = await _http.PostAsJsonAsync("api/embeddings", body, ct).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return Array.Empty<float>();
        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("embedding", out var emb)) return Array.Empty<float>();
        return emb.EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300];

    /// <summary>Lists installed models, useful for the settings pane.</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync("api/tags", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return Array.Empty<string>();
            var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("models", out var models)) return Array.Empty<string>();
            return models.EnumerateArray()
                .Select(m => m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "")
                .Where(s => s.Length > 0)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
