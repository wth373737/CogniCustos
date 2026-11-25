using CogniCustos.Core;

namespace CogniCustos.Sample;

/// <summary>
/// <c>--mail-peek</c>: read only. Shows the Thunderbird folders found and the newest
/// subjects per folder, so the parsing can be judged before anything is written.
/// </summary>
internal static class MailPeek
{
    public static int Run()
    {
        var folders = ThunderbirdMail.DiscoverFolders(ThunderbirdMail.DiscoverProfiles());
        Console.WriteLine($"profiles : {ThunderbirdMail.DiscoverProfiles().Count}");
        Console.WriteLine($"folders  : {folders.Count}");

        foreach (var folder in folders.Take(8))
        {
            Console.WriteLine();
            Console.WriteLine($"{folder.Label} / {folder.Folder}  (dir: {folder.Account})  {Math.Round(folder.Bytes / 1024d / 1024d, 1)} MB");

            var messages = ThunderbirdMail.ReadRecent(folder.Path, 3);
            Console.WriteLine($"  newest {messages.Count}:");

            foreach (var message in messages)
            {
                var subject = message.Subject.Length > 70 ? message.Subject[..70] : message.Subject;
                var body = message.Body.Replace('\n', ' ');
                var preview = body.Length > 60 ? body[..60] : body;
                Console.WriteLine($"  · [{message.Date:yyyy-MM-dd}] {subject}");
                Console.WriteLine($"     {message.From} :: {preview}");
            }
        }

        return 0;
    }
}
