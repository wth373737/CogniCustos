using System.Collections.ObjectModel;
using CogniCustos.Ui.Mvvm;
using CogniCustos.Ui.Localization;

namespace CogniCustos.Ui.ViewModels;

/// <summary>One mailbox folder found in Thunderbird, with its read switch.</summary>
public sealed class MailFolderRow : ObservableObject
{
    private bool _enabled;

    public MailFolderRow(string account, string folder, string mailbox, string server,
        double megabytes, bool enabled)
    {
        Account = account;
        Folder = folder;
        Mailbox = mailbox.Length > 0 ? mailbox : account;
        Server = server;
        SizeText = megabytes >= 1 ? $"{megabytes:0.#} MB" : "< 1 MB";
        _enabled = enabled;
    }

    public string Account { get; }
    public string Folder { get; }

    /// <summary>The real mailbox address (falls back to the directory name).</summary>
    public string Mailbox { get; }

    public string Server { get; }
    public string SizeText { get; }

    /// <summary>Mailbox + folder: what the person recognises.</summary>
    public string Display => $"{Mailbox} / {Folder}";

    /// <summary>The server directory, only when it says something the address does not.</summary>
    public string DirectoryNote => Server == Mailbox || Server.Length == 0 ? string.Empty : Server;

    public bool HasDirectoryNote => DirectoryNote.Length > 0;

    /// <summary>Flipping this reads or ignores the folder from the next fetch on.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!Set(ref _enabled, value)) return;
            Toggled?.Invoke(this, value);
        }
    }

    public event Action<MailFolderRow, bool>? Toggled;
}

/// <summary>One mailbox: a switch for all of its folders, and a "only this one" button.</summary>
public sealed class MailboxRow : ObservableObject
{
    private bool _enabled;

    public MailboxRow(string mailbox, string server, bool enabled, int folderCount)
    {
        Mailbox = mailbox;
        Server = server;
        FolderCount = folderCount;
        _enabled = enabled;

        OnlyCommand = new RelayCommand(() => OnlyRequested?.Invoke(Mailbox));
    }

    public string Mailbox { get; }
    public string Server { get; }
    public int FolderCount { get; }
    public string FolderSummary => $"{FolderCount} " + Localizer.Get("Settings.Email.FolderUnit");

    public void RefreshText() => OnPropertyChanged(nameof(FolderSummary));

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!Set(ref _enabled, value)) return;
            Toggled?.Invoke(this, value);
        }
    }

    /// <summary>The folders this mailbox contributes (so the page can nest them).</summary>
    public ObservableCollection<MailFolderRow> Folders { get; } = [];

    public RelayCommand OnlyCommand { get; }

    public event Action<MailboxRow, bool>? Toggled;
    public event Action<string>? OnlyRequested;
}

/// <summary>One account row in 邮件抓取配置.</summary>
public sealed class EmailAccountRow : ObservableObject
{
    private string _status;
    private readonly bool _unreadOnly;
    private readonly int _lookbackDays;

    public EmailAccountRow(EmailAccountInfo account)
    {
        Address = account.Address;
        Host = account.Host;
        Port = account.Port;
        _lookbackDays = account.LookbackDays;
        _unreadOnly = account.UnreadOnly;
        _status = Localizer.Get("Settings.Email.Unverified");
        DeleteCommand = new RelayCommand(() => DeleteRequested?.Invoke(this));
        TestCommand = new RelayCommand(() => TestRequested?.Invoke(this));
    }

    public string Address { get; }
    public string Host { get; }
    public int Port { get; }
    public string Server => $"{Host}:{Port}";

    /// <summary>Built from resources, so it re-renders when the language changes.</summary>
    public string Scope => string.Format(Localizer.Get("Settings.Email.ScopeFormat"), _lookbackDays,
        Localizer.Get(_unreadOnly ? "Settings.Email.UnreadOnly" : "Settings.Email.All"));

    public void RefreshText() => OnPropertyChanged(nameof(Scope));

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public RelayCommand DeleteCommand { get; }
    public RelayCommand TestCommand { get; }

    public event Action<EmailAccountRow>? DeleteRequested;
    public event Action<EmailAccountRow>? TestRequested;
}

/// <summary>
/// 邮件抓取配置: the account list, an add form, and a real connection test. The shape
/// follows the mail plugin: address + host + a secret that is never shown again, with a
/// status that comes from actually reaching the server.
/// </summary>
public sealed class EmailRetrievalViewModel : ObservableObject
{
    private string _address = string.Empty;
    private string _host = string.Empty;
    private string _port = "993";
    private string _secret = string.Empty;
    private string _days = "7";
    private bool _unreadOnly = true;
    private string _addStatus = string.Empty;

