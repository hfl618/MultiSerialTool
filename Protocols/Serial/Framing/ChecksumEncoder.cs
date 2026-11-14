using System.Text.RegularExpressions;

namespace MultiSerialTool.Protocols.Serial;

/// <summary>
/// 帧校验编码器：扫描 HEX 帧中的校验占位符并按顺序替换为真实字节。
/// 支持一帧内多个占位符；每个占位符基于此前已构建的全部字节计算。
/// </summary>
public static class ChecksumEncoder
{
    /// <summary>已注册的校验算法（按占位符索引，大小写不敏感）</summary>
    private static readonly Dictionary<string, IChecksum> Algorithms =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["[CRC16]"] = new Crc16ModbusChecksum(),
            ["[CRC8]"] = new Crc8Checksum(),
            ["[XOR]"] = new XorChecksum(),
            ["[LRC]"] = new LrcChecksum(),
            ["[SUM]"] = new Sum8Checksum(),
            ["[UDSUM]"] = new UserDataSum8Checksum()
        };

    /// <summary>占位符正则：匹配方括号内的算法名</summary>
    private static readonly Regex PlaceholderRegex =
        new(@"\[(CRC16|CRC8|XOR|LRC|SUM|UDSUM)\]", RegexOptions.IgnoreCase);

    /// <summary>注册自定义校验算法（扩展点）</summary>
    public static void Register(IChecksum checksum)
        => Algorithms[checksum.Placeholder] = checksum;

    /// <summary>文本中是否含已注册的校验占位符</summary>
    public static bool ContainsPlaceholder(string? frame)
        => !string.IsNullOrEmpty(frame) && PlaceholderRegex.IsMatch(frame);

    /// <summary>
    /// 编码 HEX 帧：替换全部校验占位符，返回归一化 HEX 文本。
    /// 无占位符时仅做归一化。
    /// </summary>
    public static string EncodeHex(string hexFrame)
    {
        if (string.IsNullOrWhiteSpace(hexFrame))
            return FrameMatcher.NormalizeHex(hexFrame);

        var bytes = new List<byte>();
        var lastIndex = 0;
        var matches = PlaceholderRegex.Matches(hexFrame);

        foreach (Match m in matches)
        {
            // 占位符之前的文本：帧体字节
            var before = hexFrame[lastIndex..m.Index];
            bytes.AddRange(Crc16.HexToBytes(FrameMatcher.NormalizeHex(before)));
            lastIndex = m.Index + m.Length;

            var token = m.Value;
            if (!Algorithms.TryGetValue(token, out var algorithm))
            {
                Services.LogService.Current.Warn("Serial", $"未知校验占位符：{token}");
                continue;
            }
            bytes.AddRange(algorithm.Compute(bytes));
        }

        // 末尾剩余字节
        var tail = hexFrame[lastIndex..];
        if (!string.IsNullOrWhiteSpace(tail))
            bytes.AddRange(Crc16.HexToBytes(FrameMatcher.NormalizeHex(tail)));

        return string.Join(' ', bytes.Select(b => b.ToString("X2")));
    }
}
