using System.Collections.ObjectModel;
using CogniCustos.Ui.Mvvm;
using CogniCustos.Ui.Localization;

namespace CogniCustos.Ui.ViewModels;

/// <summary>One account row in 邮件抓取配置.</summary>
public sealed class EmailAccountRow : ObservableObject
{
    private string _status;

    public EmailAccountRow(EmailAccountInfo account)
    {
        Address = account.Address;
        Host = account.Host;
        Port = account.Port;
        Scope = $"最近 {account.LookbackDays} 天 · {(account.UnreadOnly ? "只抓未读" : "全部")}";
        _status = "未验证";
        DeleteCommand = new RelayCommand(() => DeleteRequested?.Invoke(this));
        TestCommand = new RelayCommand(() => TestRequested?.Invoke(this));
    }

    public string Address { get; }
    public string Host { get; }
    public int Port { get; }
    public string Server => $"{Host}:{Port}";
    public string Scope { get; }

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

    public string EmptyNote => "还没有账号：添加一个邮箱后，每日抓取会读取最近若干天的新邮件。";

    public void ClearForm()
    {
        Secret = string.Empty;
    }
}

/// <summary>One row in 网页浏览配置.</summary>
public sealed class WebSourceRow : ObservableObject
{
    public WebSourceRow(string url)
    {
        Url = url;
        Host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
        DeleteCommand = new RelayCommand(() => DeleteRequested?.Invoke(this));
    }

    public string Url { get; }
    public string Host { get; }
    public string Access => "只读 GET · 表单提交被禁止";

    public RelayCommand DeleteCommand { get; }

    public event Action<WebSourceRow>? DeleteRequested;
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

    public string NewUrl
    {
        get => _newUrl;
        set => Set(ref _newUrl, value);
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
            var row = new WebSourceRow(url);
            row.DeleteRequested += r => RemoveRequested?.Invoke(r.Url);
            Sources.Add(row);
        }

        OnPropertyChanged(nameof(HasSources));
        OnPropertyChanged(nameof(EmptyNote));
    }

    public bool HasSources => Sources.Count > 0;

    public string EmptyNote => "还没有网址：添加通知页或教务页后，每日抓取只读取正文，不做任何写操作。";
}
