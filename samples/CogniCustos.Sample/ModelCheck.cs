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
    public static async Task<int> RunAsync()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "model-check");
        Directory.CreateDirectory(root);

        var rt = CogniCustosRuntime.Create(root);
        rt.LocalModels.BuiltInFactory = path => new LlamaModelProvider(path);

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
        var askClock = Stopwatch.StartNew();
        var reply = await rt.ChatAsync("用一句话说明你能做什么。");
        askClock.Stop();

        Console.WriteLine($"reply time : {askClock.Elapsed.TotalSeconds:0.0}s");
        Console.WriteLine($"reply      : {reply.Trim()}");

        var summaryClock = Stopwatch.StartNew();
        var artifact = rt.Raw.Put("note", "model-check", "校历：2026-2027 学年第二学期 2 月 23 日开学，4 月 6 日到 4 月 10 日为考试周。", "校历摘要测试");
        summaryClock.Stop();
        Console.WriteLine($"summary    : {artifact.SummaryKind} · {summaryClock.Elapsed.TotalSeconds:0.0}s · {artifact.Summary}");

        var provider = rt.Models.All.FirstOrDefault(p => p.Role == CogniCustos.Contracts.ModelRole.Reasoning);
        Console.WriteLine($"provider   : {provider?.Id ?? "(none)"} available={provider?.IsAvailable}");

        return reply.Contains("内置模型调用失败") ? 1 : 0;
    }
}
