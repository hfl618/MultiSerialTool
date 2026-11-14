namespace MultiSerialTool.Protocols.Serial;

/// <summary>
/// Modbus RTU CRC16 计算与帧占位符替换。
/// 快捷发送/自动应答帧中的 [CRC16] 标记在发送前替换为真实校验（低字节在前）。
/// </summary>
public static class Crc16
{
    /// <summary>对字节序列计算 Modbus CRC16</summary>
    public static ushort Compute(IReadOnlyList<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                bool lsb = (crc & 0x0001) != 0;
                crc >>= 1;
                if (lsb)
                    crc ^= 0xA001;
            }
        }
        return crc;
    }

    /// <summary>
    /// 替换 HEX 帧中的校验占位符。委托给 ChecksumEncoder，
    /// 除 [CRC16] 外同时支持 [CRC8]/[XOR]/[LRC]，无占位符时仅归一化。
    /// </summary>
    public static string ReplacePlaceholder(string hexFrame)
        => ChecksumEncoder.EncodeHex(hexFrame);

    /// <summary>归一化 HEX 文本转字节数组</summary>
    public static byte[] HexToBytes(string normalizedHex)
    {
        var tokens = normalizedHex.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bytes = new byte[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
            bytes[i] = Convert.ToByte(tokens[i], 16);
        return bytes;
    }
}
