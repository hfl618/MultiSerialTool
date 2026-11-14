using System.IO;
using System.Text;
using MultiSerialTool.Protocols.Serial;

namespace MultiSerialTool.Services.Production;

/// <summary>
/// 产线参数存表服务（即插即用）：数据落在程序目录 Data\Production，
/// 整个程序文件夹拷走即带全部数据，不写 C 盘用户目录。
/// 全局只有一张表（生产记录.csv），无论插拔多少次、多少台设备，
/// 测完都向同一文件追加一条；设备码作为一列保留。
/// 截图存 images 子文件夹，表内以 HYPERLINK 相对路径引用，
/// 独立图片文件同时便于后续批量生成 Word/测试报告。
/// </summary>
public sealed class ProductionRecordService
{
    /// <summary>单例</summary>
    public static ProductionRecordService Current { get; } = new();

    /// <summary>固定前置列</summary>
    private static readonly string[] LeadingColumns = { "时间", "设备码", "端口" };

    /// <summary>截图列名（放最后）</summary>
    private const string ImageColumn = "截图";

    /// <summary>全局唯一数据表文件名（不含扩展名）</summary>
    private const string TableFileName = "生产记录";

    private ProductionRecordService()
    {
    }

    /// <summary>一条记录追加完成事件（参数为 CSV 路径），数据表界面据此自动刷新</summary>
    public event Action<string>? RecordAppended;

    /// <summary>产线数据根目录（exe 同级，即插即用）</summary>
    public static string RootDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Data", "Production");

    /// <summary>截图目录（相对根目录 images）</summary>
    public static string ImageDirectory => Path.Combine(RootDirectory, "images");

    /// <summary>全局唯一数据表完整路径</summary>
    public static string TablePath =>
        Path.Combine(RootDirectory, TableFileName + ".csv");

    /// <summary>
    /// 追加一条本台设备汇总记录。
    /// </summary>
    /// <param name="rule">当前规则（决定参数列顺序）</param>
    /// <param name="values">引擎解析值 Key → 工程数值</param>
    /// <param name="deviceCode">设备上报的设备码</param>
    /// <param name="portName">端口名（仅记录，不做标识）</param>
    /// <param name="imageRelativePath">截图相对路径（相对根目录），无则空</param>
    /// <returns>写入的 CSV 完整路径</returns>
    public string Append(DeviceRuleSet rule, IReadOnlyDictionary<string, double?> values,
                         string deviceCode, string portName, string? imageRelativePath)
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(ImageDirectory);

        // 全局唯一数据表，所有设备/插拔记录追加到同一文件
        string csvPath = TablePath;

        // 参数列按规则顺序固定，保证同型号设备列一致
        var paramColumns = rule.Fields.Select(f => f.Key)
            .Concat(rule.Charts.Select(c => c.Key))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var columns = LeadingColumns
            .Concat(paramColumns)
            .Append(ImageColumn)
            .ToList();

        bool newFile = !File.Exists(csvPath) || new FileInfo(csvPath).Length == 0;

        // 已存在文件：以磁盘表头为准对齐，避免规则版本变化导致错列
        if (!newFile)
            columns = ReadHeader(csvPath);

        var rowMap = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["时间"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["设备码"] = deviceCode,
            ["端口"] = portName
        };

        foreach (var kv in values)
        {
            if (kv.Value is not null)
                rowMap[kv.Key] = kv.Value.Value.ToString("0.###");
        }

        if (!string.IsNullOrEmpty(imageRelativePath))
            rowMap[ImageColumn] =
                $"=HYPERLINK(\"{imageRelativePath.Replace('\\', '/')}\",\"查看截图\")";

        using var stream = new FileStream(csvPath, FileMode.Append,
            FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false))
        {
            AutoFlush = true
        };

        if (newFile)
        {
            stream.Write(Encoding.UTF8.Preamble);
            writer.WriteLine(string.Join(",", columns.Select(Escape)));
        }

        writer.WriteLine(string.Join(",",
            columns.Select(c => Escape(rowMap.TryGetValue(c, out var v) ? v : ""))));

        RecordAppended?.Invoke(csvPath);
        return csvPath;
    }

    /// <summary>读取现有 CSV 表头（按逗号拆分，去引号）</summary>
    private static List<string> ReadHeader(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var line = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(line))
            return new List<string>();

        var result = new List<string>();
        foreach (var raw in SplitCsv(line))
            result.Add(raw.Trim('"'));
        return result;
    }

    /// <summary>最小 CSV 拆分（表头不含换行，处理引号内逗号）</summary>
    private static IEnumerable<string> SplitCsv(string line)
    {
        var sb = new StringBuilder();
        bool inQuotes = false;
        foreach (var c in line)
        {
            if (c == '"') inQuotes = !inQuotes;
            if (c == ',' && !inQuotes)
            {
                yield return sb.ToString();
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }
        yield return sb.ToString();
    }

    /// <summary>RFC 4180 转义</summary>
    private static string Escape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}
