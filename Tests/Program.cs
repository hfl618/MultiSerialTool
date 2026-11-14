using System.IO.Ports;
using MultiSerialTool.Protocols.Serial;

// 用法：LoopbackTest [应用端口] [对端端口]，默认 COM9 / COM8
// 两个端口需为 VSPD 虚拟串口对（或物理交叉线），且两端均未被其他程序占用。
string appPort = args.Length > 0 ? args[0] : "COM9";
string peerPort = args.Length > 1 ? args[1] : "COM8";

int pass = 0, fail = 0;
void Check(string name, bool ok, string detail)
{
    Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}  {detail}");
    if (ok) pass++; else fail++;
}

Console.WriteLine($"=== MultiSerialTool 串口回环测试  应用={appPort} 对端={peerPort} ===");

// 对端：普通 SerialPort，用来模拟设备
using var peer = new SerialPort(peerPort, 9600, Parity.None, 8, StopBits.One)
{
    ReadTimeout = 2000,
    WriteTimeout = 2000
};

// 应用侧：真实 SerialConnection，preferred=9600
using var conn = new SerialConnection(appPort, 9600);

var receivedChunks = new List<SerialRawChunk>();
var rawAll = new List<byte>();
var sync = new object();
conn.ChunkReceived += chunk =>
{
    lock (sync)
    {
        receivedChunks.Add(chunk);
        rawAll.AddRange(chunk.Bytes);
    }
};

// 取出自 snapshot 之后新到达的全部原始字节（连接层不透传成帧/归组，成帧在 VM 内完成）
byte[] DrainNewRaw(int snapshot)
{
    lock (sync)
        return rawAll.GetRange(snapshot, rawAll.Count - snapshot).ToArray();
}

// 先打开应用侧连接（不依赖对端），验证连接与波特率保留
try
{
    conn.Open();
    Check("连接打开", true, $"实际波特率={conn.BaudRate}（无探测数据时保留 preferred=9600）");
}
catch (Exception ex)
{
    Check("连接打开", false, ex.Message);
    Console.WriteLine("应用侧连接失败，终止后续测试。");
    return 4;
}

bool peerReady;
try
{
    peer.Open();
    Console.WriteLine($"对端 {peerPort} 已打开");
    peerReady = true;
}
catch (Exception ex)
{
    Console.WriteLine($"对端 {peerPort} 无法打开，跳过收发回环：{ex.Message}");
    Console.WriteLine("请关闭占用该端口的软件（如 CommTone/CommMonitor），或改用其他空闲端口对后重跑。");
    peerReady = false;
}

if (!peerReady)
{
    Console.WriteLine($"=== 单端连接验证结果：{pass} 通过 / {fail} 失败（收发回环未执行）===");
    return fail == 0 ? 0 : 1;
}

// 给事件订阅一点稳定时间
Thread.Sleep(200);

// ---------- 测试1：RX 二进制 Modbus 帧（无换行，整块作为一条） ----------
byte[] modbusFrame = new byte[] { 0x01, 0x03, 0x02, 0x00, 0x7A, 0x39, 0xA7 };
peer.Write(modbusFrame, 0, modbusFrame.Length);
Thread.Sleep(500);

SerialRawChunk? rxChunk;
lock (sync) rxChunk = receivedChunks.LastOrDefault();
Check("RX 收到 Modbus 帧",
    rxChunk is not null && rxChunk.Length == 7,
    rxChunk is null ? "未收到" : $"HEX=[{rxChunk.Hex}] 字节数={rxChunk.Length}");

if (rxChunk is not null)
{
    Check("RX HEX 解析正确",
        rxChunk.Hex == "01 03 02 00 7A 39 A7",
        $"Hex=\"{rxChunk.Hex}\"");
    Check("RX ASCII 占位正确",
        rxChunk.Text == "....z9.",
        $"Text=\"{rxChunk.Text}\"（不可打印以.占位，0x7A=z，0x39=9，0xA7=不可打印）");
}

