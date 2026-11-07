using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using MultiSerialTool.Models;
using MultiSerialTool.Models.Localization;
using MultiSerialTool.ViewModels;

namespace MultiSerialTool.Views;

/// <summary>
/// 版本（关于）页 ViewModel：应用名、当前版本、GitHub 地址、可滚动的历史版本列表。
/// changelog 以嵌入资源形式按语言加载，语言切换时自动重载。
/// </summary>
public class AboutViewModel : ViewModelBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>完整应用名：MultiSerialTool-版本号-hfl</summary>
    public string AppName => AppInfo.FullName;

    /// <summary>当前程序集版本号（三段）</summary>
    public string Version => AppInfo.Version;

    /// <summary>GitHub 仓库地址（发布前替换为真实地址）</summary>
    public string GitHubUrl { get; } = "https://github.com/hfl618/MultiSerialTool";

    /// <summary>历史版本列表，已按版本号倒序，最新在最上</summary>
    public ObservableCollection<ReleaseInfo> Releases { get; } = new();

    public AboutViewModel()
    {
        LocalizationService.Instance.PropertyChanged += OnLanguageChanged;
        LoadReleases();
    }

    /// <summary>语言切换后重新加载对应语言的 changelog</summary>
    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        try
        {
            LoadReleases();
        }
        catch
        {
            // 重载失败保留当前列表，不打断用户
        }
    }

    private void LoadReleases()
    {
        var list = TryLoad(LocalizationService.Instance.CurrentCulture)
                   ?? TryLoad("zh-CN")
                   ?? new List<ReleaseInfo>();

        // 版本号倒序：新版本在上；无法解析为 System.Version 时回退字符串比较
        list.Sort((a, b) => CompareVersion(b.Version, a.Version));

        Releases.Clear();
        foreach (var release in list)
            Releases.Add(release);
    }

    private static List<ReleaseInfo>? TryLoad(string culture)
    {
        try
        {
            var path = Path.Combine(
                AppContext.BaseDirectory,
                "Changelog",
                $"changelog.{culture}.json");

            if (!File.Exists(path))
                return null;

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<ReleaseInfo>>(json, JsonOptions);
        }
        catch
        {
            // 文件缺失或损坏返回 null，由调用方回退默认语言
            return null;
        }
    }

    private static int CompareVersion(string x, string y)
    {
        if (System.Version.TryParse(x, out var vx) && System.Version.TryParse(y, out var vy))
            return vx.CompareTo(vy);

        return string.Compare(x, y, StringComparison.Ordinal);
    }
}
