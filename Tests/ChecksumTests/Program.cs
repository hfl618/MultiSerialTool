using MultiSerialTool.Protocols.Serial;

int pass = 0, fail = 0;
void Check(string name, string actual, string expected)
{
    bool ok = string.Equals(actual, expected, StringComparison.Ordinal);
    Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}  actual={actual} expected={expected}");
    if (ok) pass++; else fail++;
}

Console.WriteLine("=== 校验和封装器测试（预期向量经 Python 独立实现交叉核对） ===");

// ---------- CRC16 Modbus ----------
Check("CRC16-保持寄存器应答",
    ChecksumEncoder.EncodeHex("01 03 02 00 7A [CRC16]"),
    "01 03 02 00 7A 39 A7");
Check("CRC16-输入寄存器应答",
    ChecksumEncoder.EncodeHex("01 04 04 00 7A 00 C8 [CRC16]"),
    "01 04 04 00 7A 00 C8 DB CB");

// ---------- XOR ----------
Check("XOR-f1",
    ChecksumEncoder.EncodeHex("01 03 02 00 7A [XOR]"),
    "01 03 02 00 7A 7A");
Check("XOR-f2",
    ChecksumEncoder.EncodeHex("01 04 04 00 7A 00 C8 [XOR]"),
    "01 04 04 00 7A 00 C8 B3");
Check("XOR-f3",
    ChecksumEncoder.EncodeHex("3A 01 02 [XOR]"),
    "3A 01 02 39");

// ---------- LRC ----------
Check("LRC-f1",
    ChecksumEncoder.EncodeHex("01 03 02 00 7A [LRC]"),
    "01 03 02 00 7A 80");
Check("LRC-f3",
    ChecksumEncoder.EncodeHex("3A 01 02 [LRC]"),
    "3A 01 02 C3");

// ---------- CRC8 ----------
Check("CRC8-f1",
    ChecksumEncoder.EncodeHex("01 03 02 00 7A [CRC8]"),
    "01 03 02 00 7A EF");
Check("CRC8-f2",
    ChecksumEncoder.EncodeHex("01 04 04 00 7A 00 C8 [CRC8]"),
    "01 04 04 00 7A 00 C8 62");

// ---------- 无占位符与多占位符 ----------
Check("无占位符-仅归一化",
    ChecksumEncoder.EncodeHex("01 03"),
    "01 03");
Check("多占位符-XOR后再LRC（LRC基于含XOR的全部字节）",
    ChecksumEncoder.EncodeHex("3A 01 02 [XOR] [LRC]"),
    ChecksumEncoder.EncodeHex("3A 01 02 39 [LRC]"));
Check("小写占位符兼容",
    ChecksumEncoder.EncodeHex("01 03 02 00 7A [crc16]"),
    "01 03 02 00 7A 39 A7");

Console.WriteLine($"=== 结果：{pass} 通过 / {fail} 失败 ===");
return fail == 0 ? 0 : 1;
