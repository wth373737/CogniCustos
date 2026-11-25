using System.Text.Json;

namespace CogniCustos.Core;

/// <summary>
/// User overrides for prompt segments: the only thing P1 lets the user change.
///
/// Defaults stay in <see cref="PromptCatalog"/>; this file holds only what was edited, so
/// "restore default" is a deletion, and a broken file can always fall back to the shipped text.
/// Locked segments never get here (the catalogue refuses them before this is reached).
/// </summary>
public sealed class PromptOverrides
{
    public const string FileName = "prompts.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly string _path;

    private PromptOverrides(string path) => _path = path;

    public string Path => _path;

    /// <summary>Keyed "promptKey/segmentId".</summary>
    public IReadOnlyDictionary<string, string> All => _values;

    public static string Id(string promptKey, string segmentId) => promptKey + "/" + segmentId;

    /// <summary>Read the file; a missing or unreadable one simply means "no overrides".</summary>
    public static PromptOverrides Load(string path)
    {
        var overrides = new PromptOverrides(path);
        try
        {
            if (!File.Exists(path)) return overrides;

            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (parsed is null) return overrides;

            foreach (var (id, text) in parsed)
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                overrides._values[id] = text;
            }
        }
        catch
        {
            // a corrupt file must not take the application down with it
        }

        return overrides;
    }

    public bool TryGet(string promptKey, string segmentId, out string text) =>
        _values.TryGetValue(Id(promptKey, segmentId), out text!);

    public bool Has(string promptKey, string segmentId) => _values.ContainsKey(Id(promptKey, segmentId));

    public void Set(string promptKey, string segmentId, string text) =>
        _values[Id(promptKey, segmentId)] = text;

    public bool Clear(string promptKey, string segmentId) => _values.Remove(Id(promptKey, segmentId));

    /// <summary>Persist. Writing is best-effort: the in-memory override still applies if it fails.</summary>
    public bool Save(out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_values, Json));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
