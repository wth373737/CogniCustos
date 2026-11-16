using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CogniCustos.Contracts;

namespace CogniCustos.Core;

/// <summary>
/// Append-only record of every attempt to send text off the device, including the
/// ones the gate refused. The proposal requires exactly this ("every request,
/// including refused ones, recorded in an audit log").
/// </summary>
public sealed class OutboundAudit : IOutboundAudit
{
    private readonly string _path;
    private readonly object _sync = new();

    public OutboundAudit(string path)
    {
        _path = path;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    public void Record(OutboundRecord record)
    {
        var line = JsonSerializer.Serialize(record);
        lock (_sync)
        {
            File.AppendAllText(_path, line + "\n", Encoding.UTF8);
        }
    }

    public IReadOnlyList<OutboundRecord> Recent(int count = 20)
    {
        try
        {
            if (!File.Exists(_path)) return Array.Empty<OutboundRecord>();
            var lines = File.ReadAllLines(_path);
            var result = new List<OutboundRecord>();
            foreach (var line in lines.AsEnumerable().Reverse().Take(count).Reverse())
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var r = JsonSerializer.Deserialize<OutboundRecord>(line);
                    if (r is not null) result.Add(r);
                }
                catch { /* skip a malformed line rather than failing the whole log */ }
            }
            return result;
        }
        catch
        {
            return Array.Empty<OutboundRecord>();
        }
    }

    public static string Hash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}

/// <summary>Outcome of one attempt to use a cloud model.</summary>
public sealed record CloudSendResult(
    bool Sent,
    string Reply,
    SensitiveCheckResult Check,
    string? Refusal);

/// <summary>
/// The only way to reach a cloud model. It checks locally first, records the
/// attempt, and refuses unless the human explicitly allowed flagged content.
/// </summary>
public sealed class CloudGateway
{
    private readonly ICloudModelProvider _provider;
    private readonly ISensitiveContentChecker _checker;
    private readonly IOutboundAudit _audit;

    public CloudGateway(ICloudModelProvider provider, ISensitiveContentChecker checker, IOutboundAudit audit)
    {
        _provider = provider;
        _checker = checker;
        _audit = audit;
    }

    public string Endpoint => _provider.Endpoint;
    public bool IsAvailable => _provider.IsAvailable;

    public async Task<CloudSendResult> SendAsync(string system, string text, bool allowFlagged = false, CancellationToken ct = default)
    {
        var check = await _checker.CheckAsync(system + "\n" + text, ct).ConfigureAwait(false);

        if (check.Flagged && !allowFlagged)
        {
            _audit.Record(new OutboundRecord(DateTimeOffset.Now, _provider.Endpoint, OutboundAudit.Hash(text),
                text.Length, true, "refused", check.Reasons));
            var refusal = "本地检查发现可能敏感的内容，已拦截，未发送。\n"
                        + string.Join("\n", check.Reasons.Select(r => "  - " + r))
                        + "\n\n脱敏预览：\n" + check.RedactedPreview
                        + "\n\n如果确实要发送，请显式确认（allowFlagged = true）。";
            return new CloudSendResult(false, "", check, refusal);
        }

        _audit.Record(new OutboundRecord(DateTimeOffset.Now, _provider.Endpoint, OutboundAudit.Hash(text),
            text.Length, check.Flagged, check.Flagged ? "sent-overridden" : "sent", check.Reasons));

        try
        {
            var reply = await _provider.CompleteAsync(system, text, ct).ConfigureAwait(false);
            return new CloudSendResult(true, reply, check, null);
        }
        catch (Exception ex)
        {
            return new CloudSendResult(false, "", check, "云端调用失败：" + ex.Message);
        }
    }
}
