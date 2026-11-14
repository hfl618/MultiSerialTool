namespace MultiSerialTool.Protocols.Serial;

/// <summary>
/// 规则迁移：把旧结构（QuickSends + AutoReplies 两张表）归一化到统一的 Commands。
/// 加载规则时调用；Commands 已由新界面写入时直接保留，不重复迁移。
/// 映射不删除旧字段，迁移期内旧界面仍可工作。
/// </summary>
public static class RuleMigration
{
    /// <summary>
    /// 就地归一化：Commands 非空则跳过；否则按旧字段生成并连续编号。
    /// </summary>
    public static void Normalize(DeviceRuleSet rule)
    {
        if (rule.Commands.Count > 0)
            return;

        foreach (var quick in rule.QuickSends)
            rule.Commands.Add(FromQuickSend(quick));

        foreach (var reply in rule.AutoReplies)
            rule.Commands.Add(FromAutoReply(reply));

        for (var i = 0; i < rule.Commands.Count; i++)
            rule.Commands[i].Index = i + 1;
    }

    /// <summary>快捷发送规则 → manual 模式统一规则</summary>
    public static CommandRule FromQuickSend(QuickSendRule quick) => new()
    {
        Mode = CommandRuleMode.Manual,
        Enabled = true,
        Name = quick.Name,
        Group = quick.Group,
        TxFrame = quick.Frame,
        Format = quick.Format,
        Append = quick.Append,
        Repeat = quick.Repeat,
        IntervalMs = quick.IntervalMs,
        Expect = quick.Expect
    };

    /// <summary>自动应答规则 → auto 模式统一规则</summary>
    public static CommandRule FromAutoReply(AutoReplyRule reply) => new()
    {
        Mode = CommandRuleMode.Auto,
        Enabled = reply.Enabled,
        Name = string.IsNullOrWhiteSpace(reply.Name) ? reply.Expected : reply.Name,
        TxFrame = reply.ActualReply,
        Format = LooksLikeHex(reply.Command) ? "hex" : "ascii",
        TriggerMode = reply.MatchMode,
        TriggerFrame = reply.Command,
        DelayMs = reply.DelayMs,
        MaxTimes = reply.MaxTimes,
        HitCount = reply.HitCount
    };

    /// <summary>
    /// 反向填充：从统一 Commands 重建旧字段（迁移期内导入新格式文件后，
    /// 旧界面仍绑定 AutoReplies/QuickSends）。阶段 3 删除旧字段时一并移除。
    /// </summary>
    public static void BackfillLegacy(DeviceRuleSet rule)
    {
        rule.AutoReplies.Clear();
        rule.QuickSends.Clear();

        foreach (var cmd in rule.Commands)
        {
            if (cmd.IsAuto)
            {
                rule.AutoReplies.Add(new AutoReplyRule
                {
                    Index = cmd.Index,
                    Enabled = cmd.Enabled,
                    Name = cmd.Name,
                    MatchMode = cmd.TriggerMode,
                    Command = cmd.TriggerFrame,
                    Expected = cmd.TriggerFrame,
                    Operation = "自动回复",
                    ActualReply = cmd.TxFrame,
                    DelayMs = cmd.DelayMs,
                    MaxTimes = cmd.MaxTimes,
                    HitCount = cmd.HitCount
                });
            }

            if (cmd.IsManual)
            {
                rule.QuickSends.Add(new QuickSendRule
                {
                    Name = cmd.Name,
                    Group = cmd.Group,
                    Frame = cmd.TxFrame,
                    Format = cmd.Format,
                    Append = cmd.Append,
                    Repeat = cmd.Repeat,
                    IntervalMs = cmd.IntervalMs,
                    Expect = cmd.Expect
                });
            }
        }
    }

    /// <summary>
    /// 判别帧文本是否为 HEX：去空白后全部为十六进制字符（mask 模式允许 ? 通配）
    /// 且长度为偶数。含 AT 指令、+、换行等字符的 ASCII 帧返回 false。
    /// </summary>
    public static bool LooksLikeHex(string? frame)
    {
        if (string.IsNullOrWhiteSpace(frame))
            return false;

        var compact = frame.Replace(" ", string.Empty)
                           .Replace("\t", string.Empty)
                           .Replace("\r", string.Empty)
                           .Replace("\n", string.Empty);

        return compact.Length > 0
               && compact.Length % 2 == 0
               && compact.All(c => "0123456789ABCDEFabcdef?".Contains(c));
    }
}
