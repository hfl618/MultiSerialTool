using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Windows;
using MultiSerialTool.Models.Localization;
using MultiSerialTool.Commands;
using MultiSerialTool.Services;
using MultiSerialTool.Shared;
using MultiSerialTool.ViewModels;

namespace MultiSerialTool.Protocols.Serial;

/// <summary>
/// 串口页面的 ViewModel：枚举本机串口、维护设备集合、真实连接与工作区。
/// 每台设备独立日志、独立连接版本令牌，异步切换不串状态。
/// </summary>
public class SerialViewModel : ProtocolViewModelBase
{
    /// <summary>串口设备集合，绑定到表格</summary>
    public ObservableCollection<SerialDeviceInfo> Devices { get; } = new();

    /// <summary>强类型工作区引用，供内部访问</summary>
    private readonly SerialWorkspaceViewModel _workspace = new();

    /// <summary>已建立的真实连接，按端口名索引</summary>
    private readonly Dictionary<string, SerialConnection> _connections = new();

    private SerialDeviceInfo? _selectedDevice;
    /// <summary>当前选中的串口设备，绑定表格 SelectedItem</summary>
    public SerialDeviceInfo? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (_selectedDevice is not null)
                _selectedDevice.PropertyChanged -= OnDevicePropertyChanged;

            if (SetProperty(ref _selectedDevice, value))
                _workspace.Device = value;

            if (_selectedDevice is not null)
                _selectedDevice.PropertyChanged += OnDevicePropertyChanged;

