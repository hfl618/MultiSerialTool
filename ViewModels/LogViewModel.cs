using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using MultiSerialTool.Commands;
using MultiSerialTool.Models.Localization;
using MultiSerialTool.Services;
using MultiSerialTool.Shared.Find;

namespace MultiSerialTool.ViewModels;

/// <summary>
/// 全局调试日志页面 ViewModel：实时流与历史分页浏览、级别/来源/关键字过滤、复制与导出。
/// </summary>
public class LogViewModel : ViewModelBase
{
    private readonly LogService _log = LogService.Current;

    /// <summary>级别过滤中的“全部”选项</summary>
    private const string AllLevels = "全部";

    /// <summary>来源过滤中的“全部来源”选项</summary>
    public const string AllSources = "全部来源";

    /// <summary>过滤选项（“全部”+ 各级别）</summary>
    public IReadOnlyList<string> LevelOptions { get; }
        = new[] { AllLevels, "DEBUG", "INFO", "WARN", "ERROR" };

    // ===== 视图：实时流用 LogService.Entries 的默认视图，历史按页新建视图 =====
    private readonly ICollectionView _liveView;
    private ICollectionView? _historyView;

    /// <summary>当前绑定到列表的视图（历史页为 null 时用实时视图）</summary>
    public ICollectionView EntriesView => _historyView ?? _liveView;

    /// <summary>关键字搜索的防抖计时器：逐字符过滤百万级日志会卡死 UI</summary>
    private readonly DispatcherTimer _searchTimer;

    private string _selectedLevel = AllLevels;
    /// <summary>当前选中的级别过滤</summary>
    public string SelectedLevel
    {
        get => _selectedLevel;
        set
        {
            if (string.IsNullOrEmpty(value) || !SetProperty(ref _selectedLevel, value))
                return;
            RefreshVisible();
        }
    }

    private IReadOnlyList<string> _sourceOptions = new[] { AllSources };
    /// <summary>来源下拉选项（从当前日志中收集）</summary>
    public IReadOnlyList<string> SourceOptions
    {
        get => _sourceOptions;
        private set => SetProperty(ref _sourceOptions, value);
    }

