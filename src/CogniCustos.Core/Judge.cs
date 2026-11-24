using CogniCustos.Contracts;

namespace CogniCustos.Core;

/// <summary>One judge verdict: relevance and a category, never the material itself.</summary>
public sealed record JudgeVerdict(string ArtifactId, string Title, bool Relevant, string Category, double Score);

/// <summary>
/// The isolated judgement step (docs/PARTITION-MODEL.md §3.2): it may look at
/// unconfirmed material, but it only reports a verdict and a handle - never the body.
/// </summary>
public interface IJudge
{
    IReadOnlyList<JudgeVerdict> Find(string question, IReadOnlyList<RawArtifact> candidates, double threshold = 0.2);
}

/// <summary>
/// Deterministic v1: token overlap between the question and the artifact's title +
/// summary, with a few category buckets. No model, so it runs offline and its answers
/// are reproducible; swapping in a model means another <see cref="IJudge"/>.
/// </summary>
public sealed class KeywordJudge : IJudge
{
    private static readonly (string Category, string[] Words)[] Buckets =
    [
        ("日程", ["日程", "考试", "课表", "上课", "schedule", "exam", "class", "calendar"]),
        ("邮件", ["邮件", "通知", "email", "mail", "notice"]),
        ("课程", ["课程", "teaching", "syllabus", "akp", "course"]),
        ("任务", ["任务", "截止", "ddl", "due", "deadline", "task"]),
    ];

    public IReadOnlyList<JudgeVerdict> Find(string question, IReadOnlyList<RawArtifact> candidates,
        double threshold = 0.2)
    {
        var asked = Tokens(question);
        if (asked.Count == 0) return [];

        var verdicts = new List<JudgeVerdict>();
        foreach (var artifact in candidates)
        {
            // the judge reads the summary only: R1 is all it is allowed to see
            var material = Tokens(artifact.Title + " " + artifact.Summary);
            if (material.Count == 0) continue;

            var overlap = asked.Count(t => material.Contains(t));
            var score = (double)overlap / asked.Count;
            verdicts.Add(new JudgeVerdict(artifact.Id, artifact.Title, score >= threshold, Categorize(artifact), score));
        }

        return verdicts
            .OrderByDescending(v => v.Score)
            .ToList();
    }

    private static string Categorize(RawArtifact artifact)
    {
        var haystack = (artifact.Title + " " + artifact.Summary + " " + artifact.Source).ToLowerInvariant();
        foreach (var (category, words) in Buckets)
        {
            if (words.Any(haystack.Contains)) return category;
        }

        return "其他";
    }

    /// <summary>Latin words as words, CJK characters as single tokens (same as the KB).</summary>
    private static HashSet<string> Tokens(string text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return set;

        var buffer = new System.Text.StringBuilder();
        void Flush()
        {
            if (buffer.Length > 1) set.Add(buffer.ToString());
            buffer.Clear();
        }

        foreach (var ch in text)
        {
            if (ch >= 0x4E00 && ch <= 0x9FFF)
            {
                Flush();
                set.Add(ch.ToString());
            }
            else if (char.IsLetterOrDigit(ch))
            {
                buffer.Append(ch);
            }
            else
            {
                Flush();
            }
        }

        Flush();
        return set;
    }
}

/// <summary>
/// The model-backed judge. It keeps the same contract as the keyword one — a verdict and
/// a handle, never the material — and it still degrades to the deterministic judge when
/// no model answers, so a missing model never breaks the conversation.
///
/// The model only ever sees the title and the R1 summary (§3.2), capped to keep the prompt
/// small: the point of the judge is a cheap filter, not another assistant.
/// </summary>
public sealed class ModelJudge : IJudge
{
    private const int MaxCandidates = 8;
    private const int MaxSummaryChars = 80;

    private readonly Func<IModelProvider?> _provider;
    private readonly IJudge _fallback;

    public ModelJudge(Func<IModelProvider?> provider, IJudge? fallback = null)
    {
        _provider = provider;
        _fallback = fallback ?? new KeywordJudge();
    }

