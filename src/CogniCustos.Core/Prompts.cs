namespace CogniCustos.Core;

/// <summary>
/// Whether a prompt segment may be edited by the user later. Locked segments are the part the
/// code parses: JSON protocols, section shapes, and the privacy gate. Everything else is voice
/// and instruction, which is exactly what is worth tuning.
/// </summary>
public enum PromptSegmentKind
{
    Editable,
    Locked,
}

/// <summary>One named part of a prompt: a stable id, a human label, the text, and who owns it.</summary>
public sealed record PromptSegment(
    string Id,
    string LabelKey,
    string Text,
    PromptSegmentKind Kind);

/// <summary>
/// One prompt as it is actually sent, described well enough to be shown, exercised and (from
/// P1 on) edited. Placeholders use <c>{{name}}</c> and are listed in <see cref="Variables"/>.
/// </summary>
public sealed record PromptSpec(
    string Key,
    string GroupKey,
    string TitleKey,
    string PurposeKey,
    string CallSite,
    IReadOnlyList<PromptSegment> Segments,
    IReadOnlyList<string> Variables)
{
    public bool HasLocked => Segments.Any(s => s.Kind == PromptSegmentKind.Locked);
}

/// <summary>Stable keys. Code refers to these; the UI never contains the prompt text.</summary>
public static class PromptKeys
{
    public const string ChatSystem = "chat.system";
    public const string SummaryOneLine = "summary.one-line";
    public const string JudgeSystem = "judge.system";
    public const string JudgeFormat = "judge.format";
    public const string GuardPrivacy = "guard.privacy-check";
    public const string LecturePersona = "lecture.persona";
    public const string LectureOutline = "lecture.outline";
    public const string LectureVision = "lecture.vision-describe";
    public const string ProviderChatTemplate = "provider.chat-template";

    public const string GroupPrompt = "prompt";
    public const string GroupParameter = "parameter";
    public const string GroupConstant = "constant";
}

