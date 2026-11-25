namespace CogniCustos.Ui.ViewModels;

/// <summary>
/// Where the shell gets its content and where its actions go. The default
/// implementation is the demo data used by the tests and the screenshots; the
/// runnable sample passes one backed by CogniCustosRuntime.
/// </summary>
/// <summary>One email account as the settings page sees it (address, server, scope).</summary>
public sealed record EmailAccountInfo(string Address, string Host, int Port, int LookbackDays, bool UnreadOnly);

/// <summary>One mailbox folder as the settings page shows it.</summary>
public sealed record MailFolderInfo(string Account, string Folder, string Mailbox, string Server,
    double Megabytes, bool Enabled);

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

    /// <summary>What the email section shows about Thunderbird's local mail store.</summary>
    (string Status, bool Available, bool Enabled) ThunderbirdMail();

    bool SetUseThunderbird(bool enabled);

    /// <summary>Mailbox folders found in Thunderbird, with the on/off state.</summary>
    IReadOnlyList<MailFolderInfo> MailFolders();

    /// <summary>Read this folder from now on (or stop reading it).</summary>
    bool SetMailFolderEnabled(string account, string folder, bool enabled);

    /// <summary>Keep this mailbox and stop reading every other one.</summary>
    bool SetOnlyMailbox(string mailbox);

    /// <summary>
    /// Read the permitted mailboxes right now (the drawer's mail button) and report what
    /// happened. This is the manual version of the daily run: the user asks, the app reads.
    /// </summary>
    string FetchNow();

    void RescanMail();

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

    /// <summary>
    /// The document the point-read window opens. The demo returns drawn pages; the sample
    /// returns PDFium plus the models. The UI itself never sees either.
    /// </summary>
    IReaderService CreateReader();

    /// <summary>
    /// What the advanced panel shows: every prompt the app sends plus the inference parameters.
    /// P0 renders it read-only; the values must be the ones the call sites really use.
    /// </summary>
    AdvancedSnapshot AdvancedSnapshot();

    /// <summary>
    /// Store an edited prompt segment (P1). Returns the text now in force, or the reason the
    /// edit was refused (locked segment, changed placeholders, disk error).
    /// </summary>
    PromptEditResult SavePromptOverride(string promptKey, string segmentId, string text);

    /// <summary>Drop an override and go back to the shipped default.</summary>
    PromptEditResult ResetPromptOverride(string promptKey, string segmentId);

    /// <summary>Send one real request with the prompt as it stands, so an edit can be judged.</summary>
    Task<PromptTryResult> TryPromptAsync(string promptKey);

    /// <summary>The UI language changed ("zh-CN" / "en-US"): prompts must answer in it.</summary>
    void LanguageChanged(string culture);

    /// <summary>
    /// One real round trip to the endpoint described by these three values (what is typed in the
    /// form, not what was last saved). The settings page shows the result verbatim so a wrong URL
    /// or key is visible as the endpoint's own words instead of a silent fallback.
    /// </summary>
    Task<CloudTestResult> TestCloudAsync(string model, string url, string apiKey);
}