    public IReadOnlyList<JudgeVerdict> Find(string question, IReadOnlyList<RawArtifact> candidates,
        double threshold = 0.2)
    {
        // the deterministic pass always runs: it is free, and it is the answer if the
        // model is unavailable or answers something unparsable
        var deterministic = _fallback.Find(question, candidates, threshold);

        var provider = Provider();
        if (provider is null || candidates.Count == 0) return deterministic;

        var picked = Ask(provider, question, candidates.Take(MaxCandidates).ToList());
        if (picked is null) return deterministic;

        var merged = new List<JudgeVerdict>();
        foreach (var artifact in candidates)
        {
            if (picked.TryGetValue(artifact.Id, out var choice))
            {
                merged.Add(new JudgeVerdict(artifact.Id, artifact.Title, choice.Relevant,
                    string.IsNullOrWhiteSpace(choice.Category) ? DeterministicCategory(deterministic, artifact.Id) : choice.Category,
                    choice.Relevant ? 1.0 : 0.0));
                continue;
            }

            merged.Add(deterministic.FirstOrDefault(v => v.ArtifactId == artifact.Id)
                       ?? new JudgeVerdict(artifact.Id, artifact.Title, false, "其他", 0));
        }

        return merged;
    }

    private static string DeterministicCategory(IReadOnlyList<JudgeVerdict> deterministic, string id) =>
        deterministic.FirstOrDefault(v => v.ArtifactId == id)?.Category ?? "其他";

    private IModelProvider? Provider()
    {
        try
        {
            var provider = _provider();
            return provider is { IsAvailable: true } ? provider : null;
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, (bool Relevant, string Category)>? Ask(IModelProvider provider,
        string question, IReadOnlyList<RawArtifact> candidates)
    {
        var lines = new System.Text.StringBuilder();
        foreach (var artifact in candidates)
        {
            var summary = artifact.Summary.Length > MaxSummaryChars
                ? artifact.Summary[..MaxSummaryChars]
                : artifact.Summary;
            lines.Append($"#{artifact.Id} 标题：{artifact.Title} 摘要：{summary}\n");
        }

        var system = "你是判断器。判断哪些材料与问题相关。只输出 JSON 数组，不要写解释。";
        var format = "[{\"id\":\"材料编号\",\"relevant\":true,\"category\":\"日程|邮件|课程|任务|其他\"}]";
        var user = "问题：" + question + "\n\n材料：\n" + lines + "输出格式（只输出这个）：\n" + format;

        string answer;
        try
        {
            answer = provider.CompleteAsync(system, user).GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }

        return Parse(question, answer, candidates);
    }

    /// <summary>Tolerant parse: a small model will wrap JSON in prose or fences.</summary>
    private static Dictionary<string, (bool, string)>? Parse(string question, string answer,
        IReadOnlyList<RawArtifact> candidates)
    {
        if (string.IsNullOrWhiteSpace(answer)) return null;

        var start = answer.IndexOf('[');
        var end = answer.LastIndexOf(']');
        if (start < 0 || end <= start) return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(answer[start..(end + 1)]);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return null;

            var map = new Dictionary<string, (bool, string)>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != System.Text.Json.JsonValueKind.Object) continue;

                var id = item.TryGetProperty("id", out var idElement) ? idElement.ToString().TrimStart('#') : "";
                if (id.Length == 0) continue;

                var relevant = item.TryGetProperty("relevant", out var relevantElement)
                               && (relevantElement.ValueKind == System.Text.Json.JsonValueKind.True
                                   || (relevantElement.ValueKind == System.Text.Json.JsonValueKind.String
                                       && relevantElement.GetString() is "true" or "是" or "相关"));
                var category = item.TryGetProperty("category", out var categoryElement)
                    ? categoryElement.ToString()
                    : "";

                map[id] = (relevant, category);
            }

            return map.Count == 0 ? null : map;
        }
        catch
        {
            return null;
        }
    }
}
