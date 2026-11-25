namespace CogniCustos.Core;

/// <summary>
/// The response-language rule that every prompt producing prose carries.
///
/// The precedence is the whole point, and it is written into the text so the model cannot
/// "choose" a language on its own:
///
///   1. the language the person picked in the UI (this is the default and the fallback);
///   2. if, in this turn, the person writes in another language or explicitly asks for one,
///      answer in that one instead.
///
/// Without this, a Chinese UI would still get English paragraphs from a model that was
/// trained to mirror its system prompt — or, worse, half-and-half.
/// </summary>
public static class PromptLanguage
{
    private static string _code = "zh";

    /// <summary>"zh" or "en". Set by the application whenever the UI language changes.</summary>
    public static string Code => _code;

    /// <summary>The language name as the model should print it.</summary>
    public static string Name => _code == "en" ? "English" : "简体中文";

    public static void Set(string? culture) =>
        _code = (culture ?? string.Empty).StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";

    /// <summary>Pick the string for the current UI language (keeps message scaffolds in step).</summary>
    public static string L(string zh, string en) => _code == "en" ? en : zh;

    /// <summary>
    /// A cheap language guess for the *question*, used to tell the model when the person has
    /// switched language themselves: any CJK character means Chinese in practice for this app.
    /// </summary>
    public static bool LooksChinese(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var c in text)
            if (c >= '\u4e00' && c <= '\u9fff') return true;
        return false;
    }

    /// <summary>The directive itself, appended to prompts that produce prose.</summary>
    public static string Directive() => _code == "en"
        ? "Answer language: English, the interface language. Use it whenever the user has not "
          + "chosen otherwise.\n"
          + "The user's own language beats the interface language: if their question in this turn "
          + "is written in another language, or names one explicitly, that language wins — answer "
          + "entirely in it and do not translate the question back into English.\n"
          + "Never mix languages in one answer; keep proper names, code, file names and quoted "
          + "passages exactly as they are."
        : "回答语言：简体中文，也就是界面语言。用户没有另行选择时，一律用它。\n"
          + "但用户自己的语言优先：如果他这一轮的提问用的是别的语言，或者明确点名了某种语言，"
          + "就整段改用那种语言回答，不要把问题翻译回中文。\n"
          + "一段回答里不要中英混排；专有名词、代码、文件名、引用原文保持原样。";
}
