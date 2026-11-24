namespace CogniCustos.Ui.ViewModels;

/// <summary>
/// Where the shell gets its content and where its actions go. The default
/// implementation is the demo data used by the tests and the screenshots; the
/// runnable sample passes one backed by CogniCustosRuntime.
/// </summary>
public interface IShellDataSource
{
    IReadOnlyList<ReminderItem> Today();

    (string Title, string Empty, IReadOnlyList<CardItem> Items) CalendarCard();

    (string Title, string Empty, IReadOnlyList<CardItem> Items) PendingCard();

    (string Title, string Empty, IReadOnlyList<CardItem> Items) NoteCard();

    /// <summary>
    /// The user confirmed an incoming item: it may now be applied for real.
    /// Returns null on success, or a message to show when the write was refused.
    /// </summary>
    string? IncomingConfirmed(CardItem item);

    /// <summary>Returns null on success, or a message to show when the decision was refused.</summary>
    string? IncomingIgnored(CardItem item);

    void NoteEdited(CardItem item, string text);

    /// <summary>Free-text instruction from the input box; the answer goes back to the line.</summary>
    Task<string?> AskAsync(string instruction);

    /// <summary>What the settings page shows on the "Small local model" row.</summary>
    string LocalModelStatus();

    /// <summary>
    /// Switch to the user's Ollama model. Returns null on success, or the reason it was
    /// refused. Choosing this path is expected to free the bundled model.
    /// </summary>
    string? EnableOllama(string model);
}
