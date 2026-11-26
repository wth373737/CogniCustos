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
                "教务通知\n关于 2027 年 4 月 6 日 至 4 月 10 日 考试安排的通知：考试周期间停课。" +
                DateTime.Now.Ticks);

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

            // make it due again and give it unseen content: the ledger skips what was read
            poller.MarkRan(DateTime.Today.AddDays(-1));
            File.WriteAllText(Path.Combine(inbox, "exam-2.txt"),
                "教务通知\n关于 2027 年 4 月 6 日 考试 的通知：请提前查阅考场。" + DateTime.Now.Ticks);

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

        // ---------- retrieval configuration: accounts, secrets, pages ----------

        await Check("email: an account is saved, its secret kept out of the account list", async () =>
        {
            var settingsPath = Path.Combine(root, "state", "settings.json");
            var service = new EmailAccountsService(settingsPath);

            var (ok, _) = service.Add("student@usm.my", "outlook.office365.com", 993, "app-password", 7, true);
            var accounts = service.Accounts;
            var secret = service.SecretFor("student@usm.my");

            return ok
                   && accounts.Any(a => a.Address == "student@usm.my" && a.ImapHost == "outlook.office365.com")
                   && secret == "app-password"
                   && File.Exists(service.SecretsPath);
        });

        await Check("email: a bad account is refused with a reason", async () =>
        {
            var service = new EmailAccountsService(Path.Combine(root, "state", "settings.json"));
            var noAddress = service.Add("", "imap.usm.my", 993, "x", 7, true);
            var noHost = service.Add("a@b.c", "", 993, "x", 7, true);
            var noSecret = service.Add("a@b.c", "imap.usm.my", 993, "", 7, true);

            return !noAddress.Ok && !noHost.Ok && !noSecret.Ok;
        });

        await Check("email: Thunderbird accounts are imported without their passwords", async () =>
        {
            var profiles = Path.Combine(root, "thunderbird", "Profiles", "abc.default");
            Directory.CreateDirectory(profiles);
            File.WriteAllText(Path.Combine(profiles, "prefs.js"),
                "user_pref(\"mail.server.server1.hostname\", \"imap.usm.my\");\n" +
                "user_pref(\"mail.identity.id1.useremail\", \"imported@usm.my\");\n");

            var service = new EmailAccountsService(Path.Combine(root, "state", "settings.json"));
            var (imported, message) = service.ImportFromThunderbird(Path.Combine(root, "thunderbird", "Profiles"));

            return imported == 1
                   && service.Accounts.Any(a => a.Address == "imported@usm.my" && a.ImapHost == "imap.usm.my")
                   && message.Contains("密码");
        });

        await Check("web: pages are validated and not duplicated", async () =>
        {
            var settingsPath = Path.Combine(root, "state", "web-settings.json");
            var service = new WebSourcesService(settingsPath);

            var bad = service.Add("not a url");
            var good = service.Add("https://elearning.usm.my/notices");
            var again = service.Add("https://elearning.usm.my/notices");

            return !bad.Ok && good.Ok && again.Ok && service.Urls.Count == 1;
        });

        // ---------- mail the Thunderbird way: read its local store ----------

        await Check("thunderbird: mbox messages are decoded (subject, qp body, nesting), newest first",
            async () =>
        {
            var mbox = Path.Combine(root, "thunderbird-test", "INBOX");
            Directory.CreateDirectory(Path.GetDirectoryName(mbox)!);

            var older =
                "From - Mon Jan 01 10:00:00 2026\r\n" +
                "Subject: =?utf-8?B?5rWL6K+V5Li76aKY?=\r\n" +
                "From: office@usm.my\r\n" +
                "Date: Mon, 01 Jan 2026 10:00:00 +0800\r\n" +
                "Content-Type: multipart/mixed; boundary=\"outer\"\r\n" +
                "\r\n" +
                "--outer\r\n" +
                "Content-Type: multipart/alternative; boundary=\"inner\"\r\n" +
                "\r\n" +
                "--inner\r\n" +
                "Content-Type: text/plain; charset=utf-8\r\n" +
                "Content-Transfer-Encoding: quoted-printable\r\n" +
                "\r\n" +
                "=E8=80=83=E8=AF=95=E5=91=A8=E5=9C=A84=E6=9C=886=E6=97=A5\r\n" +
                "--inner--\r\n" +
                "--outer--\r\n";

            var newer =
                "From - Tue Jan 02 10:00:00 2026\r\n" +
                "Subject: Plain subject\r\n" +
                "From: teacher@usm.my\r\n" +
                "Date: Tue, 02 Jan 2026 10:00:00 +0800\r\n" +
                "Content-Type: text/plain; charset=utf-8\r\n" +
                "\r\n" +
                "作业截止时间改到 1 月 9 日。\r\n";

            await File.WriteAllTextAsync(mbox, older + newer, System.Text.Encoding.UTF8);

            var messages = ThunderbirdMail.ReadRecent(mbox, 10);
            var newest = messages.FirstOrDefault();
            var oldest = messages.LastOrDefault();

            Console.WriteLine($"      tb: count={messages.Count} " +
                              string.Join(" | ", messages.Select(m => $"{m.Date:yyyy-MM-dd} '{m.Subject}' '{(m.Body.Length > 20 ? m.Body[..20] : m.Body)}'")));

            return messages.Count == 2
                   && newest.Subject == "Plain subject"
                   && newest.Body.Contains("作业截止")
                   && oldest.Subject == "测试主题"
                   && oldest.Body.Contains("考试周在4月6日");
        });

        await Check("thunderbird: folders are discovered from a profile layout", async () =>
        {
            var profile = Path.Combine(root, "thunderbird-test-profile");
            var inboxDir = Path.Combine(profile, "ImapMail", "imap.test.com");
            Directory.CreateDirectory(inboxDir);
            await File.WriteAllTextAsync(Path.Combine(inboxDir, "INBOX"),
                "From - Mon Jan 01 10:00:00 2026\r\nSubject: hello\r\n\r\nbody\r\n");
            await File.WriteAllTextAsync(Path.Combine(inboxDir, "INBOX.msf"), "index");
            await File.WriteAllTextAsync(Path.Combine(inboxDir, "msgFilterRules.dat"), "");

            var folders = ThunderbirdMail.DiscoverFolders([profile]);
            return folders.Count == 1
                   && folders[0].Account == "imap.test.com"
                   && folders[0].Folder == "INBOX";
        });

        await Check("thunderbird: muted mailbox folders are not read", async () =>
        {
            var profile = Path.Combine(root, "mute-profile");
            foreach (var account in new[] { "imap.keep.com", "imap.muted.com" })
            {
                var dir = Path.Combine(profile, "ImapMail", account);
                Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(Path.Combine(dir, "INBOX"),
                    "From - Mon Jan 01 10:00:00 2026\r\nSubject: " + account + "\r\nDate: 01 Jan 2026 10:00:00 +0800\r\n\r\nbody\r\n");
            }

            var settingsPath = Path.Combine(root, "state", "mute-settings.json");
            SettingsStore.Save(settingsPath, SettingsStore.Load(settingsPath) with
            {
                MutedMailFolders = ["imap.muted.com/INBOX"],
            });

            var source = new ThunderbirdMailSource();
            var folders = ThunderbirdMail.DiscoverFolders([profile]);
            source.UseSelection(() =>
            {
                var muted = SettingsStore.Load(settingsPath).MutedMailFolders ?? [];
                return ThunderbirdMail.DiscoverFolders([profile]).Where(f => !muted.Contains($"{f.Account}/{f.Folder}"));
            });

            var items = await source.FetchAsync(CancellationToken.None);
            return folders.Count == 2
                   && items.Count == 1
                   && items[0].Source.Contains("imap.keep.com");
        });

        await Check("thunderbird: folders are labelled with the mailbox, not the server", async () =>
        {
            var profile = Path.Combine(root, "label-profile");
            Directory.CreateDirectory(profile);

            // two accounts on the same host: Thunderbird names the directories host and host-1
            await File.WriteAllTextAsync(Path.Combine(profile, "prefs.js"),
                "user_pref(\"mail.account.account1.server\", \"server1\");\n" +
                "user_pref(\"mail.account.account1.identities\", \"id1\");\n" +
                "user_pref(\"mail.account.account2.server\", \"server2\");\n" +
                "user_pref(\"mail.account.account2.identities\", \"id2\");\n" +
                "user_pref(\"mail.identity.id1.useremail\", \"first@student.usm.my\");\n" +
                "user_pref(\"mail.identity.id2.useremail\", \"second@student.usm.my\");\n" +
                "user_pref(\"mail.server.server1.hostname\", \"outlook.office365.com\");\n" +
                "user_pref(\"mail.server.server2.hostname\", \"outlook.office365.com\");\n");

            var accounts = ThunderbirdMail.ParseAccounts(profile);
            var directories = accounts.Select(a => a.Directory).ToList();

            foreach (var directory in new[] { "outlook.office365.com", "outlook.office365-1.com" })
            {
                var dir = Path.Combine(profile, "ImapMail", directory);
                Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(Path.Combine(dir, "INBOX"),
                    "From - Mon Jan 01 10:00:00 2026\r\nSubject: x\r\nDate: 01 Jan 2026 10:00:00 +0800\r\n\r\nbody\r\n");
            }

            var folders = ThunderbirdMail.DiscoverFolders([profile]);
            var labels = folders.Select(f => f.Label).OrderBy(l => l).ToList();

            return accounts.Count == 2
                   && directories.Contains("outlook.office365.com")
                   && directories.Contains("outlook.office365-1.com")
                   && labels.SequenceEqual(new[] { "first@student.usm.my", "second@student.usm.my" });
        });

        await Check("fetch: important mail is labelled, promo mail is not offered", async () =>
        {
            var inbox = Path.Combine(root, "label-inbox");
            Directory.CreateDirectory(inbox);
            File.WriteAllText(Path.Combine(inbox, "exam.txt"),
                "考试通知\n数学分析期末考试在 2027 年 4 月 8 日 09:30 开始，请提前到场。" + DateTime.Now.Ticks);
            File.WriteAllText(Path.Combine(inbox, "promo.txt"),
                "限时优惠\n今天下单立减 50%，退订请点这里。" + DateTime.Now.Ticks);

            var settingsPath = Path.Combine(root, "state", "label-settings.json");
            var poller = new InfoPoller(rt.Raw, rt.Gate,
                new IInfoSource[] { new InboxInfoSource(inbox) }, settingsPath);

            var result = await poller.RunAsync(DateTime.Today);
            var important = rt.Raw.List(200).Where(a => a.Source == "label-inbox").ToList();
            var labelled = important.Select(a => (a.Title, Label: rt.Raw.GetLabel(a.Id))).ToList();

            Console.WriteLine("      labels: " + string.Join(" | ", labelled.Select(l => $"{l.Title}={l.Label}")));

            return result.Fetched == 2
                   && result.Important == 1
                   && labelled.Any(l => l.Title.StartsWith("考试通知") && l.Label == "important")
                   && labelled.Any(l => l.Title.StartsWith("限时优惠") && l.Label == "promo");
        });

        await Check("fetch: the pending title reads like a plan entry (date, time, category)", async () =>
        {
            var inbox = Path.Combine(root, "label-inbox");
            File.WriteAllText(Path.Combine(inbox, "exam-2.txt"),
                "考试通知\n数学分析期末考试在 2027 年 4 月 8 日 09:30 开始，请提前到场。" + DateTime.Now.Ticks);
            var settingsPath = Path.Combine(root, "state", "label-settings.json");
            var poller = new InfoPoller(rt.Raw, rt.Gate,
                new IInfoSource[] { new InboxInfoSource(inbox) }, settingsPath);

            var result = await poller.RunAsync(DateTime.Today);
            if (result.ProposalIds.Count == 0) return false;

            var proposal = rt.Gate.Pending().First(p => p.Id == result.ProposalIds[0]);
            Console.WriteLine($"      brief: {proposal.Title}");

            return proposal.Title.Contains("2027-04-08")
                   && proposal.Title.Contains("09:30")
                   && proposal.Title.Contains("考试");
        });

        // ---------- point read: the document pipeline ----------

        var docs = Path.Combine(root, "docs");
        Directory.CreateDirectory(docs);
        var samplePdf = Path.Combine(docs, "two-pages.pdf");
        BuildTwoPagePdf(samplePdf);

        await Check("docs: a PDF opens with its page count", () =>
        {
            using var book = CogniCustos.Documents.DocumentBook.OpenPdf(samplePdf);
            return Task.FromResult(book.PageCount == 2 && book.HasTextLayer);
        });

        await Check("docs: a page rasterises to BGRA pixels", () =>
        {
            using var book = CogniCustos.Documents.DocumentBook.OpenPdf(samplePdf);
            var page = book.Render(1, 800);

            if (page.Width < 100 || page.Height < 100) return Task.FromResult(false);
            if (page.Bgra.Length != page.Width * page.Height * 4) return Task.FromResult(false);

            // a white page with black text: both must appear
            var dark = 0;
            var light = 0;
            for (var i = 0; i < page.Bgra.Length; i += 4)
            {
                if (page.Bgra[i] < 80) dark++;
                else if (page.Bgra[i] > 200) light++;
            }

            Console.WriteLine($"      {page.Width}x{page.Height} dark={dark} light={light}");
            return Task.FromResult(dark > 50 && light > page.Width * page.Height / 2);
        });

        await Check("docs: the text layer is searchable", () =>
        {
            using var book = CogniCustos.Documents.DocumentBook.OpenPdf(samplePdf);
            var text = book.Text(1);
            Console.WriteLine($"      text: '{text.Replace("\n", " ")}'");
            return Task.FromResult(text.Contains("HELLO") && text.Contains("WORLD"));
        });

        await Check("docs: a selection returns the words under the box", () =>
        {
            using var book = CogniCustos.Documents.DocumentBook.OpenPdf(samplePdf);
            var words = book.Words(1);
            if (words.Count == 0) return Task.FromResult(false);

            // find the box around the first word, then ask the reader for it
            var w = words[0];
            var inside = book.TextIn(1, w.X0 - 0.01, w.Y0 - 0.01, w.X1 + 0.01, w.Y1 + 0.01);
            // and a box at the very bottom of the page must not return the top line
            var below = book.TextIn(1, 0, 0.90, 1, 0.99);

            Console.WriteLine($"      first word '{w.Text}' inside '{inside}' below '{below}'");
            return Task.FromResult(inside.Contains(w.Text) && below.Length == 0);
        });

        await Check("docs: a cropped region keeps its pixels", () =>
        {
            using var book = CogniCustos.Documents.DocumentBook.OpenPdf(samplePdf);
            var page = book.Render(1, 800);
            var crop = page.Crop(0.1, 0.1, 0.5, 0.4);

            return Task.FromResult(crop.Width > 10 && crop.Height > 10
                                   && crop.Width <= page.Width / 2 + 2
                                   && crop.Bgra.Length == crop.Width * crop.Height * 4);
        });

        await Check("docs: a page encodes to a real PNG", () =>
        {
            using var book = CogniCustos.Documents.DocumentBook.OpenPdf(samplePdf);
            var png = book.Render(1, 300).ToPng();

            var signature = png.Length > 8 && png[0] == 0x89 && png[1] == 'P' && png[2] == 'N' && png[3] == 'G';
            var width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
            Console.WriteLine($"      png {png.Length} bytes, header width {width}");
            return Task.FromResult(signature && width > 10);
        });

        await Check("docs: unsupported files are refused with a reason", () =>
        {
            var unsupported = Path.Combine(docs, "note.txt");
            File.WriteAllText(unsupported, "plain text");

            var opened = CogniCustos.Documents.DocumentOpener.TryOpen(unsupported, root, out var message);
            return Task.FromResult(opened is null && message.Contains("暂不支持"));
        });

        await Check("docs: the office converter claims only office files", () =>
            Task.FromResult(CogniCustos.Documents.OfficePdfConverter.IsOfficeDocument("a.docx")
                            && CogniCustos.Documents.OfficePdfConverter.IsOfficeDocument("b.pptx")
                            && !CogniCustos.Documents.OfficePdfConverter.IsOfficeDocument("c.pdf")));

        await Check("lecture: the outline parses strict JSON from a chatty model", () =>
        {
            const string raw = """
                Sure! Here is the outline:
                {"summary":"这份材料讲研究设计。","sections":[{"page":2,"title":"研究问题"},{"page":1,"title":"导论"},{"page":99,"title":"越界"}]}
                Hope it helps.
                """;
            var outline = DocLecture.ParseOutline(raw, 3);

            return Task.FromResult(outline is not null
                                   && outline.Summary.Contains("研究设计")
                                   && outline.Sections.Count == 2
                                   && outline.Sections[0].Page == 1
                                   && outline.Source == "model");
        });

        await Check("lecture: a model that answers prose still yields an outline", () =>
        {
            var fallback = DocLecture.FallbackOutline(["第一页标题\n正文", "第二页\n更多"]);
            return Task.FromResult(fallback.Sections.Count == 2
                                   && fallback.Sections[0].Title.Contains("第一页")
                                   && fallback.Source == "fallback");
        });

        await Check("lecture: without a model the question still answers honestly", async () =>
        {
            var lecture = new DocLecture(new ModelRouter());
            var answer = await lecture.AskAsync(
                new DocQuestion("f.pdf", 1, "page text", "选中的这一句", "这是什么？"), null);
            return answer.Contains("选中的这一句") || answer.Contains("没有可用");
        });

        // ---------- prompts: the catalogue is the single source ----------

        await Check("prompts: every catalogue entry renders with its declared variables", () =>
        {
            var variables = new Dictionary<string, string> { ["tools"] = "TOOL-LIST", ["system"] = "S", ["user"] = "U" };
            foreach (var spec in PromptCatalog.All)
            {
                foreach (var segment in spec.Segments)
                {
                    var text = CogniCustos.Contracts.Template.Fill(segment.Text, variables);
                    if (string.IsNullOrWhiteSpace(text)) return Task.FromResult(false);
                }

                var whole = PromptCatalog.Render(spec.Key, variables);
                if (whole.Length == 0) return Task.FromResult(false);
            }

            return Task.FromResult(true);
        });

        await Check("prompts: a missing variable fails loudly instead of shipping {{name}}", () =>
        {
            try
            {
                _ = PromptCatalog.Render(PromptKeys.ChatSystem);   // needs {{tools}}
                return Task.FromResult(false);
            }
            catch (InvalidOperationException ex)
            {
                return Task.FromResult(ex.Message.Contains("tools"));
            }
        });

        await Check("prompts: the chat protocol still carries the parser's action contract", () =>
        {
            var text = PromptCatalog.Render(PromptKeys.ChatSystem,
                new Dictionary<string, string> { ["tools"] = "(tools)" });

            // AgentProtocol parses exactly these two shapes.
            return Task.FromResult(text.Contains("\"action\":\"tool\"")
                                   && text.Contains("\"action\":\"answer\"")
                                   && text.Contains("(tools)")
                                   && text.Contains("task.add"));
        });

        await Check("prompts: the judge and outline formats keep their JSON shape", () =>
        {
            var judge = PromptCatalog.Render(PromptKeys.JudgeFormat);
            var outline = PromptCatalog.Render(PromptKeys.LectureOutline);

            return Task.FromResult(judge.Contains("{\"id\"")
                                   && judge.Contains("relevant")
                                   && judge.Contains("category")
                                   && outline.Contains("\"summary\"")
                                   && outline.Contains("\"sections\"")
                                   && outline.Contains("\"page\""));
        });

        await Check("prompts: parse contracts are marked locked, voice is not", () =>
        {
            static bool AllLocked(string key) =>
                PromptCatalog.Get(key).Segments.All(s => s.Kind == PromptSegmentKind.Locked);

            var privacy = AllLocked(PromptKeys.GuardPrivacy);          // the outbound gate stays put
            var chatProtocol = PromptCatalog.Get(PromptKeys.ChatSystem)
                .Segments.First(s => s.Id == "protocol").Kind == PromptSegmentKind.Locked;
            var chatPersona = PromptCatalog.Get(PromptKeys.ChatSystem)
                .Segments.First(s => s.Id == "persona").Kind == PromptSegmentKind.Editable;
            var persona = AllLocked(PromptKeys.LecturePersona);

            return Task.FromResult(privacy && chatProtocol && chatPersona && !persona);
        });

        await Check("prompts: the local chat template and the adapter agree", () =>
        {
            var rendered = PromptCatalog.Render(PromptKeys.ProviderChatTemplate,
                new Dictionary<string, string> { ["system"] = "SYS", ["user"] = "USR" });

            var byAdapter = CogniCustos.Contracts.Template.Fill(
                CogniCustos.Adapters.LlamaModelProvider.ChatTemplate,
                new Dictionary<string, string> { ["system"] = "SYS", ["user"] = "USR" });

            return Task.FromResult(rendered == byAdapter
                                   && rendered.Contains("<|im_start|>system")
                                   && rendered.Contains("USR"));
        });

        await Check("prompts: the reader persona comes from the catalogue", () =>
            Task.FromResult(DocLecture.Persona.Length > 20
                            && DocLecture.Persona == PromptCatalog.Render(PromptKeys.LecturePersona)
                            && DocLecture.Persona.Contains("学长")));

        // ---------- prompts: editing (P1) ----------

        await Check("prompts: an edit changes what the call site sends, and reset brings the default back", () =>
        {
            var original = PromptCatalog.SegmentText(PromptKeys.SummaryOneLine, "instruction");
            var failure = PromptCatalog.SaveOverride(PromptKeys.SummaryOneLine, "instruction",
                "你是摘要器。用不超过 40 字概括下面这段材料。");

            var afterEdit = PromptCatalog.SegmentText(PromptKeys.SummaryOneLine, "instruction");
            var overridden = PromptCatalog.IsOverridden(PromptKeys.SummaryOneLine, "instruction");

            var resetFailure = PromptCatalog.ResetOverride(PromptKeys.SummaryOneLine, "instruction");
            var afterReset = PromptCatalog.SegmentText(PromptKeys.SummaryOneLine, "instruction");

            return Task.FromResult(failure is null && resetFailure is null
                                   && afterEdit.StartsWith("你是摘要器。用不超过 40 字")
                                   && overridden
                                   && !PromptCatalog.IsOverridden(PromptKeys.SummaryOneLine, "instruction")
                                   && afterReset == original);
        });

        await Check("prompts: a locked segment cannot be edited", () =>
        {
            var failure = PromptCatalog.SaveOverride(PromptKeys.GuardPrivacy, "system", "随便你怎么判断。");
            return Task.FromResult(failure is not null
                                   && PromptCatalog.SegmentText(PromptKeys.GuardPrivacy, "system")
                                       .StartsWith("你是本地隐私检查器"));
        });

        await Check("prompts: an edit that drops or invents a placeholder is refused", () =>
        {
            var withTools = "可用工具：\n{{tools}}";

            var dropped = PromptCatalog.CheckPlaceholders(withTools, "可用工具：（略）");
            var invented = PromptCatalog.CheckPlaceholders(withTools, "可用工具：{{tools}}\n{{extra}}");
            var fine = PromptCatalog.CheckPlaceholders(withTools, "工具清单如下：\n{{tools}}");

            // and the same rule is applied by the store: the live segment still carries {{tools}}
            var stored = PromptCatalog.SaveOverride(PromptKeys.ChatSystem, "tools", "可用工具：（略）");
            var stillHasPlaceholder = PromptCatalog.SegmentText(PromptKeys.ChatSystem, "tools").Contains("{{tools}}");

            return Task.FromResult(dropped is not null && dropped!.Contains("tools")
                                   && invented is not null && invented!.Contains("extra")
                                   && fine is null
                                   && stored is not null && stillHasPlaceholder);
        });

        await Check("prompts: blank text is refused with a reason", () =>
        {
            var failure = PromptCatalog.SaveOverride(PromptKeys.SummaryOneLine, "instruction", "   ");
            return Task.FromResult(failure is not null
                                   && PromptCatalog.SegmentText(PromptKeys.SummaryOneLine, "instruction").Length > 0);
        });

        await Check("prompts: overrides round-trip through the settings file", () =>
        {
            var path = Path.Combine(root, "state", "prompts-roundtrip.json");
            var store = PromptOverrides.Load(path);
            store.Set(PromptKeys.LecturePersona, "persona", "你是助教，说话简短。");
            var saved = store.Save(out var error);

            var reloaded = PromptOverrides.Load(path);
            var found = reloaded.TryGet(PromptKeys.LecturePersona, "persona", out var text);

            try { File.Delete(path); } catch { }
            return Task.FromResult(saved && error is null
                                   && found && text == "你是助教，说话简短。");
        });

        await Check("cloud: a configured endpoint is usable even when /models is missing", async () =>
        {
            // A stub that behaves like a real OpenAI-compatible endpoint without /models: the
            // old probe asked for it, got 404 and declared the (perfectly good) model unusable.
            using var stub = new StubEndpoint(respondToModels: false);
            var provider = new OpenAiCompatibleProvider(stub.BaseUrl, "stub-model", "test-key");

            var usable = provider.IsAvailable;
            var result = await provider.TestAsync();

            Console.WriteLine($"      test: ok={result.Ok} {result.Message}");
            return usable && result.Ok;
        });

        await Check("cloud: a base URL without /v1 falls back once on 404", async () =>
        {
            // endpoint only serves /v1/chat/completions; the first plain attempt 404s
            using var stub = new StubEndpoint(respondToModels: false, onlyUnderV1: true);
            var provider = new OpenAiCompatibleProvider(stub.BaseUrl, "stub-model", "test-key");

            var reply = await provider.CompleteAsync("sys", "hello");

            return stub.Paths.Count == 2
                   && stub.Paths[0] == "/chat/completions"
                   && stub.Paths[1] == "/v1/chat/completions"
                   && reply.Contains("stub answer");
        });

        await Check("cloud: a wrong key surfaces as the endpoint's own error", async () =>
        {
            using var stub = new StubEndpoint(respondToModels: false, status: 401, body: "{\"error\":\"invalid api key\"}");
            var provider = new OpenAiCompatibleProvider(stub.BaseUrl, "stub-model", "bad-key");

            var result = await provider.TestAsync();
            return !result.Ok && result.Message.Contains("401");
        });

        await Check("cloud: saving the configuration makes the router and the reader use it", async () =>
        {
            // Exactly the application's sequence: create the runtime, inject the HTTP factory,
            // save the form, then ask the reader a question. The point is that the *reader* ends
            // up on the cloud provider instead of the standby one.
            using var stub = new StubEndpoint(respondToModels: false);
            var cloudRoot = Path.Combine(AppContext.BaseDirectory, "cloud-" + Guid.NewGuid().ToString("N")[..6]);
            var cloudRuntime = CogniCustosRuntime.Create(cloudRoot);
            cloudRuntime.CloudModels.SetFactory((url, model, key) => new OpenAiCompatibleProvider(url, model, key));

            // something else already takes the reasoning role, like the borrowed Ollama model
            cloudRuntime.AddModel(new StubReasoningProvider());

            var (saved, message) = cloudRuntime.CloudModels.UseCloud("stub-model", stub.BaseUrl, "test-key");
            var resolved = cloudRuntime.Models.Resolve(ModelRole.Reasoning);

            var lecture = new DocLecture(cloudRuntime.Models);
            var answer = await lecture.AskAsync(
                new DocQuestion("f.pdf", 1, "page text", "选中的这一句", "这是什么？"), null);

            try { Directory.Delete(cloudRoot, true); } catch { }

            Console.WriteLine($"      saved={saved} resolved={resolved.Id} answer='{Trim(answer)}'");
            return saved && resolved is not StandbyModelProvider
                         && resolved.Id == "cloud:stub-model"
                         && stub.Paths.Count >= 1
                         && answer.Contains("stub answer");
        });

        await Check("cloud: after a restart the saved configuration is registered again", async () =>
        {
            // The app's second launch: nothing but settings.json on disk. This is the path a user
            // hits after configuring the model and closing the window.
            using var stub = new StubEndpoint(respondToModels: false);
            var root = Path.Combine(AppContext.BaseDirectory, "restart-" + Guid.NewGuid().ToString("N")[..6]);

            var first = CogniCustosRuntime.Create(root);
            first.CloudModels.SetFactory((url, model, key) => new OpenAiCompatibleProvider(url, model, key));
            var (savedSomething, _) = first.CloudModels.UseCloud("stub-model", stub.BaseUrl, "k");

            var second = CogniCustosRuntime.Create(root);
            second.CloudModels.SetFactory((url, model, key) => new OpenAiCompatibleProvider(url, model, key));
            var resolved = second.Models.Resolve(ModelRole.Reasoning);

            var lecture = new DocLecture(second.Models);
            var answer = await lecture.AskAsync(new DocQuestion("f.pdf", 2, "text", "选中的句子", "讲讲"), null);

            try { Directory.Delete(root, true); } catch { }

            Console.WriteLine($"      resolved={resolved.Id} answer='{Trim(answer)}'");
            return savedSomething && resolved.Id == "cloud:stub-model" && answer.Contains("stub answer");
        });

        await Check("prompts: prose prompts carry the answer-language rule, JSON ones do not", () =>
        {
            PromptCatalog.ConfigureLanguage("zh-CN");
            var personaZh = PromptCatalog.Render(PromptKeys.LecturePersona);
            var summaryZh = PromptCatalog.Render(PromptKeys.SummaryOneLine);
            var guardZh = PromptCatalog.Render(PromptKeys.GuardPrivacy,
                new Dictionary<string, string> { ["system"] = "sys", ["question"] = "q" });

            PromptCatalog.ConfigureLanguage("en-US");
            var personaEn = PromptCatalog.Render(PromptKeys.LecturePersona);
            PromptCatalog.ConfigureLanguage("zh-CN");

            var ok = personaZh.Contains("回答语言：简体中文")
                     && summaryZh.Contains("回答语言：简体中文")
                     && !guardZh.Contains("回答语言")
                     && personaEn.Contains("Answer language: English")
                     && personaEn.Contains("beats the interface language")
                     && !personaEn.Contains("回答语言")
                     && DocLecture.Persona == personaZh;

            Console.WriteLine($"      zh tail='{Tail(personaZh)}'");
            return Task.FromResult(ok);
        });

        await Check("reader: the question's own language beats the interface language", async () =>
        {
            // What the model receives is what decides this, so the assertion is on the prompt.
            var systemPrompts = new List<string>();
            var userPrompts = new List<string>();
            var router = new ModelRouter();
            router.AddFirst(new RecordingProvider(systemPrompts, userPrompts));
            var lecture = new DocLecture(router);

            async Task Ask(string question) => await lecture.AskAsync(
                new DocQuestion("f.pdf", 1, "page text", "boxed text", question), null);

            PromptCatalog.ConfigureLanguage("en-US");
            await Ask("这一页要求我们做什么？");
            var chineseCase = userPrompts[^1];

            await Ask("What does this page ask for?");
            var englishCase = userPrompts[^1];

            PromptCatalog.ConfigureLanguage("zh-CN");
            await Ask("What does this page ask for?");
            var chineseUiEnglishQuestion = userPrompts[^1];

            PromptCatalog.ConfigureLanguage("zh-CN");

            var ok = chineseCase.Contains("问题：")
                     && chineseCase.Contains("（请用简体中文回答。）")
                     && !chineseCase.Contains("Question:")
                     && englishCase.Contains("Question:")
                     && englishCase.Contains("(Answer in English.)")
                     && chineseUiEnglishQuestion.Contains("Question:")
                     && chineseUiEnglishQuestion.Contains("(Answer in English.)");

            Console.WriteLine($"      enUi+zhQ len={chineseCase.Length} enUi+enQ len={englishCase.Length}");
            return ok;
        });

        await Check("voice: an installed model is only used when it is asked for", async () =>
        {
            var system = new FakeSpeech("system", true, "system ok", "from system");
            var model = new FakeSpeech("model", true, "model ok", "from model");
            var voice = new VoiceInput(system, model);

            var before = voice.Status().Engine;
            var refused = voice.UseModel(false);
            var switched = voice.UseModel(true);
            var heard = await voice.ListenAsync("zh", TimeSpan.FromSeconds(1));

            return before == "system" && refused is null && switched is null
                   && voice.Status().Engine == "model"
                   && heard.Text == "from model";
        });

        await Check("voice: a missing model is refused with its own reason", async () =>
        {
            var model = new FakeSpeech("model", false, "没有安装模型", "never");
            var voice = new VoiceInput(new FakeSpeech("system", true, "system ok", "from system"), model);

            var reason = voice.UseModel(true);
            var heard = await voice.ListenAsync("zh", TimeSpan.FromSeconds(1));

            return reason == "没有安装模型"
                   && !voice.PreferModel
                   && voice.Status().Engine == "system"
                   && heard.Text == "from system";
        });
        await Check("web: a domain that is not allowed is never fetched", async () =>
        {
            var handler = new StubWebHandler("<html><title>t</title><body>hello world content</body></html>");
            var reader = new WebReader(new HttpWebFetcher(handler), () => ["allowed.example"]);

            var page = await reader.ReadAsync("https://blocked.example/page");

            return page.Verdict == WebVerdict.NotAllowed
                   && handler.Requests.Count == 0      // the gate is before the network, not after
                   && page.Detail.Contains("blocked.example", StringComparison.Ordinal);
        });

        await Check("web: an approved page yields its title and readable text", async () =>
        {
            var html = "<html><head><title>Notices &amp; dates</title><style>.x{}</style></head>"
                       + "<body><script>var a=1;</script><h1>Week 12</h1><p>The final test is on 27 Nov.</p></body></html>";
            var handler = new StubWebHandler(html);
            var reader = new WebReader(new HttpWebFetcher(handler), () => ["usm.my"]);

            var page = await reader.ReadAsync("https://elearning.usm.my/notices");

            Console.WriteLine($"      verdict={page.Verdict} status={page.Status} type='{page.ContentType}' title='{page.Title}' len={page.Text.Length} reqs={handler.Requests.Count}");
            return page.Verdict == WebVerdict.Read
                   && handler.Requests.Count == 1
                   && handler.Requests[0].Method == HttpMethod.Get
                   && page.Title == "Notices & dates"
                   && page.Text.Contains("The final test is on 27 Nov.")
                   && !page.Text.Contains("var a=1")
                   && !page.Text.Contains("<h1>");
        });

        await Check("web: a sign-in page is reported as needing a login, not stored as content", async () =>
        {
            var html = "<html><title>Sign in</title><body>Please sign in with your account and password</body></html>";
            var reader = new WebReader(new HttpWebFetcher(new StubWebHandler(html)), () => ["elearning.usm.my"]);

            var page = await reader.ReadAsync("https://elearning.usm.my/login/index.php");

            Console.WriteLine($"      verdict={page.Verdict} title='{page.Title}' len={page.Text.Length} detail='{page.Detail}'");
            return page.Verdict == WebVerdict.NeedsLogin && page.Text.Length == 0;
        });

        await Check("web: a PDF is reported as not HTML instead of being read as text", async () =>
        {
            var handler = new StubWebHandler("%PDF-1.4 binary", "application/pdf");
            var reader = new WebReader(new HttpWebFetcher(handler), () => ["example.com"]);

            var page = await reader.ReadAsync("https://example.com/file.pdf");

            return page.Verdict == WebVerdict.NotHtml && page.ContentType.Contains("pdf");
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

    /// <summary>
    /// A two-page PDF built in memory, so the document pipeline can be tested without
    /// shipping a fixture file (PdfPig writes the same format it reads).
    /// </summary>
    private static void BuildTwoPagePdf(string path)
    {
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);

        var first = builder.AddPage(595, 842);
        first.AddText("HELLO WORLD", 24, new UglyToad.PdfPig.Core.PdfPoint(60, 760), font);
        first.AddText("second line of the text layer", 12, new UglyToad.PdfPig.Core.PdfPoint(60, 700), font);

        var second = builder.AddPage(595, 842);
        second.AddText("PAGE TWO", 24, new UglyToad.PdfPig.Core.PdfPoint(60, 760), font);

        File.WriteAllBytes(path, builder.Build());
    }

    /// <summary>
    /// A minimal HTTP endpoint over a raw socket, so the cloud adapter can be exercised without
    /// a network, a certificate or a real provider. It records every path it was asked for, which
    /// is how the /v1 fallback is proven.
    /// </summary>
    private sealed class StubEndpoint : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly bool _respondToModels;
        private readonly bool _onlyUnderV1;
        private readonly int _status;
        private readonly string _body;

        public StubEndpoint(bool respondToModels, bool onlyUnderV1 = false, int status = 200,
            string body = "{\"choices\":[{\"message\":{\"content\":\"stub answer\"}}]}")
        {
            _respondToModels = respondToModels;
            _onlyUnderV1 = onlyUnderV1;
            _status = status;
            _body = body;

            _listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}";
            _ = Task.Run(ServeAsync);
        }

        public string BaseUrl { get; }

        public List<string> Paths { get; } = [];

        private async Task ServeAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                System.Net.Sockets.TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch { return; }

                using (client)
                using (var stream = client.GetStream())
                {
                    var buffer = new byte[8192];
                    var read = await stream.ReadAsync(buffer, _cts.Token);
                    var text = System.Text.Encoding.ASCII.GetString(buffer, 0, Math.Max(0, read));
                    var path = text.Split(' ').ElementAtOrDefault(1) ?? "/";
                    Paths.Add(path);

                    var ok = path != "/models" || _respondToModels;
                    if (_onlyUnderV1 && !path.StartsWith("/v1/", StringComparison.Ordinal)) ok = false;

                    var status = ok ? _status : 404;
                    var body = ok ? _body : "{\"error\":\"not found\"}";
                    var payload = System.Text.Encoding.UTF8.GetBytes(body);
                    var head = System.Text.Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\n" +
                        "Content-Type: application/json\r\n" +
                        $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");

                    await stream.WriteAsync(head, _cts.Token);
                    await stream.WriteAsync(payload, _cts.Token);
                    await stream.FlushAsync(_cts.Token);
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            _cts.Dispose();
        }
    }

    /// <summary>A stand-in for "some other model already answers": the save must still win.</summary>
    private sealed class StubReasoningProvider : IModelProvider
    {
        public string Id => "stub:local";
        public ModelRole Role => ModelRole.Reasoning;
        public bool IsAvailable => true;

        public Task<string> CompleteAsync(string system, string user, CancellationToken ct = default)
            => Task.FromResult("local stand-in");

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult(Array.Empty<float>());
    }

    /// <summary>Records what was sent, so a prompt can be asserted on without a model.</summary>
    private sealed class RecordingProvider : IModelProvider
    {
        private readonly List<string> _systems;
        private readonly List<string> _users;

        public RecordingProvider(List<string> systems, List<string> users)
        {
            _systems = systems;
            _users = users;
        }

        public string Id => "test:recording";
        public ModelRole Role => ModelRole.Reasoning;
        public bool IsAvailable => true;

        public Task<string> CompleteAsync(string system, string user, CancellationToken ct = default)
        {
            _systems.Add(system);
            _users.Add(user);
            return Task.FromResult("recorded");
        }

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult(Array.Empty<float>());
    }

    /// <summary>A recogniser that answers from a string, so the choice logic can be tested.</summary>
    private sealed class FakeSpeech : ISpeechRecognizer
    {
        private readonly string _text;

        public FakeSpeech(string id, bool available, string detail, string text)
        {
            Id = id;
            IsAvailable = available;
            Detail = detail;
            _text = text;
        }

        public string Id { get; }
        public string Engine => Id;
        public bool IsAvailable { get; }
        public string Detail { get; }

        public Task<SpeechResult> ListenAsync(string culture, TimeSpan timeout, CancellationToken ct = default) =>
            Task.FromResult(SpeechResult.Said(_text, 1, Id));
    }
    private static string Tail(string text) =>
        text.Length > 60 ? "…" + text[^60..].Replace("\n", " ") : text.Replace("\n", " ");

    /// <summary>A handler that answers with one canned body, and records what it was asked for.</summary>
    private sealed class StubWebHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly string _contentType;

        public StubWebHandler(string body, string contentType = "text/html")
        {
            _body = body;
            _contentType = contentType;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, _contentType),
                RequestMessage = request
            };
            return Task.FromResult(response);
        }
    }
    private static string Trim(string text) =>
        text.Length > 40 ? text[..40].Replace("\n", " ") + "…" : text.Replace("\n", " ");

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
