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
/// Contract (any implementation may satisfy it):
///   GET  {base}/v1/status                 -> { ok, vaultDomains: [] }
///   GET  {base}/v1/read?url=...&domain=.. -> { ok, title, text, links: [] }
/// Read-only by construction: this client only issues GET and never posts a form.
/// </summary>
public sealed class PortalSidecarTool : ITool
{
    private readonly string? _baseUrl;

    public PortalSidecarTool(string? sidecarBaseUrl)
    {
        _baseUrl = string.IsNullOrWhiteSpace(sidecarBaseUrl) ? null : sidecarBaseUrl.TrimEnd('/');
    }

    public ToolDescriptor Describe() => new(
        "portal.read",
        "Read a university page through the read-only browser sidecar (whitelisted domains only). Requires the sidecar to be running.",
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

        args.TryGetValue("url", out var url);
        if (string.IsNullOrWhiteSpace(url)) return ToolResult.Fail("url is required");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            var query = "url=" + Uri.EscapeDataString(url);
            if (args.TryGetValue("domain", out var domain) && !string.IsNullOrWhiteSpace(domain))
                query += "&domain=" + Uri.EscapeDataString(domain);

            var resp = await http.GetAsync($"{_baseUrl}/v1/read?{query}", ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode
                ? ToolResult.Ok0(body.Length > 8000 ? body[..8000] : body)
                : ToolResult.Fail($"sidecar HTTP {(int)resp.StatusCode}: {body}");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"sidecar 不可达：{ex.Message}");
        }
    }
}
