namespace CogniCustos.Ui.ViewModels;

/// <summary>
/// Where the shell gets its content and where its actions go. The default
/// implementation is the demo data used by the tests and the screenshots; the
/// runnable sample passes one backed by CogniCustosRuntime.
/// </summary>
/// <summary>One email account as the settings page sees it (address, server, scope).</summary>
public sealed record EmailAccountInfo(string Address, string Host, int Port, int LookbackDays, bool UnreadOnly);

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

    /// <summary>Download the bundled model if missing. Null on success, else the reason.</summary>
    string? InstallBuiltIn(IProgress<double> progress);

    /// <summary>Delete the bundled model's files. Null on success, else the reason.</summary>
    string? DeleteBuiltIn(out string message);

    /// <summary>Switch to the bundled model. Null on success, else the reason.</summary>
    string? SwitchToBuiltIn();

    /// <summary>What the primary-model fields should show: model, base URL, key present.</summary>
    (string Model, string Url, bool Configured) CloudSettings();

    /// <summary>Save and apply the primary model. Null on success, else the reason.</summary>
    string? SaveCloud(string model, string url, string apiKey);

    // ---------- retrieval: email accounts and web pages ----------

    IReadOnlyList<EmailAccountInfo> EmailAccounts();

    /// <summary>Add an account. Null on success, else the reason.</summary>
    string? AddEmailAccount(string address, string host, int port, string secret, int lookbackDays,
        bool unreadOnly);

    bool RemoveEmailAccount(string address);

    /// <summary>Import accounts Thunderbird already knows. Returns a status line.</summary>
    string ImportEmailAccounts();

    /// <summary>Really connect to the IMAP server; returns a status line.</summary>
    (bool Ok, string Message) TestEmailAccount(string address);

    IReadOnlyList<string> WebSources();

    string? AddWebSource(string url);

    bool RemoveWebSource(string url);

    /// <summary>Is the bundled model running on the GPU?</summary>
    bool GpuEnabled();

    /// <summary>Turn hardware acceleration on/off. Null on success, else the reason.</summary>
    string? SetGpu(bool enabled);
}
