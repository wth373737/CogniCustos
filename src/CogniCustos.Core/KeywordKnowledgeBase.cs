using System.Text.Json;
using CogniCustos.Contracts;

namespace CogniCustos.Core;

/// <summary>
/// Placeholder knowledge base. LanceDB is the plan's target; this keeps the same
/// interface with a deterministic keyword scorer so retrieval can be wired and
/// demonstrated today, and swapped for a vector store without touching callers.
/// </summary>
public sealed class KeywordKnowledgeBase : IKnowledgeBase
{
    private sealed record Doc(string Source, string Text, string[] Tokens);

    private readonly List<Doc> _docs = new();
    private readonly string _path;
    private readonly object _sync = new();

    public KeywordKnowledgeBase(string indexPath)
    {
        _path = indexPath;
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        Load();
    }

    public Task IndexAsync(string source, string text, CancellationToken ct = default)
    {
        var doc = new Doc(source, text, Tokenize(text));
        lock (_sync)
        {
            _docs.RemoveAll(d => d.Source == source);
            _docs.Add(doc);
            Save();
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, int topK = 5, CancellationToken ct = default)
    {
        var q = Tokenize(query);
        lock (_sync)
        {
            var hits = _docs
                .Select(d => new KnowledgeHit(d.Source, Snippet(d.Text, q), Score(q, d)))
                .Where(h => h.Score > 0)
                .OrderByDescending(h => h.Score)
                .Take(topK)
                .ToList();
            return Task.FromResult<IReadOnlyList<KnowledgeHit>>(hits);
        }
    }

    private static double Score(string[] q, Doc d)
    {
        if (q.Length == 0) return 0;
        var matched = q.Count(t => d.Tokens.Contains(t, StringComparer.OrdinalIgnoreCase));
        return (double)matched / q.Length;
    }

    public static string Snippet(string text, string[] q)
    {
        foreach (var t in q)
        {
            var i = text.IndexOf(t, StringComparison.OrdinalIgnoreCase);
            if (i >= 0)
            {
                var start = Math.Max(0, i - 60);
                var len = Math.Min(text.Length - start, 200);
                return text.Substring(start, len).Replace("\n", " ").Trim();
            }
        }
        return text.Length <= 200 ? text : text[..200];
    }

    public static string[] Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        var tokens = new List<string>();
        var buffer = new System.Text.StringBuilder();

        void Flush()
        {
            if (buffer.Length > 0)
            {
                var w = buffer.ToString().ToLowerInvariant();
                if (w.Length > 1) tokens.Add(w);
                buffer.Clear();
            }
        }

        foreach (var ch in text)
        {
            // Keep CJK characters as individual tokens so Chinese queries still match.
            if (ch >= 0x4E00 && ch <= 0x9FFF)
            {
                Flush();
                tokens.Add(ch.ToString());
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
        return tokens.Distinct().ToArray();
    }

    private void Save()
    {
        var payload = _docs.Select(d => new { d.Source, d.Text }).ToList();
        File.WriteAllText(_path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var rows = JsonSerializer.Deserialize<List<JsonElement>>(File.ReadAllText(_path));
            if (rows is null) return;
            foreach (var r in rows)
            {
                var src = r.GetProperty("Source").GetString() ?? "";
                var txt = r.GetProperty("Text").GetString() ?? "";
                _docs.Add(new Doc(src, txt, Tokenize(txt)));
            }
        }
        catch { /* index is a cache; a corrupt one is simply rebuilt */ }
    }
}
