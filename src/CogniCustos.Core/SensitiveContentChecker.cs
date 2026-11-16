using System.Text.RegularExpressions;
using CogniCustos.Contracts;

namespace CogniCustos.Core;

/// <summary>
/// The local gatekeeper. Runs entirely on the device: deterministic rules first,
/// then (optionally) a local model for a second opinion. Nothing it sees leaves the
/// machine, and it never rewrites the text silently — it reports and lets the human
/// decide.
/// </summary>
public sealed class SensitiveContentChecker : ISensitiveContentChecker
{
    private static readonly (string Name, Regex Pattern)[] Rules =
    {
        ("email", new Regex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.Compiled)),
        ("phone", new Regex(@"(?<!\d)(?:\+?60|0)1[0-9][-\s]?\d{3,4}[-\s]?\d{4}(?!\d)", RegexOptions.Compiled)),
        ("phone_cn", new Regex(@"(?<!\d)1[3-9]\d{9}(?!\d)", RegexOptions.Compiled)),
        // No leading \b here: CJK characters are word characters in .NET, so \b before
        // 学号 never matches ("…的学号…" has a word char on both sides).
        ("student_id", new Regex(@"(?i)(?:matric|student\s*id|学号|學號)\D{0,6}(\d{6,10})", RegexOptions.Compiled)),
        ("api_key", new Regex(@"(?i)\b(?:sk-[A-Za-z0-9]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16})\b", RegexOptions.Compiled)),
        ("private_key", new Regex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.Compiled)),
        ("password", new Regex(@"(?i)\b(?:password|passwd|otp|验证码|密码)\b\s*[:=]?\s*\S{4,}", RegexOptions.Compiled)),
        ("card", new Regex(@"(?<!\d)(?:\d[ -]?){15,19}(?!\d)", RegexOptions.Compiled)),
        ("ic_number", new Regex(@"(?<!\d)\d{6}[- ]?\d{2}[- ]?\d{4}(?!\d)", RegexOptions.Compiled)),
    };

    private readonly IModelRouter _models;
    private readonly bool _useModel;

    public SensitiveContentChecker(IModelRouter models, bool useModel = false)
    {
        _models = models;
        _useModel = useModel;
    }

    public async Task<SensitiveCheckResult> CheckAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return SensitiveCheckResult.Clean(text ?? "");

        var reasons = new List<string>();
        foreach (var (name, pattern) in Rules)
        {
            var m = pattern.Match(text);
            if (m.Success) reasons.Add($"{name}: {Mask(m.Value)}");
        }

        if (_useModel)
        {
            var provider = _models.Resolve(ModelRole.Reasoning);
            if (provider is not StandbyModelProvider)
            {
                try
                {
                    var reply = await provider.CompleteAsync(
                        "你是本地隐私检查器。只回答一行：YES 或 NO，后接不超过 12 个字的中文理由。",
                        "以下文本是否包含个人敏感信息（姓名之外的身份证、学号、电话、邮箱、密码、密钥、银行卡、住址）？\n\n" + Truncate(text, 4000),
                        ct).ConfigureAwait(false);
                    var head = reply.Trim().ToUpperInvariant();
                    if (head.StartsWith("YES"))
                        reasons.Add("local-model: " + Truncate(reply.Trim(), 80));
                }
                catch
                {
                    // A model that cannot answer must not make the gate look clean-by-model;
                    // the rule layer already ran, so the decision stays rule-based.
                }
            }
        }

        var redacted = Redact(text);
        return new SensitiveCheckResult(reasons.Count > 0, reasons, redacted);
    }

    /// <summary>What the text would look like with the detected spans masked. Shown to the user, never sent silently.</summary>
    public static string Redact(string text)
    {
        var result = text;
        foreach (var (_, pattern) in Rules)
            result = pattern.Replace(result, m => Mask(m.Value));
        return result.Length <= 600 ? result : result[..600] + "…";
    }

    private static string Mask(string value)
    {
        if (value.Length <= 4) return new string('*', value.Length);
        return value[..2] + new string('*', Math.Min(10, value.Length - 4)) + value[^2..];
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
