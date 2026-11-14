using System.Windows.Threading;

namespace MultiSerialTool.Protocols.Serial;

/// <summary>
/// 串口字节流成帧器：把驱动按块到达的字节流拆成可独立显示的 RX 单元。
/// ① 二进制定长帧：识别帧头 53 57 49 4F 54 54，按 offset6 的 Length 取整帧，
///    数据区即使含 0x0A/0x0D 也不会被当换行拆断；
/// ② ASCII 文本：按 CR LF / LF / CR 切行，行内不含终止符；
/// ③ 兜底：既无帧头又无行尾时，由空闲超时把残留缓冲作为原始块封出。
/// 全部在 UI 线程调用（chunk 已由 SerialConnection 封送到 UI 线程），无需额外加锁。
/// </summary>
public sealed class StreamFramer : IDisposable
{
    /// <summary>SCx 二进制帧头 "SWIOTT"</summary>
    private static readonly byte[] FrameHeader = { 0x53, 0x57, 0x49, 0x4F, 0x54, 0x54 };

    /// <summary>单条消息最大字节数，超过则强制封帧，防止设备持续不空闲导致无限累积</summary>
    private const int MaxMessageBytes = 64 * 1024;

    /// <summary>二进制帧最小长度：6 帧头 + 1 Length + 命令 + 内层和 + 外层和</summary>
    private const int MinFrameLength = 8;

    private readonly DispatcherTimer _idleTimer;
    private readonly List<byte> _buffer = new();
    private DateTime _startTime;

    /// <summary>一个 RX 单元完成时触发：参数为起始时间、字节、单元类型</summary>
    public event Action<DateTime, byte[], RxUnitKind>? UnitCompleted;

    /// <param name="idleGapMs">空闲兜底阈值（毫秒），间隔超过该值仍未闭合行/帧则封出残留块</param>
    public StreamFramer(int idleGapMs)
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

        if (_buffer.Count >= MaxMessageBytes)
        {
            FlushRaw();
            return;
        }

        ProcessBuffer();

        _idleTimer.Stop();
        _idleTimer.Start();
    }

    private void ProcessBuffer()
    {
        while (_buffer.Count > 0)
        {
            if (StartsWithHeader())
            {
                // 等 Length 字节可读
                if (_buffer.Count < FrameHeader.Length + 1)
                    return;

                int length = _buffer[FrameHeader.Length];
                if (length < MinFrameLength || length > MaxMessageBytes)
                {
                    // 帧头形似但长度非法：丢掉首字节重新同步，避免死锁在伪帧头
                    _buffer.RemoveAt(0);
                    continue;
                }

                if (_buffer.Count < length)
                    return; // 等整帧到齐

                var frame = _buffer.Take(length).ToArray();
                _buffer.RemoveRange(0, length);
                Emit(_startTime, frame, RxUnitKind.Binary);
                continue;
            }

            var (lineEnd, termLen) = FindLineEnd();
            if (lineEnd < 0)
                return; // 等剩余字节闭合行

            var line = _buffer.Take(lineEnd).ToArray();
            _buffer.RemoveRange(0, lineEnd + termLen);
            if (line.Length > 0)
                Emit(_startTime, line, RxUnitKind.TextLine);
        }
    }

    private bool StartsWithHeader()
    {
        if (_buffer.Count < FrameHeader.Length)
            return false;

        for (int i = 0; i < FrameHeader.Length; i++)
        {
            if (_buffer[i] != FrameHeader[i])
                return false;
        }
        return true;
    }

    /// <summary>定位最早的行终止符；返回行尾下标与终止符长度（未找到返回 -1, 0）</summary>
    private (int Index, int TermLen) FindLineEnd()
    {
        for (int i = 0; i < _buffer.Count; i++)
        {
            if (_buffer[i] == 0x0D)
            {
                if (i + 1 < _buffer.Count && _buffer[i + 1] == 0x0A)
                    return (i, 2);
                return (i, 1);
            }
            if (_buffer[i] == 0x0A)
                return (i, 1);
        }
        return (-1, 0);
    }

    private void OnIdleTick(object? sender, EventArgs e) => FlushRaw();

    /// <summary>把当前缓冲作为兜底原始块封出；缓冲为空时不触发</summary>
    private void FlushRaw()
    {
        _idleTimer.Stop();

        if (_buffer.Count == 0)
            return;

        byte[] bytes = _buffer.ToArray();
        DateTime time = _startTime;
        _buffer.Clear();
        Emit(time, bytes, RxUnitKind.Raw);
    }

    private void Emit(DateTime time, byte[] bytes, RxUnitKind kind)
    {
        if (_buffer.Count == 0)
            _startTime = time;
        UnitCompleted?.Invoke(time, bytes, kind);
    }

    /// <summary>立即封出当前缓冲（连接正常结束时调用，避免丢失最后半行/半帧）</summary>
    public void CompletePending() => FlushRaw();

    public void Dispose()
    {
        _idleTimer.Stop();
        _idleTimer.Tick -= OnIdleTick;
        _buffer.Clear();
    }
}
