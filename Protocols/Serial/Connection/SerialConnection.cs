using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using MultiSerialTool.Services;
using MultiSerialTool.Shared;

namespace MultiSerialTool.Protocols.Serial;

/// <summary>
/// 单个串口的真实连接：自动波特率探测、打开/关闭、数据接收与存储。
/// 连接状态（DeviceState）由外层 ViewModel 维护，本类只负责端口与数据。
/// 所有后台事件路径均不向外抛异常，故障通过 Faulted 事件上报。
/// </summary>
public sealed class SerialConnection : IDisposable
{
    /// <summary>自动探测的候选波特率，按现场使用频率排序</summary>
    private static readonly int[] CandidateBaudRates =
        { 115200, 57600, 38400, 19200, 9600, 4800, 2400, 1200 };

    /// <summary>每个候选速率的探测监听时长（毫秒）：只用于判断首选速率是否正确，
    /// 收到足够样本会提前结束，避免连接时固定等待</summary>
    private const int ProbeDurationMs = 350;

    /// <summary>原始字节环形缓冲的容量上限（1 MB），超出后丢弃最旧字节</summary>
    private const int MaxBufferedBytes = 1024 * 1024;

    private readonly string _portName;
    private readonly int _preferredBaudRate;
    private SerialPort? _port;

    /// <summary>写入门闩：同一端口只允许一个写者，避免后台批量下发时互相挤占驱动缓冲</summary>
    private readonly object _writeGate = new();

    /// <summary>原始字节流缓冲，线程安全</summary>
    private readonly ConcurrentQueue<byte> _rawBuffer = new();

    /// <summary>分块接收记录（每次 DataReceived 一条），在 UI 线程上更新</summary>
    public ObservableCollection<SerialRawChunk> ReceivedChunks { get; } = new();

    /// <summary>每收到一块数据时触发，回调已封送到 UI 线程</summary>
    public event Action<SerialRawChunk>? ChunkReceived;

    /// <summary>连接运行期发生故障（拔线、IO 错误、帧错误等）时在 UI 线程触发</summary>
    public event Action<Exception>? Faulted;

    /// <summary>当前实际生效的波特率（探测完成后回填）</summary>
    public int BaudRate { get; private set; }

    /// <param name="portName">端口名</param>
    /// <param name="preferredBaudRate">
    /// 设备指定/用户配置的优先波特率。自动探测全程无有效数据（设备不主动上报）时
    /// 保留该速率，不再盲目回退固定 9600，避免误判导致连接后乱码、收发失败。
    /// </param>
    public SerialConnection(string portName, int preferredBaudRate = 9600)
    {
        _portName = portName;
        _preferredBaudRate = preferredBaudRate > 0 ? preferredBaudRate : 9600;
    }

    /// <summary>
    /// 自动探测波特率并打开端口，订阅接收事件。
    /// 端口被占用等异常直接抛出，由上层捕获并提示，不会导致进程退出。
    /// </summary>
    public void Open()
    {
        // 先用首选速率打开并监听：设备静默（收不到字节）或数据质量已确认时，
        // 直接沿用这次打开的端口。部分 USB 转串设备（如带 DTR 控制的网关）
        // 在关闭再重开后要十几秒才恢复发送，多一次 Open/Close 就会表现为“连上收不到数据”。
        var port = OpenPortAt(_portName, _preferredBaudRate);
        var (sampleData, frameErrors) = Sample(port, ProbeDurationMs);
        var score = ScoreData(sampleData, frameErrors);

        int baud = _preferredBaudRate;
        if (sampleData.Count > 0 && score < 85)
        {
            // 首选速率收到的数据质量差（波特率不匹配）：换其余候选速率探测
            port.Dispose();
            baud = ProbeOtherRates(_portName, _preferredBaudRate);
            port = OpenPortAt(_portName, baud);
        }

        BaudRate = baud;
        _port = port;

        // 部分 USB 转串驱动在 Open 瞬间会先把控制线按默认态上线，再被设置项覆盖；
        // Open 之后按配置重新落一次控制线状态，避免残留高电平边沿拉低 BOOT/EN。
        ApplyControlLines(port);
        port.DataReceived += OnDataReceived;
        port.ErrorReceived += OnErrorReceived;

        var cfg = AppConfig.Load();
        if (cfg.AutoDtrEnable && !cfg.DtrEnable)
            LogService.Current.Info("Serial",
                $"{_portName} 打开端口时已自动使能 DTR（可在设置-控制线中关闭）");
    }

