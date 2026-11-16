using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CogniCustos.Contracts;

namespace CogniCustos.Adapters;

/// <summary>
/// Any OpenAI-compatible endpoint. This is the interface the proposal commits to
/// ("external models will be reached through the OpenAI-compatible API"), so the
/// same class talks to a real cloud provider, to a local llama.cpp server, or to
/// Ollama's /v1 shim — which is how the outbound gate can be demonstrated without
/// spending money.
/// </summary>
public sealed class OpenAiCompatibleProvider : ICloudModelProvider
{
    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private readonly string _model;
    private bool? _available;

    public OpenAiCompatibleProvider(string baseUrl, string model, string? apiKey)
    {
        var normalized = baseUrl.TrimEnd('/');
        Endpoint = normalized;
        _model = model;
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _http = new HttpClient { BaseAddress = new Uri(normalized + "/"), Timeout = TimeSpan.FromSeconds(120) };
        if (_apiKey is not null)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    public string Endpoint { get; }
    public string Id => $"cloud:{_model}";
    public ModelRole Role => ModelRole.Reasoning;
    public bool IsAvailable => _available ??= Probe();

    public void ResetAvailability() => _available = null;

    private bool Probe()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var resp = _http.GetAsync("models", cts.Token).GetAwaiter().GetResult();
            _available = resp.IsSuccessStatusCode;
        }
        catch
        {
            _available = false;
        }
        return _available.Value;
    }

    public async Task<string> CompleteAsync(string system, string user, CancellationToken ct = default)
    {
        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(system)) messages.Add(new { role = "system", content = system });
        messages.Add(new { role = "user", content = user });

        var body = new { model = _model, messages, stream = false, temperature = 0.2 };
        using var resp = await _http.PostAsJsonAsync("chat/completions", body, ct).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"云端 HTTP {(int)resp.StatusCode}: {(raw.Length > 200 ? raw[..200] : raw)}");

        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content))
                return content.GetString() ?? "";
        }
        throw new InvalidOperationException("云端响应没有 choices[0].message.content");
    }

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default) => Task.FromResult(Array.Empty<float>());
}
