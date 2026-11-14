namespace MultiSerialTool.Protocols.Serial;

/// <summary>
/// 一次发送载荷：文本帧与 HEX 帧统一载体。
/// IsHex=true 时 Text 为归一化 HEX 串，接收侧按字节写出；
/// false 时 Text 为 ASCII 原文，按 UTF-8 写出。
/// </summary>
public sealed record SerialTxPayload(string Text, bool IsHex)
{
    /// <summary>创建文本帧</summary>
    public static SerialTxPayload FromText(string text) => new(text, false);

    /// <summary>创建 HEX 帧</summary>
    public static SerialTxPayload FromHex(string hex) => new(hex, true);
}
