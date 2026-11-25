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

    // ---------- advanced panel (demo store) ----------
    //
    // The demo keeps a tiny in-memory store with the same shape as the real one, so the panel's
    // edit / save / reset / refuse paths can be exercised without Core or a settings file.

    private static readonly Dictionary<string, string> PromptDefaults = new(StringComparer.Ordinal)
    {
        ["lecture.persona/persona"] =
            "你是一位博学、热心的学长/学姐，正在陪学弟/学妹看一份课程材料。你讲得具体、口语、不端架子。",
        ["summary.one-line/instruction"] = "你是摘要器。用一句话概括下面的材料，不超过 80 字。",
        ["chat.system/protocol"] = "输出格式：只输出一个 JSON 对象，不要代码块、不要解释。",
    };

    private readonly Dictionary<string, string> _promptStore = new(PromptDefaults, StringComparer.Ordinal);

    /// <summary>Two prompt entries and one parameter group: enough for the panel, shots and tests.</summary>
    public AdvancedSnapshot AdvancedSnapshot() => new(
    [
        new AdvancedItemData("prompt", "Prompt.Chat.Title", "Prompt.Chat.Purpose",
            "CogniCustosRuntime.ChatAsync（演示数据）",
            [
                new AdvancedRowData("Prompt.Chat.Persona", "", "你是 CogniCustos 的助手，运行在学生的本地电脑上。…",
                    false, "chat.system", "persona"),
                new AdvancedRowData("Prompt.Chat.Protocol", "", Prompt("chat.system/protocol"), true,
                    "chat.system", "protocol"),
            ]),
        new AdvancedItemData("prompt", "Prompt.LecturePersona.Title", "Prompt.LecturePersona.Purpose",
            "DocLecture.OutlineAsync / AskAsync（演示数据）",
            [
                new AdvancedRowData("Prompt.LecturePersona.Persona", "", Prompt("lecture.persona/persona"),
                    false, "lecture.persona", "persona", IsOverridden("lecture.persona/persona")),
            ],
            CanTry: true),
        new AdvancedItemData("prompt", "Prompt.Summary.Title", "Prompt.Summary.Purpose",
            "RawStore.Summarizer（演示数据）",
            [
                new AdvancedRowData("Prompt.Summary.Instruction", "", Prompt("summary.one-line/instruction"),
                    false, "summary.one-line", "instruction", IsOverridden("summary.one-line/instruction")),
            ],
            CanTry: true),
        new AdvancedItemData("parameter", "Advanced.Llama.Title", "Advanced.Llama.Purpose",
            "LlamaModelProvider（演示数据）",
            [
                new AdvancedRowData(null, "contextSize", "2048", false),
                new AdvancedRowData(null, "maxTokens", "512", false),
            ]),
    ]);

    public PromptEditResult SavePromptOverride(string promptKey, string segmentId, string text)
    {
        var id = promptKey + "/" + segmentId;
        if (!PromptDefaults.ContainsKey(id))
            return new PromptEditResult("演示数据里没有这一段。", string.Empty, false);
        if (id == "chat.system/protocol")
            return new PromptEditResult("这一段的格式由代码固定，不能编辑。", string.Empty, false);
        if (string.IsNullOrWhiteSpace(text))
            return new PromptEditResult("内容不能为空；想恢复原文请点「恢复默认」。", string.Empty, false);

        _promptStore[id] = text.Trim();
        return new PromptEditResult(null, _promptStore[id], IsOverridden(id));
    }

    public PromptEditResult ResetPromptOverride(string promptKey, string segmentId)
    {
        var id = promptKey + "/" + segmentId;
        if (!PromptDefaults.TryGetValue(id, out var text))
            return new PromptEditResult("演示数据里没有这一段。", string.Empty, false);
        if (id == "chat.system/protocol")
            return new PromptEditResult("这一段的格式由代码固定，不能编辑。", string.Empty, false);

        _promptStore[id] = text;
        return new PromptEditResult(null, text, false);
    }

    public Task<CloudTestResult> TestCloudAsync(string model, string url, string apiKey) =>
        Task.FromResult(string.IsNullOrWhiteSpace(url)
            ? new CloudTestResult(false, "演示：接口地址为空。", 1)
            : new CloudTestResult(true, "演示：已连通 " + model, 42));
    public Task<PromptTryResult> TryPromptAsync(string promptKey) =>
        Task.FromResult(new PromptTryResult(true,
            "演示：这段提示词会作为 system 消息发送。",
            "（演示回答）这条提示词看起来没问题。",
            12, string.Empty));

    private string Prompt(string id) => _promptStore.TryGetValue(id, out var text) ? text : string.Empty;

    private bool IsOverridden(string id) =>
        _promptStore.TryGetValue(id, out var text) && PromptDefaults.TryGetValue(id, out var original)
        && text != original;
}
