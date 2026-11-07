using MultiSerialTool.Models.Localization;
using MultiSerialTool.ViewModels;

namespace MultiSerialTool.Views;

/// <summary>
/// 版本（关于）页 ViewModel：应用名、版本号、GitHub 地址、更新内容。
/// </summary>
public class AboutViewModel : ViewModelBase
{
    /// <summary>应用名字（品牌名，不翻译）</summary>
    public string AppName => "MultiSerialTool";

    /// <summary>版本号，从程序集读取</summary>
    public string Version { get; }

    /// <summary>GitHub 仓库地址（发布前替换为真实地址）</summary>
    public string GitHubUrl { get; } = "https://github.com/yourname/MultiSerialTool";

    /// <summary>更新内容列表，条目走本地化</summary>
    public IReadOnlyList<string> UpdateNotes { get; }

    public AboutViewModel()
    {
        Version = typeof(AboutViewModel).Assembly.GetName().Version?.ToString() ?? "1.0.0";

        UpdateNotes = new[]
        {
            Loc.T("新增多语言切换，支持中文/英文运行时热切换"),
            Loc.T("支持多串口、TCP、MQTT 协议框架"),
            Loc.T("新增日志目录自定义与恢复默认"),
            Loc.T("优化界面刷新，切换语言不丢失连接与数据")
        };
    }
}
