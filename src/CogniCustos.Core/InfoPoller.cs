using System.Text;
using System.Text.RegularExpressions;
using CogniCustos.Contracts;

namespace CogniCustos.Core;

/// <summary>One item pulled from outside (a web page or an inbox file).</summary>
/// <param name="Key">A stable identity for dedupe (Message-ID for mail; URL for a page).</param>
public sealed record FetchedItem(string Kind, string Source, string Title, string Text, string? Url = null,
    string Key = "");

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

    /// <summary>
    /// The first pass over every fetched item: "important" gets a finer summary and a
    /// proposal, "promo" is advertising, everything else is "routine".
    /// </summary>
    public string Label(string title, string text)
    {
        var haystack = (title + " " + text).ToLowerInvariant();

        if (Ignored.Any(w => haystack.Contains(w.ToLowerInvariant()))) return "promo";
        return _words.Any(w => haystack.Contains(w.ToLowerInvariant())) ? "important" : "routine";
    }

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

    /// <summary>
    /// A time of day, when the text states one: "9:00", "09:00-10:30", "上午 9 点".
    /// Returns null when the message is not specific, so the caller can keep its default.
    /// </summary>
    public (string Start, string End)? FindTime(string text)
    {
        var range = Regex.Match(text,
            @"(?<h1>\d{1,2})[:：](?<m1>\d{2})\s*[-–~至到]\s*(?<h2>\d{1,2})[:：](?<m2>\d{2})");
        if (range.Success)
        {
            var start = Clock(range.Groups["h1"].Value, range.Groups["m1"].Value);
            var end = Clock(range.Groups["h2"].Value, range.Groups["m2"].Value);
            if (start is not null && end is not null) return (start, end);
        }

        var single = Regex.Match(text, @"(?<!\d)(?<h>\d{1,2})[:：](?<m>\d{2})(?!\d)");
        if (single.Success)
        {
            var start = Clock(single.Groups["h"].Value, single.Groups["m"].Value);
            if (start is not null)
            {
                var hour = int.Parse(start[..2]);
                var endHour = Math.Min(23, hour + 1);
                return (start, $"{endHour:00}{start[2..]}");
            }
        }

        var chinese = Regex.Match(text, @"(上午|下午|晚上)?\s*(?<h>\d{1,2})\s*点(半)?");
        if (chinese.Success)
        {
            var hour = int.Parse(chinese.Groups["h"].Value);
            if (chinese.Groups[1].Value is "下午" or "晚上" && hour < 12) hour += 12;
            if (hour > 23) return null;

            var start = $"{hour:00}:00";
            return (start, $"{Math.Min(23, hour + 1):00}:00");
        }

        return null;

        static string? Clock(string h, string m)
        {
            if (!int.TryParse(h, out var hour) || !int.TryParse(m, out var minute)) return null;
            if (hour is < 0 or > 23 || minute is < 0 or > 59) return null;
            return $"{hour:00}:{minute:00}";
        }
    }

    /// <summary>One line that says what happened, when: what the pending card shows.</summary>
    public string Brief(string title, string text, DateTime? date, (string Start, string End)? time)
    {
        var parts = new List<string>();

        if (date is not null)
        {
            parts.Add(date.Value.ToString("yyyy-MM-dd"));
            if (time is not null) parts.Add($"{time.Value.Start}-{time.Value.End}");
        }
        else if (time is not null)
        {
            parts.Add(time.Value.Start);
        }

        parts.Add(Bucket(title, text) + "：" + title.Trim());
        return string.Join(" · ", parts);
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

/// <summary>
/// Reads configured web pages through <see cref="WebReader"/>, so the poller obeys the same
/// allow-list as the settings page's "read once" button: a site nobody approved contributes
/// nothing, and a sign-in wall is reported instead of being stored as if it were content.
/// </summary>
public sealed class WebInfoSource : IInfoSource
{
    private readonly IReadOnlyList<string> _urls;
    private readonly WebReader? _reader;
    private readonly IReadOnlyList<string> _notes;

    public WebInfoSource(IReadOnlyList<string> urls, WebReader? reader = null)
    {
        _urls = urls;
        _reader = reader;
        _notes = [];
    }

    public WebInfoSource(IReadOnlyList<string> urls, IReadOnlyList<string> notes, WebReader reader)
        : this(urls, reader) => _notes = notes;

    public string Kind => "web";

    /// <summary>Why a source contributed nothing, in one line each (shown by --fetch-now).</summary>
    public IReadOnlyList<string> Notes => _notes;

    public async Task<IReadOnlyList<FetchedItem>> FetchAsync(CancellationToken ct)
    {
        var items = new List<FetchedItem>();
        if (_reader is null) return items;

        foreach (var url in _urls)
        {
            var page = await _reader.ReadAsync(url, ct).ConfigureAwait(false);
            if (!page.Ok)
            {
                // the verdict is the point: "not allowed" and "needs a login" are different
                // outcomes, and both must be visible rather than silently skipped
                if (_notes is List<string> list) list.Add(url + " → " + page.Detail);
                continue;
            }

            items.Add(new FetchedItem("web", page.Host, page.Title, page.Text, page.Url,
                $"web:{url}|{page.Text.Length}|{page.Text.GetHashCode():X}"));
        }

        return items;
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
                var stamp = File.GetLastWriteTimeUtc(file).Ticks;
                items.Add(new FetchedItem("email", Path.GetFileName(_folder), firstLine, text, file,
                    $"inbox:{file}|{stamp}"));
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
public sealed record PollResult(int Fetched, int Important, IReadOnlyList<string> ProposalIds,
    IReadOnlyList<string> Titles, int SourceCount = 0, int Skipped = 0)
{
    public string Summary =>
        SourceCount == 0
            ? "没有可抓取的来源"
            : Fetched == 0 && Skipped > 0
                ? $"没有新内容：{Skipped} 条都读过了"
                : $"抓到 {Fetched} 条" + (Skipped > 0 ? $"（跳过已读 {Skipped} 条）" : "") +
                  $"，其中 {Important} 条像重要变化，已生成 {ProposalIds.Count} 个待确认提议";
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
    private IReadOnlyList<IInfoSource> _sources;
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

    /// <summary>Replace the sources (used when the user switches a mailbox on or off).</summary>
    public void UseSources(IReadOnlyList<IInfoSource> sources) => _sources = sources;

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
        var result = await RunSourcesAsync(_sources, today, ct).ConfigureAwait(false);

        // only the scheduled run consumes the day; a manual subset run must not
        MarkRan(today);
        return result;
    }

    /// <summary>
    /// Run only some sources (the drawer's web tile uses this): the same pipeline, without
    /// consuming the daily marker, so "read the web now" does not eat today's scheduled run.
    /// </summary>
    public async Task<PollResult> RunSourcesAsync(IReadOnlyList<IInfoSource> sources, DateTime today,
        CancellationToken ct = default)
    {
        var fetched = 0;
        var important = 0;
        var skipped = 0;
        var proposalIds = new List<string>();
        var titles = new List<string>();

        foreach (var source in sources)
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
                // a message that was already read is not news; the ledger remembers every
                // Message-ID so a daily run only reports what changed
                var key = item.Key.Length > 0 ? item.Key : item.Source + "|" + item.Title;
                if (_raw.WasIngested(key))
                {
                    skipped++;
                    continue;
                }

                var artifact = _raw.Put(item.Kind, item.Source, item.Text, item.Title);
                _raw.MarkIngested(key, item.Source);
                fetched++;

                var label = _keywords.Label(item.Title, item.Text);
                _raw.SetLabel(artifact.Id, label);

                if (label != "important") continue;
                important++;

                var haystack = item.Text + " " + item.Title;
                var due = _keywords.FindDate(haystack, today);
                var time = _keywords.FindTime(item.Text);
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
                            start = time?.Start ?? "09:00",
                            end = time?.End ?? "10:00",
                            title = _keywords.Bucket(item.Title, item.Text) + "：" + item.Title,
                        })));
                }

                // the pending card leads with the brief: date/time first, then the category
                var title = _keywords.Brief(item.Title, item.Text, due, time);
                var description = $"重要邮件 · {_keywords.Bucket(item.Title, item.Text)}\n来源：{item.Source}\n\n" +
                                  (item.Text.Length > 240 ? item.Text[..240] + "…" : item.Text);

                var proposal = _gate.ProposeSet(title, description, changes, tag: "重要邮件");
                proposalIds.Add(proposal.Id);
                titles.Add(item.Title);
            }
        }

        return new PollResult(fetched, important, proposalIds, titles, sources.Count, skipped);
    }
}

