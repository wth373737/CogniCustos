using CogniCustos.Core;

namespace CogniCustos.Sample;

/// <summary>
/// <c>--fetch-now</c>: run today's fetch by hand and print what happened, including the
/// proposals it created. Sources come from <c>state/settings.json</c> (WebSources /
/// InboxPath), so this is the quickest way to see the pipeline work end to end.
/// </summary>
internal static class FetchNow
{
    public static async Task<int> RunAsync()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "fetch-check");
        Directory.CreateDirectory(root);

        var rt = CogniCustosRuntime.Create(root);
        var settings = SettingsStore.Load(Path.Combine(root, "state", "settings.json"));

        var folders = rt.MailFolders;
        Console.WriteLine($"thunderbird : {folders.Count} 个邮箱文件夹" +
                          (folders.Count > 0 ? $"（最大 {Math.Round(folders[0].Bytes / 1024d / 1024d, 1)} MB）" : ""));
        foreach (var folder in folders.Take(5))
        {
            Console.WriteLine($"  · {folder.Account}/{folder.Folder}  {Math.Round(folder.Bytes / 1024d / 1024d, 1)} MB");
        }

        Console.WriteLine($"web sources : {settings.WebSources?.Count ?? 0}");
        Console.WriteLine($"inbox       : {settings.InboxPath ?? "(not set)"}");

        var result = await rt.Poller.RunAsync(DateTime.Today);
        Console.WriteLine($"result      : {result.Summary}");

        foreach (var title in result.Titles)
        {
            Console.WriteLine($"  · {title}");
        }

        foreach (var id in result.ProposalIds)
        {
            var proposal = rt.Gate.Pending().FirstOrDefault(p => p.Id == id);
            Console.WriteLine($"  → #{id} [{proposal?.Tag ?? "-"}] {proposal?.Title ?? ""}（待确认）");
        }

        return 0;
    }
}
