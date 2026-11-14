using System.Windows;
using MultiSerialTool.Commands;
using MultiSerialTool.ViewModels;

namespace MultiSerialTool.Protocols.Instruments.TekScope;

/// <summary>
/// 泰克示波器页面 ViewModel：TCP 连接 + 后台轮询采集波形，支持实时滚动与暂停。
/// 采集在单个后台任务中按固定周期执行 WFMOUTPRE?/CURVE?，解码后封送到 UI 线程更新当前帧。
/// 连接与采集的异常统一在后台捕获，不向事件循环抛异常。
/// </summary>
public class TekScopeViewModel : ProtocolViewModelBase, IDisposable
{
    /// <summary>泰克 LAN 仪器的 SCPI 裸 TCP 端口</summary>
    private const int DefaultPort = 4000;

    private readonly ScpiClient _client = new();
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;

    private string _host = "192.168.0.2";
    private int _port = DefaultPort;
    private string _channel = "CH1";
    private int _intervalMs = 200;
    private bool _isConnected;
    private bool _isAcquiring;
    private bool _isPaused;
    private Waveform? _current;
    private string _status = "未连接";
    private int _frameCount;

    public TekScopeViewModel()
    {
        Title = "示波器";
        Workspace = this;

        ConnectCommand = new RelayCommand(() => Connect(), () => !IsConnected);
        DisconnectCommand = new RelayCommand(Disconnect, () => IsConnected);
        StartCommand = new RelayCommand(StartAcquisition, () => IsConnected && !IsAcquiring);
        StopCommand = new RelayCommand(StopAcquisition, () => IsAcquiring);
        PauseCommand = new RelayCommand(TogglePause, () => IsAcquiring);
        SingleShotCommand = new RelayCommand(() => CaptureSingle(), () => IsConnected && !IsAcquiring);
    }

    public IReadOnlyList<string> Channels { get; } = new[] { "CH1", "CH2", "CH3", "CH4" };

    public string Host
    {
        get => _host;
        set => SetProperty(ref _host, value);
    }

    public int Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }

    public string Channel
    {
        get => _channel;
        set => SetProperty(ref _channel, value);
    }

    /// <summary>轮询间隔（毫秒），越小刷新越快，但受示波器采集与传输速率限制</summary>
    public int IntervalMs
    {
        get => _intervalMs;
        set => SetProperty(ref _intervalMs, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set { if (SetProperty(ref _isConnected, value)) RefreshCommands(); }
    }

    public bool IsAcquiring
    {
        get => _isAcquiring;
        private set { if (SetProperty(ref _isAcquiring, value)) RefreshCommands(); }
    }

    public bool IsPaused
    {
        get => _isPaused;
        private set { if (SetProperty(ref _isPaused, value)) RaisePropertyChanged(nameof(PauseText)); }
    }

    /// <summary>当前最新一帧，图表绑定此属性即可刷新</summary>
    public Waveform? Current
    {
        get => _current;
        private set => SetProperty(ref _current, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public int FrameCount
    {
        get => _frameCount;
        private set => SetProperty(ref _frameCount, value);
    }

    /// <summary>暂停按钮文字，随暂停状态切换</summary>
    public string PauseText => IsPaused ? "继续" : "暂停";

    public RelayCommand ConnectCommand { get; }
    public RelayCommand DisconnectCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand SingleShotCommand { get; }

    private void RefreshCommands()
    {
        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        PauseCommand.RaiseCanExecuteChanged();
        SingleShotCommand.RaiseCanExecuteChanged();
    }

    private async void Connect()
    {
        try
        {
            Status = $"正在连接 {Host}:{Port} ...";
            string idn = await Task.Run(() =>
            {
                _client.Connect(Host, Port);
                return _client.Query("*IDN?");
            });

            IsConnected = true;
            Status = $"已连接：{idn}";
        }
        catch (Exception ex)
        {
            _client.Disconnect();
            IsConnected = false;
            Status = $"连接失败：{ex.Message}";
            ShowError(ex.Message, "连接失败");
        }
    }

    private void Disconnect()
    {
        StopAcquisition();
        _client.Disconnect();
        IsConnected = false;
        Status = "未连接";
    }

    private void StartAcquisition()
    {
        if (!IsConnected || IsAcquiring)
            return;

        IsPaused = false;
        IsAcquiring = true;
        Status = $"开始采集 {Channel}（约 {1000.0 / IntervalMs:0.#} Hz）...";

        _pollCts = new CancellationTokenSource();
        var ct = _pollCts.Token;
        _pollTask = RunPollLoop(ct);
    }

    private void StopAcquisition()
    {
        if (!IsAcquiring)
            return;

        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;

        // 等采集任务退出，避免它继续写 Current；超时则交由 Disconnect 关闭套接字收尾
        try { _pollTask?.Wait(2000); } catch { /* 取消/关闭导致的等待异常忽略 */ }
        _pollTask = null;

        IsAcquiring = false;
        IsPaused = false;
        Status = $"已停止采集（共 {FrameCount} 帧）";
    }

    private void TogglePause()
    {
        if (!IsAcquiring)
            return;

        IsPaused = !IsPaused;
        Status = IsPaused ? "已暂停" : "继续采集";
    }

    private async void CaptureSingle()
    {
        if (IsAcquiring)
            return;

        try
        {
            Status = "单次采集...";
            var wave = await Task.Run(() => AcquireFrame());
            ApplyFrame(wave);
        }
        catch (Exception ex)
        {
            Status = $"单次采集失败：{ex.Message}";
        }
    }

    private async Task RunPollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!IsPaused)
            {
                try
                {
                    var wave = await Task.Run(() => AcquireFrame(), ct);
                    PublishFrame(wave);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // 用户主动停止时套接字被关闭，读操作抛错属预期，不误报为故障
                    if (ct.IsCancellationRequested)
                        break;
                    RaiseFaulted(ex);
                    break;
                }
            }

            try
            {
                await Task.Delay(IntervalMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private Waveform AcquireFrame()
    {
        _client.Write($":DATA:SOURCE {Channel}; :DATA:ENCdg RIBINARY; :DATA:WIDTH 1");
        string preamble = _client.Query(":WFMOUTPRE?");
        byte[] data = _client.QueryBinary(":CURVE?");
        return Mdo3000WaveformDecoder.Decode(Channel, preamble, data);
    }

    private void PublishFrame(Waveform wave)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            ApplyFrame(wave);
        else
            dispatcher.BeginInvoke(new Action(() => ApplyFrame(wave)));
    }

    private void ApplyFrame(Waveform wave)
    {
        Current = wave;
        FrameCount++;
        Status = $"{Channel} 第 {FrameCount} 帧 · {wave.PointCount} 点";
    }

    private void RaiseFaulted(Exception ex)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        void Notify()
        {
            IsAcquiring = false;
            Status = $"采集异常：{ex.Message}";
        }

        if (dispatcher is null || dispatcher.CheckAccess())
            Notify();
        else
            dispatcher.BeginInvoke(new Action(Notify));
    }

    private static void ShowError(string message, string title)
        => System.Windows.MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public void Dispose()
    {
        StopAcquisition();
        _client.Dispose();
    }
}
