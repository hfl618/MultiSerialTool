using System.Globalization;
using System.Text.RegularExpressions;

namespace MultiSerialTool.Protocols.Serial;

/// <summary>单条规则的解析结果：数值（供断言/图表）与展示文本（供枚举）</summary>
public sealed record ParsedValue(double? Numeric, string? Display)
{
    /// <summary>失败结果</summary>
    public static ParsedValue Fail => new(null, null);
}

/// <summary>
/// 帧解析器：按 ParseRule 从原始字节帧或 ASCII 文本中提取字段值。
/// regex 走正则捕获；offset/modbus 按字节偏移、数据类型、字节序与位掩码求值，
/// 再乘缩放系数；enum 映射展示文本。任何异常/越界均返回 Fail，不抛到收发链路。
/// </summary>
public static class FrameParser
{
    /// <summary>解析一条规则</summary>
    public static ParsedValue Parse(ParseRule? rule, byte[] frame, string asciiText)
    {
        if (rule is null)
            return ParsedValue.Fail;

        // 帧过滤：不匹配的帧不解析本字段（避免无关帧/回环覆盖已有值）
        if (!string.IsNullOrEmpty(rule.FrameFilter)
            && !FrameMatcher.Match(rule.FrameFilterMode,
                FrameMatcher.NormalizeHex(BitConverter.ToString(frame).Replace('-', ' ')),
                asciiText, rule.FrameFilter))
        {
            return ParsedValue.Fail;
        }

        try
        {
            return rule.Type.ToLowerInvariant() switch
            {
                "regex" => ParseRegex(rule, asciiText),
                _ => ParseOffset(rule, frame)
            };
        }
        catch (Exception ex)
        {
            Services.LogService.Current.Error("Serial", $"帧解析异常：{ex.Message}");
            return ParsedValue.Fail;
        }
    }

    /// <summary>regex：取第一个捕获组（无组则整个匹配），解析为数值</summary>
    private static ParsedValue ParseRegex(ParseRule rule, string asciiText)
    {
        if (string.IsNullOrEmpty(rule.Pattern))
            return ParsedValue.Fail;

        var m = Regex.Match(asciiText, rule.Pattern);
        if (!m.Success)
            return ParsedValue.Fail;

        string token = m.Groups.Count > 1 ? m.Groups[1].Value : m.Value;
        if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return new ParsedValue(null, token);

        return new ParsedValue(ApplyScale(rule, v), ApplyEnum(rule, v));
    }

    /// <summary>offset/modbus：按偏移取字节并按类型解释</summary>
    private static ParsedValue ParseOffset(ParseRule rule, byte[] frame)
    {
        if (rule.Offset is null || rule.Length is null)
            return ParsedValue.Fail;

        int offset = rule.Offset.Value;
        int length = rule.Length.Value;
        if (offset < 0 || length <= 0 || offset + length > frame.Length)
            return ParsedValue.Fail;

        var segment = frame[offset..(offset + length)];
        var dataType = string.IsNullOrEmpty(rule.DataType)
            ? InferDataType(length)
            : rule.DataType!.ToLowerInvariant();
        bool little = string.Equals(rule.Endian, "little", StringComparison.OrdinalIgnoreCase);

        double raw = dataType switch
        {
            "u8" => segment[0],
            "i8" => (sbyte)segment[0],
            "i16" => (short)ReadUInt16(segment, little),
            "u32" => ReadUInt32(segment, little),
            "i32" => (int)ReadUInt32(segment, little),
            "float32" => ReadFloat32(segment, little),
            "float64" => ReadFloat64(segment, little),
            "double" => ReadFloat64(segment, little),
            _ => ReadUInt16(segment, little)
        };

        if (!string.IsNullOrEmpty(rule.BitMask))
        {
            long mask = Convert.ToInt64(rule.BitMask, 16);
            raw = (long)raw & mask;
        }

        double scaled = ApplyScale(rule, raw);
        return new ParsedValue(scaled, ApplyEnum(rule, raw) ?? scaled.ToString("0.###", CultureInfo.InvariantCulture));
    }

    private static ushort ReadUInt16(byte[] b, bool little)
        => little
            ? (ushort)(b[0] | (b[1] << 8))
            : (ushort)((b[0] << 8) | b[1]);

    private static uint ReadUInt32(byte[] b, bool little)
    {
        if (b.Length < 4)
            return 0;

        return little
            ? (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24))
            : (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
    }

    private static float ReadFloat32(byte[] b, bool little)
    {
        if (b.Length < 4)
            return 0;

        var ordered = little ? b : b.Reverse().ToArray();
        return BitConverter.ToSingle(ordered, 0);
    }

    private static double ReadFloat64(byte[] b, bool little)
    {
        if (b.Length < 8)
            return 0;

        var ordered = little ? b : b.Reverse().ToArray();
        return BitConverter.ToDouble(ordered, 0);
    }

    private static string InferDataType(int length) => length switch
    {
        1 => "u8",
        4 => "u32",
        _ => "u16"
    };

    private static double ApplyScale(ParseRule rule, double value)
        => rule.Scale is not null ? value * rule.Scale.Value : value;

    private static string? ApplyEnum(ParseRule rule, double raw)
    {
        if (rule.EnumMap is null)
            return null;

        var key = ((long)raw).ToString(CultureInfo.InvariantCulture);
        return rule.EnumMap.TryGetValue(key, out var text) ? text : null;
    }
}
