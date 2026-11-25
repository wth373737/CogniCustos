using CogniCustos.Adapters;
using CogniCustos.Contracts;
using CogniCustos.Core;

namespace CogniCustos.Sample;

/// <summary>
/// Print what this process would actually use, without opening a window:
///
///   CogniCustos.Sample.exe --model-status
///
/// It answers "did my configuration take effect?" with facts — every registered provider, its
/// role, whether it is usable, and which one the reader's role resolves to — instead of leaving
/// the user to guess from a message that only says "no model".
/// </summary>
public static class ModelStatus
{
    public static int Run(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        var root = AppRuntime.Home(args.Length > 1 && !args[1].StartsWith('-') ? args[1] : null);
        var (runtime, _) = AppRuntime.Build(root);

        var cloud = runtime.CloudModels;
        Console.WriteLine($"data       : {root}");
        Console.WriteLine($"settings   : {Path.Combine(root, "state", "settings.json")}");
        Console.WriteLine($"cloud      : configured={cloud.IsConfigured} model='{cloud.Model}' base='{cloud.BaseUrl}' key={cloud.KeyHint}");
        Console.WriteLine($"prompts    : {Path.Combine(root, "state", PromptOverrides.FileName)}");
        Console.WriteLine();

        Console.WriteLine("registered providers:");
        var any = false;
        foreach (var provider in runtime.Models.All)
        {
            any = true;
            Console.WriteLine($"  {provider.Id,-30} role={provider.Role,-12} available={provider.IsAvailable}");
        }

        if (!any) Console.WriteLine("  (none)");

        var resolved = runtime.Models.Resolve(ModelRole.Reasoning);
        var standby = resolved is StandbyModelProvider;
        Console.WriteLine();
        Console.WriteLine($"resolve(reasoning) = {resolved.Id}   {(standby ? "→ NOTHING CAN ANSWER" : "→ questions will use this")}");

        if (standby)
        {
            Console.WriteLine();
            Console.WriteLine(cloud.IsConfigured
                ? "云端已配置，但本实例里没有可用的 provider：检查地址/模型名，或点一次保存让工厂重新注册。"
                : "云端未配置，本地也没有可用模型：设置页填模型，或启动 Ollama / 安装内置小模型。");
        }

        return standby ? 2 : 0;
    }
}
