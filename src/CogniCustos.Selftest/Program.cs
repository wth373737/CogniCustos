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

        // ---- planning horizons and the calendar ----

        await Check("plan.add: stages a long-horizon proposal, writes nothing yet", async () =>
        {
            var t = rt.Tools.Find("plan.add")!;
            var before = (await rt.Tasks.ListAsync()).Count;
            var r = await t.InvokeAsync(new Dictionary<string, string>
            {
                ["title"] = "完成 CAT304 终稿",
                ["level"] = "long",
                ["due"] = "2026-12-27"
            });
            var after = await rt.Tasks.ListAsync();
            return r.Ok && after.Count == before && rt.Gate.Pending().Any(p => p.Target == PlannerTools.TargetPlanAdd);
        });

        await Check("plan.add: approving writes the item at the long horizon", async () =>
        {
            var p = rt.Gate.Pending().First(x => x.Target == PlannerTools.TargetPlanAdd);
            var r = rt.Gate.Approve(p.Id);
            var items = await rt.Tasks.ListAsync();
            return r.Ok && items.Any(t => t.Level == "long" && t.Title.Contains("CAT304"));
        });

        await Check("plan.tree: groups items by horizon", async () =>
        {
            var r = await rt.Tools.InvokeAsync("plan.tree", new Dictionary<string, string>());
            return r.Ok && r.Content.Contains("长期") && r.Content.Contains("短期");
        });

        await Check("schedule.add: stages a block and rejects a malformed date", async () =>
        {
            var t = rt.Tools.Find("schedule.add")!;
            var bad = await t.InvokeAsync(new Dictionary<string, string>
            {
                ["date"] = "27/12/2026", ["start"] = "09:00", ["end"] = "10:00", ["title"] = "x"
            });
            var good = await t.InvokeAsync(new Dictionary<string, string>
            {
                ["date"] = "2026-12-27", ["start"] = "09:00", ["end"] = "11:00", ["title"] = "写终稿 §5 实现"
            });
            return !bad.Ok && good.Ok && rt.Gate.Pending().Any(p => p.Target == PlannerTools.TargetScheduleAdd);
        });

        await Check("schedule.add: approving puts the block in the calendar", async () =>
        {
            var p = rt.Gate.Pending().First(x => x.Target == PlannerTools.TargetScheduleAdd);
            var r = rt.Gate.Approve(p.Id);
            var blocks = await rt.Schedule.ListDayAsync("2026-12-27");
            return r.Ok && blocks.Count == 1 && blocks[0].Title.Contains("§5");
        });

        await Check("schedule.day: shows the block for that date and nothing for another", async () =>
        {
            var hit = await rt.Tools.InvokeAsync("schedule.day", new Dictionary<string, string> { ["date"] = "2026-12-27" });
            var miss = await rt.Tools.InvokeAsync("schedule.day", new Dictionary<string, string> { ["date"] = "2026-12-28" });
            return hit.Ok && hit.Content.Contains("§5") && miss.Ok && miss.Content.Contains("空");
        });

        await Check("schedule.week: the block's week contains that day", async () =>
        {
            var r = await rt.Tools.InvokeAsync("schedule.week", new Dictionary<string, string> { ["date"] = "2026-12-27" });
            return r.Ok && r.Content.Contains("12-27") && r.Content.Contains("§5");
        });

        await Check("migration: an old tasks table without level is upgraded in place", () =>
        {
            var legacyRoot = Path.Combine(AppContext.BaseDirectory, "selftest-legacy-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(legacyRoot);
            var dbPath = Path.Combine(legacyRoot, "old.db");
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE tasks (id INTEGER PRIMARY KEY AUTOINCREMENT, title TEXT NOT NULL, due TEXT NULL, done INTEGER NOT NULL DEFAULT 0, created TEXT NOT NULL); INSERT INTO tasks(title, created) VALUES ('legacy row', '2026-01-01');";
                cmd.ExecuteNonQuery();
            }
            var store = new SqliteTaskStore(dbPath);
            var items = store.ListAsync().GetAwaiter().GetResult();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(legacyRoot, true);
            return Task.FromResult(items.Count == 1 && items[0].Level == "short");
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

            await Check("live: a planning request yields a long-horizon item AND a calendar block", async () =>
            {
                var reply = await live.ChatAsync("把「这学期完成终稿」放进长期计划，再安排 12 月 27 日上午九点到十一点写草稿。");
                Console.WriteLine("  reply: " + reply.Replace("\n", " ")[..Math.Min(200, reply.Length)]);
                var pending = live.Gate.Pending();
                Console.WriteLine("  pending targets: " + string.Join(", ", pending.Select(p => p.Target)));

                foreach (var p in pending.ToList())
                {
                    var r = live.Gate.Approve(p.Id);
                    Console.WriteLine($"  approve {p.Target}: {(r.Ok ? "ok" : "FAILED: " + r.Content)}");
                }

                var items = await live.Tasks.ListAsync();
                var blocks = await live.Schedule.ListRangeAsync("2026-12-01", "2026-12-31");
                Console.WriteLine("  levels: " + string.Join(",", items.Select(t => t.Level)));
                Console.WriteLine("  blocks: " + string.Join(",", blocks.Select(b => $"{b.Date} {b.Start}-{b.End}")));
                return items.Any(t => t.Level == "long") && blocks.Count > 0;
            });

            try { Directory.Delete(liveRoot, true); } catch { }
        }

        Console.WriteLine();
        // ---------- P0: schema, provenance, raw store, strict levels ----------

        await Check("schema: the file is at the current version and reopening is a no-op", async () =>
        {
            var dbPath = Path.Combine(root, "state", "cognicustos.db");
            using var first = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            first.Open();
            var before = SqliteSchema.UserVersion(first);
            SqliteSchema.EnsureUpgraded(first);
            var after = SqliteSchema.UserVersion(first);
            return before == SqliteSchema.Version && after == before;
        });

        await Check("schema: every authoritative row carries partition / author / source_ref / representation", async () =>
        {
            var dbPath = Path.Combine(root, "state", "cognicustos.db");
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            conn.Open();

            foreach (var table in new[] { "tasks", "schedule_blocks" })
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"PRAGMA table_info({table});";
                var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) columns.Add(reader.GetString(1));
                reader.Close();

                if (!columns.Contains("partition") || !columns.Contains("author") ||
                    !columns.Contains("source_ref") || !columns.Contains("representation"))
                    return false;
            }

            return true;
        });

        await Check("raw: the same text is one artifact (content addressed)", async () =>
        {
            var text = "校历：2026-2027 学年第二学期 2 月 23 日开学，4 月 6 日到 4 月 10 日为考试周。";
            var first = rt.Raw.Put("calendar", "portal.usm.my", text);
            var second = rt.Raw.Put("calendar", "portal.usm.my", text);
            var all = rt.Raw.List(50);
            return first.Id == second.Id && all.Count(a => a.Hash == first.Hash) == 1;
        });

        await Check("raw: the fallback summary exists offline, is deterministic and keeps its bytes", async () =>
        {
            var text = "1. 学期起止：2026-09-07 至 2027-01-16。\n2. 考试周：2027-01-04 至 2027-01-15。";
            var artifact = rt.Raw.Put("calendar", "portal.usm.my", text, "2026-2027 校历");

            var again = rt.Raw.Put("calendar", "portal.usm.my", text, "2026-2027 校历");
            var summaryOk = artifact.SummaryKind == "fallback"
                            && artifact.Summary.Length > 0
                            && artifact.Summary == again.Summary;
            var bytesOk = artifact.BytesPath is not null
                          && File.Exists(Path.Combine(root, "state", artifact.BytesPath))
                          && rt.Raw.ReadText(artifact.Id) == text;
            return summaryOk && bytesOk && artifact.Title == "2026-2027 校历";
        });

        await Check("raw: retrieval can find a summary by keyword without opening the bytes", async () =>
        {
            var hits = rt.Raw.SearchSummaries("考试周");
            return hits.Count > 0 && hits.Any(h => h.Summary.Contains("考试周"));
        });

        await Check("level: an unknown level is refused, not silently turned into short", async () =>
        {
            var rejected = !SqliteTaskStore.TryNormalizeLevel("urgent", out _)
                           && SqliteTaskStore.TryNormalizeLevel("long", out var normalized)
                           && normalized == "long";

            var tool = rt.Tools.Find("task.add")!;
            var result = await tool.InvokeAsync(new Dictionary<string, string>
            {
                ["title"] = "级别写错的任务",
                ["level"] = "urgent",
            });

            return rejected && !result.Ok && result.Content.Contains("level");
        });

        // ---------- P0-2: the sandbox is itemised, the markdown is an export ----------

        await Check("sandbox: two writes of the same section are two entries, the file keeps the latest", async () =>
        {
            await rt.Sandbox.WriteSectionAsync("notes", "第一版判断");
            await Task.Delay(5);
            await rt.Sandbox.WriteSectionAsync("notes", "第二版判断，修正了第一版");

            var store = (SandboxStore)rt.Sandbox;
            var entries = store.Entries("notes");
            var file = await File.ReadAllTextAsync(store.DocumentPath);

            return entries.Count >= 2
                   && entries[0].Text.Contains("第二版")
                   && entries[1].Text.Contains("第一版")
                   && entries[0].CreatedAt >= entries[1].CreatedAt
                   && file.Contains("第二版判断")
                   && !file.Contains("第一版判断");
        });

        await Check("sandbox: a thought is recorded as its own entry", async () =>
        {
            await rt.Sandbox.AppendThoughtAsync("thinking about the calendar import");
            var store = (SandboxStore)rt.Sandbox;
            var thoughts = store.Entries("thoughts");
            return thoughts.Count >= 1 && thoughts[0].Kind == "thought";
        });

        // ---------- P0-3: one proposal = one change set = one transaction ----------

        await Check("change set: two database targets land together", async () =>
        {
            var before = rt.Tasks.ListAsync().Result.Count;
            var summary = rt.Gate.ProposeSet("考试安排", "一条提议，两个落点", new Change[]
            {
                new(BuiltinTools.TargetTasksAdd, """{"title":"复习组织行为学","due":"2026-12-20","level":"mid"}"""),
                new(PlannerTools.TargetScheduleAdd, """{"date":"2026-12-20","start":"15:00","end":"17:00","title":"组织行为学复习"}"""),
            });

            var result = rt.Gate.Approve(summary.Id);
            var after = rt.Tasks.ListAsync().Result;
            var blocks = rt.Schedule
                .ListRangeAsync("2026-12-20", "2026-12-20").Result;

            return result.Ok
                   && rt.Gate.Get(summary.Id)!.Status == ProposalStatus.Approved
                   && after.Count == before + 1
                   && after.Any(t => t.Title == "复习组织行为学" && t.Level == "mid")
                   && blocks.Any(b => b.Title == "组织行为学复习");
        });

        await Check("change set: one failing change rolls the whole set back", async () =>
        {
            var beforeTasks = rt.Tasks.ListAsync().Result.Count;
            var set = rt.Gate.ProposeSet("半对半错", "第二条必然失败", new Change[]
            {
                new(BuiltinTools.TargetTasksAdd, """{"title":"不该留下来的任务","due":"2026-12-21","level":"short"}"""),
                new(BuiltinTools.TargetTasksComplete, """{"id":999999}"""),
            });

            var result = rt.Gate.Approve(set.Id);
            var afterTasks = rt.Tasks.ListAsync().Result;
            var stillPending = rt.Gate.Get(set.Id)!.Status == ProposalStatus.Pending;

            return !result.Ok
                   && result.Content.Contains("nothing was written")
                   && afterTasks.Count == beforeTasks
                   && afterTasks.All(t => t.Title != "不该留下来的任务")
                   && stillPending;
        });

        await Check("change set: a file-backed target may not join a set", async () =>
        {
            var before = rt.Tasks.ListAsync().Result.Count;
            var set = rt.Gate.ProposeSet("混合落点", "文件型目标不能进事务", new Change[]
            {
                new(BuiltinTools.TargetTasksAdd, """{"title":"混合提议里的任务","level":"short"}"""),
                new(BuiltinTools.TargetKbIndex, """{"source":"mixed.md","text":"内容"}"""),
            });

            var result = rt.Gate.Approve(set.Id);
            var after = rt.Tasks.ListAsync().Result;

            return !result.Ok
                   && result.Content.Contains("atomically")
                   && after.Count == before
                   && rt.Gate.Get(set.Id)!.Status == ProposalStatus.Pending;
        });

        await Check("proposals: a new runtime over the same root sees the same decisions", async () =>
        {
            var set = rt.Gate.ProposeSet("跨重启", "重启后仍可查", new Change[]
            {
                new(BuiltinTools.TargetTasksAdd, """{"title":"重启后还在的提议","level":"short"}"""),
            });

            var restarted = CogniCustosRuntime.Create(root);
            var found = restarted.Gate.Get(set.Id);

            restarted.Gate.Reject(set.Id);
            var rejected = rt.Gate.Get(set.Id)!.Status == ProposalStatus.Rejected;

            return found is not null && found.Status == ProposalStatus.Pending && rejected;
        });

        // ---------- P1: the card line is what would really be written ----------

        await Check("renderer: every registered target has a human line", async () =>
        {
            var schedule = ChangeRenderer.Describe(new Change(PlannerTools.TargetScheduleAdd,
                """{"date":"2026-12-20","start":"15:00","end":"17:00","title":"组织行为学复习"}"""));
            var task = ChangeRenderer.Describe(new Change(BuiltinTools.TargetTasksAdd,
                """{"title":"写终稿","due":"2026-12-27","level":"long"}"""));
            var plan = ChangeRenderer.Describe(new Change(PlannerTools.TargetPlanAdd,
                """{"title":"期末周","level":"mid","parentId":3}"""));
            var complete = ChangeRenderer.Describe(new Change(BuiltinTools.TargetTasksComplete,
                """{"id":12}"""));

            return schedule.Ok && schedule.Line.Contains("12/20") && schedule.Line.Contains("15:00–17:00")
                   && schedule.Line.Contains("组织行为学复习")
                   && task.Ok && task.Line.Contains("写终稿") && task.Line.Contains("2026-12-27") && task.Line.Contains("长期")
                   && plan.Ok && plan.Line.Contains("中期") && plan.Line.Contains("#3")
                   && complete.Ok && complete.Line.Contains("#12");
        });

        await Check("renderer: a malformed payload is reported before the user decides", async () =>
        {
            var bad = ChangeRenderer.Describe(new Change(BuiltinTools.TargetTasksAdd, """{"due":"2026-12-01"}"""));
            var broken = ChangeRenderer.Describe(new Change(BuiltinTools.TargetTasksAdd, "not json"));

            return !bad.Ok && bad.Problem is not null && !broken.Ok && broken.Problem is not null;
        });

        await Check("preview: a valid set previews without writing and can then be applied", async () =>
        {
            var before = rt.Tasks.ListAsync().Result.Count;
            var set = rt.Gate.ProposeSet("预演用例", "两处落点", new Change[]
            {
                new(BuiltinTools.TargetTasksAdd, """{"title":"预演任务","due":"2026-12-22","level":"short"}"""),
                new(PlannerTools.TargetScheduleAdd, """{"date":"2026-12-22","start":"09:00","end":"10:00","title":"预演日程"}"""),
            });

            var preview = rt.Gate.Preview(set.Id);
            var afterPreview = rt.Tasks.ListAsync().Result.Count;
            var applied = rt.Gate.Approve(set.Id);
            var afterApply = rt.Tasks.ListAsync().Result.Count;

            return preview.Ok
                   && preview.Changes.Count == 2
                   && preview.Text.Contains("预演任务")
                   && afterPreview == before
                   && applied.Ok
                   && afterApply == before + 1;
        });

        await Check("preview: a set that cannot apply is refused before confirming", async () =>
        {
            var before = rt.Tasks.ListAsync().Result.Count;
            var set = rt.Gate.ProposeSet("预演失败", "第二条不存在", new Change[]
            {
                new(BuiltinTools.TargetTasksAdd, """{"title":"预演时不该出现","level":"short"}"""),
                new(BuiltinTools.TargetTasksComplete, """{"id":888888}"""),
            });

            var preview = rt.Gate.Preview(set.Id);
            var after = rt.Tasks.ListAsync().Result;

            return !preview.Ok
                   && preview.Problem is not null
                   && preview.Problem.Contains("预演")
                   && after.Count == before
                   && rt.Gate.Get(set.Id)!.Status == ProposalStatus.Pending;
        });

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
