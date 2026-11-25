using System.Text;
using System.Text.RegularExpressions;
using CogniCustos.Contracts;

namespace CogniCustos.Core;

/// <summary>One item pulled from outside (a web page or an inbox file).</summary>
public sealed record FetchedItem(string Kind, string Source, string Title, string Text, string? Url = null);

/// <summary>A place the poller reads from. Web pages and an inbox folder are enough for v1.</summary>
public interface IInfoSource
{
    string Kind { get; }
    Task<IReadOnlyList<FetchedItem>> FetchAsync(CancellationToken ct);
}

/// <summary>
/// Keyword screening: "important change" is judged by the words in the item, and a date is
/// pulled out when one is written in a plain form (2026-04-06, 4月6日, 04/06).
/// Cheap on purpose: this runs before any model, so it works offline.
/// </summary>
public sealed class InfoKeywords
{
    private static readonly string[] Defaults =
    [
        "考试", "作业", "截止", "deadline", "due", "缴费", "注册", "选课", "成绩", "放假",
        "会议", "讲座", "调课", "补课", "重要", "通知", "变更",
    ];

    /// <summary>
    /// Real mail is mostly advertising; these words mean "nothing to act on", so a message
    /// carrying them is skipped even when it also contains an important-sounding word.
    /// </summary>
    private static readonly string[] Ignored =
    [
        "unsubscribe", "退订", "newsletter", "促销", "优惠", "限时", "coinbase", "tracking update",
        "privacy policy", "cookie", "webinar recording", "verify your account", "sign-in",
        "登录活动", "安全提醒", "您的 Google", "деталь",
    ];

    private readonly string[] _words;

    public InfoKeywords(IEnumerable<string>? words = null)
        => _words = (words ?? Defaults).Where(w => w.Length > 0).ToArray();

    public IReadOnlyList<string> Words => _words;

    public bool LooksImportant(string title, string text)
    {
        var haystack = (title + " " + text).ToLowerInvariant();
        if (Ignored.Any(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase))) return false;

