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

    /// <summary>The user confirmed an incoming item: it may now be applied for real.</summary>
    void IncomingConfirmed(CardItem item);

    void IncomingIgnored(CardItem item);

    void NoteEdited(CardItem item, string text);

    /// <summary>Free-text instruction from the input box; the answer goes back to the line.</summary>
    Task<string?> AskAsync(string instruction);
}
