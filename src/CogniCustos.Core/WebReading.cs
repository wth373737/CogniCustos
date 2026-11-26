using System.Net;
using System.Text.RegularExpressions;

namespace CogniCustos.Core;

/// <summary>What a read actually produced. "Blocked" and "NeedsLogin" are verdicts, not failures.</summary>
public enum WebVerdict
{
    /// <summary>Real text came back.</summary>
    Read,

    /// <summary>The domain is not on the person's allow-list, so nothing was fetched at all.</summary>
    NotAllowed,

    /// <summary>The page is a sign-in form (or redirected into one): no article text exists behind it.</summary>
    NeedsLogin,

    /// <summary>Fetched fine, but there is no readable body (a JS-only shell, an empty page).</summary>
    NoText,

    /// <summary>Not an HTML page (a PDF, an image, a JSON API).</summary>
    NotHtml,

    /// <summary>The server said no.</summary>
    HttpError,

    /// <summary>The request itself failed (DNS, TLS, timeout).</summary>
    Failed
}

/// <summary>One fetched page, with the verdict the caller should branch on instead of guessing.</summary>
public sealed record WebPage(
    string Url,
    string Host,
    int Status,
    string ContentType,
    string Title,
    string Text,
    WebVerdict Verdict,
    string Detail)
{
    public bool Ok => Verdict == WebVerdict.Read;

    /// <summary>Enough text to show in a status line without drowning it.</summary>
    public string Preview(int max = 160) =>
        Text.Length <= max ? Text : Text[..Math.Max(0, max)] + "…";
}

/// <summary>
/// Everything that can turn a URL into text. One implementation for now (anonymous HTTP); a
/// cookie-bearing one slots in beside it without the reader or the UI changing.
/// </summary>
public interface IWebFetcher
{
    Task<WebPage> GetAsync(Uri url, string? cookieHeader, CancellationToken ct = default);
}

/// <summary>
/// Read-only HTTP: GET only, no forms, no writes. It decides what the response *is* — an article,
/// a sign-in wall, a JS shell — because a reader that stores a login page as "the notes" is worse
/// than one that says "this needs a login".
/// </summary>
public sealed class HttpWebFetcher : IWebFetcher
{
    /// <summary>How a sign-in page announces itself in its own text.</summary>
    private static readonly string[] LoginMarkers =
    [
        "sign in", "log in", "login", "signin", "password", "authenticate",
        "统一身份认证", "登录", "登入", "账号密码"
    ];

    /// <summary>Paths that only exist to ask for credentials.</summary>
    private static readonly string[] LoginPaths =
    [
        "/login", "/signin", "/sign-in", "/auth", "/sso", "/cas", "/adfs", "/oauth", "/saml"
    ];

    private readonly HttpMessageHandler? _handler;
    private readonly TimeSpan _timeout;

    public HttpWebFetcher(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _handler = handler;
        _timeout = timeout ?? TimeSpan.FromSeconds(20);
    }

    public async Task<WebPage> GetAsync(Uri url, string? cookieHeader, CancellationToken ct = default)
    {
        using var http = _handler is null ? new HttpClient() : new HttpClient(_handler);
        http.Timeout = _timeout;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CogniCustos/1.0 (read-only; +https://example.invalid)");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(cookieHeader))
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);

            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);

            var finalUrl = response.RequestMessage?.RequestUri ?? url;
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

            if (!response.IsSuccessStatusCode)
            {
                return new WebPage(finalUrl.ToString(), finalUrl.Host, (int)response.StatusCode, mediaType,
                    string.Empty, string.Empty, WebVerdict.HttpError,
                    "服务器返回 HTTP " + (int)response.StatusCode + "（" + response.ReasonPhrase + "）");
            }

            if (mediaType.Length > 0 && !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                return new WebPage(finalUrl.ToString(), finalUrl.Host, (int)response.StatusCode, mediaType,
                    string.Empty, string.Empty, WebVerdict.NotHtml,
                    "这不是 HTML 页面（" + mediaType + "）");
            }

            var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var title = TitleOf(html);
            var text = ReadableText(html);

            if (LooksLikeLogin(finalUrl, text))
            {
                return new WebPage(finalUrl.ToString(), finalUrl.Host, (int)response.StatusCode, mediaType,
                    title, string.Empty, WebVerdict.NeedsLogin,
                    "这一页是登录页（" + Host(finalUrl) + "）：需要登录后才能读到正文。");
            }

            if (text.Length < 40)
            {
                return new WebPage(finalUrl.ToString(), finalUrl.Host, (int)response.StatusCode, mediaType,
                    title, text, WebVerdict.NoText,
                    "这一页没有可读正文（可能整页由脚本渲染，或内容为空）。");
            }

