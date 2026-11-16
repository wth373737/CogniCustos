using System.Text;
using System.Text.Json;
using CogniCustos.Contracts;

namespace CogniCustos.Core;

/// <summary>
/// Wires the layers together. Everything below this class is replaceable through
/// the interfaces in CogniCustos.Contracts; nothing here knows Avalonia exists.
/// </summary>
public sealed class CogniCustosRuntime
{
    private readonly ToolRegistry _registry;

    private CogniCustosRuntime(string root, SandboxStore sandbox, ApprovalGate gate, SqliteTaskStore tasks,
        KeywordKnowledgeBase knowledge, ToolRegistry registry, ModelRouter models,
        SelfDocs selfDocs, ISensitiveContentChecker checker, IOutboundAudit audit)
    {
        Root = root;
        Sandbox = sandbox;
        Gate = gate;
        Tasks = tasks;
        Knowledge = knowledge;
        _registry = registry;
        Models = models;
        SelfDocs = selfDocs;
        Checker = checker;
        Audit = audit;
    }

    public string Root { get; }
    public ISandbox Sandbox { get; }
    public IApprovalGate Gate { get; }
    public ITaskStore Tasks { get; }
    public IKnowledgeBase Knowledge { get; }
    public IModelRouter Models { get; }
    public ISelfDocs SelfDocs { get; }
    public ISensitiveContentChecker Checker { get; }
    public IOutboundAudit Audit { get; }
    public CloudGateway? Cloud { get; private set; }
    public IToolRegistry Tools => _registry;
    public event Action<string>? Log;

    public static CogniCustosRuntime Create(string root, Action<string>? log = null, string? appRoot = null)
    {
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        var state = Path.Combine(root, "state");
        Directory.CreateDirectory(state);

        var sandbox = new SandboxStore(Path.Combine(root, "sandbox"));
        var gate = new ApprovalGate(Path.Combine(state, "proposals.json"));
        var tasks = new SqliteTaskStore(Path.Combine(state, "cognicustos.db"));
        var knowledge = new KeywordKnowledgeBase(Path.Combine(state, "knowledge.json"));
        var registry = new ToolRegistry();
        var models = new ModelRouter();
        var selfDocs = new SelfDocs(appRoot ?? CogniCustos.Core.SelfDocs.ResolveAppRoot());
        var checker = new SensitiveContentChecker(models);
        var audit = new OutboundAudit(Path.Combine(state, "outbound-audit.jsonl"));

        var rt = new CogniCustosRuntime(root, sandbox, gate, tasks, knowledge, registry, models, selfDocs, checker, audit);
        if (log is not null) rt.Log += log;

        // The confirmation handler owns the only path from a proposal to the real stores.
        gate.RegisterApplier(BuiltinTools.TargetTasksAdd, payload =>
        {
            using var doc = JsonDocument.Parse(payload);
            var title = doc.RootElement.GetProperty("title").GetString() ?? "";
            var due = doc.RootElement.TryGetProperty("due", out var d) ? d.GetString() : null;
            var id = tasks.AddAsync(title, string.IsNullOrWhiteSpace(due) ? null : due).GetAwaiter().GetResult();
            return ToolResult.Ok0($"task #{id} added: {title}");
        });

        gate.RegisterApplier(BuiltinTools.TargetTasksComplete, payload =>
        {
            using var doc = JsonDocument.Parse(payload);
            var id = doc.RootElement.GetProperty("id").GetInt32();
            var ok = tasks.CompleteAsync(id).GetAwaiter().GetResult();
            return ok ? ToolResult.Ok0($"task #{id} marked done") : ToolResult.Fail($"no task #{id}");
        });

        gate.RegisterApplier(BuiltinTools.TargetKbIndex, payload =>
        {
            using var doc = JsonDocument.Parse(payload);
            var source = doc.RootElement.GetProperty("source").GetString() ?? "";
            var text = doc.RootElement.GetProperty("text").GetString() ?? "";
            knowledge.IndexAsync(source, text).GetAwaiter().GetResult();
            return ToolResult.Ok0($"indexed '{source}' ({text.Length} chars)");
        });

        foreach (var tool in BuiltinTools.Create(sandbox, gate, tasks, knowledge, gate.Pending, () => models.All))
            registry.Register(tool);

        foreach (var tool in ObjectiveTools.Create(selfDocs, checker, () => rt.Cloud, audit, selfDocs.Root))
            registry.Register(tool);

        // Index the application's own docs/source once so doc.search works from the first turn.
        try { selfDocs.IndexAsync().GetAwaiter().GetResult(); } catch (Exception ex) { log?.Invoke("self-docs index failed: " + ex.Message); }

        return rt;
    }

    /// <summary>The only way a cloud model becomes reachable: through the gated gateway.</summary>
    public void AddCloud(ICloudModelProvider provider)
    {
        Cloud = new CloudGateway(provider, Checker, Audit);
        Log?.Invoke($"cloud registered: {provider.Endpoint} (available={provider.IsAvailable})");
    }

    /// <summary>Adapters register their models here; the runtime never constructs one.</summary>
    public void AddModel(IModelProvider provider)
    {
        ((ModelRouter)Models).Add(provider);
        Log?.Invoke($"model registered: {provider.Id} ({provider.Role}, available={provider.IsAvailable})");
    }

