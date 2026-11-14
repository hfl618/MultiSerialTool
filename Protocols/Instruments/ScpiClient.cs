using System.IO;
using System.Net.Sockets;
using System.Text;

namespace MultiSerialTool.Protocols.Instruments;

/// <summary>
/// 通用 SCPI 客户端：通过 TCP 裸连接向 LAN 仪器（泰克示波器等）发送 SCPI 指令并读取响应。
/// 零第三方依赖，仅用 .NET 自带 TcpClient。
/// 协议约定：指令后补 '\n'；文本响应以 '\n' 结束；二进制数据为 IEEE488.2 定长块 #&lt;N&gt;&lt;len&gt;&lt;data&gt;。
/// 线程安全：同一连接上的收发用同一把锁串行化，避免轮询与单次采集相互穿插。
/// </summary>
public sealed class ScpiClient : IDisposable
{
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly object _sync = new();

    public string Host { get; private set; } = string.Empty;
    public int Port { get; private set; }

    /// <summary>接收/发送超时（毫秒），超时抛异常由调用方处理</summary>
    public int TimeoutMs { get; set; } = 3000;

    public bool IsConnected => _client?.Connected ?? false;

    public void Connect(string host, int port)
    {
        Disconnect();

        _client = new TcpClient { NoDelay = true };
        _client.Connect(host, port);
        _stream = _client.GetStream();
        _stream.ReadTimeout = TimeoutMs;
        _stream.WriteTimeout = TimeoutMs;

        Host = host;
        Port = port;
    }

    /// <summary>
    /// 带取消/超时的异步连接。Windows 上同步 Connect 对不可达地址可能挂起十几秒，
    /// 由调用方传入超时令牌（如 8 秒）可让连接失败及时反馈到界面。
    /// </summary>
    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        Disconnect();

        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);

            // 长时间轮询波形时保持链路活性，避免路由器/网卡空闲回收
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

            _client = client;
            _stream = client.GetStream();
            _stream.ReadTimeout = TimeoutMs;
            _stream.WriteTimeout = TimeoutMs;

            Host = host;
            Port = port;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public void Disconnect()
    {
        _client?.Close();
        _client = null;
        _stream = null;
    }

    /// <summary>发送无需响应的指令</summary>
    public void Write(string command)
    {
        var stream = GetStream();
        lock (_sync)
            stream.Write(Encoding.ASCII.GetBytes(command + "\n"));
    }

    /// <summary>发送查询并读取一行文本响应（去掉尾部换行）</summary>
    public string Query(string command)
    {
        var stream = GetStream();
        lock (_sync)
        {
            stream.Write(Encoding.ASCII.GetBytes(command + "\n"));
            return ReadLine(stream);
        }
    }

    /// <summary>
    /// 发送查询并读取一行文本响应，本次调用使用指定读写超时（毫秒）。
    /// 用于可选的测量查询：仪器固件不支持该命令时可快速失败，不长时间阻塞波形采集。
    /// 超时在锁内临时修改，不会影响并发中的其他查询。
    /// </summary>
    public string QueryWithTimeout(string command, int timeoutMs)
    {
        var stream = GetStream();
        lock (_sync)
        {
            int oldRead = stream.ReadTimeout;
            int oldWrite = stream.WriteTimeout;
            try
            {
                stream.ReadTimeout = timeoutMs;
                stream.WriteTimeout = timeoutMs;
                stream.Write(Encoding.ASCII.GetBytes(command + "\n"));
                return ReadLine(stream);
            }
            finally
            {
                stream.ReadTimeout = oldRead;
                stream.WriteTimeout = oldWrite;
            }
        }
    }

    /// <summary>
    /// 发送查询并读取二进制块响应（如 CURVE?）。
    /// 块格式：'#' + 1 位数字 N + N 位十进制长度 + 长度字节数据 + 行结束符。
    /// </summary>
    public byte[] QueryBinary(string command)
    {
        var stream = GetStream();
        lock (_sync)
        {
            stream.Write(Encoding.ASCII.GetBytes(command + "\n"));

            int head = ReadByte(stream);
            if (head != '#')
                throw new InvalidDataException($"期望二进制块起始 '#'，实际 0x{head:X2}");

            int digitCount = ReadByte(stream) - '0';
            if (digitCount is < 1 or > 9)
                throw new InvalidDataException($"非法长度位数：{(char)(digitCount + '0')}");

            int length = int.Parse(Encoding.ASCII.GetString(ReadExact(stream, digitCount)));
            byte[] data = ReadExact(stream, length);

            // 块后紧跟行结束符（'\n' 或 '\r\n'），消费掉以保持流对齐，避免污染下一条响应
            int term = ReadByte(stream);
            if (term == '\r')
                ReadByte(stream);

            return data;
        }
    }

    private static string ReadLine(NetworkStream stream)
    {
        var sb = new StringBuilder();
        int b;
        while ((b = stream.ReadByte()) >= 0)
        {
            if (b == '\n')
                break;
            if (b != '\r')
                sb.Append((char)b);
        }
        return sb.ToString();
    }

    private static int ReadByte(NetworkStream stream)
    {
        int b = stream.ReadByte();
        if (b < 0)
            throw new EndOfStreamException("连接在读取响应时被关闭");
        return b;
    }

    private static byte[] ReadExact(NetworkStream stream, int count)
    {
        var buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = stream.Read(buffer, offset, count - offset);
            if (read <= 0)
                throw new EndOfStreamException("连接在读取二进制数据时被关闭");
            offset += read;
        }
        return buffer;
    }

    private NetworkStream GetStream()
        => _stream ?? throw new InvalidOperationException("未连接，请先 Connect");

    public void Dispose() => Disconnect();
}
