using CogniCustos.Contracts;

namespace CogniCustos.Core;

/// <summary>
/// The application's own documentation and source, made searchable so the assistant
/// can answer "how do I use this?" from facts instead of inventing them
/// (proposal objective 7 / UC14).
/// </summary>
public sealed class SelfDocs : ISelfDocs
{
    private sealed record Doc(string Source, string Text, string[] Tokens);

    private readonly List<Doc> _docs = new();
    private readonly object _sync = new();

    public SelfDocs(string root) => Root = Path.GetFullPath(root);

    public string Root { get; }

    /// <summary>
    /// Finds the application root (the directory holding README.md) by walking up from
    /// the running assembly. Avoids hard-coding a checkout path into the app.
    /// </summary>
    public static string ResolveAppRoot(string? start = null)
    {
        var dir = new DirectoryInfo(start ?? AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "README.md")) &&
                Directory.Exists(Path.Combine(dir.FullName, "src")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }

    public Task<int> IndexAsync(CancellationToken ct = default)
    {
        var fresh = new List<Doc>();

        // 1. README, split by heading so a hit points at a section.
        var readme = Directory.EnumerateFiles(Root, "README*.md", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (readme is not null)
        {
            var lines = File.ReadAllLines(readme);
            var section = "README";
            var buffer = new List<string>();
            foreach (var line in lines)
            {
                if (line.StartsWith("## "))
                {
                    AddDoc(fresh, $"{Path.GetFileName(readme)} › {section}", string.Join("\n", buffer));
                    section = line[3..].Trim();
                    buffer.Clear();
                }
                else if (line.StartsWith("# "))
                {
                    section = line[2..].Trim();
                }
                else buffer.Add(line);
            }
            AddDoc(fresh, $"{Path.GetFileName(readme)} › {section}", string.Join("\n", buffer));
        }

        // 2. Source files: path + the leading summary, enough to answer "where is X implemented".
        foreach (var file in Directory.EnumerateFiles(Root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            var text = File.ReadAllText(file);
            var rel = Path.GetRelativePath(Root, file).Replace('\\', '/');
            var head = string.Join("\n", text.Split('\n').Take(40));
            AddDoc(fresh, rel, head);
        }

        lock (_sync)
        {
            _docs.Clear();
            _docs.AddRange(fresh);
        }
        return Task.FromResult(fresh.Count);
    }

    private static void AddDoc(List<Doc> target, string source, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        target.Add(new Doc(source, text, KeywordKnowledgeBase.Tokenize(text)));
    }

    public IReadOnlyList<KnowledgeHit> Search(string query, int topK = 5)
    {
        var q = KeywordKnowledgeBase.Tokenize(query);
        if (q.Length == 0) return Array.Empty<KnowledgeHit>();
        lock (_sync)
        {
            return _docs
                .Select(d => new KnowledgeHit(d.Source,
                    KeywordKnowledgeBase.Snippet(d.Text, q),
                    (double)q.Count(t => d.Tokens.Contains(t, StringComparer.OrdinalIgnoreCase)) / q.Length))
                .Where(h => h.Score > 0)
                .OrderByDescending(h => h.Score)
                .Take(topK)
                .ToList();
        }
    }

    public int Count { get { lock (_sync) return _docs.Count; } }
}
