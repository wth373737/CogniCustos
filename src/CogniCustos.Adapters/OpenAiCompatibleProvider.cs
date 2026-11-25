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

    public OpenAiCompatibleProvider(string baseUrl, string model, string? apiKey, double temperature = 0.2)
    {
        var normalized = baseUrl.TrimEnd('/');
        Endpoint = normalized;
        _model = model;
        Temperature = temperature;
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _http = new HttpClient { BaseAddress = new Uri(normalized + "/"), Timeout = TimeSpan.FromSeconds(120) };
        if (_apiKey is not null)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    public string Endpoint { get; }

    /// <summary>Sampling temperature this instance sends (was a literal in the request body).</summary>
    public double Temperature { get; }
    public string Id => $"cloud:{_model}";
    public ModelRole Role => ModelRole.Reasoning;

    /// <summary>
    /// A configured provider is *usable*; whether the key and the URL are right is the job of a
    /// real request, not of a probe.
    ///
    /// This used to probe <c>GET /models</c> and cache the answer. Plenty of OpenAI-compatible
    /// endpoints do not serve that path (or need a <c>/v1</c> prefix), so a perfectly good
    /// configuration was declared unavailable and the router silently fell back to standby —
    /// which the user saw as "配置完反而说找不到模型". A wrong key now surfaces as the provider's
    /// own HTTP error on the first call, and the settings page's test button says so up front.
    /// </summary>
    public bool IsAvailable => true;

    public void ResetAvailability()
    {
        // kept for the callers that used to invalidate the probe cache
    }

    /// <summary>
    /// One real round trip, for the settings page's "test" button: it reports what actually
    /// happened (status, latency, or the endpoint's own error text) instead of a cached guess.
    /// </summary>
    public async Task<CloudTestResult> TestAsync(CancellationToken ct = default)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var reply = await CompleteAsync("你是连通性测试器。", "只回复两个字：可以。", ct)
                .ConfigureAwait(false);
            return new CloudTestResult(true, Clock(reply, clock.ElapsedMilliseconds), clock.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return new CloudTestResult(false, ex.Message, clock.ElapsedMilliseconds);
        }
    }

    private static string Clock(string reply, long ms) =>
        ms + " ms · " + (reply.Length > 60 ? reply[..60] + "…" : reply);

    public async Task<string> CompleteAsync(string system, string user, CancellationToken ct = default)
    {
        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(system)) messages.Add(new { role = "system", content = system });
        messages.Add(new { role = "user", content = user });

        var body = new { model = _model, messages, stream = false, temperature = Temperature };

        // Some endpoints expect the base URL to include /v1, others to exclude it. Instead of
        // making the user guess, try the plain path and, on a 404 only, the /v1 one.
        var (resp, raw) = await PostAsync("chat/completions", body, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            resp.Dispose();
            (resp, raw) = await PostAsync("v1/chat/completions", body, ct).ConfigureAwait(false);
        }

        using var _ = resp;
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"云端 HTTP {(int)resp.StatusCode}（{Endpoint}）：{(raw.Length > 200 ? raw[..200] : raw)}");

        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content))
                return content.GetString() ?? "";
        }

        throw new InvalidOperationException("云端响应没有 choices[0].message.content");
    }

    private async Task<(HttpResponseMessage Response, string Raw)> PostAsync(string path, object body,
        CancellationToken ct)
    {
        var response = await _http.PostAsJsonAsync(path, body, ct).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return (response, raw);
    }

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default) => Task.FromResult(Array.Empty<float>());
}
