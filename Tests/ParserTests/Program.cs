using MultiSerialTool.Protocols.Serial;

int pass = 0, fail = 0;
void CheckNum(string name, double? actual, double? expected)
{
    bool ok = (actual is null && expected is null)
              || (actual is not null && expected is not null
                  && Math.Abs(actual.Value - expected.Value) < 1e-9);
    Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}  actual={actual} expected={expected}");
    if (ok) pass++; else fail++;
}

void CheckText(string name, string? actual, string? expected)
{
    bool ok = actual == expected;
    Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}  actual={actual} expected={expected}");
    if (ok) pass++; else fail++;
}

Console.WriteLine("=== FrameParser 帧解析测试 ===");

// 电压采集器应答：01 03 02 00 7A 39 A7，offset3 len2 → 122 ×0.1 = 12.2
var voltageFrame = Crc16.HexToBytes("01 03 02 00 7A 39 A7");
var voltageRule = new ParseRule
{
    Type = "offset", Offset = 3, Length = 2, Scale = 0.1, Unit = "V"
};
CheckNum("offset-u16+scale 电压",
    FrameParser.Parse(voltageRule, voltageFrame, "").Numeric, 12.2);

// 输入寄存器应答：01 04 04 00 7A 00 C8 DB CB，offset5 len2 → 0x00C8=200 ×0.01 = 2.0
var currentFrame = Crc16.HexToBytes("01 04 04 00 7A 00 C8 DB CB");
var currentRule = new ParseRule
{
    Type = "modbus", Offset = 5, Length = 2, Scale = 0.01, Unit = "A"
};
CheckNum("modbus-u16+scale 电流",
    FrameParser.Parse(currentRule, currentFrame, "").Numeric, 2.0);

// 小端 u16：字节 7A 00 little → 122
CheckNum("little-endian u16",
    FrameParser.Parse(new ParseRule
    {
        Type = "offset", Offset = 0, Length = 2, Endian = "little"
    }, Crc16.HexToBytes("7A 00"), "").Numeric, 122);

// 有符号 i8：0xFF → -1
CheckNum("i8 有符号负数",
    FrameParser.Parse(new ParseRule
    {
        Type = "offset", Offset = 0, Length = 1, DataType = "i8"
    }, Crc16.HexToBytes("FF"), "").Numeric, -1);

// 位掩码：0x1234 & 0x000F = 4
CheckNum("bitMask 取低4位",
    FrameParser.Parse(new ParseRule
    {
        Type = "offset", Offset = 0, Length = 2, BitMask = "0x000F"
    }, Crc16.HexToBytes("12 34"), "").Numeric, 4);

// float32 big：1.0 的大端字节 3F 80 00 00
CheckNum("float32 big",
    FrameParser.Parse(new ParseRule
    {
        Type = "offset", Offset = 0, Length = 4, DataType = "float32"
    }, Crc16.HexToBytes("3F 80 00 00"), "").Numeric, 1.0);

// 正则捕获：voltage: 12.34
CheckNum("regex 捕获数值",
    FrameParser.Parse(new ParseRule
    {
        Type = "regex", Pattern = @"voltage:\s*([\d.]+)"
    }, Crc16.HexToBytes(""), "voltage: 12.34").Numeric, 12.34);

// 枚举映射
var enumRule = new ParseRule
{
    Type = "offset", Offset = 0, Length = 1,
    EnumMap = new Dictionary<string, string> { ["0"] = "停机", ["1"] = "运行" }
};
var enumVal = FrameParser.Parse(enumRule, Crc16.HexToBytes("01"), "");
CheckNum("enum 数值保留", enumVal.Numeric, 1);
CheckText("enum 展示文本", enumVal.Display, "运行");

// 越界：offset 超出帧长度 → Fail
CheckNum("越界返回 null",
    FrameParser.Parse(new ParseRule
    {
        Type = "offset", Offset = 8, Length = 2
    }, voltageFrame, "").Numeric, null);

// 长度不足的 u32 → Fail 安全（不抛异常）
CheckNum("长度不足返回 null",
    FrameParser.Parse(new ParseRule
    {
        Type = "offset", Offset = 0, Length = 4, DataType = "u32"
    }, Crc16.HexToBytes("01 02"), "").Numeric, null);

Console.WriteLine($"=== 结果：{pass} 通过 / {fail} 失败 ===");
return fail == 0 ? 0 : 1;
