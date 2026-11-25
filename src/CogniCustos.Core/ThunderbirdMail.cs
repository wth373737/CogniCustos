using System.Text;
using System.Text.RegularExpressions;

namespace CogniCustos.Core;

/// <summary>One mail folder found in a Thunderbird profile.</summary>
public sealed record ThunderbirdFolder(string Account, string Folder, string Path, long Bytes);

/// <summary>
/// Reads mail the way the user already set it up: from Thunderbird's own local store.
///
/// Why not IMAP: many providers (Gmail, Outlook, 163 …) need app passwords or OAuth that a
/// small app cannot reasonably collect, and the protocol dance is fragile. Thunderbird has
/// already solved that; its mbox files sit on disk in a plain, stable format. We read the
/// tail of each INBOX, so a 1 GB file costs a few MB of I/O, and nothing is ever written.
/// </summary>
public static class ThunderbirdMail
{
    /// <summary>How much of the end of an mbox is read: enough for the newest messages.</summary>
    private const int TailBytes = 4 * 1024 * 1024;

    private const int MaxBodyChars = 4000;

    public static string ProfilesRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Thunderbird", "Profiles");

    /// <summary>Profiles that actually exist on this machine.</summary>
    public static IReadOnlyList<string> DiscoverProfiles(string? root = null)
    {
        var profilesRoot = root ?? ProfilesRoot;
        if (!Directory.Exists(profilesRoot))
        {
            // some installs use a lowercase folder name; Windows is case-insensitive but a
            // custom root may not be
            return [];
        }

        return Directory.EnumerateDirectories(profilesRoot).ToList();
    }

    /// <summary>Folders with mail in them (mbox files that are not the .msf index).</summary>
    public static IReadOnlyList<ThunderbirdFolder> DiscoverFolders(IEnumerable<string> profiles)
    {
        var folders = new List<ThunderbirdFolder>();

        foreach (var profile in profiles)
        {
            foreach (var root in new[] { "ImapMail", "Mail" })
            {
                var rootPath = Path.Combine(profile, root);
                if (!Directory.Exists(rootPath)) continue;

                foreach (var accountDir in Directory.EnumerateDirectories(rootPath))
                {
                    foreach (var file in Directory.EnumerateFiles(accountDir))
                    {
                        var name = Path.GetFileName(file);
                        if (name.EndsWith(".msf", StringComparison.OrdinalIgnoreCase)) continue;
                        if (name.StartsWith("msgFilterRules", StringComparison.OrdinalIgnoreCase)) continue;

                        var info = new FileInfo(file);
                        if (info.Length == 0) continue;

                        folders.Add(new ThunderbirdFolder(Path.GetFileName(accountDir), name, file, info.Length));
                    }
                }
            }
        }

        return folders.OrderByDescending(f => f.Bytes).ToList();
    }

    /// <summary>The newest messages of one mbox, newest first.</summary>
    public static IReadOnlyList<(string Subject, string From, DateTime Date, string Body)> ReadRecent(
        string mboxPath, int max = 20)
    {
        if (!File.Exists(mboxPath)) return [];

        string text;
        try
        {
            text = ReadTail(mboxPath, TailBytes);
        }
        catch
        {
            return [];
        }

        // mbox: messages are separated by a line starting with "From "
        var segments = Regex.Split("\n" + text, @"\nFrom -? ?[^\n]*\n")
            .Where(s => s.Trim().Length > 0)
            .ToList();

        var messages = new List<(string Subject, string From, DateTime Date, string Body)>();
        foreach (var segment in segments)
        {
            var headerEnd = segment.IndexOf("\n\n", StringComparison.Ordinal);
            if (headerEnd <= 0) continue;

            var headers = segment[..headerEnd];
            var body = segment[(headerEnd + 2)..];

            var subject = DecodeHeader(Header(headers, "Subject") ?? "(无主题)");
            var from = DecodeHeader(Header(headers, "From") ?? "");
            var date = ParseDate(Header(headers, "Date") ?? Header(headers, "Received"));

            messages.Add((subject, from, date, ReadableBody(headers, body)));
        }

        return messages
            .OrderByDescending(m => m.Date)
            .Take(max)
            .ToList();
    }

