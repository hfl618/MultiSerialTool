using System.Text.RegularExpressions;

namespace MultiSerialTool.Protocols.Serial;

/// <summary>
/// 帧匹配工具：自动应答与断言判定共用。
/// 支持 exact 精确、mask 通配、regex 正则三种模式；
/// 模式串为纯 HEX 帧时按字节比较，否则按 ASCII 文本比较（mask 用 ? 匹配单字符）。
/// 匹配前统一归一化：去除空白与 0x 前缀，HEX 转大写。
/// </summary>
public static class FrameMatcher
{
    /// <summary>通配字节标记</summary>
    public const string Wildcard = "??";

    /// <summary>归一化 HEX 文本：去空白/0x 前缀，转大写；空输入返回空串</summary>
    public static string NormalizeHex(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var cleaned = input.Replace("0x", string.Empty).Replace("0X", string.Empty);
        var tokens = cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', tokens).ToUpperInvariant();
    }

    /// <summary>
    /// 匹配判定。模式串能解析为 HEX 帧时按字节比较；否则按 ASCII 文本比较
    /// （exact 忽略行尾 CR/LF，mask 用 ? 匹配任意单个字符）。
    /// </summary>
    /// <param name="mode">exact / mask / regex</param>
    /// <param name="actualHex">实际帧归一化 HEX</param>
    /// <param name="actualText">实际帧 ASCII 文本（regex 可对文本判定）</param>
    /// <param name="pattern">规则中的模式串</param>
    public static bool Match(string mode, string actualHex, string actualText, string pattern)
    {
        try
        {
            var normalizedPattern = NormalizeHex(pattern);
            bool hexPattern = LooksLikeHexFrame(normalizedPattern);

            return mode.ToLowerInvariant() switch
            {
                "mask" => hexPattern
                    ? MaskMatch(actualHex, normalizedPattern)
                    : TextMaskMatch(actualText, pattern),
                "regex" => Regex.IsMatch(actualText, pattern)
                           || Regex.IsMatch(actualHex, pattern),
                _ => hexPattern
                    ? string.Equals(actualHex, normalizedPattern, StringComparison.Ordinal)
                    : TextExactMatch(actualText, pattern)
            };
        }
        catch (Exception ex)
        {
            Services.LogService.Current.Error("Serial", $"帧匹配异常：{ex.Message}");
            return false;
        }
    }

    /// <summary>归一化后的模式串是否可按 HEX 帧逐字节比较（每段为两字符十六进制或 ??）</summary>
    private static bool LooksLikeHexFrame(string normalizedHex)
    {
        var tokens = normalizedHex.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length > 0
               && tokens.All(t => t.Length == 2
                                  && (t == Wildcard || t.All(Uri.IsHexDigit)));
    }

    /// <summary>ASCII 精确比较：忽略行尾 CR/LF（成帧后文本不含终止符）</summary>
    private static bool TextExactMatch(string actualText, string pattern)
        => string.Equals(TrimLineEnd(actualText), TrimLineEnd(pattern), StringComparison.Ordinal);

    /// <summary>ASCII 通配比较：? 匹配任意单个字符，长度必须一致，忽略行尾 CR/LF</summary>
    private static bool TextMaskMatch(string actualText, string pattern)
    {
        var actual = TrimLineEnd(actualText);
        var expected = TrimLineEnd(pattern);
        if (actual.Length != expected.Length)
            return false;

        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i] != '?' && expected[i] != actual[i])
                return false;
        }
        return true;
    }

    private static string TrimLineEnd(string? text)
        => (text ?? string.Empty).TrimEnd('\r', '\n');

    /// <summary>mask 匹配：逐字节比较，?? 跳过任意字节；字节数不同直接失败</summary>
    public static bool MaskMatch(string actualHex, string patternHex)
    {
        var actual = actualHex.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var expected = patternHex.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (actual.Length != expected.Length)
            return false;

        for (var i = 0; i < actual.Length; i++)
        {
            if (expected[i] == Wildcard)
                continue;
            if (!string.Equals(actual[i], expected[i], StringComparison.Ordinal))
                return false;
        }
        return true;
    }
}
