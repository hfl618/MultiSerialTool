namespace MultiSerialTool.Protocols.Serial;

/// <summary>
/// 单台设备的文件导出规则（每设备独立一份）。
/// 控制串口收发数据是否实时落盘、落盘位置、文件名与文件结构。
/// </summary>
public class FileExportRule
{
    /// <summary>缓存结构版本，后续不兼容升级时按此迁移</summary>
    public int Version { get; set; } = 1;

    /// <summary>是否启用实时写入：开启后每收到/发送一条消息立即追加到文件</summary>
    public bool RealtimeEnabled { get; set; }

    /// <summary>文件导出目录（绝对路径）</summary>
    public string Directory { get; set; } = string.Empty;

    /// <summary>
    /// 自定义文件名（不含扩展名）。支持占位符：
    /// {port} 端口名、{date} 日期、{time} 时间、{datetime} 日期时间、{device} 设备 Key。
    /// 留空时使用默认 {port}_{datetime}。
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// 导出类型/文件结构：csv / txt / hex / json。
    /// 扩展名由类型决定，无需在文件名中手写。
    /// </summary>
    public string Format { get; set; } = FileExportFormats.Csv;
}

/// <summary>文件导出类型常量与可选项</summary>
public static class FileExportFormats
{
    /// <summary>CSV：时间、方向、HEX、ASCII 分列（UTF-8 BOM，Excel 可直接打开）</summary>
    public const string Csv = "csv";

    /// <summary>TXT：带时间戳与方向的可读文本日志</summary>
    public const string Txt = "txt";

    /// <summary>HEX：仅记录十六进制字节，空格分隔</summary>
    public const string Hex = "hex";

    /// <summary>JSON Lines：每行一个 JSON 对象，便于实时追加与程序解析</summary>
    public const string Json = "json";

    /// <summary>界面下拉可选项（保持固定顺序）</summary>
    public static readonly string[] All = { Csv, Txt, Hex, Json };

    /// <summary>按导出类型取文件扩展名</summary>
    public static string GetExtension(string format) => format switch
    {
        Csv => ".csv",
        Txt => ".txt",
        Hex => ".hex",
        Json => ".jsonl",
        _ => ".txt"
    };
}
