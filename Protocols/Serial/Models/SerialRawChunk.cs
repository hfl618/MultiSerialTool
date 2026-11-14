using System.Text;

namespace MultiSerialTool.Protocols.Serial;

/// <summary>RX 单元类型：二进制定长帧 / ASCII 行 / 空闲兜底原始块</summary>
public enum RxUnitKind
{
    /// <summary>按帧头 + Length 取出的二进制定长帧</summary>
    Binary,

    /// <summary>按 CR/LF 切出的一行 ASCII 文本</summary>
    TextLine,

    /// <summary>无帧头、无行尾，由空闲超时兜底封出的原始块</summary>
    Raw
}

/// <summary>
/// 串口单次接收到的原始数据块：同时保留原始字节、HEX 与文本三种形态。
/// </summary>
public sealed class SerialRawChunk
{
    /// <summary>接收时刻</summary>
    public DateTime Time { get; init; }

    /// <summary>单元类型（二进制帧 / 文本行 / 原始块）</summary>
    public RxUnitKind Kind { get; init; } = RxUnitKind.Raw;

    /// <summary>原始字节</summary>
    public byte[] Bytes { get; init; } = Array.Empty<byte>();

    /// <summary>字节数</summary>
    public int Length => Bytes.Length;

    /// <summary>十六进制表示，如 "41 54 0D"</summary>
    public string Hex => BitConverter.ToString(Bytes).Replace('-', ' ');

    /// <summary>ASCII 文本表示（不可打印字符以 '.' 占位）</summary>
    public string Text
    {
        get
        {
            var view = new byte[Bytes.Length];
            for (int i = 0; i < Bytes.Length; i++)
            {
                byte b = Bytes[i];
                view[i] = b is (>= 0x20 and <= 0x7E) or 0x0A or 0x0D or 0x09
                    ? b
                    : (byte)'.';
            }
            return Encoding.ASCII.GetString(view);
        }
    }
}
