namespace CogniCustos.Contracts;

/// <summary>
/// One <c>{{name}}</c> substitution over a template string.
///
/// It lives in Contracts because two ends need the same rules: the prompt catalog (Core) fills
/// <c>{{tools}}</c>, and the local-model adapter (Adapters) fills the chat template. Contracts
/// has no dependencies, so neither side has to know about the other.
/// </summary>
public static class Template
{
    /// <summary>Every distinct placeholder name in the text, in first-seen order.</summary>
    public static IReadOnlyList<string> Placeholders(string text)
    {
        var found = new List<string>();
        var index = 0;
        while (true)
        {
            var open = text.IndexOf("{{", index, StringComparison.Ordinal);
            if (open < 0) break;
            var close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0) break;
            var name = text[(open + 2)..close];
            if (!found.Contains(name)) found.Add(name);
            index = close + 2;
        }

        return found;
    }

    /// <summary>
    /// Replace every placeholder in one pass. A missing value throws: a template that still
    /// says <c>{{user}}</c> when it reaches the model is a bug, not a degraded answer.
    /// </summary>
    public static string Fill(string text, IReadOnlyDictionary<string, string>? values)
    {
        var output = new System.Text.StringBuilder(text.Length);
        var index = 0;
        while (true)
        {
            var open = text.IndexOf("{{", index, StringComparison.Ordinal);
            if (open < 0)
            {
                output.Append(text, index, text.Length - index);
                break;
            }

            var close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                output.Append(text, index, text.Length - index);
                break;
            }

            output.Append(text, index, open - index);
            var name = text[(open + 2)..close];
            if (values is null || !values.TryGetValue(name, out var value))
                throw new InvalidOperationException($"template placeholder '{name}' was not supplied");

            output.Append(value);
            index = close + 2;
        }

        return output.ToString();
    }
}

/// <summary>
/// The chat formats this application knows how to speak. A model's prompt format is part of
/// the model, not of any single feature, so it is kept next to the adapters' contract and shown
/// (read-only) in the advanced panel instead of being retyped in three places.
/// </summary>
public static class ChatTemplates
{
    /// <summary>Qwen2.5 / Qwen2 — what the bundled GGUF expects.</summary>
    public const string QwenInstruct =
        "<|im_start|>system\n{{system}}<|im_end|>\n" +
        "<|im_start|>user\n{{user}}<|im_end|>\n" +
        "<|im_start|>assistant\n";
}
