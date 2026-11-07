namespace MultiSerialTool.Models;

/// <summary>
/// 一条历史版本发布记录，对应 changelog 文件中的一个版本块。
/// </summary>
public sealed class ReleaseInfo
{
    /// <summary>语义化版本号，如 1.2.0</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>发布日期，yyyy-MM-dd</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>该版本的更新条目</summary>
    public List<string> Notes { get; set; } = new();
}