    public void AddTool(ITool tool)
    {
        _registry.Register(tool);
        Log?.Invoke($"tool registered: {tool.Describe().Name}");
    }

    public async Task<string> ChatAsync(string userMessage, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage)) return "";
        await Sandbox.AppendThoughtAsync("user: " + userMessage, ct).ConfigureAwait(false);

        var direct = await TryDirectCommandAsync(userMessage, ct).ConfigureAwait(false);
        if (direct is not null) return direct;

        var provider = Models.Resolve(ModelRole.Reasoning);
        if (provider is StandbyModelProvider)
            return await provider.CompleteAsync("", userMessage, ct).ConfigureAwait(false);

        const int maxSteps = 3;
        var transcript = new StringBuilder();
        var workingUser = userMessage;

        for (var step = 0; step < maxSteps; step++)
        {
            string raw;
            try
            {
                raw = await provider.CompleteAsync(SystemPrompt(), workingUser, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return $"模型调用失败（{provider.Id}）：{ex.Message}";
            }

            var call = AgentProtocol.Parse(raw, name => _registry.Find(name) is not null);
            if (call is null)
                return raw.Trim();

            if (call.Kind == AgentProtocol.Kind.Answer)
                return call.Text;

            var result = await _registry.InvokeAsync(call.ToolName, call.Args, ct).ConfigureAwait(false);
            Log?.Invoke($"tool {call.ToolName} -> {(result.Ok ? "ok" : "fail")}");
            transcript.AppendLine($"[tool] {call.ToolName}({string.Join(",", call.Args.Select(kv => kv.Key + "=" + kv.Value))})");
            transcript.AppendLine($"[result] {(result.Ok ? "" : "ERROR: ")}{result.Content}");
            workingUser = userMessage + "\n\n--- 你已经调用的工具与结果 ---\n" + transcript + "\n请据此要么继续调用一个工具，要么给出最终回答。";
        }

        return "已达到工具调用上限。已知信息：\n" + transcript;
    }

    private async Task<string?> TryDirectCommandAsync(string message, CancellationToken ct)
    {
        var m = message.Trim();
        if (!m.StartsWith('/')) return null;
        var parts = m.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant();
        var rest = parts.Length > 1 ? parts[1] : "";

        switch (cmd)
        {
            case "/tools":
                return Tools.DescribeForPrompt();
            case "/tasks":
            {
                var r = await _registry.InvokeAsync("task.list", new Dictionary<string, string>(), ct).ConfigureAwait(false);
                return r.Content;
            }
            case "/sandbox":
                return await Sandbox.ReadAsync(ct: ct).ConfigureAwait(false);
            case "/pending":
            {
                var r = await _registry.InvokeAsync("proposal.list", new Dictionary<string, string>(), ct).ConfigureAwait(false);
                return r.Content;
            }
            case "/add":
            {
                var r = await _registry.InvokeAsync("task.add", new Dictionary<string, string> { ["title"] = rest }, ct).ConfigureAwait(false);
                return r.Content;
            }
            case "/search":
            {
                var r = await _registry.InvokeAsync("kb.search", new Dictionary<string, string> { ["query"] = rest }, ct).ConfigureAwait(false);
                return r.Content;
            }
            case "/models":
            {
                var r = await _registry.InvokeAsync("model.list", new Dictionary<string, string>(), ct).ConfigureAwait(false);
                return r.Content;
            }
            case "/audit":
            {
                var r = await _registry.InvokeAsync("audit.recent", new Dictionary<string, string>(), ct).ConfigureAwait(false);
                return r.Content;
            }
            case "/docs":
            {
                var r = await _registry.InvokeAsync("doc.search", new Dictionary<string, string> { ["query"] = rest }, ct).ConfigureAwait(false);
                return r.Content;
            }
            case "/check":
            {
                var r = await _registry.InvokeAsync("sensitive.check", new Dictionary<string, string> { ["text"] = rest }, ct).ConfigureAwait(false);
                return r.Content;
            }
            case "/cloud":
            {
                var r = await _registry.InvokeAsync("cloud.ask", new Dictionary<string, string> { ["text"] = rest }, ct).ConfigureAwait(false);
                return r.Content;
            }
            default:
                return $"未知命令 {cmd}。可用：/tools /tasks /sandbox /pending /add /search /models /audit /docs /check /cloud";
        }
    }

    private string SystemPrompt() => $$"""
        你是 CogniCustos 的助手，运行在学生的本地电脑上。

        规则（不可违背）：
        1. 你不能直接写主数据库或精选知识库。task.add / task.complete / kb.index 只会生成"提议"，由学生确认后才生效。
        2. 你可以自由写自己的沙箱（sandbox.write），沙箱只存文本，不能执行。
        3. 需要事实时就调用工具，不要凭空编造任务列表或检索结果。

        可用工具：
        {{Tools.DescribeForPrompt()}}

        输出格式：只输出一个 JSON 对象，不要代码块、不要解释。
        - 调用工具：{"action":"tool","tool":"<名字>","args":{"<参数>":"<值>"} }
        - 最终回答：{"action":"answer","text":"<给学生看的回答>"}
        """;
}
