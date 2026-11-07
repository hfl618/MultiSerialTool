using System.Data;
using System.Globalization;
using System.IO;
using System.Text;

namespace MultiSerialTool.Services;

/// <summary>
/// CSV 导出工具：把 DataTable 写成符合 RFC 4180 习惯的 CSV 文件。
/// 自动处理逗号、引号、换行转义，输出带 UTF-8 BOM，Excel 直接打开中文不乱码。
/// </summary>
public static class CsvExporter
{
    /// <summary>把数据表写入指定路径的 CSV 文件（UTF-8 BOM）</summary>
    public static void WriteDataTable(DataTable table, string path)
    {
        var sb = new StringBuilder();
        var columns = table.Columns.Cast<DataColumn>().ToList();

        // 表头
        sb.AppendLine(string.Join(",", columns.Select(c => Escape(c.ColumnName))));

        // 数据行
        foreach (DataRow dataRow in table.Rows)
        {
            var fields = columns.Select(c => FormatValue(dataRow[c], c.DataType));
            sb.AppendLine(string.Join(",", fields));
        }

        // UTF-8 BOM：保证 Excel 正确识别编码
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string FormatValue(object value, Type dataType)
    {
        if (value is null || value == DBNull.Value)
            return string.Empty;

        if (dataType == typeof(DateTime) && value is DateTime time)
            return Escape(time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));

        string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return Escape(text);
    }

    /// <summary>含逗号、引号或换行时用双引号包裹，内部引号双写</summary>
    private static string Escape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";

        return value;
    }
}
