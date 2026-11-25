using System.Diagnostics;
using CogniCustos.Adapters;
using CogniCustos.Core;

namespace CogniCustos.Sample;

/// <summary>
/// Headless proof that the point-read pipeline works on a real file: open, rasterise,
/// read the text layer, run the whole-file outline, then ask one question about one box.
///
/// Run it with a course PDF and, once, with a docx to exercise the Office conversion:
///   CogniCustos.Sample.exe --reader-check "…/0. Briefing CAT 304_Sem I_26.pdf"
///   CogniCustos.Sample.exe --reader-check "…/CogniCustos_Extended_Proposal_clean.docx"
/// It uses Ollama when it is running and degrades honestly when it is not.
/// </summary>
public static class ReaderCheck
{
    public static int Run(string path)
    {
        // The report is Chinese; write UTF-8 even when the shell redirects it to a file.
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        if (!File.Exists(path))
        {
            Console.WriteLine($"no such file: {path}");
            return 1;
        }

        var root = AppRuntime.Home();

        var runtime = CogniCustosRuntime.Create(root);
        var ollama = new OllamaModelProvider("http://127.0.0.1:11434", "qwen2.5:7b");
        if (ollama.IsAvailable)
        {
            runtime.AddModel(ollama);
            Console.WriteLine("model: ollama qwen2.5:7b");
        }
        else
        {
            Console.WriteLine("model: none (Ollama not reachable) — the outline will fall back");
        }

        var vision = new OllamaVisionModel("http://127.0.0.1:11434", "qwen3-vl:4b");
        Console.WriteLine($"vision: {vision.Id} available={vision.IsAvailable}");

        var reader = new RuntimeReaderService(runtime, vision,
            Environment.GetEnvironmentVariable("COGNI_DOC_DEBUG") == "1"
                ? message => Console.WriteLine("  convert: " + message)
                : null);

        var sw = Stopwatch.StartNew();
        if (reader.Open(path) is { } failure)
        {
            Console.WriteLine($"OPEN FAILED: {failure}");
            return 1;
        }

        Console.WriteLine($"open:   {reader.Title} · {reader.PageCount} pages in {sw.ElapsedMilliseconds} ms");

        sw.Restart();
        var page = reader.Render(1, 1400);
        Console.WriteLine(page is null
            ? "render: FAILED"
            : $"render: {page.Width}x{page.Height} in {sw.ElapsedMilliseconds} ms ({page.Bgra.Length / 1024} KB)");

        var text = reader.PageText(1);
        Console.WriteLine($"text:   p1 has {text.Length} chars: {Clip(text.Replace("\n", " "), 140)}");

        sw.Restart();
        var outline = reader.OutlineAsync().GetAwaiter().GetResult();
        Console.WriteLine($"outline ({sw.ElapsedMilliseconds} ms):");
        Console.WriteLine("  summary: " + Clip(outline.Summary.Replace("\n", " "), 400));
        foreach (var section in outline.Sections.Take(10))
            Console.WriteLine($"  p{section.Page}: {section.Title}");
        if (outline.Sections.Count > 10) Console.WriteLine($"  … {outline.Sections.Count - 10} more");

        // A box across the upper third of page 1: on a title slide that is the title block.
        var region = reader.TextIn(1, 0.04, 0.12, 0.96, 0.42);
        Console.WriteLine($"region: {region.Length} chars under the box: {Clip(region, 120)}");

        sw.Restart();
        var answer = reader.AskAsync(1, 0.04, 0.12, 0.96, 0.42, "这块内容在讲什么？我该怎么用它？")
            .GetAwaiter().GetResult();
        Console.WriteLine($"answer ({sw.ElapsedMilliseconds} ms):");
        Console.WriteLine("  " + Clip(answer.Replace("\n", "\n  "), 900));

        // Keep one page as a PNG so the result can be looked at without the window.
        var dump = Path.Combine(root, "reader-check");
        Directory.CreateDirectory(dump);
        var firstPage = reader.Render(1, 1200);
        if (firstPage is not null)
        {
            var file = Path.Combine(dump, "page1.png");
            File.WriteAllBytes(file, CogniCustos.Documents.PngEncoder.FromBgra(firstPage.Width, firstPage.Height, firstPage.Bgra));
            Console.WriteLine("page image: " + file);
        }

        reader.Close();
        Console.WriteLine("OK");
        return 0;
    }

    private static string Clip(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
