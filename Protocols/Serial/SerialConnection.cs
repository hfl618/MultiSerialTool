using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
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

    /// <summary>每个候选速率的探测监听时长（毫秒），保证至少收到一帧</summary>
    private const int ProbeDurationMs = 600;

    /// <summary>原始字节环形缓冲的容量上限（1 MB），超出后丢弃最旧字节</summary>
    private const int MaxBufferedBytes = 1024 * 1024;

    private readonly string _portName;
    private SerialPort? _port;

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

    public SerialConnection(string portName)
    {
        _portName = portName;
    }

    /// <summary>
    /// 自动探测波特率并打开端口，订阅接收事件。
    /// 端口被占用等异常直接抛出，由上层捕获并提示，不会导致进程退出。
    /// </summary>
    public void Open()
    {
        BaudRate = DetectBaudRate(_portName);

        _port = CreatePort(_portName, BaudRate);
        _port.Open();
        _port.DataReceived += OnDataReceived;
        _port.ErrorReceived += OnErrorReceived;
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
    /// 自动波特率探测：逐速率打开监听，按接收数据质量评分，取最高分。
    /// 全程无有效数据时回退 9600。
    /// </summary>
    private static int DetectBaudRate(string portName)
    {
        int bestBaud = 9600;
        int bestScore = 0;

        foreach (int baud in CandidateBaudRates)
        {
            using var probe = CreatePort(portName, baud);
            int frameErrors = 0;
            void OnProbeError(object? s, SerialErrorReceivedEventArgs ea)
            {
                if (ea.EventType is SerialError.Frame or SerialError.RXParity or SerialError.Overrun)
                    Interlocked.Increment(ref frameErrors);
            }

            try
            {
                probe.Open();
            }
            catch (UnauthorizedAccessException ex)
            {
                // 串口被其他程序独占占用，任何波特率都无法打开，直接终止探测
                throw new UnauthorizedAccessException(
                    $"串口 {portName} 已被其他程序占用，请关闭占用该端口的软件后重试。", ex);
            }
            catch (IOException ex)
            {
                throw new IOException(
                    $"串口 {portName} 打开失败，设备可能已拔出或驱动异常：{ex.Message}", ex);
            }

            probe.ErrorReceived += OnProbeError;

            var data = new List<byte>();
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < ProbeDurationMs)
            {
                Thread.Sleep(50);

                int available;
                try
                {
                    available = probe.BytesToRead;
                }
                catch (IOException)
                {
                    // 监听期间设备失效，按当前无数据处理，继续下一个候选速率
                    break;
                }

                if (available <= 0)
                    continue;

                var buffer = new byte[available];
                int read;
                try
                {
                    read = probe.Read(buffer, 0, available);
                }
                catch (IOException)
                {
                    break;
                }

                data.AddRange(buffer.Take(read));
            }

            probe.ErrorReceived -= OnProbeError;

            int score = ScoreData(data, frameErrors);
            if (score > bestScore)
            {
                bestScore = score;
                bestBaud = baud;
            }

            // 高置信度命中（无帧错误且数据特征明确），提前结束探测
            if (bestScore >= 85)
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
    {
        if (_port is null || !_port.IsOpen)
            throw new InvalidOperationException($"{_portName} 未连接，无法发送");

        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
        _port.Write(bytes, 0, bytes.Length);
    }

    private static SerialPort CreatePort(string portName, int baudRate) => new()
    {
        PortName = portName,
        BaudRate = baudRate,
        DataBits = 8,
        Parity = Parity.None,
        StopBits = StopBits.One,
        ReadTimeout = 500,
        WriteTimeout = 500,
        DtrEnable = true,
        RtsEnable = true
    };

    public void Dispose() => Close();
}
