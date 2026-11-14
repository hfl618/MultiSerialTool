using System.Data;
using System.IO;
using System.Text;
using System.Windows;
using MultiSerialTool.Services.Production;

namespace MultiSerialTool.ViewModels;

/// <summary>
/// 设备面板（测试页 + 测试数据表）ViewModel。
/// 数据表读取全局唯一 CSV（生产记录.csv），无论插拔多少次都展示同一张表；
/// 订阅存表服务的 RecordAppended 事件，保存后自动刷新到最新行。
/// </summary>
public sealed class DevicePanelViewModel : ViewModelBase
{
    /// <summary>测试数据表（GridControl 绑定其 DefaultView）</summary>
    public DataTable ProductionTable { get; } = CreateEmptyTable();

    public DevicePanelViewModel()
    {
        Reload();
        ProductionRecordService.Current.RecordAppended += OnRecordAppended;
    }

    /// <summary>记录追加回调：切回 UI 线程重新加载</summary>
    private void OnRecordAppended(string _)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            Reload();
        else
            dispatcher.BeginInvoke(new Action(Reload));
    }

    /// <summary>从全局 CSV 重新读取到 DataTable</summary>
    public void Reload()
    {
        var loaded = LoadCsv(ProductionRecordService.TablePath);

        ProductionTable.Rows.Clear();
        ProductionTable.Columns.Clear();

        if (loaded.Columns.Count == 0)
            return;

        foreach (DataColumn column in loaded.Columns)
            ProductionTable.Columns.Add(column.ColumnName, typeof(string));

        foreach (DataRow row in loaded.Rows)
            ProductionTable.ImportRow(row);
    }

    /// <summary>解析 CSV 为 DataTable（支持引号内逗号/双引号，识别 UTF-8 BOM）</summary>
    private static DataTable LoadCsv(string path)
    {
        var table = CreateEmptyTable();
        if (!File.Exists(path))
            return table;

        var lines = File.ReadAllLines(path, Encoding.UTF8);
        if (lines.Length == 0)
            return table;

        var header = SplitCsvLine(lines[0]);
        foreach (var col in header)
            table.Columns.Add(col.Trim(), typeof(string));

        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrEmpty(lines[i]))
                continue;
            var fields = SplitCsvLine(lines[i]);
            var row = table.NewRow();
            for (int c = 0; c < table.Columns.Count && c < fields.Count; c++)
                row[c] = fields[c];
            table.Rows.Add(row);
        }

        return table;
    }

    /// <summary>单行 CSV 拆分，处理双引号包裹与转义</summary>
    private static List<string> SplitCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                // 两个连续引号表示一个字面引号
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }
        result.Add(sb.ToString());
        return result;
    }

    private static DataTable CreateEmptyTable()
    {
        var table = new DataTable("Production");
        table.Locale = System.Globalization.CultureInfo.InvariantCulture;
        return table;
    }
}
