namespace MultiSerialTool.Shared;

/// <summary>
/// 一条收发日志：所有协议通用。
/// </summary>
public class LogEntry
{
    /// <summary>时间戳</summary>
    public DateTime Time { get; set; }

    /// <summary>方向：true=发送(TX)，false=接收(RX)</summary>
    public bool IsTx { get; set; }

    /// <summary>日志文本内容（ASCII 视图）</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// 十六进制视图（RX 原始字节 / TX 写出字节均填充，如 "00 11 22"），
    /// 无原始字节时为 null。
    /// </summary>
    public string? HexText { get; set; }

    /// <summary>原始字节（HEX 视图与通篇切换依据），无原始字节时为 null</summary>
    public byte[]? Bytes { get; set; }

    /// <summary>
    /// 显示用前缀，TX 显示“TX”，RX 显示“RX”。
    /// </summary>
    public string Prefix => IsTx ? "TX" : "RX";
}
