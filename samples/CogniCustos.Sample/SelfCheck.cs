using CogniCustos.Contracts;
using CogniCustos.Core;
using CogniCustos.Ui.ViewModels;

namespace CogniCustos.Sample;

/// <summary>
/// Headless check for the sample's wiring: build a runtime in a throwaway directory,
/// read what the shell would show, then confirm an incoming item the way the dialog
/// does and verify the row really landed in SQLite. Run with <c>--check</c>.
/// </summary>
internal static class SelfCheck
{
    public static int Run()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "sample-check");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);

        try
        {
            var runtime = CogniCustosRuntime.Create(root);
            var source = new RuntimeShellDataSource(runtime);

            var line = source.Today();
            var plan = source.CalendarCard();
            var pending = source.PendingCard();
            var notes = source.NoteCard();

            Console.WriteLine($"line     : {line.Count} entries");
            Console.WriteLine($"plan     : {plan.Items.Count} blocks");
            Console.WriteLine($"incoming : {pending.Items.Count} proposals");
            Console.WriteLine($"notes    : {notes.Items.Count} paragraphs");

            var before = runtime.Schedule
                .ListRangeAsync(DateTime.Today.ToString("yyyy-MM-dd"), DateTime.Today.AddDays(30).ToString("yyyy-MM-dd"))
                .GetAwaiter().GetResult().Count;

            var item = pending.Items.FirstOrDefault();
            if (item is null)
            {
                Console.WriteLine("CHECK FAIL: no incoming item to confirm");
                return 1;
            }

            source.IncomingConfirmed(item);

            var after = runtime.Schedule
                .ListRangeAsync(DateTime.Today.ToString("yyyy-MM-dd"), DateTime.Today.AddDays(30).ToString("yyyy-MM-dd"))
                .GetAwaiter().GetResult().Count;
            var stillPending = runtime.Gate.Pending().Count;

            Console.WriteLine($"approve  : blocks {before} -> {after}, pending left {stillPending}");

            // a two-landing-point set must show up as two rows on the incoming card
            runtime.Gate.ProposeSet("two lands", "card shows one row per landing point", new[]
            {
                new Change("tasks.add", """{"title":"row one","due":"2026-12-24","level":"short"}"""),
                new Change("schedule.add", """{"date":"2026-12-24","start":"10:00","end":"11:00","title":"row two"}"""),
            });

            var setRows = source.PendingCard().Items;
            var multiOk = setRows.Count >= 2
                          && setRows.Any(row => row.Title.Contains("row one"))
                          && setRows.Any(row => row.Title.Contains("row two"))
                          && setRows.Any(row => row.When is not null && row.When.Contains("共 2 处写入"));

            Console.WriteLine($"card rows: {setRows.Count} (multi landing point: {multiOk})");

            var ok = after == before + 1 && stillPending == 0 && multiOk;
            Console.WriteLine(ok ? "OK: confirm wrote through the approval gate" : "CHECK FAIL");
            return ok ? 0 : 1;
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // the check must not fail on cleanup
            }
        }
    }
}
