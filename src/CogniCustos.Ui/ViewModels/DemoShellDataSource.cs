using CogniCustos.Ui.Localization;

namespace CogniCustos.Ui.ViewModels;

/// <summary>
/// The demo content the sandbox has always shown. Behaviour is identical to the
/// hard-coded seeds it replaced, so the tests and the screenshot states do not change.
/// </summary>
public sealed class DemoShellDataSource : IShellDataSource
{
    public IReadOnlyList<ReminderItem> Today() =>
    [
        new(Localizer.Get("Demo.TodayUrgent1"), true),
        new(Localizer.Get("Demo.TodayNormal"), false),
        new(Localizer.Get("Demo.TodayUrgent2"), true),
        new(Localizer.Get("Demo.TodayUrgent3"), true),
    ];

    public (string Title, string Empty, IReadOnlyList<CardItem> Items) CalendarCard() => (
        DateTime.Today.ToString("yy/MM/dd"),
        string.Empty,
        [
            new CardItem(Localizer.Get("Demo.Plan1.Title"), Localizer.Get("Demo.Plan1.When")),
        ]);

    public (string Title, string Empty, IReadOnlyList<CardItem> Items) PendingCard() => (
        Localizer.Get("Card.Pending"),
        Localizer.Get("Card.Empty.Pending"),
        [
            new CardItem(Localizer.Get("Demo.Plan1.Title"), Localizer.Get("Demo.Plan1.When"),
                Localizer.Get("Demo.Incoming.Source"), Localizer.Get("Demo.Incoming.File"),
                tag: Localizer.Get("Card.Tag.Mail")),
            new CardItem(Localizer.Get("Demo.Plan2.Title"), Localizer.Get("Demo.Plan2.When")),
        ]);

    public (string Title, string Empty, IReadOnlyList<CardItem> Items) NoteCard() => (
        Localizer.Get("Card.Note"),
        Localizer.Get("Card.Empty.Note"),
        [
            new CardItem(Localizer.Get("Demo.Note1")),
        ]);

    public string? IncomingConfirmed(CardItem item) => null;

    public string? IncomingIgnored(CardItem item) => null;

    public void NoteEdited(CardItem item, string text)
    {
    }

    public Task<string?> AskAsync(string instruction) => Task.FromResult<string?>(null);

    public string LocalModelStatus() => "qwen2.5:7b（内置）";

    public string? EnableOllama(string model) => null;

    public string? InstallBuiltIn(IProgress<double> progress)
    {
        progress.Report(1);
        return null;
    }

    public string? DeleteBuiltIn(out string message)
    {
        message = "已删除内置模型（演示）";
        return null;
    }

    public string? SwitchToBuiltIn() => null;

    public (string Status, bool Available, bool Enabled) ThunderbirdMail() =>
        ("3 个文件夹 · 最大 943 MB", true, true);

    public bool SetUseThunderbird(bool enabled) => true;

    public IReadOnlyList<MailFolderInfo> MailFolders() =>
    [
        new("outlook.office365.com", "INBOX", "wutianhao@student.usm.my", "outlook.office365.com", 282.3, true),
        new("imap.gmail-1.com", "INBOX", "wutianhao373737@gmail.com", "imap.gmail.com", 107.6, true),
        new("imap.gmail-2.com", "INBOX", "wth373737@gmail.com", "imap.gmail.com", 76.6, false),
        new("imap.163-1.com", "INBOX", "18916954605@163.com", "imap.163.com", 0.4, true),
    ];

    public bool SetMailFolderEnabled(string account, string folder, bool enabled) => true;

    public bool SetOnlyMailbox(string mailbox) => true;

    public string FetchNow() => "演示：抓到 1 条，生成 1 个待确认提议";

    public void RescanMail()
    {
    }

    public IReadOnlyList<EmailAccountInfo> EmailAccounts() =>
        [new EmailAccountInfo("student@usm.my", "outlook.office365.com", 993, 7, true)];

    public string? AddEmailAccount(string address, string host, int port, string secret, int lookbackDays,
        bool unreadOnly) => null;

    public bool RemoveEmailAccount(string address) => true;

    public string ImportEmailAccounts() => "演示：没有找到 Thunderbird 配置";

    public (bool Ok, string Message) TestEmailAccount(string address) =>
        (true, "已连接 outlook.office365.com（演示）");

    public IReadOnlyList<string> WebSources() => ["https://elearning.usm.my/notices"];

    public string? AddWebSource(string url) => null;

    public bool RemoveWebSource(string url) => true;

    public (string Model, string Url, bool Configured) CloudSettings() =>
        ("deepseek-flash", "https://api.deepseek.com", true);

    public string? SaveCloud(string model, string url, string apiKey) => null;

    public bool GpuEnabled() => false;

    public string? SetGpu(bool enabled) => null;

    public IReaderService CreateReader() => new DemoReaderService();
}
