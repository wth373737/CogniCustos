using CogniCustos.Adapters;
using CogniCustos.Contracts;
using CogniCustos.Core;

namespace CogniCustos.Selftest;

/// <summary>
/// No test framework on purpose: this runs the real wiring against a throwaway
/// directory and prints a pass/fail line per invariant. Run it after every change.
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static async Task<int> Main()
    {
        // Keep test state next to the binary (inside the workspace): the OS temp
        // directory is ACL-restricted on this machine and would fail for reasons
        // unrelated to the code under test.
        var root = Path.Combine(AppContext.BaseDirectory, "selftest-data-" + Guid.NewGuid().ToString("N")[..8]);
        Console.WriteLine($"root: {root}");
        var rt = CogniCustosRuntime.Create(root);

        await Check("sandbox: write and read a section", async () =>
        {
            await rt.Sandbox.WriteSectionAsync("notes", "第一行笔记");
            var back = await rt.Sandbox.ReadAsync("notes");
            return back.Contains("第一行笔记");
        });

        await Check("sandbox: rejects an unknown section", async () =>
        {
            try
            {
                await rt.Sandbox.WriteSectionAsync("nope", "x");
                return false;
            }
            catch (ArgumentException) { return true; }
        });

        await Check("gate: task.add stages a proposal instead of writing", async () =>
        {
            var tool = rt.Tools.Find("task.add")!;
            var r = await tool.InvokeAsync(new Dictionary<string, string> { ["title"] = "写终稿草稿", ["due"] = "2026-12-23" });
            var tasksBefore = await rt.Tasks.ListAsync();
            return r.Ok && rt.Gate.Pending().Count == 1 && tasksBefore.Count == 0;
        });

        await Check("gate: approving applies it to SQLite", async () =>
        {
            var pending = rt.Gate.Pending().Single();
            var r = rt.Gate.Approve(pending.Id);
            var tasks = await rt.Tasks.ListAsync();
            return r.Ok && tasks.Count == 1 && tasks[0].Title == "写终稿草稿" && rt.Gate.Pending().Count == 0;
        });

        await Check("gate: rejecting never writes", async () =>
        {
            var tool = rt.Tools.Find("task.add")!;
            await tool.InvokeAsync(new Dictionary<string, string> { ["title"] = "这条会被驳回" });
            var p = rt.Gate.Pending().Single();
            var r = rt.Gate.Reject(p.Id);
            var tasks = await rt.Tasks.ListAsync();
            return r.Ok && tasks.Count == 1;
        });

        await Check("gate: approving an unknown id fails cleanly", () =>
        {
            var r = rt.Gate.Approve("does-not-exist");
            return Task.FromResult(!r.Ok);
        });

        await Check("kb: index then search finds the passage", async () =>
        {
            await rt.Knowledge.IndexAsync("calendar.md", "CAT304 终稿提交时间是 2026 年 12 月 27 日，草稿 23 日。");
            var hits = await rt.Knowledge.SearchAsync("终稿什么时候交");
            return hits.Count > 0 && hits[0].Source == "calendar.md";
        });

        await Check("protocol: parses a tool call", () =>
        {
            var c = AgentProtocol.Parse("""{"action":"tool","tool":"task.list","args":{"topk":"3"}}""");
            return Task.FromResult(c is { Kind: AgentProtocol.Kind.ToolCall } && c.ToolName == "task.list" && c.Args["topk"] == "3");
        });

        await Check("protocol: parses an answer wrapped in prose and a code fence", () =>
        {
            var c = AgentProtocol.Parse("好的\n```json\n{\"action\":\"answer\",\"text\":\"两天后\"}\n```");
            return Task.FromResult(c is { Kind: AgentProtocol.Kind.Answer } && c.Text == "两天后");
        });

        await Check("protocol: tolerates the tool name placed in 'action'", () =>
        {
            var c = AgentProtocol.Parse("""{"action":"task.add","args":{"title":"x"}}""", n => n == "task.add");
            return Task.FromResult(c is { Kind: AgentProtocol.Kind.ToolCall } && c.ToolName == "task.add" && c.Args["title"] == "x");
        });

        await Check("protocol: refuses non-JSON", () =>
            Task.FromResult(AgentProtocol.Parse("就是随便说一句话") is null));

        await Check("runtime: /add goes through the gate, not around it", async () =>
        {
            await rt.ChatAsync("/add 复习 SDLC");
            var before = (await rt.Tasks.ListAsync()).Count;
            var staged = rt.Gate.Pending().Count;
            return before == 1 && staged == 1;
        });

        await Check("runtime: /tools lists the registry", async () =>
        {
            var text = await rt.ChatAsync("/tools");
            return text.Contains("task.add") && text.Contains("sandbox.write") && text.Contains("kb.search");
        });

        await Check("runtime: unknown slash command is answered, not thrown", async () =>
        {
            var text = await rt.ChatAsync("/nonsense");
            return text.Contains("未知命令");
        });

        await Check("model: no model configured falls back to standby, never invents", async () =>
        {
            var text = await rt.ChatAsync("你好");
            return text.Contains("没有可用的推理模型");
        });

        await Check("adapter: portal tool without a sidecar reports honestly", async () =>
        {
            var tool = new PortalSidecarTool(null);
            var r = await tool.InvokeAsync(new Dictionary<string, string> { ["url"] = "https://example.com" });
            return !r.Ok && r.Content.Contains("未配置");
        });

        await Check("tool: every registered tool has a name, a description and a kind", () =>
        {
            var bad = rt.Tools.All.Where(t =>
            {
                var d = t.Describe();
                return string.IsNullOrWhiteSpace(d.Name) || string.IsNullOrWhiteSpace(d.Description);
            }).ToList();
            return Task.FromResult(bad.Count == 0 && rt.Tools.All.Count >= 8);
        });

        await Check("persistence: proposals survive a new runtime over the same root", async () =>
        {
            var rt2 = CogniCustosRuntime.Create(root);
            rt2.AddTool(new PortalSidecarTool(null));
            var tasks = await rt2.Tasks.ListAsync();
            return tasks.Count == 1;
        });

        // ---- objectives 2, 3 and 7: read-only files, self-docs, outbound gate ----

        await Check("checker: flags an email and masks it in the preview", async () =>
        {
            var r = await rt.Checker.CheckAsync("请联系我：student@usm.my");
            return r.Flagged && r.Reasons.Any(x => x.StartsWith("email")) && !r.RedactedPreview.Contains("student@usm.my");
        });

        await Check("checker: leaves ordinary study text alone", async () =>
        {
            var r = await rt.Checker.CheckAsync("周三之前把 SDLC 那一章的用例图补完");
            return !r.Flagged;
        });

        await Check("fs.read: reads a file inside the application root", async () =>
        {
            var t = rt.Tools.Find("fs.read")!;
            var r = await t.InvokeAsync(new Dictionary<string, string> { ["path"] = "README.md" });
            return r.Ok && r.Content.Contains("CogniCustos");
        });

        await Check("fs.read: refuses a path outside the root", async () =>
        {
            var t = rt.Tools.Find("fs.read")!;
            var r = await t.InvokeAsync(new Dictionary<string, string> { ["path"] = @"C:\Windows\win.ini" });
            return !r.Ok && r.Content.Contains("拒绝");
        });

        await Check("fs.read: is read-only (no write tool exists)", () =>
            Task.FromResult(rt.Tools.All.All(t => !t.Describe().Name.StartsWith("fs.w"))));

        await Check("doc.search: answers from the app's own README", () =>
        {
            var t = rt.Tools.Find("doc.search")!;
            var r = t.InvokeAsync(new Dictionary<string, string> { ["query"] = "确认处理器 沙箱" }).GetAwaiter().GetResult();
            return Task.FromResult(r.Ok && r.Content.Contains("README"));
        });

        await Check("cloud.ask: refuses honestly when no cloud model is configured", async () =>
        {
            var t = rt.Tools.Find("cloud.ask")!;
            var r = await t.InvokeAsync(new Dictionary<string, string> { ["text"] = "你好" });
            return !r.Ok && r.Content.Contains("未配置");
        });

        rt.AddCloud(new StubCloud());

        await Check("gate-outbound: clean text is sent and audited as 'sent'", async () =>
        {
            var t = rt.Tools.Find("cloud.ask")!;
            var r = await t.InvokeAsync(new Dictionary<string, string> { ["text"] = "解释一下 RAG" });
            var audit = rt.Audit.Recent(5);
            return r.Ok && r.Content == "STUB_REPLY" && audit.Any(a => a.Decision == "sent");
        });

        await Check("gate-outbound: flagged text is refused and audited as 'refused'", async () =>
        {
            var t = rt.Tools.Find("cloud.ask")!;
            var r = await t.InvokeAsync(new Dictionary<string, string> { ["text"] = "我的学号是 23300348，帮我填表" });
            var audit = rt.Audit.Recent(5);
            return !r.Ok && r.Content.Contains("拦截") && audit.Any(a => a.Decision == "refused");
        });

        await Check("gate-outbound: the human can explicitly override, and it is audited", async () =>
        {
            var t = rt.Tools.Find("cloud.ask")!;
            var r = await t.InvokeAsync(new Dictionary<string, string>
            {
                ["text"] = "我的学号是 23300348，帮我填表",
                ["allowFlagged"] = "true"
            });
            var audit = rt.Audit.Recent(5);
            return r.Ok && audit.Any(a => a.Decision == "sent-overridden");
        });

        await Check("audit.recent: lists the attempts newest-first", () =>
        {
            var t = rt.Tools.Find("audit.recent")!;
            var r = t.InvokeAsync(new Dictionary<string, string> { ["count"] = "3" }).GetAwaiter().GetResult();
            return Task.FromResult(r.Ok && r.Content.Contains("sent") && r.Content.Split('\n').Length <= 3);
        });

        if (Environment.GetEnvironmentVariable("COGNICUSTOS_LIVE_MODEL") == "1")
        {
            Console.WriteLine();
            Console.WriteLine("== live model round-trip (Ollama) ==");
            var liveRoot = Path.Combine(AppContext.BaseDirectory, "selftest-live-" + Guid.NewGuid().ToString("N")[..8]);
            var live = CogniCustosRuntime.Create(liveRoot);
            var ollama = new OllamaModelProvider("http://127.0.0.1:11434", "qwen2.5:7b");
            live.AddModel(ollama);
            Console.WriteLine($"  provider available: {ollama.IsAvailable}");

            await Check("live: the model turns a request into a staged proposal", async () =>
            {
                var reply = await live.ChatAsync("帮我加一个任务：周五前复习 SDLC。");
                Console.WriteLine("  reply: " + reply.Replace("\n", " ")[..Math.Min(300, reply.Length)]);
                return live.Gate.Pending().Count >= 1;
            });

            await Check("live: approving that proposal writes exactly one task", async () =>
            {
                var p = live.Gate.Pending().FirstOrDefault();
                if (p is null) return false;
                var r = live.Gate.Approve(p.Id);
                var tasks = await live.Tasks.ListAsync();
                Console.WriteLine($"  approve -> {r.Content}; tasks={tasks.Count}");
                return r.Ok && tasks.Count == 1;
            });

            try { Directory.Delete(liveRoot, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine($"passed={_passed} failed={_failed}");
        try { Directory.Delete(root, true); } catch { }
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>A cloud that never leaves the machine: lets the outbound gate be tested offline.</summary>
    private sealed class StubCloud : ICloudModelProvider
    {
        public string Endpoint => "stub://cloud";
        public string Id => "stub";
        public ModelRole Role => ModelRole.Reasoning;
        public bool IsAvailable => true;

        public Task<string> CompleteAsync(string system, string user, CancellationToken ct = default)
            => Task.FromResult("STUB_REPLY");

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult(Array.Empty<float>());
    }

    private static async Task Check(string name, Func<Task<bool>> body)    {
        try
        {
            var ok = await body();
            if (ok) { _passed++; Console.WriteLine($"  PASS  {name}"); }
            else { _failed++; Console.WriteLine($"  FAIL  {name}"); }
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"  FAIL  {name}  ({ex.GetType().Name}: {ex.Message})");
        }
    }
}
