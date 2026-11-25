using CogniCustos.Contracts;

namespace CogniCustos.Core;

/// <summary>
/// Resolves a provider per role. Call sites never name a model; they ask for a role.
/// Providers are registered by adapters (local Ollama, cloud OpenAI-compatible, ...).
/// </summary>
public sealed class ModelRouter : IModelRouter
{
    private readonly List<IModelProvider> _providers = new();
    private readonly StandbyModelProvider _standby = new();

    public IReadOnlyList<IModelProvider> All => _providers.ToList();

    public void Add(IModelProvider provider) => _providers.Add(provider);

    /// <summary>Register at highest priority (a configured cloud model outranks local ones).</summary>
    public void AddFirst(IModelProvider provider) => _providers.Insert(0, provider);

    /// <summary>Replace every provider of one role (switching local model source).</summary>
    public void ReplaceRole(ModelRole role, IModelProvider? provider)
    {
        _providers.RemoveAll(p => p.Role == role);
        if (provider is not null) _providers.Add(provider);
    }

    public IModelProvider Resolve(ModelRole role)
    {
        var available = _providers.FirstOrDefault(p => p.Role == role && p.IsAvailable);
        return available ?? _standby;
    }
}

/// <summary>
/// Always-present last resort so the application still answers when no model is
/// reachable. It never invents content: it describes what the system can do.
/// </summary>
public sealed class StandbyModelProvider : IModelProvider
{
    private readonly Func<string> _catalog;

    public StandbyModelProvider(Func<string>? catalog = null)
    {
        _catalog = catalog ?? (() => "(tool catalog unavailable)");
    }

    public string Id => "standby";
    public ModelRole Role => ModelRole.Reasoning;
    public bool IsAvailable => true;

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        => Task.FromResult(Array.Empty<float>());

    public Task<string> CompleteAsync(string system, string user, CancellationToken ct = default)
    {
        var reply = $"""
            当前没有可用的推理模型（本地 Ollama 未连接，且未配置云端模型）。
            系统仍在运行，可直接调用的能力如下：

            {_catalog()}

            你可以直接让我建立任务提议、写入沙箱、检索知识库——这些不需要模型。
            配置模型后（模型设置 -> Ollama 地址 / 模型名），对话即可由模型接管。
            """;
        return Task.FromResult(reply);
    }
}
