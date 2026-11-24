using CogniCustos.Contracts;

namespace CogniCustos.Core;

public enum LocalModelSource
{
    /// <summary>The model shipped with the app (free, no setup).</summary>
    BuiltIn,

    /// <summary>The user's own Ollama install; choosing it removes the bundled model.</summary>
    Ollama,

    /// <summary>Nothing local: the runtime falls back to standby or a cloud model.</summary>
    None,
}

/// <summary>
/// Owns the switch between the bundled local model and the user's Ollama
/// (docs/PARTITION-MODEL.md §8.7, and the settings sketch):
///
/// - the Ollama option is the **advanced, pre-filled** path: address is fixed at
///   127.0.0.1:11434, the user only types a model name;
/// - choosing it **removes the bundled model** on purpose — the point of that button is
///   to free the disk, not to run both;
/// - adding a model *in addition* is the "add custom models" path, not this one.
/// </summary>
public sealed class LocalModelService
{
    private readonly string _settingsPath;
    private readonly ModelRouter _router;
    private readonly Func<string, string, IModelProvider> _ollamaFactory;
    private readonly string _modelsRoot;

    public LocalModelService(ModelRouter router, string settingsPath, string modelsRoot,
        Func<string, string, IModelProvider>? ollamaFactory = null)
    {
        _router = router;
        _settingsPath = settingsPath;
        _modelsRoot = modelsRoot;
        _ollamaFactory = ollamaFactory ?? ((url, model) => new StandbyOllamaProvider(url, model));

        Settings = SettingsStore.Load(_settingsPath);
        Source = Parse(Settings.LocalModelSource);
        Installer = new BuiltInModelInstaller(_modelsRoot);
    }

    /// <summary>Manages the bundled model's file (download / verify / path).</summary>
    public BuiltInModelInstaller Installer { get; }

    /// <summary>
    /// Creates the provider for a built-in model file. The desktop app sets this to the
    /// LLamaSharp provider; Core cannot reference the adapter that owns it.
    /// </summary>
    public Func<string, IModelProvider>? BuiltInFactory { get; set; }

    public AppSettings Settings { get; private set; }

    public LocalModelSource Source { get; private set; }

    /// <summary>Fixed by design; shown read-only in the configuration window.</summary>
    public string OllamaBaseUrl => "http://127.0.0.1:11434";

    public string OllamaModel => Settings.OllamaModel;

    public bool BuiltInInstalled => Settings.BuiltInInstalled && Installer.IsInstalled;

    public string BuiltInPath => Installer.Folder;

    /// <summary>The bundled model's name, as the settings page shows it.</summary>
    public string BuiltInModelName => BuiltInInstalled && File.Exists(Path.Combine(BuiltInPath, "model.json"))
        ? ReadModelName()
        : string.Empty;

    // ---------- switching ----------

    /// <summary>
    /// Switch to Ollama. Per the design this also deletes the bundled model, because the
    /// user is choosing this option to free the disk.
    /// </summary>
    public (bool Ok, string Message) UseOllama(string model)
    {
        model = (model ?? string.Empty).Trim();
        if (model.Length == 0) return (false, "请填写 Ollama 里的模型名称（例如 qwen2.5:7b）。");

        var deleted = DeleteBuiltIn(out var deletedMessage);
        if (!deleted) return (false, deletedMessage);

        Settings = Settings with
        {
            LocalModelSource = "ollama",
            OllamaModel = model,
            BuiltInInstalled = false,
            OllamaBaseUrl = OllamaBaseUrl,
        };
        SettingsStore.Save(_settingsPath, Settings);
        Source = LocalModelSource.Ollama;

        _router.ReplaceRole(ModelRole.Reasoning, _ollamaFactory(OllamaBaseUrl, model));

        var note = deletedMessage.Length > 0 ? $"；{deletedMessage}" : "";
        return (true, $"已切换到 Ollama 模型 {model}{note}");
    }

