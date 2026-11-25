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
    /// <summary>How many tool calls one assistant turn may make before it must answer.</summary>
    public const int MaxToolSteps = 4;

    private readonly ToolRegistry _registry;

    private CogniCustosRuntime(string root, SandboxStore sandbox, ApprovalGate gate, SqliteTaskStore tasks,
        SqliteScheduleStore schedule, IRawStore raw, KeywordKnowledgeBase knowledge, ToolRegistry registry,
        ModelRouter models, SelfDocs selfDocs, ISensitiveContentChecker checker, IOutboundAudit audit)
    {
        Root = root;
        Sandbox = sandbox;
        Gate = gate;
        Tasks = tasks;
        Schedule = schedule;
        Raw = raw;
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
    public IScheduleStore Schedule { get; }

    /// <summary>The information side: what was fetched, byte for byte, append only.</summary>
    public IRawStore Raw { get; }

    /// <summary>
    /// The isolated judgement step: verdicts and handles, never material. It follows
    /// whichever model is active and falls back to the deterministic judge when none is.
    /// </summary>
    public IJudge Judge { get; private set; } = new KeywordJudge();

    /// <summary>Bundled local model ⇄ the user's Ollama; choosing Ollama frees the bundled model.</summary>
    public LocalModelService LocalModels { get; private init; } = null!;

    /// <summary>The primary inference model from the settings page (OpenAI-compatible).</summary>
    public CloudModelService CloudModels { get; private init; } = null!;

    /// <summary>
    /// Startup entry point: run today's fetch if it has not run yet. Silent about the
    /// reasons a source may be unconfigured; the result says what happened.
    /// </summary>
    public async Task<PollResult?> RunDailyFetchAsync(DateTime? today = null,
        CancellationToken ct = default)
    {
        var day = today ?? DateTime.Today;
        if (!Poller.IsDue(day)) return null;

        return await Poller.RunAsync(day, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The daily "look for changes" job: fetch, keep everything in the raw zone, turn
    /// keyword hits into proposals. Nothing reaches the main database without confirmation.
    /// </summary>
    public InfoPoller Poller { get; private init; } = null!;

    /// <summary>Email accounts for the fetch: add / remove / really test the server.</summary>
    public EmailAccountsService EmailAccounts { get; private init; } = null!;

    /// <summary>Mail folders found in the Thunderbird profiles (empty = nothing to read).</summary>
    public IReadOnlyList<ThunderbirdFolder> MailFolders { get; private set; } = [];

    /// <summary>Where the user's settings live (hosts read/write the source flags here).</summary>
    public string SettingsPath { get; private set; } = "";

    /// <summary>Re-scan Thunderbird after the user adds an account there.</summary>
    public void RescanMail()
    {
        MailFolders = ThunderbirdMail.DiscoverFolders(ThunderbirdMail.DiscoverProfiles());
        Poller.UseSources(BuildSources(SettingsPath, MailSource));
    }

    /// <summary>The Thunderbird source (the settings page switches folders on it).</summary>
    public ThunderbirdMailSource MailSource { get; private set; } = null!;

    /// <summary>Folders with their on/off state, for the settings page.</summary>
    public IReadOnlyList<(string Account, string Folder, double Megabytes, bool Enabled, string Mailbox, string Server)> MailFolderViews()
    {
        var muted = SettingsStore.Load(SettingsPath).MutedMailFolders ?? [];
        return MailFolders
            .Select(f => (f.Account, f.Folder, Math.Round(f.Bytes / 1024d / 1024d, 1),
                !muted.Contains($"{f.Account}/{f.Folder}"), f.Label, f.Hostname))
            .ToList();
    }

    /// <summary>Turn one mailbox folder on or off; the next fetch only reads the kept ones.</summary>
    public bool SetMailFolderEnabled(string account, string folder, bool enabled)
    {
        var settings = SettingsStore.Load(SettingsPath);
        var muted = (settings.MutedMailFolders ?? []).ToList();
        var key = $"{account}/{folder}";

        if (enabled) muted.Remove(key);
        else if (!muted.Contains(key)) muted.Add(key);

        SettingsStore.Save(SettingsPath, settings with { MutedMailFolders = muted });
        Poller.UseSources(BuildSources(SettingsPath, MailSource));
        return true;
    }

    /// <summary>"只读这个邮箱": mute every folder that does not belong to it.</summary>
    public bool SetOnlyMailbox(string mailbox)
    {
        var settings = SettingsStore.Load(SettingsPath);
        var muted = MailFolders
            .Where(f => !string.Equals(f.Label, mailbox, StringComparison.OrdinalIgnoreCase))
            .Select(f => $"{f.Account}/{f.Folder}")
            .Distinct()
            .ToList();

        SettingsStore.Save(SettingsPath, settings with { MutedMailFolders = muted });
        Poller.UseSources(BuildSources(SettingsPath, MailSource));
        return true;
    }

    /// <summary>All discovered folders (the source asks this, then drops the muted ones).</summary>
    private static IEnumerable<ThunderbirdFolder> Candidates(ThunderbirdMailSource source, string settingsPath) =>
        source.Folders();

    /// <summary>Sources for a run: Thunderbird (if on), configured pages, an inbox folder.</summary>
    private static IReadOnlyList<IInfoSource> BuildSources(string settingsPath, ThunderbirdMailSource mail)
    {
        var settings = SettingsStore.Load(settingsPath);
        var sources = new List<IInfoSource>();

        if (settings.UseThunderbird)
        {
            mail.UseSelection(() =>
            {
                var muted = SettingsStore.Load(settingsPath).MutedMailFolders ?? [];
                return mail.Folders().Where(f => !muted.Contains($"{f.Account}/{f.Folder}"));
            });
            sources.Add(mail);
        }

        if (settings.WebSources is { Count: > 0 }) sources.Add(new WebInfoSource(settings.WebSources));
        if (!string.IsNullOrWhiteSpace(settings.InboxPath)) sources.Add(new InboxInfoSource(settings.InboxPath!));

        return sources;
    }

    /// <summary>The page list the daily fetch reads; edited from 网页浏览配置.</summary>
    public WebSourcesService WebSources { get; private init; } = null!;

    /// <summary>
    /// Switch to the bundled model and, when it is actually registered, let the raw store
    /// use it for R1 summaries (docs/PARTITION-MODEL.md §6.9: fall back when absent).
    /// </summary>
    public (bool Ok, string Message) SwitchToBuiltIn()
    {
        // an explicitly chosen local model takes over from the cloud one; the saved
        // cloud configuration is kept so the user can switch back
        CloudModels.Unregister();

        var (ok, message) = LocalModels.UseBuiltIn();
        if (!ok) return (ok, message);

        var provider = Models.All.FirstOrDefault(p =>
            p.Role == ModelRole.Reasoning && p.Id.StartsWith("builtin:", StringComparison.Ordinal));

        Judge = provider is null ? new KeywordJudge() : new ModelJudge(() => provider);

        Raw.BackgroundSummaries = provider is not null;
        Raw.Summarizer = provider is null
            ? null
            : text =>
            {
                var summary = provider
                    .CompleteAsync(PromptCatalog.Render(PromptKeys.SummaryOneLine), text)
                    .GetAwaiter().GetResult();
                return string.IsNullOrWhiteSpace(summary) ? null : summary;
            };

        return (true, message + (provider is null ? "（未接内置运行时）" : "（已接内置运行时）"));
    }

    /// <summary>Switch to Ollama and stop using a local model for summaries.</summary>
    public (bool Ok, string Message) SwitchToOllama(string model)
    {
        var (ok, message) = LocalModels.UseOllama(model);
        if (!ok) return (ok, message);

        CloudModels.Unregister();

        Raw.BackgroundSummaries = false;
        Raw.Summarizer = null;

        var provider = Models.All.FirstOrDefault(p => p.Id.StartsWith("ollama:", StringComparison.Ordinal));
        Judge = provider is null ? new KeywordJudge() : new ModelJudge(() => provider);
        return (ok, message);
    }
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

        // itemised storage lives in the same database as the main tables
        var sandbox = new SandboxStore(Path.Combine(root, "sandbox"), Path.Combine(state, "cognicustos.db"));
        // proposals live in the database; proposals.json is only its export
        var gate = new ApprovalGate(Path.Combine(state, "cognicustos.db"));
        var tasks = new SqliteTaskStore(Path.Combine(state, "cognicustos.db"));
        var schedule = new SqliteScheduleStore(tasks.DbPath);
        var raw = new SqliteRawStore(state);
        var knowledge = new KeywordKnowledgeBase(Path.Combine(state, "knowledge.json"));
        var registry = new ToolRegistry();
        var models = new ModelRouter();
        var localModels = new LocalModelService(models, Path.Combine(state, "settings.json"), Path.Combine(root, "models"));
        var cloudModels = new CloudModelService(models, Path.Combine(state, "settings.json"));
        var settingsPath = Path.Combine(state, "settings.json");
        var mailSource = new ThunderbirdMailSource();
        mailSource.UseSelection(() => Candidates(mailSource, settingsPath));

        var poller = new InfoPoller(raw, gate, BuildSources(settingsPath, mailSource), settingsPath);
        var emailAccounts = new EmailAccountsService(settingsPath);
        var webSources = new WebSourcesService(settingsPath);
        var selfDocs = new SelfDocs(appRoot ?? CogniCustos.Core.SelfDocs.ResolveAppRoot());
        var checker = new SensitiveContentChecker(models);
        var audit = new OutboundAudit(Path.Combine(state, "outbound-audit.jsonl"));

        var rt = new CogniCustosRuntime(root, sandbox, gate, tasks, schedule, raw, knowledge, registry, models, selfDocs, checker, audit)
        {
            LocalModels = localModels,
            CloudModels = cloudModels,
            Poller = poller,
            EmailAccounts = emailAccounts,
            WebSources = webSources,
            MailFolders = ThunderbirdMail.DiscoverFolders(ThunderbirdMail.DiscoverProfiles()),
            SettingsPath = settingsPath,
            MailSource = mailSource,
        };
        if (log is not null) rt.Log += log;

        // The confirmation handler owns the only path from a proposal to the real stores.
        // Database targets register the transactional form: they run inside the change
        // set's transaction, so a set lands completely or not at all.
        gate.RegisterTransactionalApplier(BuiltinTools.TargetTasksAdd, (conn, tx, payload) =>
        {
            using var doc = JsonDocument.Parse(payload);
            var title = doc.RootElement.GetProperty("title").GetString() ?? "";
            var due = doc.RootElement.TryGetProperty("due", out var d) ? d.GetString() : null;
            var level = doc.RootElement.TryGetProperty("level", out var l) ? l.GetString() : "short";
            if (!SqliteTaskStore.TryNormalizeLevel(level, out var normalized))
                return ToolResult.Fail($"unknown level '{level}' (expected long | mid | short)");

            var id = tasks.AddPlanned(conn, tx, title, string.IsNullOrWhiteSpace(due) ? null : due, normalized, null);
            return ToolResult.Ok0($"task #{id} added: {title}");
        });

        gate.RegisterTransactionalApplier(BuiltinTools.TargetTasksComplete, (conn, tx, payload) =>
        {
            using var doc = JsonDocument.Parse(payload);
            var id = doc.RootElement.GetProperty("id").GetInt32();
            return tasks.Complete(conn, tx, id)
                ? ToolResult.Ok0($"task #{id} marked done")
                : ToolResult.Fail($"no task #{id}");
        });

        // Planning horizon: a plan item is just a task with a level and an optional parent.
        gate.RegisterTransactionalApplier(PlannerTools.TargetPlanAdd, (conn, tx, payload) =>
        {
            using var doc = JsonDocument.Parse(payload);
            var title = doc.RootElement.GetProperty("title").GetString() ?? "";
            var due = doc.RootElement.TryGetProperty("due", out var d) ? d.GetString() : null;
            var level = doc.RootElement.TryGetProperty("level", out var l) ? l.GetString() : "short";
            int? parent = doc.RootElement.TryGetProperty("parentId", out var p) && p.ValueKind == JsonValueKind.Number
                ? p.GetInt32() : null;
            if (!SqliteTaskStore.TryNormalizeLevel(level, out var normalized))
                return ToolResult.Fail($"unknown level '{level}' (expected long | mid | short)");

            var id = tasks.AddPlanned(conn, tx, title, string.IsNullOrWhiteSpace(due) ? null : due, normalized, parent);
            return ToolResult.Ok0($"plan item #{id} added ({normalized}): {title}");
        });

        gate.RegisterTransactionalApplier(PlannerTools.TargetScheduleAdd, (conn, tx, payload) =>
        {
            using var doc = JsonDocument.Parse(payload);
            var date = doc.RootElement.GetProperty("date").GetString() ?? "";
            var startTime = doc.RootElement.GetProperty("start").GetString() ?? "";
            var endTime = doc.RootElement.GetProperty("end").GetString() ?? "";
            var title = doc.RootElement.GetProperty("title").GetString() ?? "";
            int? taskId = doc.RootElement.TryGetProperty("taskId", out var t) && t.ValueKind == JsonValueKind.Number
                ? t.GetInt32() : null;

            var id = schedule.Add(conn, tx, date, startTime, endTime, title, taskId);
            return ToolResult.Ok0($"block #{id} added: {date} {startTime}-{endTime} {title}");
        });

        // The knowledge base is file-backed: it cannot join the SQL transaction, so it is
        // registered in the non-transactional form and may only be proposed alone.
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

        // discovery only: the model may see that raw material exists and read its summary
        foreach (var tool in RawTools.Create(raw))
            registry.Register(tool);

        foreach (var tool in PlannerTools.Create(sandbox, gate, tasks, schedule))
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

    /// <summary>
    /// Answer, but first say whether unconfirmed material looks relevant
    /// (docs/PARTITION-MODEL.md §3.2: the main conversation gets a verdict and a handle,
    /// never the material itself).
    /// </summary>
    public async Task<string> ChatAsync(string userMessage, CancellationToken ct = default)
    {
        var notice = MaterialNotice(userMessage);
        var answer = await ChatCoreAsync(userMessage, ct).ConfigureAwait(false);
        return notice is null ? answer : notice + "\n\n" + answer;
    }

    /// <summary>
    /// The "确认收录" path that belongs to the user, not the model: the original is read by
    /// the application, indexed as confirmed material, and its status becomes accepted.
    /// </summary>
    public async Task<ToolResult> AcceptRawAsync(string artifactId, CancellationToken ct = default)
    {
        var artifact = Raw.Get(artifactId);
        if (artifact is null) return ToolResult.Fail($"no raw material #{artifactId}");

        var text = Raw.ReadText(artifactId);
        if (string.IsNullOrWhiteSpace(text)) return ToolResult.Fail($"raw #{artifactId} has no readable bytes");

        await Knowledge.IndexAsync("raw:" + artifact.Id, text, ct).ConfigureAwait(false);
        Raw.SetStatus(artifact.Id, "accepted");
        await Sandbox.AppendThoughtAsync($"accepted raw #{artifact.Id} into the knowledge base", ct).ConfigureAwait(false);

        return ToolResult.Ok0($"raw #{artifact.Id} 已确认为知识库资料（{text.Length} 字）");
    }

    private string? MaterialNotice(string question)
    {
        if (string.IsNullOrWhiteSpace(question) || Raw.List(1).Count == 0) return null;

        // once the user accepted a piece it lives in the knowledge base; do not keep
        // asking about it as if it were still unconfirmed
        var unconfirmed = Raw.List(20).Where(a => a.Status != "accepted").ToList();
        if (unconfirmed.Count == 0) return null;

        var hits = Judge.Find(question, unconfirmed)
            .Where(v => v.Relevant)
            .Take(2)
            .ToList();

        if (hits.Count == 0) return null;

        var handles = string.Join("；", hits.Select(h => $"#{h.ArtifactId}「{h.Title}」{h.Category}"));
        return $"（未确认材料，未进入上下文）{handles} —— 要确认后我再用吗？";
    }

    private async Task<string> ChatCoreAsync(string userMessage, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userMessage)) return "";
        await Sandbox.AppendThoughtAsync("user: " + userMessage, ct).ConfigureAwait(false);

        var direct = await TryDirectCommandAsync(userMessage, ct).ConfigureAwait(false);
        if (direct is not null) return direct;

        var provider = Models.Resolve(ModelRole.Reasoning);
        if (provider is StandbyModelProvider)
            return await provider.CompleteAsync("", userMessage, ct).ConfigureAwait(false);

        var maxSteps = MaxToolSteps;
        var transcript = new StringBuilder();
        var workingUser = userMessage;

        for (var step = 0; step < maxSteps; step++)
        {
            string raw;
            try
            {
                raw = await provider.CompleteAsync(ChatSystemPrompt(), workingUser, ct).ConfigureAwait(false);
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
            case "/plan":
            {
                var r = await _registry.InvokeAsync("plan.tree", new Dictionary<string, string>(), ct).ConfigureAwait(false);
                return r.Content;
            }
            case "/today":
            {
                var args = string.IsNullOrWhiteSpace(rest) ? new Dictionary<string, string>() : new Dictionary<string, string> { ["date"] = rest };
                var r = await _registry.InvokeAsync("schedule.day", args, ct).ConfigureAwait(false);
                return r.Content;
            }
            case "/week":
            {
                var args = string.IsNullOrWhiteSpace(rest) ? new Dictionary<string, string>() : new Dictionary<string, string> { ["date"] = rest };
                var r = await _registry.InvokeAsync("schedule.week", args, ct).ConfigureAwait(false);
                return r.Content;
            }
            default:
                return $"未知命令 {cmd}。可用：/tools /tasks /sandbox /pending /add /search /models /audit /docs /check /cloud /plan /today /week";
        }
    }

    /// <summary>
    /// The chat system prompt, with the tool catalogue injected. The text itself lives in
    /// <see cref="PromptCatalog"/> so the advanced-settings panel shows exactly what is sent.
    /// </summary>
    private string ChatSystemPrompt() =>
        PromptCatalog.Render(PromptKeys.ChatSystem, new Dictionary<string, string>
        {
            ["tools"] = Tools.DescribeForPrompt(),
        });
}
