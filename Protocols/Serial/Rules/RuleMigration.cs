namespace MultiSerialTool.Protocols.Serial;

/// <summary>
/// 规则迁移：把旧结构（QuickSends + AutoReplies 两张表）并入统一的 Commands，
/// 并入后清空旧字段，之后只有 Commands 一份数据，界面与引擎不会再各写各的。
/// 加载规则时调用；重复调用幂等。
/// </summary>
public static class RuleMigration
{
    /// <summary>
    /// 就地归一化：旧字段中尚未出现在 Commands 里的条目按统一模型补齐，
    /// 然后清空旧字段并重排序号。
    /// </summary>
    public static void Normalize(DeviceRuleSet rule)
    {
        rule.Fields ??= new();
        rule.Charts ??= new();
        rule.AutoReplies ??= new();
        rule.QuickSends ??= new();
        rule.Commands ??= new();

        // 只在对应方向完全缺失时迁移：部分迁移过的文件（例如旧快捷发送已并入、
        // 应答仍在旧表里）也能补齐，而不会重复添加已有条目
        if (!rule.Commands.Any(c => c.IsManual))
        {
            foreach (var quick in rule.QuickSends)
            {
                if (!rule.Commands.Any(c => c.IsManual
                        && c.Name == quick.Name && c.TxFrame == quick.Frame))
                    rule.Commands.Add(FromQuickSend(quick));
            }
        }

        if (!rule.Commands.Any(c => c.IsAuto))
        {
            foreach (var reply in rule.AutoReplies)
            {
                if (!rule.Commands.Any(c => c.IsAuto
                        && c.TriggerFrame == reply.Command && c.TxFrame == reply.ActualReply))
                    rule.Commands.Add(FromAutoReply(reply));
            }
        }

        // 旧字段已经并入统一模型，清空避免两份数据再次分叉
        rule.AutoReplies.Clear();
        rule.QuickSends.Clear();

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