    /// <summary>Go back to the bundled model. Refused when it is not installed (it was removed).</summary>
    public (bool Ok, string Message) UseBuiltIn()
    {
        if (!Installer.IsInstalled)
            return (false, "内置模型尚未安装；请先在设置页下载，或继续使用 Ollama。");

        Settings = Settings with { LocalModelSource = "builtin", BuiltInInstalled = true };
        SettingsStore.Save(_settingsPath, Settings);
        Source = LocalModelSource.BuiltIn;

        _router.ReplaceRole(ModelRole.Reasoning,
            BuiltInFactory is null ? null : BuiltInFactory(Installer.ModelPath));

        return (true, "已切换回内置本地模型");
    }

    /// <summary>Download the bundled model; then switch to it.</summary>
    public async Task<(bool Ok, string Message)> InstallBuiltInAsync(IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var (ok, message) = await Installer.InstallAsync(progress, ct).ConfigureAwait(false);
        if (!ok) return (false, message);

        Settings = Settings with { BuiltInInstalled = true };
        SettingsStore.Save(_settingsPath, Settings);
        return (true, message);
    }

    /// <summary>Remove the bundled model's files. Used by both the switch and the Delete button.</summary>
    public bool DeleteBuiltIn(out string message)
    {
        message = string.Empty;

        if (!Directory.Exists(BuiltInPath))
        {
            Settings = Settings with { BuiltInInstalled = false };
            SettingsStore.Save(_settingsPath, Settings);
            message = "没有可删除的内置模型";
            return true;
        }

        long bytes = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(BuiltInPath, "*", SearchOption.AllDirectories))
            {
                bytes += new FileInfo(file).Length;
            }

            Directory.Delete(BuiltInPath, recursive: true);
        }
        catch (Exception)
        {
            return false;
        }

        Settings = Settings with { BuiltInInstalled = false };
        SettingsStore.Save(_settingsPath, Settings);
        message = $"已删除内置模型（释放 {Math.Round(bytes / 1024d / 1024d, 1)} MB）";

        if (Source == LocalModelSource.BuiltIn)
        {
            Source = LocalModelSource.None;
        }

        return true;
    }

    /// <summary>What the settings page shows on the "Small local model" row.</summary>
    public string StatusText() => Source switch
    {
        LocalModelSource.Ollama => $"Ollama · {OllamaModel}",
        LocalModelSource.BuiltIn => BuiltInInstalled ? Path.GetFileName(BuiltInPath) : "内置模型未安装",
        _ => BuiltInInstalled ? BuiltInModelName : "未安装本地模型",
    };

    private string ReadModelName()
    {
        try
        {
            var json = File.ReadAllText(Path.Combine(BuiltInPath, "model.json"));
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("name", out var name) ? name.GetString() ?? "builtin" : "builtin";
        }
        catch
        {
            return "builtin";
        }
    }

    private static LocalModelSource Parse(string? value) => value switch
    {
        "ollama" => LocalModelSource.Ollama,
        "none" => LocalModelSource.None,
        _ => LocalModelSource.BuiltIn,
    };
}

/// <summary>
/// Placeholder provider for the Ollama path when no real HTTP client is injected: it
/// reports unavailable instead of pretending to answer. The desktop app injects the real
/// Ollama adapter; tests inject their own.
/// </summary>
internal sealed class StandbyOllamaProvider : IModelProvider
{
    public StandbyOllamaProvider(string baseUrl, string model)
    {
        BaseUrl = baseUrl;
        Model = model;
    }

    public string BaseUrl { get; }
    public string Model { get; }

    public string Id => $"ollama:{Model}";
    public ModelRole Role => ModelRole.Reasoning;
    public bool IsAvailable => false;

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        => Task.FromResult(Array.Empty<float>());

    public Task<string> CompleteAsync(string system, string user, CancellationToken ct = default)
        => Task.FromResult("(Ollama provider not wired in this process)");
}