    private string _selectedSource = AllSources;
    /// <summary>当前选中的来源过滤</summary>
    public string SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (string.IsNullOrEmpty(value) || !SetProperty(ref _selectedSource, value))
                return;
            RefreshVisible();
        }
    }

    private string _searchText = string.Empty;
    /// <summary>关键字过滤，支持 | 表示或、\ 表示与（与串口页查找一致）</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? string.Empty))
                return;
            _searchTimer.Stop();
            _searchTimer.Start();
        }
    }

    // ===== 历史日志 =====

    /// <summary>日志文件下拉项；Date 为 null 表示“实时流”</summary>
    public sealed class LogDayOption
    {
        public DateOnly? Date { get; init; }
        /// <summary>该天的日志文件（归档分卷在前，当天当前文件在后）</summary>
        public IReadOnlyList<string> Paths { get; init; } = Array.Empty<string>();
        public string Display { get; init; } = string.Empty;
    }

    private IReadOnlyList<LogDayOption> _fileOptions = Array.Empty<LogDayOption>();

    // 历史分页状态：page 0 = 最新一页
    private int _historyPage;
    private int _historyTotal;
    private int _historyPages = 1;
    private bool _hasOlder;
    /// <summary>加载序号：页面切换后丢弃仍在读取的旧结果</summary>
    private int _loadVersion;

    /// <summary>日志文件下拉选项：第一项为实时流，其后为按天分组的日志文件</summary>
    public IReadOnlyList<LogDayOption> FileOptions
    {
        get => _fileOptions;
        private set => SetProperty(ref _fileOptions, value);
    }

    private LogDayOption? _selectedDay;
    /// <summary>当前浏览的日志文件；Date 为 null 表示实时流</summary>
    public LogDayOption? SelectedDay
    {
        get => _selectedDay;
        set
        {
            if (value is null || !SetProperty(ref _selectedDay, value))
                return;

            _loadVersion++;          // 丢弃仍在读取的旧请求
            if (value.Date is null)
                SwitchToLive();
            else
                LoadHistoryPage(0);
        }
    }

    /// <summary>是否正在浏览历史文件（非实时流）</summary>
    public bool IsHistory => _selectedDay?.Date is not null;

    private string _pageInfo = string.Empty;
    /// <summary>分页信息文本</summary>
    public string PageInfo
    {
        get => _pageInfo;
        private set => SetProperty(ref _pageInfo, value);
    }

    /// <summary>当前日志目录（界面显示用）</summary>
    public string CurrentLogDirectory => _log.LogDirectory;

    public RelayCommand ClearCommand { get; }
    public RelayCommand ClearFileCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand BrowseFolderCommand { get; }
    public RelayCommand ResetFolderCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand CopyAllCommand { get; }
    public RelayCommand LoadOlderCommand { get; }
    public RelayCommand LoadNewerCommand { get; }

    public LogViewModel()
    {
        _liveView = CollectionViewSource.GetDefaultView(_log.Entries);
        _liveView.Filter = FilterEntry;

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            RefreshVisible();
        };

        ClearCommand = new RelayCommand(ClearDisplay, () => !IsHistory);
        ClearFileCommand = new RelayCommand(ClearLogFile);
        OpenFolderCommand = new RelayCommand(OpenLogFolder);
        BrowseFolderCommand = new RelayCommand(BrowseFolder);
        ResetFolderCommand = new RelayCommand(
            () => ApplyDirectory(LogService.DefaultLogDirectory));
        ExportCommand = new RelayCommand(ExportVisible);
        CopyAllCommand = new RelayCommand(CopyAll);
        LoadOlderCommand = new RelayCommand(() => LoadHistoryPage(_historyPage + 1), () => _hasOlder);
        LoadNewerCommand = new RelayCommand(() => LoadHistoryPage(_historyPage - 1), () => _historyPage > 0);

        _log.DirectoryChanged += OnLogDirectoryChanged;
        RefreshFileOptions();
        RefreshSourceOptions();
    }

    /// <summary>每次打开日志页时调用：刷新文件列表与来源列表</summary>
    public void OnPageActivated()
    {
        RefreshFileOptions();
        RefreshSourceOptions();
    }

    // ===================== 过滤 =====================

    private bool FilterEntry(object item)
    {
        if (item is not DebugLogEntry entry)
            return false;

        if (!string.Equals(_selectedLevel, AllLevels, StringComparison.Ordinal)
            && !string.Equals(entry.LevelText, _selectedLevel, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.Equals(_selectedSource, AllSources, StringComparison.Ordinal)
            && !string.Equals(entry.Source, _selectedSource, StringComparison.Ordinal))
            return false;

        if (_searchText.Length == 0)
            return true;

        // 与串口页查找同一套语法：| 表示或，\ 表示与，不做空格拆词
        return FindMatcher.FilterMatch(
            entry.ToLine(), _searchText, FindMatchMode.Contains, matchCase: false, out _);
    }

    /// <summary>按当前过滤条件刷新视图，并同步来源下拉选项</summary>
    private void RefreshVisible()
    {
        RefreshSourceOptions();
        EntriesView.Refresh();
        ClearCommand?.RaiseCanExecuteChanged();
    }

    /// <summary>从当前视图的底层集合收集出现过的来源</summary>
    private void RefreshSourceOptions()
    {
        var source = _historyView?.SourceCollection ?? _log.Entries;
        var seen = new List<string> { AllSources };
        var set = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in source)
        {
            if (item is not DebugLogEntry entry || entry.Source.Length == 0)
                continue;
            if (set.Add(entry.Source))
                seen.Add(entry.Source);
        }

        // 保持“全部来源”在首位，其余按名称排序，方便定位
        if (seen.Count > 2)
            seen = new[] { seen[0] }.Concat(seen.Skip(1).OrderBy(s => s, StringComparer.Ordinal)).ToList();

        SourceOptions = seen;

        // 曾经选中的来源在新一轮收集里消失时回落到“全部来源”
        if (!seen.Contains(_selectedSource))
        {
            _selectedSource = AllSources;
            RaisePropertyChanged(nameof(SelectedSource));
        }
    }

    // ===================== 复制与导出 =====================

    /// <summary>当前视图中的可见条目</summary>
    public IEnumerable<DebugLogEntry> VisibleEntries()
    {
        foreach (var item in EntriesView)
        {
            if (item is DebugLogEntry entry)
                yield return entry;
        }
    }

    /// <summary>复制当前视图的全部日志</summary>
    private void CopyAll()
    {
        var lines = VisibleEntries().Select(entry => entry.ToLine()).ToList();
        CopyToClipboard(string.Join(Environment.NewLine, lines), lines.Count);
    }

    /// <summary>把文本放入剪贴板并提示条数；无内容时提示</summary>
    public void CopyToClipboard(string text, int count)
    {
        try
        {
            if (count <= 0 || text.Length == 0)
            {
                System.Windows.MessageBox.Show(
                    Loc.T("没有可复制的日志。"), Loc.T("复制"),
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }

            System.Windows.Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            // 剪贴板被其它程序占用时只记录，不打断用户
            LogService.Current.Warn(nameof(LogViewModel), "Copy", ex.Message);
        }
    }

    private void ExportVisible()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("导出日志"),
            Filter = $"{Loc.T("文本文件")} (*.txt)|*.txt|{Loc.T("所有文件")} (*.*)|*.*",
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                                    $"log_{DateTime.Now:yyyyMMdd_HHmmss}.txt")
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            using var writer = new StreamWriter(dialog.FileName, false, new System.Text.UTF8Encoding(true));
            foreach (var entry in VisibleEntries())
                writer.WriteLine(entry.ToLine());
        }
        catch (Exception ex)
        {
            LogService.Current.Error(nameof(LogViewModel), "Export", "导出日志失败", ex);
            System.Windows.MessageBox.Show(
                Loc.T("导出失败：") + ex.Message, Loc.T("导出日志"),
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    // ===================== 清空 =====================

    /// <summary>清空当前显示的日志（只清内存，不动日志文件）</summary>
    private void ClearDisplay()
    {
        var confirm = System.Windows.MessageBox.Show(
            Loc.T("确定清空当前显示的日志？日志文件不受影响。"),
            Loc.T("清空"), System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (confirm == System.Windows.MessageBoxResult.Yes)
            _log.Clear();
    }

    /// <summary>删除当天的日志文件并按新文件继续记录</summary>
    private void ClearLogFile()
    {
        var confirm = System.Windows.MessageBox.Show(
            Loc.T("确定删除当天的日志文件？此操作不可恢复。"),
            Loc.T("清空日志文件"), System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (confirm != System.Windows.MessageBoxResult.Yes)
            return;

        if (!_log.ClearLogFile())
        {
            System.Windows.MessageBox.Show(
                Loc.T("日志文件被占用，删除失败。"), Loc.T("清空日志文件"),
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        _log.Clear();
        RefreshFileOptions();
    }

    // ===================== 历史日志 =====================

    private void RefreshFileOptions()
    {
        var options = new List<LogDayOption>
        {
            new() { Date = null, Display = Loc.T("实时") }
        };

        foreach (var file in _log.GetLogFiles())
        {
            options.Add(new LogDayOption
            {
                Date = file.Date,
                Paths = file.Paths,
                Display = $"{file.Date:yyyy-MM-dd} · {FormatSize(file.TotalBytes)}"
            });
        }

        FileOptions = options;

        // 保留当前选择；文件被删除或不在列表中时回到实时流
        int keep = _selectedDay is null
            ? 0
            : options.FindIndex(o => o.Date == _selectedDay.Date);
        if (keep < 0)
            keep = 0;

        if (!ReferenceEquals(_selectedDay, options[keep]))
        {
            _selectedDay = options[keep];
            RaisePropertyChanged(nameof(SelectedDay));
            RaisePropertyChanged(nameof(IsHistory));

            if (_selectedDay.Date is not null)
                LoadHistoryPage(0);
            else
                SwitchToLive();
        }
    }

    private void SwitchToLive()
    {
        _historyView = null;
        _hasOlder = false;
        _historyPage = 0;
        PageInfo = string.Empty;
        RaisePropertyChanged(nameof(EntriesView));
        RaisePropertyChanged(nameof(IsHistory));
        ClearCommand.RaiseCanExecuteChanged();
        LoadOlderCommand.RaiseCanExecuteChanged();
        LoadNewerCommand.RaiseCanExecuteChanged();
        RefreshSourceOptions();
    }

    /// <summary>读取指定页（0 = 最新一页），在后台线程完成文件扫描</summary>
    private async void LoadHistoryPage(int page)
    {
        var paths = _selectedDay?.Paths;
        if (paths is null || paths.Count == 0 || page < 0)
            return;

        int version = ++_loadVersion;
        PageInfo = Loc.T("正在读取…");

        LogService.LogPage result;
        try
        {
            result = await System.Threading.Tasks.Task.Run(() => _log.ReadPage(paths, page));
        }
        catch (Exception ex)
        {
            if (version == _loadVersion)
                PageInfo = Loc.T("读取失败：") + ex.Message;
            return;
        }

        if (version != _loadVersion)
            return;   // 用户已切到别的日期或实时流

        _historyPage = page;
        _historyTotal = result.TotalCount;
        _historyPages = Math.Max(1, (result.TotalCount + LogService.PageSize - 1) / LogService.PageSize);
        _hasOlder = result.HasMore;

        var view = new System.Windows.Data.ListCollectionView(result.Entries.ToList());
        view.Filter = FilterEntry;
        _historyView = view;

        PageInfo = string.Format(
            Loc.T("第 {0}/{1} 页 · 共 {2} 条"),
            _historyPages - page, _historyPages, _historyTotal);

        RaisePropertyChanged(nameof(EntriesView));
        RaisePropertyChanged(nameof(IsHistory));
        LoadOlderCommand.RaiseCanExecuteChanged();
        LoadNewerCommand.RaiseCanExecuteChanged();
        ClearCommand.RaiseCanExecuteChanged();
        RefreshSourceOptions();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB"
    };

    private void OnLogDirectoryChanged(string _)
    {
        RaisePropertyChanged(nameof(CurrentLogDirectory));
        RefreshFileOptions();
    }

    // ===================== 目录操作 =====================

    /// <summary>弹出系统文件夹选择对话框，选择后切换日志目录</summary>
    private void BrowseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Loc.T("选择日志保存目录"),
            InitialDirectory = _log.LogDirectory
        };
        if (dialog.ShowDialog() == true)
            ApplyDirectory(dialog.FolderName);
    }

    private void ApplyDirectory(string path)
    {
        if (_log.SetLogDirectory(path))
        {
            RaisePropertyChanged(nameof(CurrentLogDirectory));
            RefreshFileOptions();
        }
        else
        {
            System.Windows.MessageBox.Show(
                Loc.T("所选目录不可写，请重新选择。"), Loc.T("切换失败"),
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private void OpenLogFolder()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = _log.LogDirectory,
            UseShellExecute = true
        });
    }
}
