namespace MultiSerialTool.Protocols.Serial;

/// <summary>
/// 帧校验和算法抽象：占位符标记 + 对帧体字节计算校验字节。
/// 新增校验算法只需实现此接口并在 ChecksumEncoder 注册。
/// </summary>
public interface IChecksum
{
    /// <summary>占位符标记（含方括号，大写），如 [CRC16]</summary>
    string Placeholder { get; }

    /// <summary>对帧体字节计算校验结果，返回 1 或 2 个字节（已按协议字节序排列）</summary>
    byte[] Compute(IReadOnlyList<byte> body);
}

/// <summary>Modbus CRC16（多项式 0xA001，低字节在前）</summary>
public sealed class Crc16ModbusChecksum : IChecksum
{
    /// <inheritdoc />
    public string Placeholder => "[CRC16]";

    /// <inheritdoc />
    public byte[] Compute(IReadOnlyList<byte> body)
    {
        ushort crc = Crc16.Compute(body);
        return new[] { (byte)(crc & 0xFF), (byte)(crc >> 8) };
    }
}

/// <summary>CRC-8（多项式 0x07，初始值 0x00）</summary>
public sealed class Crc8Checksum : IChecksum
{
    /// <inheritdoc />
    public string Placeholder => "[CRC8]";

    /// <inheritdoc />
    public byte[] Compute(IReadOnlyList<byte> body)
    {
        byte crc = 0;
        foreach (var b in body)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 0x80) != 0 ? (byte)((crc << 1) ^ 0x07) : (byte)(crc << 1);
        }
        return new[] { crc };
    }
}

/// <summary>单字节异或校验：所有帧体字节依次 XOR</summary>
public sealed class XorChecksum : IChecksum
{
    /// <inheritdoc />
    public string Placeholder => "[XOR]";

    /// <inheritdoc />
    public byte[] Compute(IReadOnlyList<byte> body)
    {
        byte sum = 0;
        foreach (var b in body)
            sum ^= b;
        return new[] { sum };
    }
}

/// <summary>LRC 纵向冗余校验：求和取二进制补码（单字节）</summary>
public sealed class LrcChecksum : IChecksum
{
    /// <inheritdoc />
    public string Placeholder => "[LRC]";

    /// <inheritdoc />
    public byte[] Compute(IReadOnlyList<byte> body)
    {
        byte sum = 0;
        foreach (var b in body)
            sum = (byte)(sum + b);
        return new[] { (byte)(-sum & 0xFF) };
    }
}

/// <summary>单字节累加和校验（SUM8）：对此前全部字节直接求和取低 8 位，不取补码</summary>
public sealed class Sum8Checksum : IChecksum
{
    /// <inheritdoc />
    public string Placeholder => "[SUM]";

    /// <inheritdoc />
    public byte[] Compute(IReadOnlyList<byte> body)
    {
        byte sum = 0;
        foreach (var b in body)
            sum = (byte)(sum + b);
        return new[] { sum };
    }
}

/// <summary>
/// 用户数据区累加和校验（SWIOTT 双层校验内层）：
/// 帧结构为 6 字节帧头 + 1 字节整帧 Length + 用户数据区，
/// 因此用户数据区固定从字节偏移 7 开始，对该偏移起到占位符前的字节求和取低 8 位。
/// </summary>
public sealed class UserDataSum8Checksum : IChecksum
{
    /// <summary>用户数据区在完整帧中的固定起始偏移（6 字节帧头 + 1 字节 Length）</summary>
    public const int UserDataOffset = 7;

    /// <inheritdoc />
    public string Placeholder => "[UDSUM]";

    /// <inheritdoc />
    public byte[] Compute(IReadOnlyList<byte> body)
    {
        byte sum = 0;
        for (var i = UserDataOffset; i < body.Count; i++)
            sum = (byte)(sum + body[i]);
        return new[] { sum };
    }
}
