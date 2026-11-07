namespace MultiSerialTool.Services;

/// <summary>
/// 调试日志级别，严重程度递增。
/// </summary>
public enum LogLevel
{
    /// <summary>调试细节，开发期排障用，发布版可关闭</summary>
    Debug = 0,

    /// <summary>常规运行信息，如连接、打开设备</summary>
    Info = 1,

    /// <summary>异常但可继续运行的情况</summary>
    Warn = 2,

    /// <summary>错误，通常伴随异常</summary>
    Error = 3
}
