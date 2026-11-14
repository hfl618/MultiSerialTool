using MultiSerialTool.Protocols.Serial;

namespace MultiSerialTool.Services;

/// <summary>
/// 当前选中串口设备跟踪（单例）。规则页是独立 ViewModel、不随设备表格切换重建，
/// 通过本服务获知当前选中的设备，使其文件规则编辑跟随左侧选中行。
/// </summary>
public sealed class CurrentDeviceService
{
    /// <summary>单例</summary>
    public static CurrentDeviceService Current { get; } = new();

    /// <summary>当前选中的串口设备；可为 null</summary>
    public SerialDeviceInfo? Device { get; private set; }

    /// <summary>设备切换事件（参数可能为 null）</summary>
    public event Action<SerialDeviceInfo?>? DeviceChanged;

    private CurrentDeviceService()
    {
    }

    /// <summary>更新当前设备并通知订阅者</summary>
    public void Publish(SerialDeviceInfo? device)
    {
        if (ReferenceEquals(Device, device))
            return;

        Device = device;
        DeviceChanged?.Invoke(device);
    }
}
