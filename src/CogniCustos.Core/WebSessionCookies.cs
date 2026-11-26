namespace CogniCustos.Core;

/// <summary>
/// A session cookie per host, held in memory only.
///
/// Deliberately not persisted: a pasted cookie is a live credential, and the honest default is
/// that closing the app ends it. Nothing here is logged, echoed back to the UI, or written to
/// settings — <see cref="Describe"/> only says whether one is set.
///
/// A cookie set for a parent domain also applies to its subdomains (that is what a
/// <c>Domain=.usm.my</c> cookie does), which is why a person can paste one for the site they
/// log into and have it cover the course pages underneath.
/// </summary>
public sealed class WebSessionCookies
{
    private readonly Dictionary<string, string> _byHost = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Accept a host or a URL; store a cleaned Cookie header.</summary>
    public (bool Ok, string Message) Set(string hostOrUrl, string cookieHeader)
    {
        var host = Normalise(hostOrUrl);
        if (host.Length == 0) return (false, "请先指定域名。");

        var header = (cookieHeader ?? string.Empty).Trim();
        if (header.Length == 0) return (false, "Cookie 内容为空。");
        if (header.Contains('\n') || header.Contains('\r')) return (false, "Cookie 里不能有换行。");
        if (!header.Contains('=', StringComparison.Ordinal)) return (false, "Cookie 应该像 name=value; name2=value2。");

        _byHost[host] = header;
        return (true, "已设置（只在本次运行有效）");
    }

    /// <summary>The header to send to this host: exact match first, then the closest parent.</summary>
    public bool TryGet(string host, out string header)
    {
        header = string.Empty;
        var normalized = Normalise(host);
        if (normalized.Length == 0) return false;

        if (_byHost.TryGetValue(normalized, out header!)) return true;

        // longest parent wins, so ".usm.my" beats ".my"
        var best = string.Empty;
        foreach (var key in _byHost.Keys)
        {
            if (!normalized.EndsWith("." + key, StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Length <= best.Length) continue;
            best = key;
            header = _byHost[key];
        }

        return best.Length > 0;
    }

    public bool Has(string host) => TryGet(host, out _);

    public bool Clear(string hostOrUrl) => _byHost.Remove(Normalise(hostOrUrl));

    /// <summary>Never returns the cookie itself.</summary>
    public string Describe(string hostOrUrl) =>
        Has(hostOrUrl) ? "已设置（仅本次运行）" : "未设置";

    public IReadOnlyList<string> Hosts => _byHost.Keys.ToList();

    public void ClearAll() => _byHost.Clear();

    private static string Normalise(string hostOrUrl)
    {
        var value = (hostOrUrl ?? string.Empty).Trim();
        if (value.Length == 0) return string.Empty;
        if (value.Contains("://", StringComparison.Ordinal))
            return WebReader.HostOf(value);
        return value.TrimStart('.').ToLowerInvariant();
    }
}