    /// <summary>按指定速率打开端口，把驱动异常翻译成用户可读提示</summary>
    private static SerialPort OpenPortAt(string portName, int baudRate)
    {
        var port = CreatePort(portName, baudRate);
        try
        {
            port.Open();
            ApplyControlLines(port);
            return port;
        }
        catch (UnauthorizedAccessException ex)
        {
            port.Dispose();
            throw new UnauthorizedAccessException(
                $"串口 {portName} 已被其他程序占用，请关闭占用该端口的软件后重试。", ex);
        }
        catch (IOException ex)
        {
            port.Dispose();
            throw new IOException(
                $"串口 {portName} 打开失败，设备可能已拔出或驱动异常：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 在指定时长内收集端口到达的字节与帧错误计数。
    /// 样本足够（16 字节且无帧错误）即提前结束，缩短连接时的固定等待。
    /// </summary>
    private static (List<byte> Data, int FrameErrors) Sample(SerialPort port, int durationMs)
    {
        int frameErrors = 0;
        void OnProbeError(object? s, SerialErrorReceivedEventArgs ea)
        {
            if (ea.EventType is SerialError.Frame or SerialError.RXParity or SerialError.Overrun)
                Interlocked.Increment(ref frameErrors);
        }

        port.ErrorReceived += OnProbeError;

        var data = new List<byte>();
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < durationMs)
        {
            Thread.Sleep(20);

            int available;
            try
            {
                available = port.BytesToRead;
            }
            catch (IOException)
            {
                break;
            }

            if (available <= 0)
                continue;

            var buffer = new byte[available];
            int read;
            try
            {
                read = port.Read(buffer, 0, available);
            }
            catch (IOException)
            {
                break;
            }

            data.AddRange(buffer.Take(read));

            if (data.Count >= 16 && Volatile.Read(ref frameErrors) == 0)
                break;
        }

        port.ErrorReceived -= OnProbeError;
        return (data, frameErrors);
    }

    /// <summary>关闭端口，停止接收；任何清理异常都吞掉，保证不向外抛</summary>
    public void Close()
    {
        var port = _port;
        _port = null;
        if (port is null)
            return;

        try
        {
            port.DataReceived -= OnDataReceived;
            port.ErrorReceived -= OnErrorReceived;
        }
        catch
        {
            // 退订失败不影响后续关闭
        }

        try
        {
            // 关闭前先把控制线按配置复位（默认禁用），避免 Close/Dispose 阶段
            // 控制线跳变再次产生复位边沿。
            ApplyControlLines(port);
            if (port.IsOpen)
                port.Close();
        }
        catch
        {
            // 驱动层已失效时 Close 可能抛错，忽略以保证 Dispose 安全
        }
        finally
        {
            port.Dispose();
        }
    }

    /// <summary>
    /// 首选速率数据质量不佳时，按候选速率逐个打开监听并评分，返回最优速率。
    /// 都不优于首选时保留首选速率。
    /// </summary>
    private static int ProbeOtherRates(string portName, int preferredBaudRate)
    {
        int bestBaud = preferredBaudRate;
        int bestScore = 0;

        foreach (int baud in CandidateBaudRates.Where(b => b != preferredBaudRate))
        {
            using var probe = OpenPortAt(portName, baud);
            var (data, frameErrors) = Sample(probe, ProbeDurationMs);

            int score = ScoreData(data, frameErrors);
            if (score > bestScore)
            {
                bestScore = score;
                bestBaud = baud;
            }

            // 高置信度命中（无帧错误且数据特征明确），提前结束探测
            if (bestScore >= 85)
                break;

            // 该速率一个字节都没收到：设备没在发数据，其余候选同样收不到，
            // 继续探测只会多出 Open/Close 的控制线边沿，可能把目标板打进烧录模式
            if (data.Count == 0)
                break;
        }

        return bestBaud;
    }

    /// <summary>
    /// 数据质量评分（同时适用于文本与二进制帧）：
    /// 硬件帧错误是波特率不匹配的最可靠信号，每个重罚；
    /// 有数据且无帧错误给基础分，再叠加 ASCII 可打印比例与换行帧特征。
    /// 样本不足 8 字节视为无法判断，返回 0。
    /// </summary>
    private static int ScoreData(List<byte> data, int frameErrors)
    {
        if (data.Count < 8)
            return 0;

        // 帧错误直接淘汰该速率
        if (frameErrors > 0)
            return Math.Max(0, 20 - frameErrors * 20);

        int printable = data.Count(b =>
            b is (>= 0x20 and <= 0x7E) or 0x0A or 0x0D or 0x09);
        int garbage = data.Count(b => b is 0x00 or 0xFF);

        double printableRatio = (double)printable / data.Count;
        double garbageRatio = (double)garbage / data.Count;

        // 无帧错误且收到稳定数据：基础 55 分，再按内容特征调整
        int score = 55 + (int)((printableRatio - garbageRatio) * 40);
        if (data.Any(b => b is 0x0A or 0x0D))
            score += 10;

        return Math.Clamp(score, 0, 100);
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            if (_port is null || !_port.IsOpen)
                return;

            int available = _port.BytesToRead;
            if (available <= 0)
                return;

            var buffer = new byte[available];
            int read = _port.Read(buffer, 0, available);
            if (read <= 0)
                return;

            var actual = buffer[..read];
            AppendRawBytes(actual);

            var chunk = new SerialRawChunk
            {
                Time = DateTime.Now,
                Bytes = actual
            };

            DispatchOnUi(() =>
            {
                ReceivedChunks.Add(chunk);
                ChunkReceived?.Invoke(chunk);
            });
        }
        catch (Exception ex)
        {
            // 后台线程异常绝不能逃逸，否则会触发进程终止；统一转故障上报
            RaiseFaulted(ex);
        }
    }

    private void OnErrorReceived(object sender, SerialErrorReceivedEventArgs e)
    {
        // 帧错误/奇偶错误通常意味着波特率不匹配或线路故障
        RaiseFaulted(new IOException($"{_portName} 串口线路错误：{e.EventType}，请检查波特率与接线"));
    }

    private void RaiseFaulted(Exception ex)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        if (dispatcher.CheckAccess())
            Faulted?.Invoke(ex);
        else
            dispatcher.BeginInvoke(new Action(() => Faulted?.Invoke(ex)));
    }

