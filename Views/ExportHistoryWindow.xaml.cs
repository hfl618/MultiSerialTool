using System.Windows;
using System.Windows.Controls;
using MultiSerialTool.Models.Localization;
using MultiSerialTool.Services.Storage;

namespace MultiSerialTool.Views;

/// <summary>
/// 导出历史列表：显示每次实时写入文件的时间、行数与字节数。
/// 数据来自 ExportHistoryStore，打开时取一次快照。
/// </summary>
public partial class ExportHistoryWindow : Window
{
    public ExportHistoryWindow()
    {
        InitializeComponent();
        LoadRows();
    }

    private void LoadRows()
    {
        var loc = LocalizationService.Instance;
        var rows = ExportHistoryStore.Snapshot()
            .Select(r => new HistoryRow
            {
                Record = r,
                DeviceName = r.DeviceName,
                FilePath = r.FilePath,
                StartText = r.StartTime.ToString("yyyy-MM-dd HH:mm:ss"),
                EndText = r.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty,
                Lines = r.Lines,
                Bytes = r.Bytes,
                Status = r.EndTime is null ? loc["记录中"] : loc["已完成"]
            })
            .ToList();

        HistoryGrid.ItemsSource = rows;
        EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectionState();
    }

    private void HistoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionState();

    private void UpdateSelectionState()
    {
        var count = HistoryGrid.SelectedItems.Count;
        DeleteButton.IsEnabled = count > 0;
        SelectionHint.Text = Loc.T("已选 {0} 条", count);
        SelectionHint.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var selected = HistoryGrid.SelectedItems.Cast<HistoryRow>().ToList();
        if (selected.Count == 0)
            return;

        var answer = System.Windows.MessageBox.Show(this,
            Loc.T("删除选中的 {0} 条导出记录？此操作不可撤销。", selected.Count),
            Loc.T("删除历史记录"),
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (answer != System.Windows.MessageBoxResult.OK)
            return;

        ExportHistoryStore.Remove(selected.Select(r => r.Record));
        LoadRows();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>表格行：时间与状态已格式化为可直接显示的文本</summary>
    private sealed class HistoryRow
    {
        public required ExportRecord Record { get; init; }
        public string DeviceName { get; init; } = string.Empty;
        public string FilePath { get; init; } = string.Empty;
        public string StartText { get; init; } = string.Empty;
        public string EndText { get; init; } = string.Empty;
        public long Lines { get; init; }
        public long Bytes { get; init; }
        public string Status { get; init; } = string.Empty;
    }
}
