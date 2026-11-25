using System.Diagnostics;
using CogniCustos.Adapters;
using CogniCustos.Core;

namespace CogniCustos.Sample;

/// <summary>
/// The reader's question path, run headlessly with the exact prompt printed.
///
///   CogniCustos.Sample.exe --reader-ask "file.pdf" 1 0.1 0.2 0.5 0.4 "这块是什么意思？"
///
/// It exists because "the reader does not answer well" can mean four different things — no
/// text under the box, the box is tiny, the model got no usable context, or the model answered
/// badly — and only the prompt tells them apart.
/// </summary>
public static class ReaderAsk
{
    public static int Run(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        if (args.Length < 2)
        {
            Console.WriteLine("usage: --reader-ask <file> [page=1] [x0=0 y0=0 x1=0 y1=0] [question]");
            return 1;
        }

        var path = args[1];
        var page = args.Length > 2 && int.TryParse(args[2], out var p) ? p : 1;

        var rect = new[] { 0d, 0d, 0d, 0d };
        if (args.Length > 6)
        {
            for (var i = 0; i < 4; i++)
            {
                if (double.TryParse(args[3 + i], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var value))
                    rect[i] = value;
            }
        }

        var question = args.Length > 7 ? string.Join(' ', args.Skip(7)) : string.Empty;

        var root = AppRuntime.Home();
        var (runtime, vision) = AppRuntime.Build(root);
        var reader = new RuntimeReaderService(runtime, vision);
        reader.TracePrompts((system, user) =>
        {
            Console.WriteLine("---- system ----");
            Console.WriteLine(system);
            Console.WriteLine("---- user ----");
            Console.WriteLine(user);
            Console.WriteLine("---- end prompt ----");
        });

        if (reader.Open(path) is { } failure)
        {
            Console.WriteLine("OPEN FAILED: " + failure);
            return 1;
        }

        Console.WriteLine($"open: {reader.Title} · {reader.PageCount} pages · page {page}");
        Console.WriteLine($"rect: {rect[0]},{rect[1]} → {rect[2]},{rect[3]}   question: {(question.Length == 0 ? "(default)" : question)}");

        var sw = Stopwatch.StartNew();
        var answer = reader.AskAsync(page, rect[0], rect[1], rect[2], rect[3], question)
            .GetAwaiter().GetResult();

        var trace = reader.LastAskTrace;
        Console.WriteLine($"trace: regionChars={trace.RegionChars} hadBox={trace.HadBox} regionPngBytes={trace.RegionPngBytes}");
        Console.WriteLine($"answer ({sw.ElapsedMilliseconds} ms, {answer.Length} chars):");
        Console.WriteLine(answer);

        reader.Close();
        return answer.Length == 0 ? 2 : 0;
    }
}
