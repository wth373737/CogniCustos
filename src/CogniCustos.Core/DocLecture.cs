using System.Text;
using System.Text.Json;
using CogniCustos.Contracts;

namespace CogniCustos.Core;

/// <summary>One row of the page index the lecture opens with.</summary>
public sealed record LectureSection(int Page, string Title);

/// <summary>What the reader shows above the page: a summary and a page index.</summary>
public sealed record LectureOutline(string Summary, IReadOnlyList<LectureSection> Sections, string Source);

/// <summary>One question about the open document, with whatever the user selected.</summary>
public sealed record DocQuestion(
    string Title,
    int Page,
    string PageText,
    string RegionText,
    string Question,
    /// <summary>True when the user actually dragged a box (an empty region is not a box).</summary>
    bool HadBox = false);

/// <summary>
/// The "博学热心的学长学姐" voice over the open document.
///
/// Two jobs, both prompt-shaped rather than model-shaped: the outline pass reads the whole
/// text layer cheaply (a few hundred characters per page) and returns a summary plus a page
/// index; the question pass reads only the current page and the selected rectangle, so a
/// selection question costs one page, not one document.
///
/// Every step degrades: no model → a deterministic outline (first lines per page); no text
/// layer in the selection → the vision model, if one is installed; neither → say so plainly
/// instead of inventing a reading.
/// </summary>
public sealed class DocLecture
{
    private readonly IModelRouter _models;
    private readonly IVisionModel? _vision;
    private readonly Action<string>? _log;

    /// <summary>
    /// Called with (system, user) right before every model call. The reader's diagnostics and
    /// the advanced panel's "try it" button both need the exact prompt, not a reconstruction.
    /// </summary>
    public Action<string, string>? PromptTrace { get; set; }

    public DocLecture(IModelRouter models, IVisionModel? vision = null, Action<string>? log = null)
    {
        _models = models;
        _vision = vision;
        _log = log;
    }

    /// <summary>
    /// Who the model is being asked to be. The text lives in <see cref="PromptCatalog"/>; this
    /// property is the resolved persona, so tests can assert what is actually sent.
    /// </summary>
    public static string Persona => PromptCatalog.Render(PromptKeys.LecturePersona);

    public const int OutlineCharsPerPage = 320;
    public const int OutlineTotalBudget = 7000;
    public const int AskCharsPerPage = 4000;

