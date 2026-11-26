using CogniCustos.Core;

namespace CogniCustos.Sample;

/// <summary>
/// Read one page the way the daily poll would:
///
///   CogniCustos.Sample.exe --web-check https://example.com/page
///   CogniCustos.Sample.exe --web-check https://example.com/page --allow
///
/// It answers three questions at once: is this domain allowed, what did the read actually return
/// (article / sign-in wall / empty shell / not HTML), and how much text is behind it.
/// </summary>
public static class WebCheck
{
    public static int Run(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        var url = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(url))
        {
            Console.WriteLine("usage: --web-check <url> [--allow]");
            return 1;
        }

        var root = AppRuntime.Home();
        var state = Path.Combine(root, "state");
        Directory.CreateDirectory(state);
        var sources = new WebSourcesService(Path.Combine(state, "settings.json"));

        if (args.Contains("--allow", StringComparer.Ordinal))
            Console.WriteLine("allow: " + string.Join(" ", sources.Allow(url).Message));

        var allowed = sources.IsAllowed(url);
        var domains = string.Join(", ", sources.AllowedDomains);
        Console.WriteLine($"url     : {url}");
        Console.WriteLine($"host    : {WebSourcesService.HostOf(url)}");
        Console.WriteLine($"allowed : {allowed}   (approved: {(domains.Length == 0 ? "(none)" : domains)})");

        var reader = new WebReader(new HttpWebFetcher(), () => sources.AllowedDomains);
        var page = reader.ReadAsync(url).GetAwaiter().GetResult();

        Console.WriteLine($"verdict : {page.Verdict}");
        Console.WriteLine($"status  : {(page.Status == 0 ? "-" : page.Status.ToString())}  type={page.ContentType}");
        Console.WriteLine($"title   : {page.Title}");
        Console.WriteLine($"text    : {page.Text.Length} chars");
        Console.WriteLine($"detail  : {page.Detail}");
        if (page.Ok) Console.WriteLine($"preview : {page.Preview(240)}");

        return page.Ok ? 0 : 2;
    }
}
