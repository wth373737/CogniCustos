using System.Net.Sockets;

namespace CogniCustos.Core;

/// <summary>One email account the daily fetch may read (IMAP).</summary>
public sealed record EmailAccount(string Address, string ImapHost, int ImapPort, int LookbackDays = 7,
    bool UnreadOnly = true);

/// <summary>
/// Email accounts for the fetch. Mirrors the mail plugin's shape: an account has an
/// address, a host/port, and a secret that is never echoed back; "test" really opens a
/// TCP connection to the IMAP port, so the status on the page means something.
/// </summary>
public sealed class EmailAccountsService
{
    /// <summary>Common providers, so the user only types an address and an app password.</summary>
    public static readonly (string Name, string Host, int Port)[] Presets =
    [
        ("Outlook / Office 365", "outlook.office365.com", 993),
        ("Gmail", "imap.gmail.com", 993),
        ("QQ 邮箱", "imap.qq.com", 993),
        ("网易 163", "imap.163.com", 993),
        ("自填服务器", "", 993),
    ];

    private readonly string _settingsPath;

    public EmailAccountsService(string settingsPath) => _settingsPath = settingsPath;

    public IReadOnlyList<EmailAccount> Accounts =>
        SettingsStore.Load(_settingsPath).EmailAccounts ?? [];

    public (bool Ok, string Message) Add(string address, string host, int port, string secret,
        int lookbackDays, bool unreadOnly)
    {
        address = (address ?? "").Trim();
        host = (host ?? "").Trim();

        if (address.Length == 0 || !address.Contains('@')) return (false, "请填写邮箱地址。");
        if (host.Length == 0) return (false, "请填写 IMAP 服务器。");
        if (port is <= 0 or > 65535) return (false, "端口应该在 1-65535 之间。");
        if (string.IsNullOrWhiteSpace(secret)) return (false, "请填写应用专用密码（不是登录密码）。");

        SaveSecret(address, secret);

        var settings = SettingsStore.Load(_settingsPath);
        var accounts = (settings.EmailAccounts ?? []).Where(a => a.Address != address).ToList();
        accounts.Add(new EmailAccount(address, host, port, Math.Clamp(lookbackDays, 1, 90), unreadOnly));

        SettingsStore.Save(_settingsPath, settings with { EmailAccounts = accounts });
        return (true, $"已添加 {address}（{host}:{port}）");
    }

    /// <summary>
    /// Where the app password goes. There is no credential vault yet, so it is a local file
    /// next to the database and the page says so; the IMAP reader is the next step and will
    /// read from here.
    /// </summary>
    public string SecretsPath => Path.Combine(Path.GetDirectoryName(_settingsPath) ?? ".", "secrets.json");

    public void SaveSecret(string address, string secret)
    {
        var secrets = LoadSecrets();
        secrets[address] = secret;
        Directory.CreateDirectory(Path.GetDirectoryName(SecretsPath)!);
        File.WriteAllText(SecretsPath, System.Text.Json.JsonSerializer.Serialize(secrets,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    public string? SecretFor(string address) =>
        LoadSecrets().TryGetValue(address, out var value) ? value : null;

    /// <summary>Never returns values, only which addresses have a secret.</summary>
    public IReadOnlyCollection<string> AddressesWithSecrets() => LoadSecrets().Keys;

    private Dictionary<string, string> LoadSecrets()
    {
        try
        {
            return File.Exists(SecretsPath)
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SecretsPath)) ?? []
                : [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Import the accounts Thunderbird already knows: read its prefs.js for server hostnames
    /// and identities' addresses. Passwords cannot be read (Thunderbird encrypts them), so the
    /// import reports that each account still needs its app password.
    /// </summary>
    public (int Imported, string Message) ImportFromThunderbird(string? profilesRoot = null)
    {
        profilesRoot ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Thunderbird", "Profiles");

        if (!Directory.Exists(profilesRoot)) return (0, "没有找到 Thunderbird 的配置目录");

        var hosts = new List<string>();
        var addresses = new List<string>();

        foreach (var prefs in Directory.EnumerateFiles(profilesRoot, "prefs.js", SearchOption.AllDirectories))
        {
            foreach (var line in File.ReadLines(prefs))
            {
                var host = System.Text.RegularExpressions.Regex.Match(line,
                    @"mail\.server\.server\d+\.hostname"",\s*""(?<v>[^""]+)""");
                if (host.Success) hosts.Add(host.Groups["v"].Value);

                var address = System.Text.RegularExpressions.Regex.Match(line,
                    @"mail\.identity\.id\d+\.useremail"",\s*""(?<v>[^""]+)""");
                if (address.Success) addresses.Add(address.Groups["v"].Value);
            }
        }

        var added = 0;
        for (var i = 0; i < Math.Min(hosts.Count, addresses.Count); i++)
        {
            var settings = SettingsStore.Load(_settingsPath);
            var accounts = (settings.EmailAccounts ?? []).Where(a => a.Address != addresses[i]).ToList();
            accounts.Add(new EmailAccount(addresses[i], hosts[i], 993));
            SettingsStore.Save(_settingsPath, settings with { EmailAccounts = accounts });
            added++;
        }

        return added == 0
            ? (0, "Thunderbird 里没有找到邮箱账号")
            : (added, $"从 Thunderbird 导入 {added} 个账号；密码无法读取，请逐个填写应用密码");
    }

    public bool Remove(string address)
    {
        var settings = SettingsStore.Load(_settingsPath);
        var accounts = (settings.EmailAccounts ?? []).Where(a => a.Address != address).ToList();
        SettingsStore.Save(_settingsPath, settings with { EmailAccounts = accounts });
        return true;
    }

    /// <summary>
    /// A real check without an IMAP library: open the TLS-less TCP port and see whether the
    /// server greets us. Enough to tell "wrong host" from "right host", honest about the rest.
    /// </summary>
    public async Task<(bool Ok, string Message)> TestAsync(EmailAccount account,
        CancellationToken ct = default)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(account.ImapHost, account.ImapPort, ct).AsTask();
            var finished = await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(8), ct));
            if (finished != connect) return (false, $"{account.ImapHost}:{account.ImapPort} 8 秒内没有响应");

            await connect;
            using var stream = client.GetStream();
            var buffer = new byte[256];
            var read = await stream.ReadAsync(buffer, ct);
            var greeting = System.Text.Encoding.ASCII.GetString(buffer, 0, Math.Max(0, read)).Trim();

            return greeting.StartsWith("* OK", StringComparison.OrdinalIgnoreCase)
                ? (true, $"已连接 {account.ImapHost}（{greeting}）")
                : (false, $"端口有响应但不像 IMAP：{greeting}");
        }
        catch (Exception ex)
        {
            return (false, $"连接失败：{ex.Message}");
        }
    }
}