    private static string ReadTail(string path, int bytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = stream.Length;
        var start = Math.Max(0, length - bytes);
        stream.Seek(start, SeekOrigin.Begin);

        var buffer = new byte[length - start];
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = stream.Read(buffer, read, buffer.Length - read);
            if (chunk == 0) break;
            read += chunk;
        }

        var text = Encoding.UTF8.GetString(buffer, 0, read);

        // Thunderbird writes CRLF on Windows; the parser works on plain 

        text = text.Replace("\r\n", "\n");

        // the first line of the tail is a fragment; drop it
        var newline = text.IndexOf('\n');
        return newline > 0 ? text[(newline + 1)..] : text;
    }

    private static string? Header(string headers, string name)
    {
        var match = Regex.Match(headers, $@"^{name}:\s*(?<v>.*(?:\n[ \t].*)*)", RegexOptions.Multiline);
        return match.Success ? Regex.Replace(match.Groups["v"].Value, @"\s*\n\s*", " ").Trim() : null;
    }

    private static DateTime ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DateTime.MinValue;

        // senders get the weekday wrong often enough that a strict parse rejects the whole
        // message; the weekday carries no information we need, so drop it
        value = Regex.Replace(value.Trim(), @"^[A-Za-z]{3},\s*", "");

        // mail dates look like "01 Jan 2026 10:00:00 +0800": a DateTimeOffset parse is the
        // one that understands the numeric zone
        if (DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var offset))
            return offset.UtcDateTime;

        return DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : DateTime.MinValue;
    }

    /// <summary>RFC 2047 encoded words: =?utf-8?B?…?= and =?gb2312?Q?…?=</summary>
    private static string DecodeHeader(string value)
    {
        try
        {
            return Regex.Replace(value, @"=\?(?<charset>[^?]+)\?(?<encoding>[BbQq])\?(?<text>[^?]*)\?=",
                match =>
                {
                    var charset = match.Groups["charset"].Value;
                    var encoding = match.Groups["encoding"].Value.ToUpperInvariant();
                    var payload = match.Groups["text"].Value;

                    var bytes = encoding == "B"
                        ? Convert.FromBase64String(pad(payload))
                        : Encoding.ASCII.GetBytes(payload.Replace('_', ' '));

                    try
                    {
                        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                        return Encoding.GetEncoding(charset).GetString(bytes);
                    }
                    catch
                    {
                        return Encoding.UTF8.GetString(bytes);
                    }

                    static string pad(string s) => s + new string('=', (4 - s.Length % 4) % 4);
                });
        }
        catch
        {
            return value;
        }
    }

    /// <summary>
    /// The readable text of one message. Mail nests multiparts (mixed → alternative →
    /// text/plain), so this walks down: the first text/plain wins, text/html is the
    /// fallback, and a part is only accepted when its own headers say it is text.
    /// </summary>
    private static string ReadableBody(string headers, string body) =>
        ExtractText(headers, body, 0);

    private static string ExtractText(string headers, string body, int depth)
    {
        if (depth > 3) return string.Empty;

        var type = Header(headers, "Content-Type") ?? string.Empty;

        if (type.Contains("multipart", StringComparison.OrdinalIgnoreCase))
        {
            var boundary = Regex.Match(headers, @"boundary=""?(?<b>[^"";\s]+)");
            if (boundary.Success)
            {
                var separator = "--" + boundary.Groups["b"].Value;
                string? html = null;

                foreach (var part in body.Split(separator))
                {
                    var (partHeaders, partBody) = SplitHeaders(part);
                    if (partHeaders is null) continue;

                    var partType = Header(partHeaders, "Content-Type") ?? string.Empty;
                    if (partType.Contains("multipart", StringComparison.OrdinalIgnoreCase))
                    {
                        var nested = ExtractText(partHeaders, partBody, depth + 1);
                        if (nested.Length > 0) return nested;
                        continue;
                    }

                    if (partType.Contains("text/plain", StringComparison.OrdinalIgnoreCase))
                        return Clean(DecodeTransfer(partHeaders, partBody), html: false);

                    if (partType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                        html ??= Clean(DecodeTransfer(partHeaders, partBody), html: true);
                }

                return html ?? string.Empty;
            }
        }

        return Clean(DecodeTransfer(headers, body), type.Contains("html", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Header block and body of a part, with the boundaries' leading blank lines removed.</summary>
    private static (string? Headers, string Body) SplitHeaders(string part)
    {
        var trimmed = part.TrimStart('\n', '\r', ' ', '\t');
        var end = trimmed.IndexOf("\n\n", StringComparison.Ordinal);
        return end <= 0 ? (null, string.Empty) : (trimmed[..end], trimmed[(end + 2)..]);
    }

    private static string DecodeTransfer(string headers, string body)
    {
        var encoding = (Header(headers, "Content-Transfer-Encoding") ?? "").ToLowerInvariant();

        if (encoding.Contains("base64"))
        {
            try
            {
                var cleaned = Regex.Replace(body, @"[^A-Za-z0-9+/=]", "");
                var padding = new string('=', (4 - cleaned.Length % 4) % 4);
                return Encoding.UTF8.GetString(Convert.FromBase64String(cleaned + padding));
            }
            catch
            {
                return body;
            }
        }

        if (encoding.Contains("quoted-printable")) return DecodeQuotedPrintable(body);

        return body;
    }

    private static string DecodeQuotedPrintable(string text)
    {
        text = Regex.Replace(text, @"=(\r?\n)", "");
        var builder = new StringBuilder();

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '=' && i + 2 < text.Length &&
                byte.TryParse(text.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var value))
            {
                builder.Append((char)value);
                i += 2;
                continue;
            }

            builder.Append(text[i]);
        }

        var bytes = Encoding.Latin1.GetBytes(builder.ToString());
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Tags out, entities decoded, blank lines collapsed: enough for the judge and a summary.</summary>
    private static string Clean(string text, bool html)
    {
        if (html)
        {
            text = Regex.Replace(text, @"<(script|style)[^>]*>.*?</\1>", " ",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            text = Regex.Replace(text, "<[^>]+>", " ");
        }

        text = System.Net.WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @"(\r?\n\s*){2,}", "\n");
        text = text.Trim();

        return text.Length > MaxBodyChars ? text[..MaxBodyChars] : text;
    }

}

/// <summary>
/// The email source the daily fetch really uses: Thunderbird's folders, newest first.
/// No account setup, no protocol, no credentials — just what the user already has.
/// </summary>
public sealed class ThunderbirdMailSource : IInfoSource
{
    private readonly int _perFolder;
    private readonly Func<bool> _enabled;

    public ThunderbirdMailSource(int perFolder = 20, Func<bool>? enabled = null)
    {
        _perFolder = perFolder;
        _enabled = enabled ?? (() => true);
    }

    /// <summary>Only the folders the user kept switched on.</summary>
    public IEnumerable<ThunderbirdFolder> Selected(string settingsPath)
    {
        var muted = SettingsStore.Load(settingsPath).MutedMailFolders ?? [];
        return Folders().Where(f => !muted.Contains($"{f.Account}/{f.Folder}"));
    }

    public string Kind => "email";

    private Func<IEnumerable<ThunderbirdFolder>> _selected = () => ThunderbirdMail.DiscoverFolders(ThunderbirdMail.DiscoverProfiles());

    /// <summary>How the source decides which folders to read (the runtime wires this to settings).</summary>
    public void UseSelection(Func<IEnumerable<ThunderbirdFolder>> selection) => _selected = selection;

    /// <summary>Folders that would be read, for the settings page.</summary>
    public IReadOnlyList<ThunderbirdFolder> Folders() =>
        ThunderbirdMail.DiscoverFolders(ThunderbirdMail.DiscoverProfiles());

    public Task<IReadOnlyList<FetchedItem>> FetchAsync(CancellationToken ct)
    {
        var items = new List<FetchedItem>();
        if (!_enabled()) return Task.FromResult<IReadOnlyList<FetchedItem>>(items);

        foreach (var folder in _selected())
        {
            ct.ThrowIfCancellationRequested();

            foreach (var message in ThunderbirdMail.ReadRecent(folder.Path, _perFolder))
            {
                items.Add(new FetchedItem("email", $"Thunderbird · {folder.Account}/{folder.Folder}",
                    message.Subject, message.Body));
            }
        }

        return Task.FromResult<IReadOnlyList<FetchedItem>>(items);
    }
}
