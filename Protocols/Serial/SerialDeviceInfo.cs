using System.Collections.ObjectModel;
using MultiSerialTool.Shared;
using MultiSerialTool.ViewModels;

namespace MultiSerialTool.Protocols.Serial;

/// <summary>
/// 串口设备行数据：对应串口表格中的一行，并持有该设备独立的日志与连接状态。
/// </summary>
public class SerialDeviceInfo : ViewModelBase
{
    /// <summary>串口名字（如 COM3）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>完整友好名称（如 "Silicon Labs CP210x USB to UART Bridge (COM5)"），WMI 读取</summary>
    public string FullName { get; set; } = string.Empty;

    /// <summary>设备描述（WMI Description）</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>厂商（WMI Manufacturer）</summary>
    public string Manufacturer { get; set; } = string.Empty;

    /// <summary>硬件 ID（如 USB\VID_10C4&PID_EA60\...）</summary>
    public string HardwareId { get; set; } = string.Empty;

    /// <summary>USB Vendor ID（十六进制字符串），非 USB 设备为 null</summary>
    public string? Vid { get; set; }

    /// <summary>USB Product ID（十六进制字符串），非 USB 设备为 null</summary>
    public string? Pid { get; set; }

    /// <summary>界面显示名称：优先完整友好名，WMI 查不到时回退短名</summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(FullName) ? Name : FullName;

    /// <summary>类型标注（RS232/RS485，系统无法自动识别，手动标注）</summary>
    public string Type { get; set; } = "";

    /// <summary>地址/描述信息</summary>
    public string Address { get; set; } = string.Empty;

    private int _baudRate = 9600;
    /// <summary>波特率：自动探测完成后回填真实生效值，通知表格刷新</summary>
    public int BaudRate
    {
        get => _baudRate;
        set
        {
            if (SetProperty(ref _baudRate, value))
                RaisePropertyChanged(nameof(StatusText));
        }
    }

    private string? _errorMessage;
    /// <summary>最近一次连接/接收错误信息，正常时为 null</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (SetProperty(ref _errorMessage, value))
                RaisePropertyChanged(nameof(StatusText));
        }
    }

    private DeviceState _state = DeviceState.Disconnected;
    /// <summary>连接状态，变化时同步刷新状态文本与行颜色</summary>
    public DeviceState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
                RaisePropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>该设备独立的收发日志，切换选中行时工作区显示对应集合</summary>
    public ObservableCollection<LogEntry> LogEntries { get; } = new();

    /// <summary>该设备独立的发送草稿与历史</summary>
    public SerialComposer Composer { get; } = new();

    /// <summary>
    /// 最新连接请求的版本令牌：每次发起连接递增，
    /// 异步连接完成后比对，过期结果（用户已取消/重连）直接丢弃。
    /// </summary>
    public int ConnectVersion { get; set; }

    /// <summary>工作区状态栏文本，随状态/波特率/错误信息变化</summary>
    public string StatusText => State switch
    {
        DeviceState.Connected => $"已连接（{BaudRate}, 8N1）",
        DeviceState.Connecting => "连接中，正在探测波特率…",
        DeviceState.Error => $"错误：{ErrorMessage}",
        _ => "未连接"
    };
}