    public EmailRetrievalViewModel()
    {
        AddCommand = new RelayCommand(() =>
        {
            if (!int.TryParse(Port, out var port)) port = 993;
            if (!int.TryParse(Days, out var days)) days = 7;
            AddRequested?.Invoke(Address.Trim(), Host.Trim(), port, Secret, days, UnreadOnly);
        });
        ImportCommand = new RelayCommand(() => ImportRequested?.Invoke());
        RescanCommand = new RelayCommand(() => RescanRequested?.Invoke());
        ToggleAdvancedCommand = new RelayCommand(() => AdvancedOpen = !AdvancedOpen);
    }

    public RelayCommand RescanCommand { get; }
    public RelayCommand ToggleAdvancedCommand { get; }

    public event Action? RescanRequested;
    public event Action<bool>? UseThunderbirdRequested;

    private bool _useThunderbird = true;
    private string _thunderbirdStatus = string.Empty;
    private bool _advancedOpen;
    private bool _thunderbirdAvailable;

    /// <summary>Read mail from Thunderbird's store: the default, because it always works.</summary>
    public bool UseThunderbird
    {
        get => _useThunderbird;
        private set => Set(ref _useThunderbird, value);
    }

    public RelayCommand ToggleThunderbirdCommand => _toggleThunderbird ??= new RelayCommand(() =>
        UseThunderbirdRequested?.Invoke(!UseThunderbird));

    private RelayCommand? _toggleThunderbird;

    /// <summary>"3 个文件夹 · 最大 943 MB" or "没有找到 Thunderbird 配置".</summary>
    public string ThunderbirdStatus
    {
        get => _thunderbirdStatus;
        private set => Set(ref _thunderbirdStatus, value);
    }

    public bool ThunderbirdAvailable
    {
        get => _thunderbirdAvailable;
        private set => Set(ref _thunderbirdAvailable, value);
    }

    /// <summary>The manual IMAP form is here for unusual setups; it stays collapsed.</summary>
    public bool AdvancedOpen
    {
        get => _advancedOpen;
        private set => Set(ref _advancedOpen, value);
    }

    public string AdvancedLabel => Localizer.Get("Settings.Email.Advanced");
    public string ThunderbirdHeader => Localizer.Get("Settings.Email.Thunderbird");
    public string ThunderbirdNote => Localizer.Get("Settings.Email.ThunderbirdNote");
    public string UseThunderbirdLabel => Localizer.Get("Settings.Email.UseThunderbird");
    public string RescanLabel => Localizer.Get("Settings.Email.Rescan");

    public void SetThunderbirdStatus(string status, bool available, bool enabled)
    {
        ThunderbirdStatus = status;
        ThunderbirdAvailable = available;
        UseThunderbird = enabled;
    }

    public ObservableCollection<EmailAccountRow> Accounts { get; } = [];

    /// <summary>Mailbox folders discovered in Thunderbird, each with a switch.</summary>
    public ObservableCollection<MailFolderRow> MailFolders { get; } = [];

    /// <summary>Mailboxes (not servers): one switch that drives every folder of that mailbox.</summary>
    public ObservableCollection<MailboxRow> Mailboxes { get; } = [];

    public event Action<string, bool>? MailboxToggleRequested;

    public event Action<string, string, bool>? MailFolderToggleRequested;

    public void SetMailFolders(IEnumerable<MailFolderInfo> folders)
    {
        MailFolders.Clear();
        foreach (var folder in folders)
        {
            var row = new MailFolderRow(folder.Account, folder.Folder, folder.Mailbox, folder.Server,
                folder.Megabytes, folder.Enabled);
            row.Toggled += (r, enabled) => MailFolderToggleRequested?.Invoke(r.Account, r.Folder, enabled);
            MailFolders.Add(row);
        }

        OnPropertyChanged(nameof(HasMailFolders));
    }

    public bool HasMailFolders => MailFolders.Count > 0;

    public string OnlyOneHint => Localizer.Get("Settings.Email.OnlyOneHint");

    /// <summary>"只读 wutianhao@student.usm.my"：one click keeps that mailbox and drops the rest.</summary>
    public event Action<string>? OnlyMailboxRequested;

