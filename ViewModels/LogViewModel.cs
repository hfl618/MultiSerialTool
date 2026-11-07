using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using MultiSerialTool.Models.Localization;
using MultiSerialTool.Commands;
using MultiSerialTool.Services;

namespace MultiSerialTool.ViewModels;

/// <summary>
/// 全局调试日志页面 ViewModel：路径显示与切换、级别过滤、清空、打开日志目录。
/// </summary>
public class LogViewModel : ViewModelBase
{
    private readonly LogService _log = LogService.Current;

    /// <summary>过滤选项（“全部”+ 各级别）</summary>
    public IReadOnlyList<string> LevelOptions { get; }
        = new[] { "全部", "DEBUG", "INFO", "WARN", "ERROR" };

    private string _selectedLevel = "全部";
    /// <summary>当前选中的级别过滤</summary>
    public string SelectedLevel
    {
        get => _selectedLevel;
        set
        {
            if (SetProperty(ref _selectedLevel, value))
                EntriesView.Refresh();
        }
    }

    /// <summary>当前日志目录（界面显示用）</summary>
    public string CurrentLogDirectory => _log.LogDirectory;

    /// <summary>带过滤的日志视图</summary>
    public ICollectionView EntriesView { get; }

    public RelayCommand ClearCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand BrowseFolderCommand { get; }
    public RelayCommand ResetFolderCommand { get; }

    public LogViewModel()
    {
        EntriesView = CollectionViewSource.GetDefaultView(_log.Entries);
        EntriesView.Filter = FilterByLevel;

        ClearCommand = new RelayCommand(() => _log.Clear());
        OpenFolderCommand = new RelayCommand(OpenLogFolder);
        BrowseFolderCommand = new RelayCommand(BrowseFolder);
        ResetFolderCommand = new RelayCommand(
            () => ApplyDirectory(LogService.DefaultLogDirectory));

        // 目录切换后刷新显示
        _log.DirectoryChanged += _ => RaisePropertyChanged(nameof(CurrentLogDirectory));
    }

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
            EntriesView.Refresh();
        }
        else
        {
            System.Windows.MessageBox.Show(
                Loc.T("所选目录不可写，请重新选择。"), Loc.T("切换失败"),
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private bool FilterByLevel(object obj)
    {
        if (_selectedLevel == "全部")
            return true;
        return obj is DebugLogEntry entry && entry.LevelText == _selectedLevel;
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
