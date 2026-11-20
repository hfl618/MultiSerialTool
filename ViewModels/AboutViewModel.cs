using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using MultiSerialTool.Commands;
using MultiSerialTool.Models;
using MultiSerialTool.Models.Localization;
using MultiSerialTool.Services;
using MultiSerialTool.Services.Update;
using MultiSerialTool.ViewModels;

namespace MultiSerialTool.Views;

/// <summary>
/// 版本（关于）页 ViewModel：应用名、当前版本、GitHub 地址、可滚动的历史版本列表，
/// 以及更新检查与下载。changelog 以嵌入资源形式按语言加载，语言切换时自动重载。
/// </summary>
public class AboutViewModel : ViewModelBase, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private bool _isBusy;
    private bool _hasUpdate;
    private bool _disposed;
    private string _statusText = string.Empty;
    private string _progressText = string.Empty;

    /// <summary>完整应用名：MultiSerialTool-版本号-hfl</summary>
    public string AppName => AppInfo.FullName;

    /// <summary>当前程序集版本号（三段）</summary>
    public string Version => AppInfo.Version;

    /// <summary>GitHub 仓库地址（发布前替换为真实地址）</summary>
    public string GitHubUrl { get; } = "https://github.com/hfl618/MultiSerialTool";

    /// <summary>历史版本列表，已按版本号倒序，最新在最上</summary>
    public ObservableCollection<ReleaseInfo> Releases { get; } = new();

    /// <summary>检查 / 下载进行中，按钮置灰</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
                return;

            CheckCommand.RaiseCanExecuteChanged();
            UpdateCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>是否发现可下载的新版本</summary>
    public bool HasUpdate
    {
        get => _hasUpdate;
        private set
        {
            if (SetProperty(ref _hasUpdate, value))
                UpdateCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>检查结果的一行说明</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>下载进度，空闲时为空</summary>
    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public RelayCommand CheckCommand { get; }

    public RelayCommand UpdateCommand { get; }

    public AboutViewModel()
    {
        CheckCommand = new RelayCommand(() => _ = CheckAsync(), () => !IsBusy);
        UpdateCommand = new RelayCommand(() => _ = UpdateAsync(), () => !IsBusy && HasUpdate);

        LocalizationService.Instance.PropertyChanged += OnLanguageChanged;
        UpdateService.Current.ResultChanged += OnUpdateResultChanged;
        LoadReleases();

        if (UpdateService.Current.LastResult is { } last)
            ApplyResult(last);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        LocalizationService.Instance.PropertyChanged -= OnLanguageChanged;
        UpdateService.Current.ResultChanged -= OnUpdateResultChanged;
    }

    /// <summary>语言切换后重新加载对应语言的 changelog</summary>
    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        try
        {
            LoadReleases();

            // 状态文字是本地化字符串，跟着语言重刷一次
            if (UpdateService.Current.LastResult is { } last)
                ApplyResult(last);
        }
        catch
        {
            // 重载失败保留当前列表，不打断用户
        }
    }

    private void OnUpdateResultChanged(object? sender, UpdateCheckResult result)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            ApplyResult(result);
        else
            dispatcher.BeginInvoke(new Action(() => ApplyResult(result)));
    }

    private void ApplyResult(UpdateCheckResult result)
    {
        if (!string.IsNullOrEmpty(result.Error))
        {
            HasUpdate = false;
            StatusText = Loc.T("更新检查失败，请稍后重试");
            return;
        }

        if (!result.Available)
        {
            HasUpdate = false;
            StatusText = Loc.T("已是最新版本");
            return;
        }

        HasUpdate = true;
        string text = Loc.T("发现新版本：{0}", result.Latest);
        if (!result.Force)
        {
            StatusText = text;
            return;
        }

        string forced = Loc.T("此更新为强制版本");
        StatusText = $"{text}  {forced}";
    }

    private async Task CheckAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        ProgressText = string.Empty;
        StatusText = Loc.T("正在检查更新…");

        try
        {
            var result = await UpdateService.Current.CheckAsync();
            ApplyResult(result);
        }
        catch (Exception ex)
        {
            StatusText = Loc.T("更新检查失败，请稍后重试");
            LogService.Current.Error("Update", "Check", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task UpdateAsync()
    {
        if (UpdateService.Current.LastResult is not { Available: true, Manifest: not null } result)
            return;

        var answer = System.Windows.MessageBox.Show(
            Loc.T("更新将关闭程序并断开串口连接，是否继续？"),
            Loc.T("立即更新"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK)
            return;

        IsBusy = true;
        StatusText = Loc.T("正在下载更新包…");

        try
        {
            var status = new Progress<string>(text => StatusText = text);
            var progress = new Progress<double>(value =>
                ProgressText = Loc.T("正在下载 {0}%", (int)Math.Round(value * 100)));

            await UpdateApplier.ApplyAsync(result, status, progress);

            // 正常走到这里说明替换脚本已启动，程序即将退出
            StatusText = Loc.T("更新即将重启程序…");
        }
        catch (Exception ex)
        {
            ProgressText = string.Empty;
            StatusText = $"{Loc.T("更新失败，请稍后重试")}（{ex.Message}）";
            LogService.Current.Error("Update", "Apply", ex.Message);
            IsBusy = false;
        }
    }

    private void LoadReleases()
    {
        var list = TryLoad(LocalizationService.Instance.CurrentCulture)
                   ?? TryLoad("zh-CN")
                   ?? new List<ReleaseInfo>();

        // 版本号倒序：新版本在上；无法解析为 System.Version 时回退字符串比较
        list.Sort((a, b) => UpdateVersions.Compare(b.Version, a.Version));

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
}
