using System.Text;

namespace MultiSerialTool.Protocols.Serial;

/// <summary>
/// 一次上报的归组结果：把近乎同时到达、同属一条上报的若干 RX 单元（多行 ASCII
/// 或单个二进制帧）聚合在一起，供规则解析与图表按“一条记录”处理。
/// </summary>
public sealed class ReportGroup
{
    /// <summary>组起始时间（取首个单元时间）</summary>
    public DateTime Time { get; init; }

    /// <summary>组内单元，按到达顺序</summary>
    public List<SerialRawChunk> Units { get; } = new();

    /// <summary>合并文本（行以 LF 拼接），供 regex 解析与图表匹配</summary>
    public string CombinedText { get; private set; } = string.Empty;

    /// <summary>合并 HEX（单元间空格分隔），供应答/断言匹配</summary>
    public string CombinedHex { get; private set; } = string.Empty;

    /// <summary>主帧字节：二进制帧取该帧；纯文本组取首行字节，供 offset 解析</summary>
    public byte[] PrimaryBytes { get; private set; } = Array.Empty<byte>();

    /// <summary>是否为独立二进制帧组（不与后续 ASCII 行合并）</summary>
    public bool IsBinary { get; private set; }

    /// <summary>追加一个单元并重算聚合视图</summary>
    public void Add(SerialRawChunk unit)
    {
        Units.Add(unit);

        CombinedText = string.Join("\n", Units.Select(u => u.Text));
        CombinedHex = string.Join(' ', Units.Select(u => u.Hex));

        if (PrimaryBytes.Length == 0)
            PrimaryBytes = unit.Bytes;

        if (unit.Kind == RxUnitKind.Binary)
            IsBinary = true;
    }
}
