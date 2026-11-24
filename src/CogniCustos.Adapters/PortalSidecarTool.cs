using System.Text.Json;
using CogniCustos.Contracts;
using CogniCustos.Core;

namespace CogniCustos.Adapters;

/// <summary>
/// The seam that keeps the JavaScript work usable: dsh-plugin-cookie-whitelist already
/// implements per-domain approval, an in-memory vault and a read-only browser. Rather
/// than porting that to C#, this tool calls a local sidecar that speaks a tiny HTTP
/// contract. When no sidecar is running the tool reports that honestly instead of
/// pretending to browse.
///
/// Every successful read is written to the raw store first (docs/PARTITION-MODEL.md §1.3):
/// the bytes live there, and what the tool returns is the text plus the raw handle.
///
/// Contract (any implementation may satisfy it):
///   GET  {base}/v1/status                 -> { ok, vaultDomains: [] }
///   GET  {base}/v1/read?url=...&domain=.. -> { ok, title, text, links: [] }
/// Read-only by construction: this client only issues GET and never posts a form.
/// </summary>
public sealed class PortalSidecarTool : ITool
{
    private readonly string? _baseUrl;
    private readonly IRawStore? _raw;
    private readonly Func<string, string?, Task<(bool Ok, string Body)>> _fetch;

    public PortalSidecarTool(string? sidecarBaseUrl, IRawStore? raw = null,
        Func<string, string?, Task<(bool Ok, string Body)>>? fetch = null)
    {
        _baseUrl = string.IsNullOrWhiteSpace(sidecarBaseUrl) ? null : sidecarBaseUrl.TrimEnd('/');
        _raw = raw;
        _fetch = fetch ?? FetchOverHttp;
    }

    public ToolDescriptor Describe() => new(
        "portal.read",
        "Read a university page through the read-only browser sidecar (whitelisted domains only). The page is stored in the raw store; you get its text and the raw id.",
        new[]
        {
            new ToolParameter("url", "string", "http(s) URL to read", true),
            new ToolParameter("domain", "string", "Whitelisted domain this URL belongs to"),
        },
        ToolKind.Read);

    public async Task<ToolResult> InvokeAsync(IReadOnlyDictionary<string, string> args, CancellationToken ct = default)
    {
        if (_baseUrl is null)
            return ToolResult.Fail("portal sidecar 未配置（可在设置里填入 dsh-plugin-cookie-whitelist 的本地接收端地址）。这条链路已按接口预留，但本轮不启用。");

        if (!args.TryGetValue("url", out var url) || string.IsNullOrWhiteSpace(url))
            return ToolResult.Fail("url is required");

        args.TryGetValue("domain", out var domain);

        bool ok;
        string body;
        try
        {
            (ok, body) = await _fetch(url, string.IsNullOrWhiteSpace(domain) ? null : domain).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"sidecar 不可达：{ex.Message}");
        }

        if (!ok) return ToolResult.Fail($"read failed: {body}");

        var title = TitleOf(body, url);
        var text = TextOf(body);
        var artifact = _raw?.Put("portal", url, text, title);

        var handle = artifact is null ? "" : $"\n\n[raw #{artifact.Id}] {artifact.Title}";
        var trimmed = text.Length > 8000 ? text[..8000] : text;
        return ToolResult.Ok0(trimmed + handle, artifact);
    }

    private async Task<(bool Ok, string Body)> FetchOverHttp(string url, string? domain)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var query = "url=" + Uri.EscapeDataString(url);
        if (domain is not null) query += "&domain=" + Uri.EscapeDataString(domain);

        var resp = await http.GetAsync($"{_baseUrl}/v1/read?{query}").ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        return (resp.IsSuccessStatusCode, resp.IsSuccessStatusCode ? body : $"sidecar HTTP {(int)resp.StatusCode}: {body}");
    }

    private static string TitleOf(string body, string url)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
            {
                var value = title.GetString();
                if (!string.IsNullOrWhiteSpace(value)) return value!;
            }
        }
        catch
        {
            // the sidecar may have returned plain text; fall back to the URL
        }

        return url;
    }

    private static string TextOf(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                return text.GetString() ?? "";
        }
        catch
        {
            // plain text body
        }

        return body;
    }
}