            return new WebPage(finalUrl.ToString(), finalUrl.Host, (int)response.StatusCode, mediaType,
                title, text, WebVerdict.Read, title);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new WebPage(url.ToString(), url.Host, 0, string.Empty, string.Empty, string.Empty,
                WebVerdict.Failed, "抓取失败：" + ex.Message);
        }
    }

    private static string Host(Uri uri) => uri.Host;

    /// <summary>Title, else empty. Entities decoded, whitespace collapsed.</summary>
    public static string TitleOf(string html)
    {
        var match = Regex.Match(html, @"<title[^>]*>(?<t>.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return match.Success ? Clean(match.Groups["t"].Value) : string.Empty;
    }

    /// <summary>
    /// The readable body: scripts and styles removed, tags stripped, entities decoded, whitespace
    /// collapsed. It is deliberately crude — a real article extractor would hide navigation, but
    /// it would also need a model to be reliable, and this has to work offline.
    /// </summary>
    public static string ReadableText(string html)
    {
        var withoutScripts = Regex.Replace(html, @"<(script|style|noscript)[^>]*>.*?</\1>",
            " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var withoutComments = Regex.Replace(withoutScripts, @"<!--.*?-->", " ", RegexOptions.Singleline);
        var text = Regex.Replace(withoutComments, @"<[^>]+>", " ");
        return Clean(text);
    }

    public static string Clean(string text) =>
        Regex.Replace(WebUtility.HtmlDecode(text ?? string.Empty), @"\s+", " ").Trim();

    /// <summary>
    /// A sign-in wall, three ways: the request ended on a login path, the page is short and talks
    /// about signing in, or it redirected to a different host that asks for credentials.
    /// </summary>
    private static bool LooksLikeLogin(Uri finalUrl, string text)
    {
        var path = finalUrl.AbsolutePath.ToLowerInvariant();
        if (LoginPaths.Any(p => path.Contains(p, StringComparison.Ordinal))) return true;

        var head = text.Length <= 600 ? text : text[..600];
        var lowered = head.ToLowerInvariant();
        var mentionsLogin = LoginMarkers.Any(m => lowered.Contains(m, StringComparison.Ordinal));
        return mentionsLogin && text.Length < 600;
    }
}

/// <summary>
/// The gate in front of the fetcher: nothing is fetched from a domain the person has not allowed,
/// and the reason is a verdict rather than an exception. This mirrors how the cookie plugin on
/// this machine behaves — a new site is not read until it is explicitly approved.
/// </summary>
public sealed class WebReader
{
    private readonly IWebFetcher _fetcher;
    private readonly Func<IReadOnlyList<string>> _allowedDomains;
    private readonly Func<string, string?> _cookieFor;

    public WebReader(IWebFetcher fetcher, Func<IReadOnlyList<string>> allowedDomains,
        Func<string, string?>? cookieFor = null)
    {
        _fetcher = fetcher;
        _allowedDomains = allowedDomains;
        _cookieFor = cookieFor ?? (_ => null);
    }

    /// <summary>The host a URL belongs to, lowercased, without a trailing dot.</summary>
    public static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : string.Empty;

    /// <summary>
    /// An allowed domain covers itself and its subdomains: approving <c>usm.my</c> is how a person
    /// thinks about it, and approving <c>elearning.usm.my</c> must not silently cover all of usm.my.
    /// </summary>
    public static bool DomainAllows(string allowedDomain, string host)
    {
        var allowed = allowedDomain.Trim().TrimStart('.').ToLowerInvariant();
        if (allowed.Length == 0) return false;
        return host.Equals(allowed, StringComparison.OrdinalIgnoreCase)
               || host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase);
    }

    public bool IsAllowed(string url)
    {
        var host = HostOf(url);
        return host.Length > 0 && _allowedDomains().Any(d => DomainAllows(d, host));
    }

    public async Task<WebPage> ReadAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new WebPage(url ?? string.Empty, string.Empty, 0, string.Empty, string.Empty, string.Empty,
                WebVerdict.Failed, "不是 http(s) 网址。");
        }

        if (!IsAllowed(url))
        {
            return new WebPage(url, uri.Host, 0, string.Empty, string.Empty, string.Empty,
                WebVerdict.NotAllowed,
                "域名 " + uri.Host + " 还没有被允许读取；在设置里允许之后才会抓取。");
        }

        var verdict = await _fetcher.GetAsync(uri, _cookieFor(uri.Host), ct).ConfigureAwait(false);
        return verdict;
    }
}
