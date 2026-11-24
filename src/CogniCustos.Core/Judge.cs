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