    public void BuildMailboxes()
    {
        Mailboxes.Clear();

        foreach (var group in MailFolders.GroupBy(f => f.Mailbox))
        {
            var row = new MailboxRow(group.Key,
                group.Select(f => f.Server).Distinct().FirstOrDefault() ?? "",
                group.All(f => f.Enabled),
                group.Count(f => f.Enabled));

            row.Toggled += (_, enabled) => MailboxToggleRequested?.Invoke(row.Mailbox, enabled);
            row.OnlyRequested += mailbox => OnlyMailboxRequested?.Invoke(mailbox);
            foreach (var folder in group) row.Folders.Add(folder);

            Mailboxes.Add(row);
        }

        OnPropertyChanged(nameof(HasMailboxes));
    }

    public bool HasMailboxes => Mailboxes.Count > 0;

    public RelayCommand AddCommand { get; }
    public RelayCommand ImportCommand { get; }

    public event Action<string, string, int, string, int, bool>? AddRequested;
    public event Action? ImportRequested;
    public event Action<string>? RemoveRequested;
    public event Action<string>? TestRequested;

    public string Address
    {
        get => _address;
        set => Set(ref _address, value);
    }

    public string Host
    {
        get => _host;
        set => Set(ref _host, value);
    }

    public string Port
    {
        get => _port;
        set => Set(ref _port, value);
    }

    /// <summary>An app password: the box hides it and it is never read back.</summary>
    public string Secret
    {
        get => _secret;
        set => Set(ref _secret, value);
    }

    public string Days
    {
        get => _days;
        set => Set(ref _days, value);
    }

    public bool UnreadOnly
    {
        get => _unreadOnly;
        set => Set(ref _unreadOnly, value);
    }

    public string AddStatus
    {
        get => _addStatus;
        private set
        {
            if (!Set(ref _addStatus, value)) return;
            OnPropertyChanged(nameof(HasAddStatus));
        }
    }

    public bool HasAddStatus => _addStatus.Length > 0;

    public void ShowStatus(string text) => AddStatus = text;

    public void SetAccounts(IEnumerable<EmailAccountInfo> accounts)
    {
        Accounts.Clear();
        foreach (var account in accounts)
        {
            var row = new EmailAccountRow(account);

            // the row's buttons speak for the whole list, so the shell only subscribes once
            row.DeleteRequested += r => RemoveRequested?.Invoke(r.Address);
            row.TestRequested += r => TestRequested?.Invoke(r.Address);
            Accounts.Add(row);
        }

        OnPropertyChanged(nameof(HasAccounts));
        OnPropertyChanged(nameof(EmptyNote));
    }

    public bool HasAccounts => Accounts.Count > 0;

    /// <summary>Re-render every resource-derived string after a language switch.</summary>
    public void RefreshText()
    {
        OnPropertyChanged(nameof(AdvancedLabel));
        OnPropertyChanged(nameof(ThunderbirdHeader));
        OnPropertyChanged(nameof(ThunderbirdNote));
        OnPropertyChanged(nameof(UseThunderbirdLabel));
        OnPropertyChanged(nameof(RescanLabel));
        OnPropertyChanged(nameof(OnlyOneHint));
        OnPropertyChanged(nameof(EmptyNote));

        foreach (var row in Accounts) row.RefreshText();
        foreach (var mailbox in Mailboxes) mailbox.RefreshText();
    }

    public string EmptyNote => Localizer.Get("Settings.Email.EmptyNote");

    public void ClearForm()
    {
        Secret = string.Empty;
    }
}

/// <summary>One row in 网页浏览配置: a page, its domain's approval, and a read-once check.</summary>
public sealed class WebSourceRow : ObservableObject
{
    private bool _approved;
    private string _verdict = string.Empty;

    public WebSourceRow(string url, bool approved = false)
    {
        Url = url;
        Host = HostOf(url);
        _approved = approved;

        DeleteCommand = new RelayCommand(() => DeleteRequested?.Invoke(this));
        AllowCommand = new RelayCommand(() => AllowRequested?.Invoke(this));
        RevokeCommand = new RelayCommand(() => RevokeRequested?.Invoke(this), () => Approved);
        ReadOnceCommand = new RelayCommand(() => ReadOnceRequested?.Invoke(this));
    }

    public string Url { get; }
    public string Host { get; }

    /// <summary>The host of a URL; what an approval names.</summary>
    public static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
    public string Access => Localizer.Get("Settings.Web.Access");

    /// <summary>Whether this page's domain may be fetched at all.</summary>
    public bool Approved
    {
        get => _approved;
        private set
        {
            if (!Set(ref _approved, value)) return;
            OnPropertyChanged(nameof(ApprovedLabel));
            OnPropertyChanged(nameof(ApprovedBrush));
            RevokeCommand.RaiseCanExecuteChanged();
        }
    }

