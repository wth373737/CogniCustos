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
            var tool = new PortalSidecarTool(null, rt.Raw);
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
            rt2.AddTool(new PortalSidecarTool(null, rt2.Raw));
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

        // ---------- step 1: the fetcher writes the raw store, the model may only discover ----------

        await Check("raw: list and search expose summaries, never the bytes", async () =>
        {
            rt.Raw.Put("portal", "https://elearning.usm.my/akp202w", "AKP202w teaching plan: test 1 on 2026-11-30, 15:00-17:00.",
                "AKP202w Teaching Plan");

            var list = await rt.Tools.InvokeAsync("raw.list", new Dictionary<string, string> { ["limit"] = "5" });
            var search = await rt.Tools.InvokeAsync("raw.search", new Dictionary<string, string> { ["query"] = "AKP202w" });

            var noByteTool = rt.Tools.Find("raw.read") is null && rt.Tools.Find("raw.get") is null;

            return list.Ok && list.Content.Contains("AKP202w Teaching Plan")
                   && search.Ok && search.Content.Contains("unconfirmed")
                   && !search.Content.Contains("测试 1 在 2026-11-30") // the bytes are not returned
                   && noByteTool;
        });

        await Check("portal: a successful read lands in the raw store and returns its handle", async () =>
        {
            var before = rt.Raw.List(50).Count;
            var tool = new PortalSidecarTool("http://stub.invalid", rt.Raw, (url, domain) =>
                Task.FromResult((true, """{"title":"2026-2027 Calendar","text":"Term 2 starts 2027-02-23; exam week 2027-04-06 to 2027-04-10."}""")));

            var result = await tool.InvokeAsync(new Dictionary<string, string> { ["url"] = "https://portal.usm.my/calendar" });
            var after = rt.Raw.List(50);

            return result.Ok
                   && result.Content.Contains("[raw #")
                   && after.Count == before + 1
                   && after[0].Title == "2026-2027 Calendar"
                   && after[0].Source == "https://portal.usm.my/calendar"
                   && rt.Raw.ReadText(after[0].Id)!.Contains("exam week");
        });

        await Check("portal: the same page read twice is one artifact", async () =>
        {
            var body = """{"title":"Same Page","text":"unchanged bytes"}""";
            var tool = new PortalSidecarTool("http://stub.invalid", rt.Raw, (url, domain) =>
                Task.FromResult((true, body)));

            var first = await tool.InvokeAsync(new Dictionary<string, string> { ["url"] = "https://portal.usm.my/same" });
            var second = await tool.InvokeAsync(new Dictionary<string, string> { ["url"] = "https://portal.usm.my/same" });

            return first.Ok && second.Ok
                   && ((RawArtifact)first.Data!).Id == ((RawArtifact)second.Data!).Id;
        });

        // ---------- P2: the visibility ladder and the isolated judge ----------

        await Check("visibility: raw material is R1, sandbox is S, only K/M may enter the main context", async () =>
        {
            var artifact = rt.Raw.Put("calendar", "portal.usm.my", "Visibility test material: exam week 2027-04-06.",
                "Visibility test");
            var sandbox = ((SandboxStore)rt.Sandbox).Entries("notes")[0];

            return artifact.Visibility == Visibility.RawSummary
                   && sandbox.Visibility == Visibility.Sandbox
                   && !Visibility.AllowsMainContext(Visibility.RawSummary)
                   && !Visibility.AllowsMainContext(Visibility.Sandbox)
                   && Visibility.AllowsMainContext(Visibility.Knowledge)
                   && Visibility.AllowsMainContext(Visibility.Main)
                   && Visibility.Normalize("nonsense") == Visibility.RawOriginal;
        });

        await Check("visibility: what reaches the knowledge base is confirmed and citable", async () =>
        {
            var indexFile = Path.Combine(root, "state", "knowledge.json");
            await rt.Knowledge.IndexAsync("confirmed.md", "已确认的资料：考试周是 2027-04-06 到 2027-04-10。");

            var raw = await File.ReadAllTextAsync(indexFile);
            var hits = await rt.Knowledge.SearchAsync("考试周");

            return raw.Contains("\"K\"")
                   && hits.Count > 0
                   && hits[0].Source == "confirmed.md";
        });

        await Check("judge: answers with a verdict and a handle, never the material", async () =>
        {
            var artifact = rt.Raw.Put("calendar", "portal.usm.my",
                "The bytes mention a hidden detail: 具体教室在 A3-201。",
                "2027 考试周安排");

            var verdicts = rt.Judge.Find("考试周什么时候开始", rt.Raw.List(20));
            var hit = verdicts.FirstOrDefault(v => v.ArtifactId == artifact.Id);

            return hit is not null
                   && hit.Relevant
                   && hit.Category is "日程" or "课程"
                   && hit.Title.Contains("考试周")
                   && !verdicts.Any(v => v.Title.Contains("具体教室"));
        });

        await Check("judge: only the summary is read - a detail past its end is invisible", async () =>
        {
            // the R1 summary is the first 200 characters, so this detail sits beyond it
            var bytes = new string('x', 400) + " 秘密教室在 A3-201";
            var artifact = rt.Raw.Put("note", "manual", bytes, "与问题无关的标题");

            var verdicts = rt.Judge.Find("秘密教室在哪个楼", rt.Raw.List(20));
            var hit = verdicts.FirstOrDefault(v => v.ArtifactId == artifact.Id);

            // …and the same question does match once the detail is inside the summary
            var inSummary = rt.Raw.Put("note", "manual2", "秘密教室在 A3-201", "与问题无关的标题");
            var second = rt.Judge.Find("秘密教室在哪个楼", rt.Raw.List(20))
                .FirstOrDefault(v => v.ArtifactId == inSummary.Id);

            return hit is not null && !hit.Relevant && hit.Score < 0.2
                   && second is not null && second.Relevant;
        });

        // ---------- P3: the notice and the user's own acceptance ----------

        await Check("chat: a relevant unconfirmed material is mentioned by handle, not quoted", async () =>
        {
            var artifact = rt.Raw.Put("calendar", "portal.usm.my",
                "考试周：2027-04-06 至 2027-04-10，具体安排见附件。", "2027 考试周（未确认）");

            var reply = await rt.ChatAsync("考试周是什么时候");
            var mentioned = reply.Contains("未确认材料") && reply.Contains("#" + artifact.Id) && reply.Contains("考试周");
            var noBody = !reply.Contains("具体安排见附件");

            return mentioned && noBody;
        });

        await Check("chat: an unrelated question gets no notice", async () =>
        {
            var reply = await rt.ChatAsync("今天心情不错");
            return !reply.Contains("未确认材料");
        });

        await Check("accept: the user can turn raw material into confirmed knowledge", async () =>
        {
            var artifact = rt.Raw.Put("calendar", "manual", "确认后才进知识库：2027-05-01 是校庆日。", "校庆日");

            var accepted = await rt.AcceptRawAsync(artifact.Id);
            var hits = await rt.Knowledge.SearchAsync("校庆日");
            var stored = rt.Raw.Get(artifact.Id)!;
            var reply = await rt.ChatAsync("校庆日是哪天");

            // other unconfirmed material may legitimately still be mentioned; what must
            // stop is this artifact being offered as unconfirmed
            return accepted.Ok
                   && stored is not null && stored.Status == "accepted"
                   && hits.Any(h => h.Source == "raw:" + artifact.Id)
                   && !reply.Contains("#" + artifact.Id);
        });

        await Check("accept: an unknown id fails cleanly", async () =>
        {
            var result = await rt.AcceptRawAsync("999999");
            return !result.Ok && result.Content.Contains("no raw material");
        });

        // ---------- local model: bundled ⇄ Ollama, with the automatic cleanup ----------

        await Check("local model: choosing Ollama deletes the bundled model and persists", async () =>
        {
            // simulate an installed bundled model
            var builtIn = Path.Combine(root, "models", "builtin");
            Directory.CreateDirectory(builtIn);
            // the installer looks for the model file by its released name
            await File.WriteAllBytesAsync(Path.Combine(builtIn, BuiltInModelInstaller.ModelFileName),
                new byte[1024 * 512]);
            await File.WriteAllTextAsync(Path.Combine(builtIn, "model.json"), """{"name":"qwen2.5:7b"}""");

            var service = new LocalModelService((ModelRouter)rt.Models,
                Path.Combine(root, "state", "settings.json"), Path.Combine(root, "models"));

            var installedBefore = service.BuiltInInstalled;
            var switched = service.UseOllama("qwen2.5:7b");

            var reloaded = SettingsStore.Load(Path.Combine(root, "state", "settings.json"));
            var provider = rt.Models.All.FirstOrDefault(p => p.Id == "ollama:qwen2.5:7b");

            return installedBefore
                   && switched.Ok
                   && switched.Message.Contains("释放")
                   && !Directory.Exists(builtIn)
                   && reloaded.LocalModelSource == "ollama"
                   && !reloaded.BuiltInInstalled
                   && provider is not null;
        });

        await Check("local model: the Ollama address is fixed and the model name is required", async () =>
        {
            var service = new LocalModelService((ModelRouter)rt.Models,
                Path.Combine(root, "state", "settings.json"), Path.Combine(root, "models"));

            var empty = service.UseOllama("   ");
            var back = service.UseBuiltIn();

            return service.OllamaBaseUrl == "http://127.0.0.1:11434"
                   && !empty.Ok
                   && !back.Ok; // the bundled model was deleted by the previous switch
        });

        await Check("local model: deleting an absent bundled model is not an error", async () =>
        {
            var service = new LocalModelService((ModelRouter)rt.Models,
                Path.Combine(root, "state", "settings.json"), Path.Combine(root, "models"));

            var deleted = service.DeleteBuiltIn(out var message);
            return deleted && message.Contains("没有可删除");
        });

        // ---------- built-in model: file management and model summaries ----------

        await Check("builtin: the installer knows its file and reports it as missing", async () =>
        {
            var installer = new BuiltInModelInstaller(Path.Combine(root, "models-none"));
            return BuiltInModelInstaller.ModelFileName.EndsWith(".gguf")
                   && BuiltInModelInstaller.DownloadUrl.StartsWith("https://")
                   && !installer.IsInstalled
                   && installer.InstalledBytes == 0;
        });

        await Check("raw: a summary from the model is recorded as summary_kind=model", async () =>
        {
            var store = new SqliteRawStore(Path.Combine(root, "state"));
            store.Summarizer = text => "模型摘要：" + text[..Math.Min(20, text.Length)];

            var artifact = store.Put("note", "test", "这是一段很长的原始材料，用来验证模型摘要会被记录。", "带模型摘要的材料");
            return artifact.SummaryKind == "model" && artifact.Summary.StartsWith("模型摘要：");
        });

        await Check("raw: a broken summariser falls back instead of losing the ingest", async () =>
        {
            var store = new SqliteRawStore(Path.Combine(root, "state"));
            store.Summarizer = _ => throw new InvalidOperationException("no model");

            var artifact = store.Put("note", "test", "模型坏了也必须能入库。", "坏的摘要器");
            return artifact.SummaryKind == "fallback" && artifact.Summary.Length > 0;
        });

        // ---------- the judge: model verdicts, with the deterministic fallback ----------

        await Check("judge: a model verdict is used and merged with the deterministic one", async () =>
        {
            var artifacts = new List<RawArtifact>
            {
                new("1", "h1", "note", "portal", "校历", "2026-2027 学年第二学期", "fallback", null, 0,
                    DateTimeOffset.Now, "kept", "R1"),
                new("2", "h2", "note", "portal", "考试周安排", "4 月 6 日至 10 日", "fallback", null, 0,
                    DateTimeOffset.Now, "kept", "R1"),
            };

            var provider = new ScriptedProvider("""[{"id":"2","relevant":true,"category":"日程"}]""");
            var judge = new ModelJudge(() => provider);

            var verdicts = judge.Find("考试周是什么时候", artifacts);

            var second = verdicts.First(v => v.ArtifactId == "2");
            var first = verdicts.First(v => v.ArtifactId == "1");
            return second.Relevant && second.Category == "日程" && !first.Relevant;
        });

        await Check("judge: an unparsable model answer falls back to keywords", async () =>
        {
            var artifacts = new List<RawArtifact>
            {
                new("3", "h3", "note", "portal", "考试周", "4 月 6 日到 4 月 10 日", "fallback", null, 0,
                    DateTimeOffset.Now, "kept", "R1"),
            };

            var provider = new ScriptedProvider("抱歉，我不确定。");
            var byModel = new ModelJudge(() => provider).Find("考试周", artifacts);
            var byKeywords = new KeywordJudge().Find("考试周", artifacts);

            return byModel.Count == byKeywords.Count
                   && byModel[0].Relevant == byKeywords[0].Relevant
                   && byModel[0].Score == byKeywords[0].Score;
        });

        await Check("judge: no model means the deterministic judge, not an empty answer", async () =>
        {
            var artifacts = new List<RawArtifact>
            {
                new("4", "h4", "note", "portal", "考试周", "4 月 6 日到 4 月 10 日", "fallback", null, 0,
                    DateTimeOffset.Now, "kept", "R1"),
            };

            var verdicts = new ModelJudge(() => null).Find("考试周", artifacts);
            return verdicts.Count == 1 && verdicts[0].Relevant;
        });

        await Check("raw: a background summary does not block the ingest", async () =>
        {
            var store = new SqliteRawStore(Path.Combine(root, "state"));
            store.BackgroundSummaries = true;
            store.Summarizer = text => "模型摘要（后台）：" + text[..Math.Min(12, text.Length)];

            var updated = new TaskCompletionSource<RawArtifact>();
            store.SummaryUpdated += artifact => updated.TrySetResult(artifact);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var artifact = store.Put("note", "background", "后台摘要测试：这段文字会先以降级摘要入库。", "后台摘要");
            clock.Stop();

            var fast = clock.ElapsedMilliseconds < 500 && artifact.SummaryKind == "fallback";

            var finished = await Task.WhenAny(updated.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            if (finished != updated.Task) return false;

            var stored = store.Get(artifact.Id)!;
            return fast
                   && stored.SummaryKind == "model"
                   && stored.Summary.StartsWith("模型摘要（后台）")
                   && updated.Task.Result.Id == artifact.Id;
        });

        await Check("raw: a failing background summariser keeps the deterministic one", async () =>
        {
            var store = new SqliteRawStore(Path.Combine(root, "state"));
            store.BackgroundSummaries = true;
            store.Summarizer = _ => throw new InvalidOperationException("model gone");

            var artifact = store.Put("note", "background", "坏模型不能把已入库的摘要弄丢。", "坏模型");
            await Task.Delay(600);

            var stored = store.Get(artifact.Id)!;
            return stored.SummaryKind == "fallback" && stored.Summary.Length > 0;
        });

        // ---------- the daily fetch: raw first, proposals next, main DB on confirmation ----------

        await Check("fetch: keyword hits become proposals, everything lands in the raw zone", async () =>
        {
            var inbox = Path.Combine(root, "inbox");
            Directory.CreateDirectory(inbox);
            File.WriteAllText(Path.Combine(inbox, "exam.txt"),
                "教务通知\n关于 2027 年 4 月 6 日 至 4 月 10 日 考试安排的通知：考试周期间停课。");

            var settingsPath = Path.Combine(root, "state", "settings.json");
            var settings = SettingsStore.Load(settingsPath);
            SettingsStore.Save(settingsPath, settings with { InboxPath = inbox });

            var rawBefore = rt.Raw.List(100).Count;
            var poller = new InfoPoller(rt.Raw, rt.Gate,
                new IInfoSource[] { new InboxInfoSource(inbox) }, settingsPath);

            var result = await poller.RunAsync(DateTime.Today);

            var rawAfter = rt.Raw.List(100).Count;
            Console.WriteLine($"      fetch: {result.Summary} raw {rawBefore}->{rawAfter} titles=[{string.Join(",", result.Titles)}]");
            return result.Fetched >= 1
                   && result.Important >= 1
                   && result.ProposalIds.Count >= 1
                   && rawAfter > rawBefore
                   && result.Titles.Count == 1;
        });

        await Check("fetch: confirming a fetched proposal puts it on the plan and the calendar", async () =>
        {
            var inbox = Path.Combine(root, "inbox");
            var settingsPath = Path.Combine(root, "state", "settings.json");
            var poller = new InfoPoller(rt.Raw, rt.Gate,
                new IInfoSource[] { new InboxInfoSource(inbox) }, settingsPath);

            // make it due again: the previous assertion marked today as done
            poller.MarkRan(DateTime.Today.AddDays(-1));

            var result = await poller.RunAsync(DateTime.Today);
            Console.WriteLine($"      confirm: {result.Summary}");
            if (result.ProposalIds.Count == 0) return false;

            var approval = rt.Gate.Approve(result.ProposalIds[0]);
            Console.WriteLine($"      approve: ok={approval.Ok} {approval.Content}");
            if (!approval.Ok) return false;

            var tasks = rt.Tasks.ListAsync().Result;
            var added = tasks.FirstOrDefault(t => t.Title.Contains("教务通知"));
            var blocks = rt.Schedule.ListRangeAsync("2027-04-06", "2027-04-06").Result;

            Console.WriteLine($"      plan: task={added?.Title} due={added?.Due} blocks={blocks.Count}");
            return added is not null
                   && added.Due == "2027-04-06"
                   && blocks.Count > 0;
        });

        await Check("fetch: the daily gate runs at most once per day", async () =>
        {
            var settingsPath = Path.Combine(root, "state", "settings.json");
            var poller = new InfoPoller(rt.Raw, rt.Gate,
                new IInfoSource[] { new InboxInfoSource(Path.Combine(root, "inbox")) }, settingsPath);

            poller.MarkRan(DateTime.Today.AddDays(-1));
            var dueAfterYesterday = poller.IsDue(DateTime.Today);

            await poller.RunAsync(DateTime.Today);
            var dueAgain = poller.IsDue(DateTime.Today);

            return dueAfterYesterday && !dueAgain;
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

/// <summary>A provider that always answers with one fixed string, for judge tests.</summary>
internal sealed class ScriptedProvider(string answer) : CogniCustos.Contracts.IModelProvider
{
    public string Id => "scripted";
    public CogniCustos.Contracts.ModelRole Role => CogniCustos.Contracts.ModelRole.Reasoning;
    public bool IsAvailable => true;

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        => Task.FromResult(Array.Empty<float>());

    public Task<string> CompleteAsync(string system, string user, CancellationToken ct = default)
        => Task.FromResult(answer);
}
