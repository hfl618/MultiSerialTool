using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Windows;
using System.Windows.Threading;
using MultiSerialTool.Models.Localization;
using MultiSerialTool.Commands;
using MultiSerialTool.Services;
using MultiSerialTool.Shared;
using MultiSerialTool.ViewModels;

namespace MultiSerialTool.Protocols.Serial;

/// <summary>
/// 串口页面的 ViewModel：枚举本机串口、维护设备集合、真实连接与工作区。
/// 每台设备独立日志、独立连接版本令牌，异步切换不串状态。
/// 支持热插拔自动刷新与按 VID/PID（或端口名）自动重连。
/// </summary>
public class SerialViewModel : ProtocolViewModelBase, IDisposable
{
    /// <summary>串口设备集合，绑定到表格</summary>
    public ObservableCollection<SerialDeviceInfo> Devices { get; } = new();

    /// <summary>强类型工作区引用，供内部访问</summary>
    private readonly SerialWorkspaceViewModel _workspace = new();

    /// <summary>已建立的真实连接，按端口名索引</summary>
    private readonly Dictionary<string, SerialConnection> _connections = new();

    /// <summary>在途连接任务（含波特率探测），按端口名索引，用于重连前等待旧任务结束</summary>
    private readonly Dictionary<string, Task> _connectTasks = new();

    /// <summary>每台设备的消息组包器（按端口名索引），把连续到达的字节合并为一条消息</summary>
    private readonly Dictionary<string, MessageAssembler> _assemblers = new();

    /// <summary>热插拔 WMI 监听</summary>
    private readonly SerialDeviceWatcher _deviceWatcher = new();

    /// <summary>设备变更去抖定时器：一次插拔会连续触发多个 WMI 事件，合并为一次刷新</summary>
    private readonly DispatcherTimer _debounceTimer;

    /// <summary>
    /// 需要自动重连的设备标识：USB 设备按 VID/PID（重插后 COM 号可能变），
    /// 无 VID/PID 的原生/虚拟串口按端口名兜底。仅在连接成功后登记，手动断开时移除。
    /// </summary>
    private readonly HashSet<string> _reconnectKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>当前去抖周期内是否收到过设备移除事件（WMI EventType=3）</summary>
    private bool _pendingRemoval;

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
        RefreshCommand = new RelayCommand(() => RefreshPorts());

        _workspace.TxSubmitted += OnTxSubmitted;

        // 语言切换时刷新动态文本（异常保护，避免通知失败影响其他订阅者）
        LocalizationService.Instance.PropertyChanged += OnLanguageChanged;

        // 去抖 500ms：WMI 事件在后台线程到达，封送到 UI 线程后重启定时器
        _debounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _debounceTimer.Tick += OnDebounceTick;

        _deviceWatcher.DeviceChanged += OnDeviceChanged;
        _deviceWatcher.Start();

        LoadPorts();

