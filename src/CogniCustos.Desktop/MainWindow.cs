using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CogniCustos.Adapters;
using CogniCustos.Contracts;
using CogniCustos.Core;

namespace CogniCustos.Desktop;

/// <summary>
/// Deliberately plain UI. It exists to exercise the architecture end to end:
/// chat -> tool registry -> sandbox/proposal -> confirmation -> main database.
/// The visual design is a placeholder and is expected to be replaced.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly CogniCustosRuntime _rt;
    private readonly string _statePath;
    private readonly string _settingsPath;
    private AppSettings _settings;
    private OllamaModelProvider? _ollama;
    private readonly ReminderService _reminders;

    private readonly TextBox _chat = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        FontFamily = new FontFamily("Consolas, Microsoft YaHei UI, monospace"), FontSize = 13
    };
    private readonly TextBox _input = new() { PlaceholderText = "问点什么，或 /tools /tasks /sandbox /pending /add /search /models", Height = 40 };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Foreground = Brushes.Gray };
    private readonly TextBox _sandboxView = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas, Microsoft YaHei UI, monospace"), FontSize = 12 };
    private readonly ListBox _proposals = new();
    private readonly ListBox _tasks = new();
    private readonly ListBox _tools = new();
    private readonly TextBox _scheduleView = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas, Microsoft YaHei UI, monospace"), FontSize = 12 };
    private readonly TextBox _scheduleDate = new() { PlaceholderText = "yyyy-MM-dd（留空=今天）", Width = 140 };
    private readonly TextBox _taskTitle = new() { PlaceholderText = "新任务标题" };
    private readonly TextBox _taskDue = new() { PlaceholderText = "截止（如 2026-11-20）", Width = 160 };
    private Button _sendButton = new() { Content = "发送", Width = 80 };
    private bool _busy;

    public MainWindow()
    {
        var home = Environment.GetEnvironmentVariable("COGNICUSTOS_HOME");
        home ??= Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(home);
        _statePath = Path.Combine(home, "state");
        Directory.CreateDirectory(_statePath);
        _settingsPath = Path.Combine(_statePath, "settings.json");
        _settings = SettingsStore.Load(_settingsPath);

        Title = "CogniCustos — 可运行骨架（架构验证版）";
        Width = 1280;
        Height = 820;
        Background = new SolidColorBrush(Color.Parse("#F7F7F9"));

        _rt = CogniCustosRuntime.Create(home, msg => AppendLine("[log] " + msg));
        TryWireOllama();
        TryWireCloud();
        _rt.AddTool(new PortalSidecarTool(_settings.PortalSidecarUrl));

        _reminders = new ReminderService(_rt.Tasks, TimeSpan.FromSeconds(20));
        _reminders.ReminderRaised += text => AppendLine("🔔 " + text);

        Content = BuildLayout();
        AppendLine("CogniCustos 骨架已启动。");
        AppendLine($"数据目录：{home}");
        AppendLine("架构：聊天 -> 工具注册表 -> 沙箱/提议 -> 确认处理器 -> 主数据库（SQLite）");
        AppendLine("提示：输入 /tools 看已注册能力；输入 /add 买牛奶 会生成一条待确认提议。");
        RefreshAll();

        // Dev aid: COGNICUSTOS_SHOT=<path> renders this window once and exits.
        // Renders the window itself, so nothing outside the app is captured.
        Opened += async (_, _) =>
        {
            var shot = Environment.GetEnvironmentVariable("COGNICUSTOS_SHOT");
            if (string.IsNullOrWhiteSpace(shot)) return;
            await Task.Delay(1500);
            try
            {
                var w = Math.Max(1, (int)ClientSize.Width);
                var h = Math.Max(1, (int)ClientSize.Height);
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
                bitmap.Render(this);
                bitmap.Save(shot, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                AppendLine("[dev] 已渲染窗口截图：" + shot);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("shot failed: " + ex);
            }
            Close();
        };
    }

    private void TryWireOllama()
    {
        _ollama = new OllamaModelProvider(_settings.OllamaBaseUrl, _settings.OllamaModel);
        _rt.AddModel(_ollama);
    }

    private void TryWireCloud()
    {
        if (string.IsNullOrWhiteSpace(_settings.CloudBaseUrl) || string.IsNullOrWhiteSpace(_settings.CloudModel))
            return;
        _rt.AddCloud(new OpenAiCompatibleProvider(_settings.CloudBaseUrl!, _settings.CloudModel!, _settings.CloudApiKey));
    }

    private Control BuildLayout()
    {
        var root = new DockPanel();

        // ---- top bar ----
        var top = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FFFFFF")),
            BorderBrush = new SolidColorBrush(Color.Parse("#E3E3E8")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 8),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "CogniCustos", FontSize = 16, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                    _status,
                    MakeButton("刷新", () => RefreshAll()),
                    MakeButton("沙箱", () => ShowSandbox()),
                    MakeButton("索引 README", () => _ = IndexReadmeAsync()),
                    MakeButton("重连模型", () => Reconnect()),
                }
            }
        };
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        // ---- three columns ----
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("230,*,430"), Margin = new Thickness(8) };

        // left: tools
        var left = new DockPanel { Margin = new Thickness(0, 0, 8, 0) };
        var leftHeader = new TextBlock { Text = "已注册能力（工具层）", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(leftHeader, Dock.Top);
        left.Children.Add(leftHeader);
        left.Children.Add(new ScrollViewer { Content = _tools });
        _tools.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((s, _) => new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(2, 3) });
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        // center: chat
        var center = new DockPanel();
        var inputRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 6, 0, 0) };
        Grid.SetColumn(_input, 0);
        Grid.SetColumn(_sendButton, 1);
        _sendButton.Margin = new Thickness(6, 0, 0, 0);
        _sendButton.Click += async (_, _) => await SendAsync();
        _input.KeyDown += async (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter && !e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift))
            {
                e.Handled = true;
                await SendAsync();
            }
        };
        inputRow.Children.Add(_input);
        inputRow.Children.Add(_sendButton);
        DockPanel.SetDock(inputRow, Dock.Bottom);
        center.Children.Add(inputRow);
        center.Children.Add(new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#E3E3E8")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Child = _chat
        });
        Grid.SetColumn(center, 1);
        grid.Children.Add(center);

        // right: tabs
        var tabs = new TabControl { Margin = new Thickness(8, 0, 0, 0) };
        tabs.Items.Add(BuildProposalTab());
        tabs.Items.Add(BuildTaskTab());
        tabs.Items.Add(BuildScheduleTab());
        tabs.Items.Add(BuildSandboxTab());
        tabs.Items.Add(BuildSettingsTab());
        Grid.SetColumn(tabs, 2);
        grid.Children.Add(tabs);

        root.Children.Add(grid);
        return root;
    }

    private TabItem BuildProposalTab()
    {
        var panel = new DockPanel();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0),
            Children =
            {
                MakeButton("批准", () => Apply(approve: true)),
                MakeButton("驳回", () => Apply(approve: false)),
                MakeButton("刷新", () => RefreshAll()),
            }
        };
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(new ScrollViewer { Content = _proposals });
        _proposals.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Proposal>((p, _) => new TextBlock
        {
            Text = $"{p.Title}\n  {p.Summary}   → {p.Target}   [{p.Status}]",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(2, 4)
        });
        return new TabItem { Header = "待确认提议", Content = panel };
    }

    private TabItem BuildTaskTab()
    {
        var panel = new DockPanel();
        var addRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 8, 0, 0),
            Children = { _taskTitle, _taskDue, MakeButton("直接建（不经 AI）", () => _ = AddTaskDirectAsync()) }
        };
        _taskTitle.Width = 200;
        DockPanel.SetDock(addRow, Dock.Bottom);
        panel.Children.Add(addRow);
        panel.Children.Add(new ScrollViewer { Content = _tasks });
        _tasks.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<TaskItem>((t, _) => new TextBlock
        {
            Text = $"[{t.Level}] #{(t.Done ? "✓" : "")}{t.Id} {t.Title}{(string.IsNullOrWhiteSpace(t.Due) ? "" : "   (" + t.Due + ")")}",
            FontSize = 12,
            Margin = new Thickness(2, 4)
        });
        return new TabItem { Header = "任务 / 规划", Content = panel };
    }

    private TabItem BuildScheduleTab()
    {
        var panel = new DockPanel();
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 8, 0, 0),
            Children =
            {
                _scheduleDate,
                MakeButton("这一日", () => _ = ShowScheduleAsync(day: true)),
                MakeButton("这一周", () => _ = ShowScheduleAsync(day: false)),
            }
        };
        DockPanel.SetDock(bar, Dock.Bottom);
        panel.Children.Add(bar);
        panel.Children.Add(new ScrollViewer { Content = _scheduleView });
        return new TabItem { Header = "日程", Content = panel };
    }

    private TabItem BuildSandboxTab()
    {
        var panel = new DockPanel();
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0),
            Children = { MakeButton("刷新", () => ShowSandbox()), MakeButton("打开文件", () => OpenInShell(_rt.Sandbox.DocumentPath)) }
        };
        DockPanel.SetDock(bar, Dock.Bottom);
        panel.Children.Add(bar);
        panel.Children.Add(new ScrollViewer { Content = _sandboxView });
        return new TabItem { Header = "AI 沙箱", Content = panel };
    }

    private TabItem BuildSettingsTab()
    {
        var url = new TextBox { Text = _settings.OllamaBaseUrl, Width = 300 };
        var model = new TextBox { Text = _settings.OllamaModel, Width = 200 };
        var sidecar = new TextBox { Text = _settings.PortalSidecarUrl ?? "", Width = 300, PlaceholderText = "留空表示不启用门户链路" };
        var cloudBase = new TextBox { Text = _settings.CloudBaseUrl ?? "", Width = 300, PlaceholderText = "如 http://127.0.0.1:11434/v1（用它就能离线验证出站闸门）" };
        var cloudModel = new TextBox { Text = _settings.CloudModel ?? "", Width = 200 };
        var cloudKey = new TextBox { Text = _settings.CloudApiKey ?? "", Width = 300, PasswordChar = '•', PlaceholderText = "可留空（本地端点通常不需要）" };

        var save = MakeButton("保存并重连", () =>
        {
            _settings = _settings with
            {
                OllamaBaseUrl = url.Text?.Trim() ?? _settings.OllamaBaseUrl,
                OllamaModel = model.Text?.Trim() ?? _settings.OllamaModel,
                PortalSidecarUrl = string.IsNullOrWhiteSpace(sidecar.Text) ? null : sidecar.Text!.Trim(),
                CloudBaseUrl = string.IsNullOrWhiteSpace(cloudBase.Text) ? null : cloudBase.Text!.Trim(),
                CloudModel = string.IsNullOrWhiteSpace(cloudModel.Text) ? null : cloudModel.Text!.Trim(),
                CloudApiKey = string.IsNullOrWhiteSpace(cloudKey.Text) ? null : cloudKey.Text!.Trim()
            };
            SettingsStore.Save(_settingsPath, _settings);
            Reconnect();
        });

        var stack = new StackPanel { Spacing = 8, Margin = new Thickness(0, 10) };
        stack.Children.Add(new TextBlock { Text = "本地模型 · Ollama 地址", FontWeight = FontWeight.SemiBold });
        stack.Children.Add(url);
        stack.Children.Add(new TextBlock { Text = "本地模型 · 模型名", FontWeight = FontWeight.SemiBold });
        stack.Children.Add(model);
        stack.Children.Add(new TextBlock { Text = "云端模型 · OpenAI 兼容端点（出站前必过本地检查）", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        stack.Children.Add(cloudBase);
        stack.Children.Add(cloudModel);
        stack.Children.Add(cloudKey);
        stack.Children.Add(new TextBlock { Text = "门户 sidecar（cookie-whitelist 接口）", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        stack.Children.Add(sidecar);
        stack.Children.Add(save);
        stack.Children.Add(new TextBlock
        {
            Text = "说明：模型按角色选择；门户链路通过本地 HTTP 接口复用既有 JS 实现，未配置时如实报错；"
                 + "cloud.ask 会先跑本地敏感内容检查，被判定敏感就拒绝发送并写入审计。",
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.Gray
        });
        return new TabItem { Header = "设置", Content = new ScrollViewer { Content = stack } };
    }

    private Button MakeButton(string text, Action onClick)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 4) };
        b.Click += (_, _) =>
        {
            try { onClick(); }
            catch (Exception ex) { AppendLine("⚠ " + ex.Message); }
        };
        return b;
    }

    private void Reconnect()
    {
        TryWireOllama();
        TryWireCloud();
        RefreshAll();
        AppendLine($"[log] 模型：{_settings.OllamaBaseUrl} / {_settings.OllamaModel}，可用={_ollama?.IsAvailable}");
        AppendLine(_rt.Cloud is null
            ? "[log] 云端：未配置（cloud.ask 会如实拒绝）"
            : $"[log] 云端：{_rt.Cloud.Endpoint}，可用={_rt.Cloud.IsAvailable}（出站前本地检查）");
    }

    private async Task SendAsync()
    {
        if (_busy) return;
        var text = _input.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        _input.Text = "";
        AppendLine("你：" + text);
        _busy = true;
        _sendButton.IsEnabled = false;
        try
        {
            var reply = await _rt.ChatAsync(text);
            AppendLine("助手：" + reply);
        }
        catch (Exception ex)
        {
            AppendLine("⚠ 调用失败：" + ex.Message);
        }
        finally
        {
            _busy = false;
            _sendButton.IsEnabled = true;
            RefreshAll();
        }
    }

    private async Task AddTaskDirectAsync()
    {
        var title = _taskTitle.Text?.Trim();
        if (string.IsNullOrEmpty(title)) return;
        var due = string.IsNullOrWhiteSpace(_taskDue.Text) ? null : _taskDue.Text!.Trim();
        var id = await _rt.Tasks.AddAsync(title, due);
        AppendLine($"[直接写入] 任务 #{id} 已建（这条路径属于用户，不经 AI）");
        _taskTitle.Text = "";
        _taskDue.Text = "";
        RefreshAll();
    }

    private async Task IndexReadmeAsync()
    {
        var readme = Path.Combine(_rt.Root, "README.md");
        if (!File.Exists(readme))
        {
            AppendLine("⚠ 没找到 README.md，先放一份到 " + _rt.Root);
            return;
        }
        var text = await File.ReadAllTextAsync(readme);
        await _rt.Knowledge.IndexAsync("README.md", text);
        AppendLine($"[知识库] 已索引 README.md（{text.Length} 字符，关键词占位实现）");
        RefreshAll();
    }

    private void Apply(bool approve)
    {
        if (_proposals.SelectedItem is not Proposal p)
        {
            AppendLine("⚠ 先选中一条提议");
            return;
        }
        var result = approve ? _rt.Gate.Approve(p.Id) : _rt.Gate.Reject(p.Id);
        AppendLine((approve ? "[批准] " : "[驳回] ") + result.Content);
        RefreshAll();
    }

    private async Task ShowScheduleAsync(bool day)
    {
        var args = new Dictionary<string, string>();
        var date = _scheduleDate.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(date)) args["date"] = date!;
        var r = await _rt.Tools.InvokeAsync(day ? "schedule.day" : "schedule.week", args);
        _scheduleView.Text = r.Content;
    }

    private void ShowSandbox()
    {
        try
        {
            _sandboxView.Text = File.ReadAllText(_rt.Sandbox.DocumentPath);
        }
        catch (Exception ex)
        {
            _sandboxView.Text = "读取沙箱失败：" + ex.Message;
        }
    }

    private void OpenInShell(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLine("⚠ 打开失败：" + ex.Message);
        }
    }

    private void RefreshAll()
    {
        _tools.ItemsSource = _rt.Tools.All.Select(t =>
        {
            var d = t.Describe();
            return $"{d.Name}  [{d.Kind}]  {d.Description}";
        }).ToList();

        _proposals.ItemsSource = _rt.Gate.All().OrderByDescending(p => p.CreatedAt).ToList();
        _proposals.SelectedItem ??= _rt.Gate.Pending().FirstOrDefault();

        _tasks.ItemsSource = _rt.Tasks.ListAsync().GetAwaiter().GetResult();
        ShowSandbox();
        _ = ShowScheduleAsync(day: true);

        var modelText = _ollama is null
            ? "模型：未配置"
            : $"模型：{_settings.OllamaModel}（{(_ollama.IsAvailable ? "已连接" : "未连接")}）";
        var cloudText = _rt.Cloud is null ? "云端：未配置" : $"云端：{(_rt.Cloud.IsAvailable ? "可用" : "不可达")}";
        _status.Text = $"{modelText} · {cloudText} · 待确认 {_rt.Gate.Pending().Count} 条 · 工具 {_rt.Tools.All.Count} 个";
    }

    private void AppendLine(string line)
    {
        var sb = new StringBuilder(_chat.Text ?? "");
        sb.AppendLine(line);
        var all = sb.ToString();
        if (all.Length > 20000) all = all[^20000..];
        _chat.Text = all;
        _chat.CaretIndex = all.Length;
    }
}