    private static void DispatchOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.Invoke(action);
    }

    /// <summary>写入原始字节环形缓冲，超容量时丢弃最旧数据</summary>
    private void AppendRawBytes(byte[] bytes)
    {
        foreach (byte b in bytes)
            _rawBuffer.Enqueue(b);

        while (_rawBuffer.Count > MaxBufferedBytes)
            _rawBuffer.TryDequeue(out _);
    }

    /// <summary>取出当前缓冲区内的全部原始字节（不清空）</summary>
    public byte[] GetBufferedBytes() => _rawBuffer.ToArray();

    /// <summary>
    /// 向端口写入文本（按 UTF-8 编码，兼容 ASCII 指令）。
    /// 写入失败不向外抛，转 Faulted 事件由上层处理。
    /// </summary>
    public void Write(string text)
        => WriteBytes(System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>向端口写入原始字节（HEX 帧使用）</summary>
    public void WriteBytes(byte[] bytes)
    {
        // 取本地快照，避免写入途中连接被关闭导致空引用
        var port = _port;
        if (port is null || !port.IsOpen)
            throw new InvalidOperationException($"{_portName} 未连接，无法发送");

        lock (_writeGate)
            port.Write(bytes, 0, bytes.Length);
    }

    /// <summary>按配置把 DTR/RTS 控制线状态重新落到端口上（默认禁用），忽略驱动层异常</summary>
    private static void ApplyControlLines(SerialPort port)
    {
        var cfg = AppConfig.Load();
        try
        {
            port.DtrEnable = cfg.DtrEnable || cfg.AutoDtrEnable;
            port.RtsEnable = cfg.RtsEnable;
        }
        catch
        {
            // 端口句柄失效时控制线设置可能抛错，忽略，不影响打开/关闭流程
        }
    }

    private static SerialPort CreatePort(string portName, int baudRate)
    {
        var cfg = AppConfig.Load();

        var parity = cfg.Parity switch
        {
            "Odd" => Parity.Odd,
            "Even" => Parity.Even,
            _ => Parity.None
        };
        var stopBits = cfg.StopBits == "Two" ? StopBits.Two : StopBits.One;
        var dataBits = cfg.DataBits is 7 or 8 ? cfg.DataBits : 8;

        return new SerialPort
        {
            PortName = portName,
            BaudRate = baudRate,
            DataBits = dataBits,
            Parity = parity,
            StopBits = stopBits,
            ReadTimeout = 500,
            WriteTimeout = 500,
            // 控制线默认关闭（见 AppConfig）：纯 UART 收发不主动驱动 DTR/RTS，
            // 避免开/关串口时的电平边沿把目标板 BOOTSEL/BOOT0/EN 拉低，
            // 导致设备复位采样到下载模式而枚举成 RPI-RP2 烧写盘。
            DtrEnable = cfg.DtrEnable || cfg.AutoDtrEnable,
            RtsEnable = cfg.RtsEnable
        };
    }

    public void Dispose() => Close();
}
