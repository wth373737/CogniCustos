using CogniCustos.Contracts;

namespace CogniCustos.Core;

/// <summary>
/// The primary inference model from the settings page: an OpenAI-compatible endpoint
/// (base URL + model name + API key). Configured here, it outranks the bundled model, which
/// in turn outranks standby — so "primary" means exactly what the settings page says.
///
/// The Core project cannot reference the HTTP adapter, so the provider is injected.
/// </summary>
public sealed class CloudModelService
{
    private readonly ModelRouter _router;
    private readonly string _settingsPath;
    private Func<string, string, string?, IModelProvider> _factory;

    public CloudModelService(ModelRouter router, string settingsPath,
        Func<string, string, string?, IModelProvider>? factory = null)
    {
        _router = router;
        _settingsPath = settingsPath;
        _factory = factory ?? ((url, model, key) => new UnconfiguredCloudProvider(url, model));

        Settings = SettingsStore.Load(_settingsPath);
        if (IsConfigured) Register(Settings.CloudBaseUrl!, Settings.CloudModel!, Settings.CloudApiKey);
    }

    /// <summary>
    /// Late binding: the desktop app injects the real HTTP adapter after construction.
    /// Registering again makes an already-saved configuration take effect immediately.
    /// </summary>
    public void SetFactory(Func<string, string, string?, IModelProvider> factory)
    {
        _factory = factory;
        if (IsConfigured) Register(Settings.CloudModel!, Settings.CloudBaseUrl!, Settings.CloudApiKey);
    }

    public AppSettings Settings { get; private set; }

    /// <summary>A model name and a base URL are enough; the key is optional for local endpoints.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Settings.CloudBaseUrl) && !string.IsNullOrWhiteSpace(Settings.CloudModel);

    public string Model => Settings.CloudModel ?? string.Empty;

    public string BaseUrl => Settings.CloudBaseUrl ?? string.Empty;

    /// <summary>Never returns the key itself.</summary>
    public string KeyHint => string.IsNullOrWhiteSpace(Settings.CloudApiKey) ? "" : "已填写";

    public (bool Ok, string Message) UseCloud(string model, string baseUrl, string apiKey)
    {
        model = (model ?? string.Empty).Trim();
        baseUrl = (baseUrl ?? string.Empty).Trim();
        apiKey = (apiKey ?? string.Empty).Trim();

        if (model.Length == 0) return (false, "请填写模型名称。");
        if (baseUrl.Length == 0) return (false, "请填写接口地址（OpenAI 格式）。");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return (false, "接口地址必须是 http(s) 的绝对地址。");

        Settings = Settings with { CloudModel = model, CloudBaseUrl = baseUrl, CloudApiKey = apiKey };
        SettingsStore.Save(_settingsPath, Settings);
        Register(model, baseUrl, apiKey);

        return (true, $"主推理模型已保存：{model} @ {uri.Host}");
    }

    /// <summary>
    /// Stop using the cloud model without forgetting the configuration (the user may be
    /// trying the local one for a while; the fields stay filled).
    /// </summary>
    public void Unregister() => _router.ReplaceRole(ModelRole.Reasoning, null);

    public (bool Ok, string Message) ClearCloud()
    {
        Settings = Settings with { CloudBaseUrl = null, CloudModel = null, CloudApiKey = null };
        SettingsStore.Save(_settingsPath, Settings);
        _router.ReplaceRole(ModelRole.Reasoning, null);

        return (true, "已清除主推理模型");
    }

    /// <summary>What the settings page shows under the three fields.</summary>
    public string StatusText() => IsConfigured
        ? $"已保存：{Model} @ {HostOf(BaseUrl)}"
        : "未配置：当前使用本地模型";

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    private void Register(string model, string baseUrl, string? apiKey)
    {
        _router.ReplaceRole(ModelRole.Reasoning, null);
        var provider = _factory(baseUrl, model, apiKey);
        _router.AddFirst(provider);
    }
}

/// <summary>Placeholder when no HTTP adapter is injected: it reports unavailable, honestly.</summary>
internal sealed class UnconfiguredCloudProvider : IModelProvider
{
    public UnconfiguredCloudProvider(string baseUrl, string model)
    {
        BaseUrl = baseUrl;
        Model = model;
    }

    public string BaseUrl { get; }
    public string Model { get; }

    public string Id => $"cloud:{Model}";
    public ModelRole Role => ModelRole.Reasoning;
    public bool IsAvailable => false;

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        => Task.FromResult(Array.Empty<float>());

    public Task<string> CompleteAsync(string system, string user, CancellationToken ct = default)
        => Task.FromResult("(cloud provider not wired in this process)");
}
