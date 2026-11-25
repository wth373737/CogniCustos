using CogniCustos.Adapters;
using CogniCustos.Contracts;
using CogniCustos.Core;

namespace CogniCustos.Sample;

/// <summary>
/// The one place that assembles a runtime for this demo.
///
/// It exists because "the settings test passes but the reader still says there is no model"
/// can only be answered if the GUI, the CLI checks and the diagnostics build the runtime the
/// same way. Before this, each entry point wired its own copy and they could drift.
/// </summary>
internal static class AppRuntime
{
    public const string OllamaUrl = "http://127.0.0.1:11434";
    public const string VisionModel = "qwen3-vl:4b";

    public static string Home(string? overrideRoot = null)
    {
        var root = overrideRoot ?? Environment.GetEnvironmentVariable("COGNICUSTOS_HOME");
        root ??= Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>Runtime + the vision adapter the reader uses for boxes without a text layer.</summary>
    public static (CogniCustosRuntime Runtime, OllamaVisionModel Vision) Build(string root,
        bool borrowLocalModel = true, Action<string>? log = null)
    {
        var runtime = log is null ? CogniCustosRuntime.Create(root) : CogniCustosRuntime.Create(root, log);

        // the desktop app injects the real HTTP and llama.cpp adapters after construction
        runtime.CloudModels.SetFactory((url, model, key) => new OpenAiCompatibleProvider(url, model, key));
        runtime.LocalModels.BuiltInFactory = (path, gpu) =>
            new LlamaModelProvider(path, gpuLayers: gpu ? 99 : 0, preferVulkan: gpu);

        var vision = new OllamaVisionModel(OllamaUrl, VisionModel);

        if (borrowLocalModel) BorrowLocalModel(runtime);

        return (runtime, vision);
    }

    /// <summary>
    /// Register a local Ollama model as the session's fallback, but only when nothing else is
    /// available and only if one is already installed. This is what makes a fresh demo folder
    /// able to answer questions without a configuration step; it changes no settings on disk.
    /// </summary>
    public static void BorrowLocalModel(CogniCustosRuntime runtime)
    {
        try
        {
            var already = runtime.Models.All.Any(p =>
                p.Role == ModelRole.Reasoning && p.IsAvailable);
            if (already) return;

            var probe = new OllamaModelProvider(OllamaUrl, "probe");
            if (!probe.IsAvailable) return;

            var installed = probe.ListModelsAsync().GetAwaiter().GetResult();
            var pick = installed.FirstOrDefault(m => m.StartsWith("qwen2.5:7b", StringComparison.OrdinalIgnoreCase))
                       ?? installed.FirstOrDefault(m => !m.Contains("vl", StringComparison.OrdinalIgnoreCase)
                                                       && !m.Contains("coder", StringComparison.OrdinalIgnoreCase)
                                                       && !m.Contains("r1", StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrWhiteSpace(pick)) return;

            runtime.AddModel(new OllamaModelProvider(OllamaUrl, pick));
            Console.WriteLine($"borrowed local model: {pick}");
        }
        catch
        {
            // borrowing is a convenience; failing to borrow must not stop the app
        }
    }
}
