using CogniCustos.Ui.Mvvm;

namespace CogniCustos.Ui.Localization;

/// <summary>
/// All user-facing text, in one place, switchable at runtime.
/// Every property is computed from <see cref="IsChinese"/>, so toggling the
/// language raises one "all properties changed" event and every binding refreshes.
/// </summary>
public sealed class Strings : ObservableObject
{
    private bool _isChinese;

    public bool IsChinese
    {
        get => _isChinese;
        set
        {
            if (!Set(ref _isChinese, value)) return;
            OnAllPropertiesChanged();
        }
    }

    public string LanguageButton => _isChinese ? "中" : "En";

    public string Brand => "CogniCustos";
    public string Home => _isChinese ? "主页" : "Home";
    public string SearchHint => _isChinese ? "搜索或跳转…" : "Search or jump to…";
    public string EnterText => _isChinese ? "输入文字" : "Enter text";

    public string CardToday => "26/11/20";
    public string CardPending => _isChinese ? "待办任务" : "Pending Tasks";
    public string CardNote => _isChinese ? "便签" : "note";

    public string ReasoningRecord => _isChinese ? "推理记录" : "Reasoning Record";
    public string Thinking => _isChinese ? "小 AI 正在思考…" : "Little AI is thinking";

    public string TileKnowledge => _isChinese ? "知识库" : "Knowledge base";
    public string TileData => _isChinese ? "数据库" : "Data base";
    public string TileSandbox => _isChinese ? "沙箱" : "Sandbox";
    public string TileAddFiles => _isChinese ? "添加文件" : "Add files";
    public string TileAddAddress => _isChinese ? "添加地址" : "Add address";
    public string TileCheckMail => _isChinese ? "检查邮件" : "Check email";
    public string TileSearchGraphical => _isChinese ? "图形检索" : "Search graphical";

    public string FutureAreaText => _isChinese
        ? "一块为后续功能预留的区域"
        : "An area designed for additional future features";

    public string SettingsTitle => _isChinese ? "设置页" : "Settings Page";
    public string SettingsPlaceholder => _isChinese ? "设置内容区（尚未实现）" : "Settings content area (not implemented yet)";
    public string HelpTitle => _isChinese ? "帮助" : "Help";
    public string HelpPlaceholder => _isChinese ? "帮助内容面试装区" : "Help content placeholder";

    /// <summary>Settings navigation, in the order of the sketch.</summary>
    public IReadOnlyList<string> SettingsSections => _isChinese
        ? new[]
        {
            "通用", "模型配置", "邮件抓取配置", "聊天抓取配置", "网页浏览配置",
            "学生网络配置", "提示词配置", "快捷键设置", "隐私设置", "知识库配置",
            "数据库配置", "沙箱配置", "存储与数据", "软件详情", "通知设置", "离线与网络",
        }
        : new[]
        {
            "General", "Model Configuration", "Email Retrieval Configuration",
            "Chat Retrieval Configuration", "Website Browsing Configuration",
            "Student Network Configuration", "Prompt Configuration", "Hotkey Settings",
            "Privacy Settings", "Knowledge Base Configuration", "Database Configuration",
            "Sandbox Configuration", "Storage & Data", "Software Details",
            "Notification Setting", "Offline & Network",
        };
}