            RaisePropertyChanged(nameof(ToggleText));
        }
    }

    /// <summary>菜单/操作显示文字，随选中设备状态切换</summary>
    public string ToggleText =>
        _selectedDevice?.State == DeviceState.Connected
            ? Loc.T("断开连接")
            : Loc.T("连接串口");

    /// <summary>切换连接/断开（双击行、右键菜单共用）</summary>
    public RelayCommand ToggleConnectCommand { get; }

    /// <summary>重新扫描本机串口（差量更新，保留已连接设备）</summary>
    public RelayCommand RefreshCommand { get; }

    public SerialViewModel()
    {
        Title = Loc.T("串口");
        Workspace = _workspace;
        ToggleConnectCommand = new RelayCommand(ToggleConnect);
        RefreshCommand = new RelayCommand(RefreshPorts);

        _workspace.TxSubmitted += OnTxSubmitted;

        // 语言切换时刷新动态文本（异常保护，避免通知失败影响其他订阅者）
        LocalizationService.Instance.PropertyChanged += OnLanguageChanged;

        LoadPorts();
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        try
        {
            RaisePropertyChanged(nameof(ToggleText));
        }
        catch
        {
            // 本地化刷新异常被吞掉，不影响主流程
        }
    }

    private void ToggleConnect()
    {
        if (_selectedDevice is null)
            return;

        switch (_selectedDevice.State)
        {
            case DeviceState.Connected:
                Disconnect(_selectedDevice);
                break;
            case DeviceState.Connecting:
                CancelConnecting(_selectedDevice);
                break;
            default:
                _ = ConnectAsync(_selectedDevice);
                break;
        }
    }

    /// <summary>
    /// 建立真实连接：后台线程执行自动波特率探测与打开，避免阻塞 UI。
    /// 版本令牌保证过期的异步结果被丢弃。
    /// </summary>
    private async Task ConnectAsync(SerialDeviceInfo device)
    {
        if (device.State is DeviceState.Connecting or DeviceState.Connected)
            return;

        int version = ++device.ConnectVersion;
        device.ErrorMessage = null;
        device.State = DeviceState.Connecting;

        var connection = new SerialConnection(device.Name);
        connection.ChunkReceived += chunk => OnChunkReceived(device, version, chunk);
        connection.Faulted += ex => OnConnectionFaulted(device, version, ex);

        try
        {
            await Task.Run(() => connection.Open());

            // 探测期间用户已取消或再次发起连接，过期结果直接释放
            if (version != device.ConnectVersion)
            {
                connection.Dispose();
                return;
            }

            if (_connections.TryGetValue(device.Name, out var old))
                old.Dispose();
            _connections[device.Name] = connection;

            device.BaudRate = connection.BaudRate;
            device.State = DeviceState.Connected;
            LogService.Current.Info("Serial", $"{device.Name} 已连接，波特率 {connection.BaudRate}");
        }
        catch (Exception ex)
        {
            connection.Dispose();

            if (version != device.ConnectVersion)
                return;

            device.ErrorMessage = ex.Message;
            device.State = DeviceState.Error;
            LogService.Current.Error("Serial", $"{device.Name} 连接失败：{ex.Message}");
            NotifyError(device, ex.Message, Loc.T("串口连接失败"));
        }
    }

    /// <summary>
    /// 错误通知：仅当故障设备是当前选中设备时弹窗，避免后台设备打断正在进行的操作；
    /// 非选中设备只保留行状态（红色+悬停提示）与日志，切过去即可看到。
    /// </summary>
    private void NotifyError(SerialDeviceInfo device, string message, string title)
    {
        if (ReferenceEquals(device, _selectedDevice))
            ShowError(message, title);
    }

    /// <summary>取消正在进行的连接：版本递增使后台结果过期，立即回退状态</summary>
    private void CancelConnecting(SerialDeviceInfo device)
    {
        device.ConnectVersion++;
        device.ErrorMessage = null;
        device.State = DeviceState.Disconnected;
        LogService.Current.Info("Serial", $"{device.Name} 已取消连接");
    }

    /// <summary>
    /// 运行期故障（拔线、IO/帧错误）：仅当版本匹配时处理，清理连接、置 Error 并弹窗。
    /// </summary>
    private void OnConnectionFaulted(SerialDeviceInfo device, int version, Exception ex)
    {
        if (version != device.ConnectVersion)
            return;

        if (_connections.TryGetValue(device.Name, out var connection))
        {
            connection.Dispose();
            _connections.Remove(device.Name);
        }

        device.ErrorMessage = ex.Message;
        device.State = DeviceState.Error;
        LogService.Current.Error("Serial", $"{device.Name} 运行异常：{ex.Message}");
        NotifyError(device, ex.Message, Loc.T("串口连接异常"));
    }

    /// <summary>统一错误弹窗：仅提示，不退出软件</summary>
    private static void ShowError(string message, string title)
    {
        System.Windows.MessageBox.Show(
            message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>断开并释放连接</summary>
    private void Disconnect(SerialDeviceInfo device)
    {
        // 同时使任何在途的异步结果过期
        device.ConnectVersion++;

        if (_connections.TryGetValue(device.Name, out var connection))
        {
            connection.Dispose();
            _connections.Remove(device.Name);
        }

        device.ErrorMessage = null;
        device.State = DeviceState.Disconnected;
        LogService.Current.Info("Serial", $"{device.Name} 已断开");
    }

    /// <summary>
    /// 收到数据：写入该设备自己的日志集合（无论是否选中，后台持续记录）。
    /// 按 CR/LF 把一个接收块拆成多条日志，保证一条日志只占一行，
    /// HEX 与 ASCII 按相同字节边界拆分；无换行的二进制帧保持整块。
    /// </summary>
    private void OnChunkReceived(SerialDeviceInfo device, int version, SerialRawChunk chunk)
    {
        if (version != device.ConnectVersion || chunk.Length == 0)
            return;

        foreach (byte[] segment in SplitByLineEnding(chunk.Bytes))
        {
            var part = new SerialRawChunk { Time = chunk.Time, Bytes = segment };
            device.LogEntries.Add(new LogEntry
            {
                Time = chunk.Time,
                IsTx = false,
                Text = part.Text,
                HexText = part.Hex
            });
        }
    }

    /// <summary>
    /// 按 0x0D/0x0A 切分字节块：连续的 CR/LF（含 CRLF）不产生空条目，
    /// 整块只含换行符时返回空列表，不含换行符时返回整块；除换行符外不丢任何字节。
    /// </summary>
    private static List<byte[]> SplitByLineEnding(byte[] bytes)
    {
        var result = new List<byte[]>();
        int start = 0;
        bool hasSeparator = false;

        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] is not (0x0D or 0x0A))
                continue;

            hasSeparator = true;

            if (i > start)
                result.Add(bytes[start..i]);

            // 兼容 CRLF：CR 后紧跟 LF 时跳过 LF
            if (bytes[i] == 0x0D && i + 1 < bytes.Length && bytes[i + 1] == 0x0A)
                i++;

            start = i + 1;
        }

        // 含换行符时，末尾最后一段承载剩余内容
        if (hasSeparator && start < bytes.Length)
            result.Add(bytes[start..]);

        // 不含任何换行符：整块作为一条
        if (!hasSeparator)
            result.Add(bytes);

        return result;
    }

    /// <summary>
    /// 工作区发送事件：TX 先记录到当前设备日志，再写入其真实连接。
    /// 未连接或写入失败时提示，异常不逃逸、软件继续运行。
    /// </summary>
    private void OnTxSubmitted(string text)
    {
        if (_selectedDevice is null || string.IsNullOrEmpty(text))
            return;

        var device = _selectedDevice;
        device.LogEntries.Add(new LogEntry
        {
            Time = DateTime.Now,
            IsTx = true,
            Text = text
        });

        if (!_connections.TryGetValue(device.Name, out var connection))
        {
            string msg = $"{device.Name} 未连接，无法发送，请先建立连接";
            LogService.Current.Warn("Serial", msg);
            ShowError(msg, Loc.T("发送失败"));
            return;
        }

        try
        {
            connection.Write(text);
        }
        catch (Exception ex)
        {
            LogService.Current.Error("Serial", $"{device.Name} 发送失败：{ex.Message}");
            ShowError(ex.Message, Loc.T("发送失败"));
        }
    }

    /// <summary>
    /// 首次枚举本机所有 COM 口并生成行数据。
    /// </summary>
    private void LoadPorts()
    {
        foreach (string port in SerialPort.GetPortNames())
            Devices.Add(CreateDevice(port));
    }

    /// <summary>
    /// 重新扫描并差量更新：
    /// 已消失的口（含已连接）关闭移除，新出现的口加入，未变化的保留原状态。
    /// </summary>
    private void RefreshPorts()
    {
        string[] currentPorts = SerialPort.GetPortNames();
        var currentSet = new HashSet<string>(currentPorts, StringComparer.OrdinalIgnoreCase);

        bool selectedRemoved = false;

        // 移除已消失的口
        foreach (var existing in Devices.Where(d => !currentSet.Contains(d.Name)).ToList())
        {
            existing.ConnectVersion++;
            existing.PropertyChanged -= OnDevicePropertyChanged;

            if (ReferenceEquals(existing, _selectedDevice))
                selectedRemoved = true;

            if (_connections.TryGetValue(existing.Name, out var connection))
            {
                connection.Dispose();
                _connections.Remove(existing.Name);
            }
            Devices.Remove(existing);
        }

        // 加入新插入的口
        var existingNames = new HashSet<string>(
            Devices.Select(d => d.Name), StringComparer.OrdinalIgnoreCase);

        foreach (string port in currentPorts)
        {
            if (!existingNames.Contains(port))
                Devices.Add(CreateDevice(port));
        }

        // 选中设备已消失：改选剩余首行，没有则清空，避免工作区悬空指向已移除设备
        if (selectedRemoved)
            SelectedDevice = Devices.FirstOrDefault();

        LogService.Current.Debug("Serial", $"串口刷新完成，当前 {Devices.Count} 个端口");
    }

    private static SerialDeviceInfo CreateDevice(string port) => new()
    {
        Name = port,
        Type = "RS232",
        Address = "本机串口",
        BaudRate = 9600,
        State = DeviceState.Disconnected
    };

    private void OnDevicePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SerialDeviceInfo.State))
            RaisePropertyChanged(nameof(ToggleText));
    }
}