    /// <summary>
    /// Read the whole text layer once and produce summary + page index. With no model
    /// available this still returns something usable (and says where it came from).
    /// </summary>
    public async Task<LectureOutline> OutlineAsync(string title, IReadOnlyList<string> pageTexts,
        CancellationToken ct = default)
    {
        var corpus = BuildCorpus(pageTexts);
        if (corpus.Length == 0)
        {
            return new LectureOutline("（这份文件没有可读的文字层，可能整份都是图片。）", [], "none");
        }

        var provider = _models.Resolve(ModelRole.Reasoning);
        if (provider is StandbyModelProvider || provider is null)
        {
            return FallbackOutline(pageTexts);
        }

        var user = new StringBuilder()
            .AppendLine($"文件名：{title}")
            .AppendLine($"共 {pageTexts.Count} 页。下面是每一页的开头（不是全文）：")
            .AppendLine(corpus)
            .AppendLine()
            .Append(PromptCatalog.Render(PromptKeys.LectureOutline));

        string raw;
        try
        {
            PromptTrace?.Invoke(Persona, user.ToString());
            raw = await provider.CompleteAsync(Persona, user.ToString(), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke("lecture outline failed: " + ex.Message);
            return FallbackOutline(pageTexts);
        }

        return ParseOutline(raw, pageTexts.Count) ?? FallbackOutline(pageTexts);
    }

    /// <summary>
    /// Answer one question about one selection. <paramref name="regionPng"/> is only used
    /// when the selection has no text layer (a scanned page, a chart, a photo).
    /// </summary>
    public async Task<string> AskAsync(DocQuestion question, byte[]? regionPng, CancellationToken ct = default)
    {
        var hasText = !string.IsNullOrWhiteSpace(question.RegionText);
        var provider = _models.Resolve(ModelRole.Reasoning);

        string? seen = null;
        if (!hasText && regionPng is not null && _vision is { IsAvailable: true })
        {
            try
            {
                seen = await _vision.DescribeAsync(
                    PromptCatalog.Render(PromptKeys.LectureVision),
                    regionPng, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.Invoke("vision describe failed: " + ex.Message);
            }
        }

        if (provider is null || provider is StandbyModelProvider)
        {
            // Never pretend this is an answer. Without a model the reader can still show the
            // user the words they boxed, and say plainly why nothing can answer — "no model"
            // alone sends people looking in the wrong place.
            var head = "（当前没有可用的推理模型，所以只能把你选中的原文给你看。"
                       + WhyNoModel() + "）\n\n";
            var what = hasText
                ? head + $"选中内容：{Clip(question.RegionText, 600)}"
                : seen is null
                    ? head + "（也没有识别出这块区域里的文字。）"
                    : head + $"这块区域看起来是：{Clip(seen, 600)}";
            return what;
        }

        var pageText = Clip(question.PageText, AskCharsPerPage);
        var user = new StringBuilder()
            .AppendLine($"材料：{question.Title}，当前第 {question.Page} 页。")
            .AppendLine("这一页的文字（可能不完整）：")
            .AppendLine(pageText.Length == 0 ? "（这一页没有文字层）" : pageText)
            .AppendLine();

        if (hasText)
        {
            user.AppendLine("学弟/学妹用方框选中的内容：")
                .AppendLine(Clip(question.RegionText, 1500))
                .AppendLine();
        }
        else if (question.HadBox && seen is not null)
        {
            user.AppendLine("学弟/学妹用方框选中的区域，视觉识别结果是：")
                .AppendLine(Clip(seen, 1500))
                .AppendLine("（以上是识别结果，可能有误差；拿不准就说明。）");
        }
        else if (!question.HadBox)
        {
            user.AppendLine("学弟/学妹这一问没有框选，是想让你按整页内容回答。");
            user.AppendLine("能定位到具体段落就说明在第几句/哪个位置；需要聚焦某处时可以请对方先框一下。");
            user.AppendLine();
        }
        else
        {
            user.AppendLine("学弟/学妹选中了一块区域，但既没有文字层也没有视觉模型，");
            user.AppendLine("所以你看不到那块内容。请在回答里说明这一点，再按整页内容给出能给的帮助。");
        }

        user.Append("问题：").AppendLine(string.IsNullOrWhiteSpace(question.Question)
            ? "这块内容是什么意思？为什么重要？"
            : question.Question);

        try
        {
            PromptTrace?.Invoke(Persona, user.ToString());
            return (await provider.CompleteAsync(Persona, user.ToString(), ct).ConfigureAwait(false)).Trim();
        }
        catch (Exception ex)
        {
            return "模型调用失败：" + ex.Message;
        }
    }

    /// <summary>
    /// Why nothing can answer, in the reader's own words: what is registered, what is usable, and
    /// what to do about it. "找不到模型" is useless when the real cause is "the cloud provider is
    /// registered but its key is wrong" or "nothing is registered at all".
    /// </summary>
    private string WhyNoModel()
    {
        List<IModelProvider> all;
        try { all = _models.All.ToList(); }
        catch { all = []; }

        if (all.Count == 0)
            return "本实例里一个模型都没有注册：开设置页填好模型或启动本地模型再试";

        var withoutCloud = all.All(p => !p.Id.StartsWith("cloud:", StringComparison.Ordinal));
        var parts = all.Select(p => $"{p.Id} {(p.IsAvailable ? "可用" : "不可用")}");

        return "已注册 " + string.Join("、", parts) +
               (withoutCloud ? "；云端模型还没有注册到本实例（填完要点一次保存）" : "；请检查云端地址、模型名与 API 密钥");
    }

    /// <summary>Deterministic outline: first line of each page, no model involved.</summary>
    public static LectureOutline FallbackOutline(IReadOnlyList<string> pageTexts)
    {
        var sections = new List<LectureSection>();
        for (var i = 0; i < pageTexts.Count; i++)
        {
            var line = FirstMeaningfulLine(pageTexts[i]);
            if (line.Length > 0) sections.Add(new LectureSection(i + 1, Clip(line, 48)));
            if (sections.Count >= 40) break;
        }

        var head = pageTexts.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        var summary = head is null
            ? "（没有文字层）"
            : Clip(Collapse(head), 220);

        return new LectureOutline(summary, sections, "fallback");
    }

    /// <summary>Parse the model's JSON leniently; null when it is not JSON at all.</summary>
    public static LectureOutline? ParseOutline(string raw, int pageCount)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;

        try
        {
            using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
            var root = doc.RootElement;

            var summary = root.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String
                ? (s.GetString() ?? string.Empty).Trim()
                : string.Empty;

            var sections = new List<LectureSection>();
            if (root.TryGetProperty("sections", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var page = item.TryGetProperty("page", out var p) && p.TryGetInt32(out var n) ? n : 0;
                    var title = item.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                        ? (t.GetString() ?? string.Empty).Trim()
                        : string.Empty;
                    if (page < 1 || page > pageCount || title.Length == 0) continue;
                    sections.Add(new LectureSection(page, Clip(title, 60)));
                }
            }

            if (summary.Length == 0 && sections.Count == 0) return null;
            sections.Sort((a, b) => a.Page.CompareTo(b.Page));
            return new LectureOutline(summary, sections, "model");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildCorpus(IReadOnlyList<string> pageTexts)
    {
        var text = new StringBuilder();
        for (var i = 0; i < pageTexts.Count; i++)
        {
            var body = Collapse(pageTexts[i]);
            if (body.Length == 0) continue;
            if (text.Length + body.Length > OutlineTotalBudget) break;
            text.Append(i + 1).Append(". ").AppendLine(Clip(body, OutlineCharsPerPage));
        }

        return text.ToString();
    }

    private static string FirstMeaningfulLine(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = Collapse(raw);
            if (line.Length >= 3) return line;
        }

        return string.Empty;
    }

    private static string Collapse(string text) =>
        string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static string Clip(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