        return _words.Any(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    public string Bucket(string title, string text)
    {
        var haystack = title + " " + text;
        if (haystack.Contains("考试") || haystack.Contains("exam")) return "考试";
        if (haystack.Contains("缴费") || haystack.Contains("注册")) return "行政";
        if (haystack.Contains("会议") || haystack.Contains("讲座")) return "活动";
        if (haystack.Contains("作业") || haystack.Contains("截止")) return "作业";
        return "通知";
    }

    /// <summary>The first plausible date in the text, as a date only (no time guessing).</summary>
    public DateTime? FindDate(string text, DateTime today)
    {
        var ymd = Regex.Match(text, @"(?<y>20\d{2})[-/.](?<m>\d{1,2})[-/.](?<d>\d{1,2})");
        if (ymd.Success) return Build(ymd.Groups["y"].Value, ymd.Groups["m"].Value, ymd.Groups["d"].Value);

        var md = Regex.Match(text, @"(?<m>\d{1,2})\s*月\s*(?<d>\d{1,2})\s*日");
        if (md.Success)
        {
            var sameYear = Build(today.Year.ToString(), md.Groups["m"].Value, md.Groups["d"].Value);
            if (sameYear is null) return null;

            // "4 月 6 日" written in November means next year's April, not last April
            return sameYear.Value < today.Date ? sameYear.Value.AddYears(1) : sameYear;
        }

        return null;
    }

    private static DateTime? Build(string y, string m, string d)
    {
        if (!int.TryParse(y, out var year) || !int.TryParse(m, out var month) || !int.TryParse(d, out var day))
            return null;
        if (month is < 1 or > 12 || day is < 1 or > 31) return null;
        try
        {
            return new DateTime(year, month, day);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Reads configured web pages and keeps their readable text.</summary>
public sealed class WebInfoSource : IInfoSource
{
    private readonly IReadOnlyList<string> _urls;
    private readonly HttpMessageHandler? _handler;

    public WebInfoSource(IReadOnlyList<string> urls, HttpMessageHandler? handler = null)
    {
        _urls = urls;
        _handler = handler;
    }

    public string Kind => "web";

    public async Task<IReadOnlyList<FetchedItem>> FetchAsync(CancellationToken ct)
    {
        var items = new List<FetchedItem>();
        using var http = _handler is null ? new HttpClient() : new HttpClient(_handler);
        http.Timeout = TimeSpan.FromSeconds(20);

        foreach (var url in _urls)
        {
            try
            {
                var html = await http.GetStringAsync(url, ct).ConfigureAwait(false);
                var title = Regex.Match(html, @"<title[^>]*>(?<t>.*?)</title>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups["t"].Value;
                items.Add(new FetchedItem("web", new Uri(url).Host, Clean(title), Readable(html), url));
            }
            catch
            {
                // a dead page must not stop the run; it simply contributes nothing
            }
        }

        return items;
    }

    private static string Clean(string text) =>
        Regex.Replace(System.Net.WebUtility.HtmlDecode(text ?? ""), @"\s+", " ").Trim();

    /// <summary>Tags out, entities decoded, blank lines collapsed: enough for a summary.</summary>
    private static string Readable(string html)
    {
        var withoutScripts = Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>",
            " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var text = Regex.Replace(withoutScripts, "<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @"(\r?\n\s*){2,}", "\n");
        return text.Trim();
    }
}

/// <summary>
/// Reads an inbox folder: one file per message (.txt or .eml). Subject is the first line.
/// A folder keeps v1 credential-free; an IMAP source can implement the same interface.
/// </summary>
public sealed class InboxInfoSource : IInfoSource
{
    private readonly string _folder;

    public InboxInfoSource(string folder) => _folder = folder;

    public string Kind => "email";

    public Task<IReadOnlyList<FetchedItem>> FetchAsync(CancellationToken ct)
    {
        var items = new List<FetchedItem>();
        if (!Directory.Exists(_folder)) return Task.FromResult<IReadOnlyList<FetchedItem>>(items);

        foreach (var file in Directory.EnumerateFiles(_folder, "*.*", SearchOption.TopDirectoryOnly))
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension is not (".txt" or ".eml" or ".md")) continue;

            try
            {
                var text = File.ReadAllText(file, Encoding.UTF8);
                var firstLine = text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? Path.GetFileName(file);
                items.Add(new FetchedItem("email", Path.GetFileName(_folder), firstLine, text, file));
            }
            catch
            {
                // unreadable file: skip it
            }
        }

        return Task.FromResult<IReadOnlyList<FetchedItem>>(items);
    }
}

/// <summary>What one poll produced.</summary>
public sealed record PollResult(int Fetched, int Important, IReadOnlyList<string> ProposalIds, IReadOnlyList<string> Titles)
{
    public string Summary => Fetched == 0
        ? "没有可抓取的来源"
        : $"抓到 {Fetched} 条，其中 {Important} 条像重要变化，已生成 {ProposalIds.Count} 个待确认提议";
}

/// <summary>
/// The daily "look for changes" job (docs/PARTITION-MODEL.md §1): every item lands in the
/// raw zone first; only keyword-hit items become proposals, and only a user confirmation
/// writes tasks or schedule blocks.
/// </summary>
public sealed class InfoPoller
{
    private readonly IRawStore _raw;
    private readonly IApprovalGate _gate;
    private readonly IReadOnlyList<IInfoSource> _sources;
    private readonly InfoKeywords _keywords;
    private readonly string _settingsPath;

    public InfoPoller(IRawStore raw, IApprovalGate gate, IReadOnlyList<IInfoSource> sources,
        string settingsPath, InfoKeywords? keywords = null)
    {
        _raw = raw;
        _gate = gate;
        _sources = sources;
        _keywords = keywords ?? new InfoKeywords();
        _settingsPath = settingsPath;
    }

    public InfoKeywords Keywords => _keywords;

    /// <summary>At most one automatic run per calendar day.</summary>
    public bool IsDue(DateTime today)
    {
        var settings = SettingsStore.Load(_settingsPath);
        return !string.Equals(settings.LastFetchDate, today.ToString("yyyy-MM-dd"), StringComparison.Ordinal);
    }

    public void MarkRan(DateTime today)
    {
        var settings = SettingsStore.Load(_settingsPath);
        SettingsStore.Save(_settingsPath, settings with { LastFetchDate = today.ToString("yyyy-MM-dd") });
    }

    public async Task<PollResult> RunAsync(DateTime today, CancellationToken ct = default)
    {
        var fetched = 0;
        var important = 0;
        var proposalIds = new List<string>();
        var titles = new List<string>();

        foreach (var source in _sources)
        {
            IReadOnlyList<FetchedItem> items;
            try
            {
                items = await source.FetchAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                continue;
            }

            foreach (var item in items)
            {
                // every item is kept: the raw zone is the record of what arrived
                _raw.Put(item.Kind, item.Source, item.Text, item.Title);
                fetched++;

                if (!_keywords.LooksImportant(item.Title, item.Text)) continue;
                important++;

                var due = _keywords.FindDate(item.Text + " " + item.Title, today);
                var changes = new List<Change>
                {
                    new(BuiltinTools.TargetTasksAdd, System.Text.Json.JsonSerializer.Serialize(new
                    {
                        title = item.Title.Length > 60 ? item.Title[..60] : item.Title,
                        due = (due ?? today.AddDays(7)).ToString("yyyy-MM-dd"),
                        level = "mid",
                        note = $"抓取自 {item.Source}",
                    })),
                };

                if (due is not null)
                {
                    changes.Add(new Change(PlannerTools.TargetScheduleAdd,
                        System.Text.Json.JsonSerializer.Serialize(new
                        {
                            date = due.Value.ToString("yyyy-MM-dd"),
                            start = "09:00",
                            end = "10:00",
                            title = _keywords.Bucket(item.Title, item.Text) + "：" + item.Title,
                        })));
                }

                var proposal = _gate.ProposeSet($"抓取：{item.Title}", $"来自 {item.Source}（{_keywords.Bucket(item.Title, item.Text)}）", changes);
                proposalIds.Add(proposal.Id);
                titles.Add(item.Title);
            }
        }

        MarkRan(today);
        return new PollResult(fetched, important, proposalIds, titles);
    }
}

/// <summary>
/// The web list as the settings page edits it: add and remove pages, each just a URL.
/// The poller reads the same list, so a change takes effect on the next run.
/// </summary>
public sealed class WebSourcesService
{
    private readonly string _settingsPath;

    public WebSourcesService(string settingsPath) => _settingsPath = settingsPath;

    public IReadOnlyList<string> Urls => SettingsStore.Load(_settingsPath).WebSources ?? [];

    public (bool Ok, string Message) Add(string url)
    {
        url = (url ?? "").Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return (false, "请填写 http(s) 开头的网址。");

        var settings = SettingsStore.Load(_settingsPath);
        var urls = (settings.WebSources ?? []).ToList();
        if (urls.Contains(url)) return (true, "这个网址已经在列表里了");

        urls.Add(url);
        SettingsStore.Save(_settingsPath, settings with { WebSources = urls });
        return (true, $"已添加 {uri.Host}");
    }

    public bool Remove(string url)
    {
        var settings = SettingsStore.Load(_settingsPath);
        var urls = (settings.WebSources ?? []).Where(u => u != url).ToList();
        SettingsStore.Save(_settingsPath, settings with { WebSources = urls });
        return true;
    }
}

/// <summary>Default sources built from settings: configured pages plus an inbox folder.</summary>
public static class InfoSources
{
    public static IReadOnlyList<IInfoSource> From(AppSettings settings, HttpMessageHandler? handler = null)
    {
        var sources = new List<IInfoSource>();

        // Thunderbird first: it is the path that works for every mailbox, because the user
        // already logged in there and Thunderbird keeps the mail on disk
        if (settings.UseThunderbird)
            sources.Add(new ThunderbirdMailSource());

        if (settings.WebSources is { Count: > 0 })
            sources.Add(new WebInfoSource(settings.WebSources, handler));

        if (!string.IsNullOrWhiteSpace(settings.InboxPath))
            sources.Add(new InboxInfoSource(settings.InboxPath!));

        return sources;
    }
}
