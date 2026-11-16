using System.Text;
using CogniCustos.Contracts;

namespace CogniCustos.Core;

/// <summary>
/// Tools that complete proposal objectives 2, 3 and 7:
///   fs.read          read-only file access (the AI may read, never write)
///   doc.search       answers from the application's own documentation and source
///   sensitive.check  what the local gatekeeper thinks about a piece of text
///   cloud.ask        the only path to a cloud model, always gated
///   audit.recent     what has been attempted off-device, including refusals
/// </summary>
public static class ObjectiveTools
{
    public static IEnumerable<ITool> Create(
        ISelfDocs selfDocs,
        ISensitiveContentChecker checker,
        Func<CloudGateway?> cloudAccessor,
        IOutboundAudit audit,
        string readRoot)
    {
        yield return new DelegateTool(
            new ToolDescriptor("fs.read", "Read a text file. Read-only and confined to the application root; the AI can read code and documents but cannot write anything.",
                new[] { new ToolParameter("path", "string", "Absolute path, or a path relative to the application root", true) },
                ToolKind.Read),
            (args, ct) =>
            {
                args.TryGetValue("path", out var raw);
                if (string.IsNullOrWhiteSpace(raw)) return Task.FromResult(ToolResult.Fail("path is required"));

                var full = Path.IsPathRooted(raw) ? Path.GetFullPath(raw) : Path.GetFullPath(Path.Combine(readRoot, raw));
                if (!full.StartsWith(Path.GetFullPath(readRoot), StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(ToolResult.Fail($"拒绝：路径在应用目录之外（{readRoot}）"));
                if (!File.Exists(full)) return Task.FromResult(ToolResult.Fail($"no such file: {full}"));

                var info = new FileInfo(full);
                if (info.Length > 512 * 1024) return Task.FromResult(ToolResult.Fail($"文件过大（{info.Length} 字节），上限 512 KB"));

                try
                {
                    var text = File.ReadAllText(full, Encoding.UTF8);
                    return Task.FromResult(ToolResult.Ok0(text.Length > 20000 ? text[..20000] + "\n…（已截断）" : text, full));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(ToolResult.Fail("读取失败：" + ex.Message));
                }
            });

        yield return new DelegateTool(
            new ToolDescriptor("doc.search", "Search this application's own README and source code to explain how the system works.",
                new[] { new ToolParameter("query", "string", "What to look up", true) },
                ToolKind.Read),
            (args, _) =>
            {
                args.TryGetValue("query", out var query);
                if (string.IsNullOrWhiteSpace(query)) return Task.FromResult(ToolResult.Fail("query is required"));
                var hits = selfDocs.Search(query!, 5);
                if (hits.Count == 0) return Task.FromResult(ToolResult.Ok0("(no match in the application's own docs)"));
                var text = string.Join("\n\n", hits.Select(h => $"[{h.Source}] score={h.Score:0.00}\n{h.Snippet}"));
                return Task.FromResult(ToolResult.Ok0(text, hits));
            });

        yield return new DelegateTool(
            new ToolDescriptor("sensitive.check", "Run the local sensitive-content gatekeeper over a piece of text, without sending it anywhere.",
                new[] { new ToolParameter("text", "string", "Text to check", true) },
                ToolKind.Read),
            async (args, ct) =>
            {
                args.TryGetValue("text", out var text);
                if (string.IsNullOrWhiteSpace(text)) return ToolResult.Fail("text is required");
                var r = await checker.CheckAsync(text!, ct).ConfigureAwait(false);
                if (!r.Flagged) return ToolResult.Ok0("未发现敏感内容（规则层；未启用本地模型复核）");
                var sb = new StringBuilder("发现可能敏感的内容：\n");
                foreach (var reason in r.Reasons) sb.AppendLine("  - " + reason);
                sb.AppendLine("\n脱敏预览：\n" + r.RedactedPreview);
                return ToolResult.Ok0(sb.ToString(), r);
            });

        yield return new DelegateTool(
            new ToolDescriptor("cloud.ask", "Send a question to the configured cloud model. The local gatekeeper runs first; flagged text is refused unless allowFlagged is true.",
                new[]
                {
                    new ToolParameter("text", "string", "What to ask", true),
                    new ToolParameter("allowFlagged", "bool", "true only after the user explicitly agreed to send flagged content"),
                },
                ToolKind.Write),
            async (args, ct) =>
            {
                var cloud = cloudAccessor();
                if (cloud is null) return ToolResult.Fail("未配置云端模型（设置里填 OpenAI 兼容端点）。这条链路已按接口预留。");
                args.TryGetValue("text", out var text);
                if (string.IsNullOrWhiteSpace(text)) return ToolResult.Fail("text is required");
                var allow = args.TryGetValue("allowFlagged", out var a) && bool.TryParse(a, out var b) && b;

                var result = await cloud.SendAsync("你是家用学习助手，用中文简洁回答。", text!, allow, ct).ConfigureAwait(false);
                if (!result.Sent) return ToolResult.Fail(result.Refusal ?? "未发送");
                return ToolResult.Ok0(result.Reply, result);
            });

        yield return new DelegateTool(
            new ToolDescriptor("audit.recent", "Show recent outbound attempts: what was sent, what was refused, and why.",
                new[] { new ToolParameter("count", "int", "How many records (default 10)") },
                ToolKind.Read),
            (args, _) =>
            {
                var n = args.TryGetValue("count", out var c) && int.TryParse(c, out var k) ? k : 10;
                var rows = audit.Recent(n);
                if (rows.Count == 0) return Task.FromResult(ToolResult.Ok0("(no outbound attempts recorded)"));
                var text = string.Join("\n", rows.Select(r =>
                    $"{r.At:MM-dd HH:mm:ss}  {r.Decision,-14} {r.Length,6} chars  {r.Provider}" +
                    (r.Reasons.Count > 0 ? "  [" + string.Join("; ", r.Reasons.Take(2)) + "]" : "")));
                return Task.FromResult(ToolResult.Ok0(text, rows));
            });
    }
}