    public string ApprovedLabel => Localizer.Get(Approved ? "Settings.Web.Allowed" : "Settings.Web.NotAllowed");

    public string ApprovedBrush => Approved ? "#FF2E6B5A" : "#FF8A5A1E";

    /// <summary>What the last read returned: read / needs login / not allowed / failed.</summary>
    public string Verdict
    {
        get => _verdict;
        private set
        {
            if (!Set(ref _verdict, value)) return;
            OnPropertyChanged(nameof(HasVerdict));
        }
    }

    public bool HasVerdict => _verdict.Length > 0;

    public void SetApproved(bool approved) => Approved = approved;

    public void ShowVerdict(string verdict) => Verdict = verdict;

    public RelayCommand DeleteCommand { get; }
    public RelayCommand AllowCommand { get; }
    public RelayCommand RevokeCommand { get; }
    public RelayCommand ReadOnceCommand { get; }

    public event Action<WebSourceRow>? DeleteRequested;
    public event Action<WebSourceRow>? AllowRequested;
    public event Action<WebSourceRow>? RevokeRequested;
    public event Action<WebSourceRow>? ReadOnceRequested;

    public void RefreshText()
    {
        OnPropertyChanged(nameof(Access));
        OnPropertyChanged(nameof(ApprovedLabel));
    }
}

/// <summary>
/// 网页浏览配置: the pages the daily fetch reads. Mirrors the cookie plugin's vocabulary
/// (per-site entries, read-only access); cookies come from the browser plugin, so this
/// page only lists what to read and says so.
/// </summary>
public sealed class WebBrowsingViewModel : ObservableObject
{
    private string _newUrl = string.Empty;
    private string _status = string.Empty;

    public WebBrowsingViewModel()
    {
        AddCommand = new RelayCommand(() => AddRequested?.Invoke(NewUrl.Trim()));
    }

    public ObservableCollection<WebSourceRow> Sources { get; } = [];

    public RelayCommand AddCommand { get; }

    public event Action<string>? AddRequested;
    public event Action<string>? RemoveRequested;
    public event Action<WebSourceRow>? AllowRequested;
    public event Action<WebSourceRow>? RevokeRequested;
    public event Action<WebSourceRow>? ReadOnceRequested;

    public string NewUrl
    {
        get => _newUrl;
        set => Set(ref _newUrl, value);
    }

    /// <summary>The allow-list, so the rows can show which domain is approved.</summary>
    public IReadOnlyList<string> AllowedDomains { get; private set; } = [];

    public void SetAllowedDomains(IEnumerable<string> domains) => AllowedDomains = domains.ToList();

    public bool IsDomainAllowed(string host) =>
        AllowedDomains.Any(d => DomainCovers(d, host));

    /// <summary>An approved parent domain covers its subdomains; a subdomain does not cover its parent.</summary>
    private static bool DomainCovers(string allowed, string host)
    {
        var a = (allowed ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
        var h = (host ?? string.Empty).ToLowerInvariant();
        return a.Length > 0 && (h == a || h.EndsWith("." + a, StringComparison.Ordinal));
    }

    public string Status
    {
        get => _status;
        private set
        {
            if (!Set(ref _status, value)) return;
            OnPropertyChanged(nameof(HasStatus));
        }
    }

    public bool HasStatus => _status.Length > 0;

    public void ShowStatus(string text) => Status = text;

    public void SetSources(IEnumerable<string> urls)
    {
        Sources.Clear();
        foreach (var url in urls)
        {
            var row = new WebSourceRow(url, IsDomainAllowed(WebSourceRow.HostOf(url)));
            row.DeleteRequested += r => RemoveRequested?.Invoke(r.Url);
            row.AllowRequested += r => AllowRequested?.Invoke(r);
            row.RevokeRequested += r => RevokeRequested?.Invoke(r);
            row.ReadOnceRequested += r => ReadOnceRequested?.Invoke(r);
            Sources.Add(row);
        }

        OnPropertyChanged(nameof(HasSources));
        OnPropertyChanged(nameof(EmptyNote));
    }

    public bool HasSources => Sources.Count > 0;

    public string EmptyNote => Localizer.Get("Settings.Web.EmptyNote");

    /// <summary>Re-render resource-derived strings after a language switch.</summary>

    public void RefreshText()
    {
        OnPropertyChanged(nameof(EmptyNote));
        OnPropertyChanged(nameof(Status));
    }
}