// ---------- 测试2：RX 文本帧（带 CRLF） ----------
byte[] textFrame = System.Text.Encoding.ASCII.GetBytes("AT+CSQ\r\n");
peer.Write(textFrame, 0, textFrame.Length);
Thread.Sleep(500);

SerialRawChunk? textChunk;
lock (sync) textChunk = receivedChunks.LastOrDefault();
Check("RX 收到文本帧",
    textChunk is not null && textChunk.Text == "AT+CSQ\r\n",
    textChunk is null ? "未收到" : $"Text=\"{textChunk.Text.Replace("\r", "\\r").Replace("\n", "\\n")}\"");

// ---------- 测试3：TX 应用发送，对端接收 ----------
string txText = "AT+CGSN\r\n";
byte[] expectedTx = System.Text.Encoding.UTF8.GetBytes(txText);
try
{
    conn.Write(txText);
    var buf = new byte[64];
    int read = peer.Read(buf, 0, buf.Length);
    var got = buf[..read];
    bool txOk = got.SequenceEqual(expectedTx);
    Check("TX 发送字节正确",
        txOk,
        $"对端收到 HEX=[{BitConverter.ToString(got).Replace('-', ' ')}] 期望=[{BitConverter.ToString(expectedTx).Replace('-', ' ')}]");
}
catch (Exception ex)
{
    Check("TX 发送字节正确", false, ex.Message);
}

// ---------- 测试4：中文/多字节 UTF-8 透传 ----------
string cn = "电压12.3V";
try
{
    conn.Write(cn);
    var buf = new byte[64];
    int read = peer.Read(buf, 0, buf.Length);
    string decoded = System.Text.Encoding.UTF8.GetString(buf, 0, read);
    Check("TX 中文 UTF-8 透传",
        decoded == cn,
        $"对端解码=\"{decoded}\" 字节数={read}");
}
catch (Exception ex)
{
    Check("TX 中文 UTF-8 透传", false, ex.Message);
}

// ---------- 测试5：多行 ASCII 上报块整块透传（VM 内成帧按行拆、归组按次聚合） ----------
var reportLines = new[]
{
    "----- 2026-11-13 12:35:1 -----",
    "Solar voltage: 38.72, current: 3.22, power: 124.59, current offset: 0.254, pwm: 0.78",
    "Battery voltage: 29.21, current: 4.29, capacity: 100, mode: 3",
    "LED voltage: 47.03, current: 2.63, power: 123.61, energy: 0.00"
};
byte[] reportBytes = System.Text.Encoding.ASCII.GetBytes(
    string.Join("\r\n", reportLines) + "\r\n");
int snapshot5;
lock (sync) snapshot5 = rawAll.Count;
peer.Write(reportBytes, 0, reportBytes.Length);
Thread.Sleep(500);
byte[] got5 = DrainNewRaw(snapshot5);
Check("多行上报块字节完整透传",
    got5.SequenceEqual(reportBytes),
    $"收到 {got5.Length} 字节 / 期望 {reportBytes.Length}");

// ---------- 测试6：二进制帧数据区含 0x0A/0x0D，整帧不被原始层拆断 ----------
// 帧头 SWIOTT + Length=14，载荷故意放入换行字节 0x0A 0x0D
byte[] binFrame = { 0x53, 0x57, 0x49, 0x4F, 0x54, 0x54, 0x0E, 0x00, 0x11, 0x0A, 0x0D, 0x22, 0x33, 0x44 };
int snapshot6;
lock (sync) snapshot6 = rawAll.Count;
peer.Write(binFrame, 0, binFrame.Length);
Thread.Sleep(500);
byte[] got6 = DrainNewRaw(snapshot6);
Check("含换行字节的二进制帧整帧透传",
    got6.SequenceEqual(binFrame),
    $"收到 HEX=[{BitConverter.ToString(got6).Replace('-', ' ')}] 期望 53 57 49 4F 54 54 0E 00 11 0A 0D 22 33 44");

Console.WriteLine($"=== 结果：{pass} 通过 / {fail} 失败 ===");
return fail == 0 ? 0 : 1;
