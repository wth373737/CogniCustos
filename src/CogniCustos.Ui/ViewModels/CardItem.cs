using CogniCustos.Ui.Mvvm;

namespace CogniCustos.Ui.ViewModels;

/// <summary>
/// One row inside a board card, in the format the designs use:
/// a date/time line, then the item, then (for incoming items) where it came from.
/// </summary>
public sealed class CardItem
{
    public CardItem(string title, string when = "", string? source = null, string? sourceFile = null)
    {
        Title = title;
        When = when;
        Source = source;
        SourceFile = sourceFile;
    }

    /// <summary>e.g. "AKP202w Organizational Behavior Test 1".</summary>
    public string Title { get; set; }

    /// <summary>e.g. "11/27 3pm – 5pm".</summary>
    public string When { get; set; }

    /// <summary>Incoming items only: "From elearning.usm.my".</summary>
    public string? Source { get; set; }

    /// <summary>Incoming items only: "File Teaching Plan AKP202W 2026-2027".</summary>
    public string? SourceFile { get; set; }

    public bool HasWhen => !string.IsNullOrWhiteSpace(When);
    public bool HasSource => !string.IsNullOrWhiteSpace(Source);
    public bool HasSourceFile => !string.IsNullOrWhiteSpace(SourceFile);

    /// <summary>Set by the owning card when rows are clickable; null for plain rows.</summary>
    public RelayCommand? Command { get; set; }

    public bool CanClick => Command is not null;

    /// <summary>The one-line form used in dialogs and tooltips.</summary>
    public string Summary => HasWhen ? $"{When} · {Title}" : Title;
}