        // 把设备集合注入工作区，图表页可独立选择数据源设备（默认第一个）
        _workspace.AttachDevices(Devices);
    }

    /// <summary>WMI 回调（后台线程）：封送到 UI 线程，记录事件类型并重置去抖定时器</summary>
    private void OnDeviceChanged(int eventType)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        if (dispatcher.CheckAccess())
            ArmDebounce(eventType);
        else
            dispatcher.BeginInvoke(new Action(() => ArmDebounce(eventType)));
    }

    private void ArmDebounce(int eventType)
    {
        // WMI EventType: 2=设备到达, 3=设备移除；到达由差量刷新处理，这里只需标记移除
        if (eventType == 3)
            _pendingRemoval = true;

        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounceTimer.Stop();
        HandleDeviceChanged();
    }

    /// <summary>
    /// 热插拔处理：差量刷新设备列表，对登记过自动重连的新设备静默发起连接。
    /// </summary>
    private void HandleDeviceChanged()
    {
        bool removalOccurred = _pendingRemoval;
        _pendingRemoval = false;

        var addedDevices = RefreshPorts();

        foreach (var device in addedDevices)
        {
            if (!_reconnectKeys.Contains(ReconnectKey(device)))
                continue;

            LogService.Current.Info("Serial", $"{device.Name} 设备重新插入，自动重连");
            StartConnect(device, silent: true);
        }

        // 边缘场景：去抖窗口内完成“拔出又插回”，端口名净变化为零，RefreshPorts 不会
        // 移除/新增该行，但 USB 重新枚举后旧句柄已失效。对仍在线且本周期发生过移除的
        // 已连接/连接中设备，强制丢弃旧句柄并按重连规则重新建立连接。
        if (removalOccurred)
        {
            string[] currentPorts = SerialPort.GetPortNames();
            foreach (var device in Devices.ToList())
            {
                if (device.State is not (DeviceState.Connected or DeviceState.Connecting))
                    continue;
                if (!currentPorts.Any(p => string.Equals(p, device.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (!_reconnectKeys.Contains(ReconnectKey(device)))
                    continue;

                LogService.Current.Info("Serial",
                    $"{device.Name} 检测到快速拔插，旧句柄已失效，强制重新连接");
                ForceReconnect(device);
            }
        }
    }

    /// <summary>
    /// 启动连接并跟踪在途任务：保证同一端口同时只有一个连接流程，
    /// 强制重连时可先等待旧任务（可能正在波特率探测）结束。
    /// </summary>
    private void StartConnect(SerialDeviceInfo device, bool silent = false)
    {
        if (device.State is DeviceState.Connecting or DeviceState.Connected)
            return;

        _connectTasks[device.Name] = ConnectAndTrackAsync(device, silent);
    }

    private async Task ConnectAndTrackAsync(SerialDeviceInfo device, bool silent)
    {
        try
        {
            await ConnectAsync(device, silent);
        }
        catch (Exception ex)
        {
            // ConnectAsync 内部已捕获连接异常，这里兜底防止任务成为未观察异常
            LogService.Current.Error("Serial", $"{device.Name} 连接任务异常：{ex.Message}");
        }
        finally
        {
            // 同一端口的新连接只会在旧任务结束后启动（状态检查或 ForceReconnect 等待），
            // 因此直接移除本端口的在途记录不会误删新任务
            _connectTasks.Remove(device.Name);
        }
    }

    /// <summary>
    /// 强制重连：使在途异步结果过期、释放旧句柄、等待旧连接任务结束后静默重新连接，
    /// 避免两个流程同时打开同一端口造成占用竞争。
    /// </summary>
    private async void ForceReconnect(SerialDeviceInfo device)
    {
        device.ConnectVersion++;

        if (_connections.TryGetValue(device.Name, out var connection))
        {
            connection.Dispose();
            _connections.Remove(device.Name);
        }

        device.ErrorMessage = null;
        device.State = DeviceState.Disconnected;

        // 等待在途探测/连接任务自行过期结束（版本已递增，其结果会被丢弃）
        if (_connectTasks.TryGetValue(device.Name, out var running))
        {
            try
            {
                await running;
            }
            catch
            {
                // 旧任务异常已在其内部记录，等待期间忽略
            }
        }

        StartConnect(device, silent: true);
    }

    /// <summary>
    /// 自动重连标识：USB 设备优先 VID/PID（COM 号变化也能匹配），
    /// 否则按端口名兜底。
    /// </summary>
    private static string ReconnectKey(SerialDeviceInfo device)
        => device.Vid is not null && device.Pid is not null
            ? $"USB:{device.Vid}:{device.Pid}"
            : $"PORT:{device.Name}";


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
                StartConnect(_selectedDevice, silent: false);
                break;
        }
    }

    /// <summary>
    /// 建立真实连接：后台线程执行自动波特率探测与打开，避免阻塞 UI。
    /// 版本令牌保证过期的异步结果被丢弃。
    /// </summary>
    private async Task ConnectAsync(SerialDeviceInfo device, bool silent = false)
    {
        if (device.State is DeviceState.Connecting or DeviceState.Connected)
            return;

        int version = ++device.ConnectVersion;
        device.ErrorMessage = null;
        device.State = DeviceState.Connecting;

        var connection = new SerialConnection(device.Name, device.BaudRate);

        // 重建连接时先丢弃旧组包器，避免两个组包器同时向同一设备写日志
        RemoveAssembler(device.Name);
        var assembler = new MessageAssembler(AppConfig.Load().MessageIdleGapMs);
        assembler.MessageCompleted += (msgTime, bytes)
            => OnMessageCompleted(device, version, msgTime, bytes);
        _assemblers[device.Name] = assembler;

        // 原始字节先喂给组包器，由其按空闲间隔合并成完整消息，不再逐行拆分
        connection.ChunkReceived += chunk => assembler.Append(chunk.Time, chunk.Bytes);
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
            // 登记自动重连标识，拔出后同设备再插入时自动重连
            _reconnectKeys.Add(ReconnectKey(device));
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

            // 自动重连失败不弹窗打断，仅保留行状态与日志
            if (!silent)
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

        // 物理拔线时端口已不在系统枚举中，设备行将被热插拔刷新移除，不弹窗；
        // 端口仍在线（波特率/线路错误等）才弹窗提示。
        bool stillPresent = SerialPort.GetPortNames()
            .Any(p => string.Equals(p, device.Name, StringComparison.OrdinalIgnoreCase));
        if (stillPresent)
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

        RemoveAssembler(device.Name);

        device.ErrorMessage = null;
        device.State = DeviceState.Disconnected;
        // 用户手动断开：取消自动重连登记，再次插入不再自动连接
        _reconnectKeys.Remove(ReconnectKey(device));
        LogService.Current.Info("Serial", $"{device.Name} 已断开");
    }

    /// <summary>
    /// 一条完整消息组包完成：整组字节（含内部 CR/LF）只生成一条 LogEntry，
    /// 保证同一条消息在日志中保持在一起。版本令牌校验，过期连接的消息直接丢弃。
    /// </summary>
    private void OnMessageCompleted(
        SerialDeviceInfo device, int version, DateTime time, byte[] bytes)
    {
        if (version != device.ConnectVersion || bytes.Length == 0)
            return;

        var part = new SerialRawChunk { Time = time, Bytes = bytes };
        device.LogEntries.Add(new LogEntry
        {
            Time = time,
            IsTx = false,
            Text = part.Text,
            HexText = part.Hex
        });
    }

    /// <summary>移除并释放指定端口的消息组包器</summary>
    private void RemoveAssembler(string portName)
    {
        if (_assemblers.TryGetValue(portName, out var assembler))
        {
            assembler.Dispose();
            _assemblers.Remove(portName);
        }
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
        var descriptors = SerialPortInfoProvider.QueryAll();
        foreach (string port in SerialPort.GetPortNames())
            Devices.Add(CreateDevice(port, descriptors.GetValueOrDefault(port)));
    }

    /// <summary>
    /// 重新扫描并差量更新：
    /// 已消失的口（含已连接）关闭移除，新出现的口加入，未变化的保留原状态。
    /// 返回本次新加入的设备列表，供热插拔自动重连判断。
    /// </summary>
    private List<SerialDeviceInfo> RefreshPorts()
    {
        string[] currentPorts = SerialPort.GetPortNames();
        var currentSet = new HashSet<string>(currentPorts, StringComparer.OrdinalIgnoreCase);
        var descriptors = SerialPortInfoProvider.QueryAll();

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
            RemoveAssembler(existing.Name);
            Devices.Remove(existing);
        }

        // 加入新插入的口
        var existingNames = new HashSet<string>(
            Devices.Select(d => d.Name), StringComparer.OrdinalIgnoreCase);

        var addedDevices = new List<SerialDeviceInfo>();
        foreach (string port in currentPorts)
        {
            if (existingNames.Contains(port))
                continue;

            var device = CreateDevice(port, descriptors.GetValueOrDefault(port));
            Devices.Add(device);
            addedDevices.Add(device);
        }

        // 选中设备已消失：改选剩余首行，没有则清空，避免工作区悬空指向已移除设备
        if (selectedRemoved)
            SelectedDevice = Devices.FirstOrDefault();

        // 图表数据源设备被拔出：回退到剩余首行，保证图表仍有有效数据源
        if (_workspace.ChartSelectedDevice is not null
            && !Devices.Contains(_workspace.ChartSelectedDevice))
        {
            _workspace.ChartSelectedDevice = Devices.FirstOrDefault();
        }

        LogService.Current.Debug("Serial",
            $"串口刷新完成，当前 {Devices.Count} 个端口，新增 {addedDevices.Count} 个");

        return addedDevices;
    }

    private static SerialDeviceInfo CreateDevice(string port, SerialPortDescriptor? descriptor) => new()
    {
        Name = port,
        // WMI 完整信息：查不到（虚拟串口/WMI 异常）时保留空值，界面 DisplayName 自动回退短名
        FullName = descriptor?.FullName ?? string.Empty,
        Description = descriptor?.Description ?? string.Empty,
        Manufacturer = descriptor?.Manufacturer ?? string.Empty,
        HardwareId = descriptor?.HardwareId ?? string.Empty,
        Vid = descriptor?.Vid,
        Pid = descriptor?.Pid,
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

    /// <summary>
    /// 释放热插拔资源：停止 WMI 监听与去抖定时器。
    /// 已建立的串口连接保持现状（与页面切换时的既有行为一致），不在此强制关闭。
    /// </summary>
    public void Dispose()
    {
        _debounceTimer.Stop();
        _deviceWatcher.DeviceChanged -= OnDeviceChanged;
        _deviceWatcher.Dispose();

        foreach (var assembler in _assemblers.Values)
            assembler.Dispose();
        _assemblers.Clear();
    }
}
