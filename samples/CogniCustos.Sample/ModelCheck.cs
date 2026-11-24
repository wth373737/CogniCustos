using System.Diagnostics;
using CogniCustos.Adapters;
using CogniCustos.Core;

namespace CogniCustos.Sample;

/// <summary>
/// <c>--model-check</c>: download the bundled model if needed, switch to it, and ask one
/// question so the real cost (download, load, first reply) can be measured.
/// </summary>
internal static class ModelCheck
{
    public static async Task<int> RunAsync(bool gpu = false)
    {
        var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "model-check");
        Directory.CreateDirectory(root);

        var rt = CogniCustosRuntime.Create(root);
        rt.LocalModels.BuiltInFactory = (path, useGpu) => useGpu || gpu
            ? new LlamaModelProvider(path, gpuLayers: 99, preferVulkan: true)
            : new LlamaModelProvider(path);

        Console.WriteLine($"backend    : {(gpu ? "vulkan (AMD Radeon)" : "cpu")}");

        var installer = rt.LocalModels.Installer;
        Console.WriteLine($"model root : {installer.Folder}");

        if (!installer.IsInstalled)
        {
            Console.WriteLine($"download   : {BuiltInModelInstaller.DownloadUrl}");
            var lastPercent = -10;
            var progress = new Progress<double>(p =>
            {
                var percent = (int)(p * 100);
                if (percent < lastPercent + 10) return;
                lastPercent = percent;
                Console.WriteLine($"  … {percent}%");
            });

            var clock = Stopwatch.StartNew();
            var (ok, message) = await rt.LocalModels.InstallBuiltInAsync(progress);
            clock.Stop();
            Console.WriteLine($"install    : {message} ({clock.Elapsed.TotalSeconds:0.0}s)");
            if (!ok) return 1;
        }
        else
        {
            Console.WriteLine($"installed  : {installer.InstalledBytes / 1024 / 1024} MB");
        }

        var switchClock = Stopwatch.StartNew();
        var (switched, switchMessage) = rt.SwitchToBuiltIn();
        Console.WriteLine($"switch     : {switchMessage} ({switchClock.Elapsed.TotalSeconds:0.00}s)");
        if (!switched) return 1;

        Console.WriteLine("asking the bundled model …");

        // warm-up: the first call pays the model load, the second one measures speed
        var warm = Stopwatch.StartNew();
        await rt.ChatAsync("你好。");
        warm.Stop();

        var askClock = Stopwatch.StartNew();
        var reply = await rt.ChatAsync("用一句话说明你能做什么。");
        askClock.Stop();

        var chars = reply.Trim().Length;
        Console.WriteLine($"load+warm  : {warm.Elapsed.TotalSeconds:0.0}s");
        Console.WriteLine($"reply time : {askClock.Elapsed.TotalSeconds:0.0}s  ({chars} 字, {chars / Math.Max(0.1, askClock.Elapsed.TotalSeconds):0.0} 字/秒)");
        Console.WriteLine($"reply      : {reply.Trim()}");

        // ingest must not wait for the model: it returns with the deterministic summary
        // and the model's version arrives on SummaryUpdated
        var backgroundDone = new TaskCompletionSource<CogniCustos.Contracts.RawArtifact>();
        rt.Raw.SummaryUpdated += artifact => backgroundDone.TrySetResult(artifact);

        var putClock = Stopwatch.StartNew();
        var fresh = rt.Raw.Put("note", "model-check",
            ("校历：2026-2027 学年第二学期 2 月 23 日开学，4 月 6 日到 4 月 10 日为考试周。" + DateTime.Now.Ticks), "校历摘要测试");
        putClock.Stop();
        Console.WriteLine($"ingest     : {putClock.Elapsed.TotalMilliseconds:0} ms · {fresh.SummaryKind} · {fresh.Summary}");

        var arrived = await Task.WhenAny(backgroundDone.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        if (arrived == backgroundDone.Task)
        {
            var stored = backgroundDone.Task.Result;
            Console.WriteLine($"background : {stored.SummaryKind} · {stored.Summary}");
        }
        else
        {
            Console.WriteLine("background : (no model summary within 30s)");
        }

        var provider = rt.Models.All.FirstOrDefault(p => p.Role == CogniCustos.Contracts.ModelRole.Reasoning);
        Console.WriteLine($"provider   : {provider?.Id ?? "(none)"} available={provider?.IsAvailable}");
        Console.WriteLine($"judge      : {rt.Judge.GetType().Name}");

        // the judge is the model now: prove it still answers with handles only
        var probe = rt.Raw.Put("note", "model-check", "数学分析 期末考试定在 4 月 8 日 上午 9 点。", "数学分析考试");
        var verdicts = rt.Judge.Find("数学分析考试几点", rt.Raw.List(10));
        var hit = verdicts.FirstOrDefault(v => v.ArtifactId == probe.Id);
        Console.WriteLine($"judge hit  : #{hit?.ArtifactId} relevant={hit?.Relevant} category={hit?.Category} title={hit?.Title}");

        return reply.Contains("内置模型调用失败") ? 1 : 0;
    }
}
