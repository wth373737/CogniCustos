namespace CogniCustos.Core;

/// <summary>
/// The visibility ladder of docs/PARTITION-MODEL.md §3.1 and the single place that
/// answers "may this enter the main conversation". Every retrieval path asks here
/// instead of inventing its own rule.
/// </summary>
public static class Visibility
{
    /// <summary>Original bytes: application only, the model may not even discover them.</summary>
    public const string RawOriginal = "R0";

    /// <summary>Ingest summary: discoverable, never citable, never in the main context.</summary>
    public const string RawSummary = "R1";

    /// <summary>Sandbox draft / pending proposal: readable by the AI inside the sandbox only.</summary>
    public const string Sandbox = "S";

    /// <summary>Confirmed knowledge: searchable, readable, citable.</summary>
    public const string Knowledge = "K";

    /// <summary>Confirmed fact (tasks, schedule): searchable, readable, citable.</summary>
    public const string Main = "M";

    /// <summary>Only confirmed material becomes part of what the model reasons over.</summary>
    public static bool AllowsMainContext(string? visibility) =>
        visibility is Knowledge or Main;

    /// <summary>A citation must point at confirmed material; an invented id must not pass.</summary>
    public static bool CanBeCited(string? visibility) => AllowsMainContext(visibility);

    /// <summary>Normalise anything unknown to the safest value (never silently promote).</summary>
    public static string Normalize(string? visibility) => visibility switch
    {
        RawOriginal => RawOriginal,
        RawSummary => RawSummary,
        Sandbox => Sandbox,
        Knowledge => Knowledge,
        Main => Main,
        _ => RawOriginal,
    };

    /// <summary>A human-readable label for the UI ("R1 · 原始概括").</summary>
    public static string Label(string? visibility) => Normalize(visibility) switch
    {
        RawOriginal => "R0 · 原始原件",
        RawSummary => "R1 · 原始概括",
        Sandbox => "S · 沙箱草稿",
        Knowledge => "K · 已确认资料",
        _ => "M · 已确认事实",
    };
}
