using System.Text.Json;
using CogniCustos.Contracts;

namespace CogniCustos.Core;

/// <summary>Everything the user can change about wiring. Stored next to the state, never in the workspace root.</summary>
public sealed record AppSettings
{
    public string OllamaBaseUrl { get; init; } = "http://127.0.0.1:11434";
    public string OllamaModel { get; init; } = "qwen2.5:7b";
    public string? PortalSidecarUrl { get; init; }
    public string AiName { get; init; } = "CogniCustos 助手";
}

public static class SettingsStore
{
    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
        }
        catch { /* fall back to defaults rather than refusing to start */ }
        return new AppSettings();
    }

    public static void Save(string path, AppSettings settings)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>
/// Local reminders. Deliberately tiny: it polls the task store and raises one event
/// per task per run, so the UI can show it without owning a scheduler.
/// </summary>
public sealed class ReminderService : IDisposable
{
    private readonly ITaskStore _tasks;
    private readonly System.Threading.Timer _timer;
    private readonly HashSet<int> _fired = new();
    private readonly object _sync = new();

    public ReminderService(ITaskStore tasks, TimeSpan? interval = null)
    {
        _tasks = tasks;
        var period = interval ?? TimeSpan.FromSeconds(30);
        _timer = new System.Threading.Timer(_ => _ = CheckAsync(), null, TimeSpan.FromSeconds(2), period);
    }

    public event Action<string>? ReminderRaised;

    private async Task CheckAsync()
    {
        try
        {
            var items = await _tasks.ListAsync().ConfigureAwait(false);
            foreach (var t in items)
            {
                if (t.Done || string.IsNullOrWhiteSpace(t.Due)) continue;
                if (!DateTimeOffset.TryParse(t.Due, out var due)) continue;
                var delta = due - DateTimeOffset.Now;
                if (delta > TimeSpan.FromHours(24) || delta < TimeSpan.FromHours(-12)) continue;

                lock (_sync)
                {
                    if (!_fired.Add(t.Id)) continue;
                }
                ReminderRaised?.Invoke($"提醒：#{t.Id} {t.Title}（{due:MM-dd HH:mm}）");
            }
        }
        catch { /* a reminder must never crash the app */ }
    }

    public void Dispose() => _timer.Dispose();
}