/// <summary>
/// The web list as the settings page edits it: add and remove pages, and approve the domains that
/// may actually be read. Adding a URL does not start reading it — that is the point, and it is the
/// same rule the cookie plugin on this machine follows for a new site.
/// </summary>
public sealed class WebSourcesService
{
    private readonly string _settingsPath;

    public WebSourcesService(string settingsPath) => _settingsPath = settingsPath;

    public IReadOnlyList<string> Urls => SettingsStore.Load(_settingsPath).WebSources ?? [];

    /// <summary>Domains the person approved; a URL outside these is never fetched.</summary>
    public IReadOnlyList<string> AllowedDomains =>
        SettingsStore.Load(_settingsPath).WebAllowedDomains ?? [];

    public bool IsAllowed(string url) =>
        WebReader.HostOf(url) is { Length: > 0 } host &&
        AllowedDomains.Any(d => WebReader.DomainAllows(d, host));

    /// <summary>The host a URL belongs to: what the person approves.</summary>
    public static string HostOf(string url) => WebReader.HostOf(url);

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

        var approved = IsAllowed(url);
        return (true, approved
            ? $"已添加 {uri.Host}"
            : $"已添加 {uri.Host}，但还没有允许读取这个域名（点「允许」之后才会抓取）。");
    }

    public bool Remove(string url)
    {
        var settings = SettingsStore.Load(_settingsPath);
        var urls = (settings.WebSources ?? []).Where(u => u != url).ToList();
        SettingsStore.Save(_settingsPath, settings with { WebSources = urls });
        return true;
    }

    /// <summary>Approve one domain (exact host, or a parent domain to cover its subdomains).</summary>
    public (bool Ok, string Message) Allow(string domainOrUrl)
    {
        var domain = (domainOrUrl ?? string.Empty).Trim().TrimStart('.');
        if (domain.Contains("://", StringComparison.Ordinal)) domain = HostOf(domain);
        if (domain.Length == 0) return (false, "请填写要允许的域名。");

        var settings = SettingsStore.Load(_settingsPath);
        var domains = (settings.WebAllowedDomains ?? []).ToList();
        if (domains.Any(d => WebReader.DomainAllows(d, domain))) return (true, $"{domain} 已经在允许列表里");

        domains.Add(domain.ToLowerInvariant());
        SettingsStore.Save(_settingsPath, settings with { WebAllowedDomains = domains });
        return (true, $"已允许读取 {domain}");
    }

    public (bool Ok, string Message) Revoke(string domain)
    {
        var settings = SettingsStore.Load(_settingsPath);
        var domains = (settings.WebAllowedDomains ?? [])
            .Where(d => !string.Equals(d, domain, StringComparison.OrdinalIgnoreCase))
            .ToList();
        SettingsStore.Save(_settingsPath, settings with { WebAllowedDomains = domains });
        return (true, $"已撤销 {domain}");
    }
}

/// <summary>Default sources built from settings: configured pages plus an inbox folder.</summary>
public static class InfoSources
{
    public static IReadOnlyList<IInfoSource> From(AppSettings settings, HttpMessageHandler? handler = null,
        Func<string, string?>? cookieFor = null)
    {
        var sources = new List<IInfoSource>();

        // Thunderbird first: it is the path that works for every mailbox, because the user
        // already logged in there and Thunderbird keeps the mail on disk
        if (settings.UseThunderbird)
            sources.Add(new ThunderbirdMailSource());

        if (settings.WebSources is { Count: > 0 })
        {
            // the same gate the settings page uses: nothing is read from an unapproved domain
            var allowed = settings.WebAllowedDomains ?? [];
            var reader = new WebReader(
                handler is null ? new HttpWebFetcher() : new HttpWebFetcher(handler),
                () => allowed,
                cookieFor);
            sources.Add(new WebInfoSource(settings.WebSources, reader));
        }

        if (!string.IsNullOrWhiteSpace(settings.InboxPath))
            sources.Add(new InboxInfoSource(settings.InboxPath!));

        return sources;
    }
}
