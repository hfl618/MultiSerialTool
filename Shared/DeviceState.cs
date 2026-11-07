namespace MultiSerialTool.Shared;

/// <summary>
/// 设备连接状态。
/// </summary>
public enum DeviceState
{
    /// <summary>未连接</summary>
    Disconnected,

    /// <summary>连接中</summary>
    Connecting,

    /// <summary>已连接</summary>
    Connected,

    /// <summary>错误/异常断开</summary>
    Error
}