/// <summary>
/// Every prompt the application sends, in one place.
///
/// Why this file exists: the strings used to be scattered across the runtime, the judge, the
/// privacy checker, the reader and the local-model adapter, which made them impossible to see,
/// compare or tune — and made it easy for a copy in a settings panel to drift from the text that
/// is really sent. Call sites now read from here, so what the advanced panel shows is what runs.
///
/// A prompt is split into segments at the boundary between "voice" and "contract". The contract
/// segments (JSON shapes, the privacy gate) are marked <see cref="PromptSegmentKind.Locked"/>
/// because the parser depends on them; from P1 the editable segments are what the user changes.
/// </summary>
public static class PromptCatalog
{
    private static readonly PromptSpec[] Specs =
    [
        new(
            PromptKeys.ChatSystem,
            PromptKeys.GroupPrompt,
            "Prompt.Chat.Title",
            "Prompt.Chat.Purpose",
            "CogniCustosRuntime.SystemPrompt → ChatAsync",
            [
                new("persona", "Prompt.Chat.Persona", """
                    你是 CogniCustos 的助手，运行在学生的本地电脑上。

                    规则（不可违背）：
                    1. 你不能直接写主数据库或精选知识库。task.add / task.complete / kb.index 只会生成"提议"，由学生确认后才生效。
                    2. 你可以自由写自己的沙箱（sandbox.write），沙箱只存文本，不能执行。
                    3. 需要事实时就调用工具，不要凭空编造任务列表或检索结果。
                    """, PromptSegmentKind.Editable),
                new("tools", "Prompt.Chat.Tools", """
                    可用工具：
                    {{tools}}
                    """, PromptSegmentKind.Locked),
                new("protocol", "Prompt.Chat.Protocol", """
                    输出格式：只输出一个 JSON 对象，不要代码块、不要解释。
                    - 调用工具：{"action":"tool","tool":"<名字>","args":{"<参数>":"<值>"} }
                    - 最终回答：{"action":"answer","text":"<给学生看的回答>"}

                    参数写法（照抄格式，不要自创）：
                    - task.add  / plan.add : {"title":"写终稿","level":"long","due":"2026-12-27"}
                      level 只能是 long / mid / short 这三个英文词。
                    - schedule.add : {"date":"2026-12-27","start":"09:00","end":"11:00","title":"写草稿"}
                      date 是 yyyy-MM-dd，start/end 是 HH:mm，不要写成完整日期时间。
                    - 一次只调用一个工具；拿到结果后再决定下一步或回答。
                    """, PromptSegmentKind.Locked),
            ],
            ["tools"]),

        new(
            PromptKeys.SummaryOneLine,
            PromptKeys.GroupPrompt,
            "Prompt.Summary.Title",
            "Prompt.Summary.Purpose",
            "CogniCustosRuntime → RawStore.Summarizer",
            [
                new("instruction", "Prompt.Summary.Instruction",
                    "你是摘要器。用一句话概括下面的材料，不超过 80 字。", PromptSegmentKind.Editable),
            ],
            []),

        new(
            PromptKeys.JudgeSystem,
            PromptKeys.GroupPrompt,
            "Prompt.Judge.Title",
            "Prompt.Judge.Purpose",
            "ModelJudge.Ask",
            [
                new("rule", "Prompt.Judge.Rule", "你是判断器。判断哪些材料与问题相关。", PromptSegmentKind.Editable),
                new("format", "Prompt.Judge.Format", "只输出 JSON 数组，不要写解释。", PromptSegmentKind.Locked),
            ],
            []),

        new(
            PromptKeys.JudgeFormat,
            PromptKeys.GroupPrompt,
            "Prompt.JudgeFormat.Title",
            "Prompt.JudgeFormat.Purpose",
            "ModelJudge.Ask（拼在用户消息末尾）",
            [
                new("instruction", "Prompt.JudgeFormat.Instruction", """
                    输出格式（只输出这个）：
                    [{"id":"材料编号","relevant":true,"category":"日程|邮件|课程|任务|其他"}]
                    """, PromptSegmentKind.Locked),
            ],
            []),

        new(
            PromptKeys.GuardPrivacy,
            PromptKeys.GroupPrompt,
            "Prompt.Privacy.Title",
            "Prompt.Privacy.Purpose",
            "SensitiveContentChecker.CheckAsync",
            [
                new("system", "Prompt.Privacy.System",
                    "你是本地隐私检查器。只回答一行：YES 或 NO，后接不超过 12 个字的中文理由。",
                    PromptSegmentKind.Locked),
                new("question", "Prompt.Privacy.Question",
                    "以下文本是否包含个人敏感信息（姓名之外的身份证、学号、电话、邮箱、密码、密钥、银行卡、住址）？",
                    PromptSegmentKind.Locked),
            ],
            []),

        new(
            PromptKeys.LecturePersona,
            PromptKeys.GroupPrompt,
            "Prompt.LecturePersona.Title",
            "Prompt.LecturePersona.Purpose",
            "DocLecture.OutlineAsync / AskAsync（system）",
            [
                new("persona", "Prompt.LecturePersona.Persona",
                    "你是一位博学、热心的学长/学姐，正在陪学弟/学妹看一份课程材料。你讲得具体、口语、不端架子；" +
                    "先给结论再给理由，用材料里的原话作证据，指出在哪一页。" +
                    "看不清或材料里没有写的，就直接说不确定，不要编造。" +
                    "回答用提问者使用的语言。", PromptSegmentKind.Editable),
            ],
            []),

        new(
            PromptKeys.LectureOutline,
            PromptKeys.GroupPrompt,
            "Prompt.LectureOutline.Title",
            "Prompt.LectureOutline.Purpose",
            "DocLecture.OutlineAsync（拼在用户消息末尾）",
            [
                new("instruction", "Prompt.LectureOutline.Instruction",
                    "请输出严格的 JSON，不要代码块、不要多余的话：", PromptSegmentKind.Editable),
                new("format", "Prompt.LectureOutline.Format", """
                    {"summary":"3-5 句话：这份材料在讲什么、结构如何、学之前要先会什么",
                     "sections":[{"page":1,"title":"这一页/这一章的一句话小标题"}]}
                    sections 只给有实际内容的页，最多 40 条。
                    """, PromptSegmentKind.Locked),
            ],
            []),

        new(
            PromptKeys.LectureVision,
            PromptKeys.GroupPrompt,
            "Prompt.LectureVision.Title",
            "Prompt.LectureVision.Purpose",
            "OllamaVisionModel.DescribeAsync（点读选区无文字层时）",
            [
                new("instruction", "Prompt.LectureVision.Instruction",
                    "这是课件或文档中被选中的一块区域。请把其中的文字、公式、图表内容如实描述出来，" +
                    "文字照抄，图表说清坐标轴和趋势。不要评价，只描述你看到的东西。",
                    PromptSegmentKind.Editable),
            ],
            []),

        new(
            PromptKeys.ProviderChatTemplate,
            PromptKeys.GroupPrompt,
            "Prompt.ChatTemplate.Title",
            "Prompt.ChatTemplate.Purpose",
            "LlamaModelProvider.Wrap（内置模型每次调用）",
            [
                new("template", "Prompt.ChatTemplate.Template", CogniCustos.Contracts.ChatTemplates.QwenInstruct, PromptSegmentKind.Locked),
            ],
            ["system", "user"]),
    ];

    public static IReadOnlyList<PromptSpec> All => Specs;

    public static PromptSpec Get(string key) =>
        Specs.FirstOrDefault(s => s.Key == key)
        ?? throw new KeyNotFoundException($"no prompt named '{key}'");

    /// <summary>
    /// The prompt as sent: every segment joined by a blank line, placeholders filled.
    /// A missing variable throws instead of shipping "{{tools}}" to the model — a prompt with an
    /// unreplaced placeholder is a bug, not a degraded answer.
    /// </summary>
    public static string Render(string key, IReadOnlyDictionary<string, string>? variables = null)
        => string.Join("\n\n", Get(key).Segments.Select(s => Fill(s.Text, variables)));

    /// <summary>One segment, for prompts that are sent as separate messages.</summary>
    public static string Segment(string key, string segmentId, IReadOnlyDictionary<string, string>? variables = null)
    {
        var segment = Get(key).Segments.FirstOrDefault(s => s.Id == segmentId)
                      ?? throw new KeyNotFoundException($"prompt '{key}' has no segment '{segmentId}'");
        return Fill(segment.Text, variables);
    }

    /// <summary>Every <c>{{name}}</c> left anywhere in the catalog — used by the selfcheck.</summary>
    public static IReadOnlyList<string> PlaceholdersIn(string text) =>
        CogniCustos.Contracts.Template.Placeholders(text);

    private static string Fill(string text, IReadOnlyDictionary<string, string>? variables) =>
        CogniCustos.Contracts.Template.Fill(text, variables);
}
