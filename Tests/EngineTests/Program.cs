using MultiSerialTool.Protocols.Serial;

int pass = 0, fail = 0;
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name} {detail}");
    if (ok) pass++; else fail++;
}

Console.WriteLine("=== RuleExecutionEngine 端到端测试（无真实串口） ===");

// 构造规则：电压字段 + 自动应答 + 初始查询
var rule = new DeviceRuleSet
{
    DeviceType = "测试设备",
    Fields =
    {
        new FieldRule
        {
            Key = "voltage",
            Label = "电压",
            Query = "01 03 00 00 00 01 [CRC16]",
            Refresh = true,
            Parse = new ParseRule
            {
                Type = "offset", Offset = 3, Length = 2, Scale = 0.1, Unit = "V",
                FrameFilter = "01 03 02 ?? ?? ?? ??"
            }
        }
    },
    AutoReplies =
    {
        new AutoReplyRule
        {
            Name = "读电压应答",
            Enabled = true,
            MatchMode = "exact",
            Command = "01 03 00 00 00 01 84 0A",
            ActualReply = "01 03 02 00 7A [CRC16]"
        }
    }
};

var engine = new RuleExecutionEngine();
var emittedFrames = new List<string>();
var emittedKinds = new List<bool>();
engine.FrameReady += p =>
{
    emittedFrames.Add(p.Text);
    emittedKinds.Add(p.IsHex);
};
engine.UpdateRule(rule);

// 1. 字段行已按规则重建
Check("字段行数量=1", engine.FieldRows.Count == 1,
    $"actual={engine.FieldRows.Count}");

// 2. 初始查询：CRC 占位符已计算
engine.RunInitialQueries();
Check("初始查询帧已发出",
    emittedFrames.Count == 1 && emittedFrames[0] == "01 03 00 00 00 01 84 0A",
    emittedFrames.FirstOrDefault() ?? "");

// 3. 应答帧进入：解析回填 12.2
var responseFrame = Crc16.HexToBytes("01 03 02 00 7A 39 A7");
engine.ProcessRx(responseFrame, "01 03 02 00 7A 39 A7", "");
Check("解析值=12.2",
    engine.ParsedValues.TryGetValue("voltage", out var v) && Math.Abs(v!.Value - 12.2) < 1e-9,
    $"v={v}");
Check("字段行文本含单位",
    engine.FieldRows[0].Value == "12.2 V",
    engine.FieldRows[0].Value ?? "");

// 4. 命令帧进入：自动应答命中，FrameReady 收到带 CRC 的回复
var beforeCount = emittedFrames.Count;
engine.ProcessRx(Crc16.HexToBytes("01 03 00 00 00 01 84 0A"),
    "01 03 00 00 00 01 84 0A", "");
Check("自动应答帧已发出",
    emittedFrames.Count == beforeCount + 1
    && emittedFrames[^1] == "01 03 02 00 7A 39 A7",
    emittedFrames.LastOrDefault() ?? "");

// 5. 手动刷新：按 Key 再发查询
engine.RequestField("voltage");
Check("手动刷新下发查询",
    emittedFrames.Last() == "01 03 00 00 00 01 84 0A",
    emittedFrames.Last());

// 6. 断言联动：ExpectationRunner 用引擎真实 ParsedValues 做 eq 判定
var runner = new ExpectationRunner();
var quickItem = new CommandRule
{
    Name = "读电压",
    TxFrame = "01 03 00 00 00 01 [CRC16]",
    Format = "hex",
    Expect = new ExpectRule
    {
        MatchMode = "exact",
        Expected = "01 03 02 00 7A 39 A7",
        TimeoutMs = 500,
        Asserts =
        {
            new AssertRule { Source = "voltage", Op = "eq", Expected = 12.2 }
        }
    }
};
string? resolvedTag = null;
runner.Resolved += (item, tag) => resolvedTag = tag;
runner.Start(quickItem);
runner.Feed("01 03 02 00 7A 39 A7", "", engine.ParsedValues);
Check("数值断言通过(assert_pass)", resolvedTag == "assert_pass",
    resolvedTag + " | " + quickItem.ResultMessage);

// 7. 反向：错误期望值断言失败
var runner2 = new ExpectationRunner();
var quickItem2 = new CommandRule
{
    Name = "读电压-错误期望",
    TxFrame = "01 03 00 00 00 01 [CRC16]",
    Format = "hex",
    Expect = new ExpectRule
    {
        MatchMode = "exact",
        Expected = "01 03 02 00 7A 39 A7",
        Asserts =
        {
            new AssertRule { Source = "voltage", Op = "eq", Expected = 99.9 }
        }
    }
};
string? resolvedTag2 = null;
runner2.Resolved += (item, tag) => resolvedTag2 = tag;
runner2.Start(quickItem2);
runner2.Feed("01 03 02 00 7A 39 A7", "", engine.ParsedValues);
Check("错误期望断言失败(assert_fail)", resolvedTag2 == "assert_fail",
    resolvedTag2 ?? "");

