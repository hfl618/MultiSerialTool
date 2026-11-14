namespace MultiSerialTool.Protocols.Serial;

/// <summary>
/// 自动应答引擎：RX 帧到达时按当前规则匹配启用的应答项，
/// 命中后产出应答帧（HEX 帧替换校验占位符，ASCII 帧原样发送）。
/// 命中计数与 MaxTimes 上限在此维护。
/// </summary>
public static class AutoReplyEngine
{
    /// <summary>查找命中的应答规则；无命中返回 null</summary>
    public static AutoReplyRule? FindMatch(DeviceRuleSet? rule, string actualHex, string actualText)
    {
        if (rule?.AutoReplies is null)
            return null;

        foreach (var item in rule.AutoReplies)
        {
            if (!item.Enabled)
                continue;
            if (item.MaxTimes > 0 && item.HitCount >= item.MaxTimes)
                continue;

            if (FrameMatcher.Match(item.MatchMode, actualHex, actualText, item.Command))
            {
                item.HitCount++;
                return item;
            }
        }
        return null;
    }

    /// <summary>构建应答载荷：HEX 帧替换校验占位符，ASCII 帧按原文发送</summary>
    public static SerialTxPayload BuildReplyPayload(AutoReplyRule rule)
    {
        string actualReply = rule.ActualReply ?? string.Empty;
        if (!IsHexReply(actualReply))
            return SerialTxPayload.FromText(actualReply);

        return SerialTxPayload.FromHex(
            Crc16.ReplacePlaceholder(FrameMatcher.NormalizeHex(actualReply)));
    }

    /// <summary>
    /// 判别应答帧是否为 HEX：含校验占位符时按 HEX 帧处理，
    /// 否则归一化后每段都必须是两字符十六进制（空串按 ASCII 处理）。
    /// </summary>
    public static bool IsHexReply(string? frame)
    {
        if (string.IsNullOrWhiteSpace(frame))
            return false;

        if (ChecksumEncoder.ContainsPlaceholder(frame))
            return true;

        var tokens = FrameMatcher.NormalizeHex(frame).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length > 0
               && tokens.All(t => t.Length == 2 && t.All(Uri.IsHexDigit));
    }
}
