using System.Windows.Threading;

namespace MultiSerialTool.Protocols.Serial;

/// <summary>
/// 基于“接收空闲间隔”的消息组包器。
/// 串口数据按驱动字节块流式到达，同一设备一次上报的一整组数据（内部含多条 CR/LF）
/// 可能分多次 DataReceived 到达。本类把间隔小于阈值的连续字节累积为同一条消息，
/// 空闲超过阈值（或达到最大长度）时封帧，从而把“同一条消息”保持在一起。
/// 全部在 UI 线程调用（chunk 已由 SerialConnection 封送到 UI 线程），无需额外加锁。
/// </summary>
public sealed class MessageAssembler : IDisposable
{
    /// <summary>单条消息最大字节数，超过则强制封帧，防止设备持续不空闲导致无限累积</summary>
    private const int MaxMessageBytes = 64 * 1024;

    private readonly DispatcherTimer _idleTimer;
    private readonly List<byte> _buffer = new();
    private DateTime _startTime;

    /// <summary>一条完整消息组包完成时触发：参数为消息起始时间与完整字节</summary>
    public event Action<DateTime, byte[]>? MessageCompleted;

    /// <param name="idleGapMs">空闲间隔阈值（毫秒），间隔小于该值的连续数据视为同一条消息</param>
    public MessageAssembler(int idleGapMs)
    {
        _idleTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(idleGapMs <= 0 ? 30 : idleGapMs)
        };
        _idleTimer.Tick += OnIdleTick;
    }

    /// <summary>追加一次到达的字节并重置空闲计时</summary>
    public void Append(DateTime time, byte[] bytes)
    {
        if (bytes.Length == 0)
            return;

        if (_buffer.Count == 0)
            _startTime = time;

        _buffer.AddRange(bytes);
        _idleTimer.Stop();

        // 达到上限立即封帧，避免异常长数据流撑爆内存
        if (_buffer.Count >= MaxMessageBytes)
        {
            Flush();
            return;
        }

        _idleTimer.Start();
    }

    private void OnIdleTick(object? sender, EventArgs e) => Flush();

    /// <summary>封出当前缓冲的完整消息；缓冲为空时不触发</summary>
    private void Flush()
    {
        _idleTimer.Stop();

        if (_buffer.Count == 0)
            return;

        byte[] bytes = _buffer.ToArray();
        DateTime time = _startTime;
        _buffer.Clear();
        MessageCompleted?.Invoke(time, bytes);
    }

    /// <summary>立即封出当前缓冲（连接正常结束时调用，避免丢失最后一帧）</summary>
    public void CompletePending() => Flush();

    public void Dispose()
    {
        _idleTimer.Stop();
        _idleTimer.Tick -= OnIdleTick;
        _buffer.Clear();
    }
}