// 8. ASCII 查询帧（4G 场景）：不按 HEX 解析，IsHex=false 且原文保留
var asciiRule = new DeviceRuleSet
{
    Fields =
    {
        new FieldRule
        {
            Key = "imei",
            Label = "IMEI",
            Format = "ascii",
            Query = "AT+CGSN\r\n"
        }
    }
};
var asciiEngine = new RuleExecutionEngine();
SerialTxPayload? captured = null;
asciiEngine.FrameReady += p => captured = p;
asciiEngine.UpdateRule(asciiRule);
asciiEngine.RunInitialQueries();
Check("ASCII 帧不按 HEX 解析且 IsHex=false",
    captured is not null && !captured.IsHex && captured.Text == "AT+CGSN\r\n",
    captured?.Text ?? "null");

// 9. 缺省 format 仍按 hex 处理（向后兼容）
var hexRule = new DeviceRuleSet
{
    Fields =
    {
        new FieldRule { Key = "q", Query = "01 03 00 00 00 01 [CRC16]" }
    }
};
var hexEngine = new RuleExecutionEngine();
SerialTxPayload? capturedHex = null;
hexEngine.FrameReady += p => capturedHex = p;
hexEngine.UpdateRule(hexRule);
hexEngine.RunInitialQueries();
Check("缺省 format 按 hex 且 CRC 已计算",
    capturedHex is not null && capturedHex.IsHex
    && capturedHex.Text == "01 03 00 00 00 01 84 0A",
    capturedHex?.Text ?? "null");

// 10. 规则迁移：旧两表归一化到统一 Commands
var legacy = new DeviceRuleSet
{
    QuickSends =
    {
        new QuickSendRule
        {
            Name = "读电压", Group = "查询", Frame = "01 03 00 00 00 01 [CRC16]",
            Format = "hex", Repeat = 2, IntervalMs = 200
        }
    },
    AutoReplies =
    {
        new AutoReplyRule
        {
            Name = "读保持寄存器", Enabled = false, MatchMode = "mask",
            Command = "01 03 ?? ?? ?? ?? ?? ??",
            ActualReply = "01 03 02 00 7A [CRC16]", DelayMs = 500
        },
        new AutoReplyRule
        {
            Command = "AT+CGSN\r\n",
            ActualReply = "860123456789012\r\nOK"
        }
    }
};
RuleMigration.Normalize(legacy);
Check("迁移后共 3 条且连续编号",
    legacy.Commands.Count == 3
    && legacy.Commands[0].Index == 1 && legacy.Commands[2].Index == 3,
    $"count={legacy.Commands.Count}");
Check("QuickSend 映射为 manual 且字段保留",
    legacy.Commands[0].IsManual && !legacy.Commands[0].IsAuto
    && legacy.Commands[0].TxFrame == "01 03 00 00 00 01 [CRC16]"
    && legacy.Commands[0].Repeat == 2 && legacy.Commands[0].IntervalMs == 200,
    legacy.Commands[0].Mode);
Check("AutoReply(HEX) 映射为 auto 且触发字段保留",
    legacy.Commands[1].IsAuto && legacy.Commands[1].Format == "hex"
    && !legacy.Commands[1].Enabled && legacy.Commands[1].TriggerMode == "mask"
    && legacy.Commands[1].TriggerFrame == "01 03 ?? ?? ?? ?? ?? ??"
    && legacy.Commands[1].DelayMs == 500,
    legacy.Commands[1].Mode);
Check("AutoReply(ASCII) 格式自动判别为 ascii",
    legacy.Commands[2].Format == "ascii"
    && legacy.Commands[2].TxFrame == "860123456789012\r\nOK",
    legacy.Commands[2].Format);

// 11. 已存在 Commands 时不重复迁移
var migrationCount = legacy.Commands.Count;
RuleMigration.Normalize(legacy);
Check("Commands 非空时归一化幂等", legacy.Commands.Count == migrationCount,
    $"count={legacy.Commands.Count}");

// 12. HEX 判别边界
Check("HEX 帧判别",
    RuleMigration.LooksLikeHex("01 03 02 00 7A")
    && RuleMigration.LooksLikeHex("01 03 ?? ?? ?? ?? ?? ??")
    && !RuleMigration.LooksLikeHex("AT+CGSN\r\n")
    && !RuleMigration.LooksLikeHex("01 03 0"),
    "hex/mask/ascii/奇数长度");

Console.WriteLine($"=== 结果：{pass} 通过 / {fail} 失败 ===");
return fail == 0 ? 0 : 1;
