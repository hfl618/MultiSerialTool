namespace MultiSerialTool.Services;

/// <summary>
/// 一条全局调试日志（区别于业务收发日志 LogEntry）。
/// </summary>
public class DebugLogEntry
{
    /// <summary>产生时间</summary>
    public DateTime Time { get; init; }

    /// <summary>级别</summary>
    public LogLevel Level { get; init; }

    /// <summary>来源：模块/类名，如 Serial、Tcp、App</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>操作名，如 OpenPort、SendData；纯消息式日志可为空</summary>
    public string Operation { get; init; } = string.Empty;

    /// <summary>日志详情/消息内容</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>级别显示文本</summary>
    public string LevelText => Level.ToString().ToUpperInvariant();

    /// <summary>UI 显示文本：有操作名时拼成“操作: 详情”</summary>
    public string DisplayText =>
        string.IsNullOrEmpty(Operation) ? Message : $"{Operation}: {Message}";
}
