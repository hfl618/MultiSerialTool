using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using MultiSerialTool.Models;
using MultiSerialTool.Protocols.Serial;
using MultiSerialTool.Shared.Find;

int pass = 0, fail = 0;
void Check(string name, bool ok, string detail)
{
    Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}  {detail}");
    if (ok) pass++; else fail++;
}

Console.WriteLine("=== MultiSerialTool 极限值 / 异常边界测试 ===");

// ============================================================
// 1. SerialRawChunk —— HEX / ASCII 三视图
// ============================================================
static SerialRawChunk Chunk(params byte[] b) => new() { Bytes = b };

Check("Text-空字节数组", Chunk().Text == "", $"\"{Chunk().Text}\"");
Check("Text-全可打印", Chunk(Encoding.ASCII.GetBytes("ABC abc 123")).Text == "ABC abc 123", $"\"{Chunk(Encoding.ASCII.GetBytes("ABC abc 123")).Text}\"");
Check("Text-高位字节 0x80/0xFF 占位", Chunk(0x41, 0x80, 0xFF, 0x42).Text == "A..B", $"\"{Chunk(0x41, 0x80, 0xFF, 0x42).Text}\"");
Check("Text-制表符 0x09 保留", Chunk(0x09).Text == "\t", $"\"{Chunk(0x09).Text.Replace("\t", "\\t")}\"");
Check("Text-LF/CR 保留", Chunk(0x0A, 0x0D).Text == "\n\r", $"\"{Chunk(0x0A, 0x0D).Text.Replace("\n", "\\n").Replace("\r", "\\r")}\"");
Check("Text-控制字符 0x00/0x1F 占位", Chunk(0x00, 0x01, 0x1F, 0x20).Text == "... ", $"\"{Chunk(0x00, 0x01, 0x1F, 0x20).Text}\"");

Check("Hex-空字节数组", Chunk().Hex == "", $"\"{Chunk().Hex}\"");
Check("Hex-单字节 0x00", Chunk(0x00).Hex == "00", $"\"{Chunk(0x00).Hex}\"");
Check("Hex-多字节", Chunk(0x01, 0x03, 0x0A).Hex == "01 03 0A", $"\"{Chunk(0x01, 0x03, 0x0A).Hex}\"");
Check("Hex-单字节高位", Chunk(0xFF).Hex == "FF", $"\"{Chunk(0xFF).Hex}\"");

// ============================================================
// 2. SerialComposer —— 草稿 / 历史边界
// ============================================================
var c = new SerialComposer();
Check("Composer-初始历史为空，上翻返回 false", !c.HistoryUp(), $"HistoryUp={c.HistoryUp()}");
Check("Composer-下翻返回 true(清空为草稿)", c.HistoryDown(), $"HistoryDown={c.HistoryDown()}");
c.Draft = "cmd1";
Check("Composer-Commit 返回文本并清空", c.Commit() == "cmd1" && c.Draft == "", $"Draft=\"{c.Draft}\"");
c.Draft = "cmd2"; c.Commit();
c.Draft = "cmd3"; c.Commit();
Check("Composer-上翻一次回到最新历史", c.HistoryUp() && c.Draft == "cmd3", $"Draft=\"{c.Draft}\"");
Check("Composer-上翻两次回到 cmd2", c.HistoryUp() && c.Draft == "cmd2", $"Draft=\"{c.Draft}\"");
Check("Composer-上翻到底后继续返回 false", c.HistoryUp() && !c.HistoryUp(), $"Draft=\"{c.Draft}\"");
Check("Composer-下翻越过末尾清空", c.HistoryDown() && c.HistoryDown() && c.HistoryDown() && c.Draft == "", $"Draft=\"{c.Draft}\"");

// ============================================================
// 3. FindMatcher —— 匹配引擎边界
// ============================================================
Check("Find-空 pattern 不命中", !FindMatcher.IsMatch("abc", "", FindMatchMode.Contains, false), "IsMatch(空)");
Check("Find-全词下划线作为单词字符", FindMatcher.IsMatch("foo_bar", "foo_bar", FindMatchMode.WholeWord, true), "foo_bar");
Check("Find-全词单字符", FindMatcher.IsMatch("a", "a", FindMatchMode.WholeWord, true), "单字符 a");
Check("Find-过滤前导分隔符 a|", FindMatcher.FilterMatch("abc", "a|", FindMatchMode.Contains, false, out _), "a| 命中 abc");
Check("Find-过滤尾随分隔符 |a", FindMatcher.FilterMatch("abc", "|a", FindMatchMode.Contains, false, out _), "|a 命中 abc");
Check("Find-过滤前导分隔符不命中", !FindMatcher.FilterMatch("xyz", "a|", FindMatchMode.Contains, false, out _), "a| 不命中 xyz");
Check("Find-过滤连续或 ||", FindMatcher.FilterMatch("abc", "def||abc", FindMatchMode.Contains, true, out _), "def||abc 命中 abc");
Check("Find-过滤空与条件 A\\", FindMatcher.FilterMatch("A", "A\\", FindMatchMode.Contains, true, out _), "A\\ 命中 A");
Check("Find-全词符号关键字不加边界", FindMatcher.IsMatch("a.b", ".", FindMatchMode.WholeWord, true), "点号命中任意含点文本(符号无边界)");
Check("Find-中文关键字包含", FindMatcher.IsMatch("电压12.3V", "电压", FindMatchMode.Contains, false), "中文包含");

// ============================================================
// 4. StreamFramer —— 二进制定长帧 / ASCII 切行 / 空闲兜底
// ============================================================
var frames = new List<(DateTime Time, byte[] Bytes, RxUnitKind Kind)>();
using (var framer = new StreamFramer(30))
{
    framer.UnitCompleted += (t, b, k) => frames.Add((t, b, k));

    // ASCII 两行一次到达 → 两个 TextLine 单元，行内不含终止符
    frames.Clear();
    framer.Append(DateTime.Now, Encoding.ASCII.GetBytes("line1\r\nline2\r\n"));
    Check("Framer-ASCII 切出两行", frames.Count == 2
        && frames[0].Kind == RxUnitKind.TextLine
        && frames[1].Kind == RxUnitKind.TextLine,
        $"count={frames.Count}");
    Check("Framer-行内容不含终止符",
        Encoding.ASCII.GetString(frames[0].Bytes) == "line1"
        && Encoding.ASCII.GetString(frames[1].Bytes) == "line2",
        $"{Encoding.ASCII.GetString(frames[0].Bytes)}/{Encoding.ASCII.GetString(frames[1].Bytes)}");

    // 二进制帧：帧头 + Length=12，整帧取出，不被数据区字节拆断
    frames.Clear();
    var bin = Crc16.HexToBytes("53 57 49 4F 54 54 0C 18 04 32 4E 92");
    framer.Append(DateTime.Now, bin);
    Check("Framer-二进制整帧取出", frames.Count == 1
        && frames[0].Kind == RxUnitKind.Binary
        && frames[0].Bytes.Length == 12,
        $"count={frames.Count} len={frames.FirstOrDefault().Bytes?.Length}");

    // 未闭合行由 CompletePending 兜底为 Raw
    frames.Clear();
    framer.Append(DateTime.Now, Encoding.ASCII.GetBytes("partial"));
    framer.CompletePending();
    Check("Framer-残留行兜底 Raw", frames.Count == 1 && frames[0].Kind == RxUnitKind.Raw,
        $"kind={frames.FirstOrDefault().Kind}");

    // 空数据不产生单元
    frames.Clear();
    framer.Append(DateTime.Now, Array.Empty<byte>());
    framer.CompletePending();
    Check("Framer-空数据不产生单元", frames.Count == 0, $"count={frames.Count}");
}

// ============================================================
// 5. ReportGrouper —— 锚点归组 + 二进制独立成组
// ============================================================
var groups = new List<ReportGroup>();
using (var grouper = new ReportGrouper(300))
{
    grouper.GroupCompleted += g => groups.Add(g);

    SerialRawChunk TextChunk(string text, RxUnitKind kind = RxUnitKind.TextLine) =>
        new() { Time = DateTime.Now, Bytes = Encoding.ASCII.GetBytes(text), Kind = kind };

    grouper.Accept(TextChunk("----- 2026-11-13 12:35:1 -----"));
    grouper.Accept(TextChunk("Solar voltage: 38.72, current: 3.22"));
    grouper.Accept(TextChunk("Battery voltage: 29.21, capacity: 100"));
    grouper.CompletePending();

    Check("Grouper-三行归入一组", groups.Count == 1 && groups[0].Units.Count == 3,
        $"groups={groups.Count} units={groups[0].Units.Count}");
    Check("Grouper-合并文本含两行字段",
        groups[0].CombinedText.Contains("Solar") && groups[0].CombinedText.Contains("Battery"),
        groups[0].CombinedText.Replace("\n", " | "));

    // 二进制帧独立成组，不并入前一 ASCII 组
    groups.Clear();
    grouper.Accept(TextChunk("AT+CSQ"));
    grouper.Accept(new SerialRawChunk
    {
        Time = DateTime.Now,
        Bytes = Crc16.HexToBytes("53 57 49 4F 54 54 0C 18 04 32 4E 92"),
        Kind = RxUnitKind.Binary
    });
    grouper.CompletePending();
    Check("Grouper-二进制独立成组", groups.Count == 2 && groups[1].IsBinary,
        $"groups={groups.Count}");
}

// ============================================================
// 6. ScoreData —— 反射私有静态方法（波特率评分边界）
// ============================================================
var scoreMethod = typeof(SerialConnection).GetMethod("ScoreData",
    BindingFlags.NonPublic | BindingFlags.Static);
int Score(List<byte> data, int frameErrors) =>
    (int)scoreMethod!.Invoke(null, new object[] { data, frameErrors })!;

Check("Score-样本不足 8 字节返回 0", Score(new List<byte> { 0x41, 0x42, 0x43 }, 0) == 0, $"score={Score(new List<byte> { 0x41, 0x42, 0x43 }, 0)}");
Check("Score-帧错误重罚", Score(Enumerable.Repeat((byte)0x41, 20).ToList(), 1) <= 0, $"score={Score(Enumerable.Repeat((byte)0x41, 20).ToList(), 1)}");
Check("Score-帧错误多处为 0", Score(Enumerable.Repeat((byte)0x41, 20).ToList(), 5) == 0, $"score={Score(Enumerable.Repeat((byte)0x41, 20).ToList(), 5)}");
Check("Score-全可打印无帧错误 ≥55", Score(Enumerable.Repeat((byte)0x41, 20).ToList(), 0) >= 55, $"score={Score(Enumerable.Repeat((byte)0x41, 20).ToList(), 0)}");
Check("Score-含换行加分", Score(Enumerable.Repeat((byte)0x41, 20).Concat(new byte[] { 0x0A }).ToList(), 0) >= 65, $"score={Score(Enumerable.Repeat((byte)0x41, 20).Concat(new byte[] { 0x0A }).ToList(), 0)}");
Check("Score-垃圾字节(0xFF)拉低分", Score(Enumerable.Repeat((byte)0xFF, 20).ToList(), 0) < 55, $"score={Score(Enumerable.Repeat((byte)0xFF, 20).ToList(), 0)}");

// ============================================================
// 7. ChartView 解析正则 —— 反射 internal static Regex
// ============================================================
var segRegex = (Regex)typeof(MultiSerialTool.Views.ChartView)
    .GetField("SegmentRegex", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
var phRegex = (Regex)typeof(MultiSerialTool.Views.ChartView)
    .GetField("PlaceholderRegex", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

Check("Chart-分段标记 无名称", segRegex.IsMatch("-----chart1"), "-----chart1 命中");
Check("Chart-分段标记 带名称", segRegex.IsMatch("-----chart2 n=\"光伏板信息\""), "-----chart2 n=... 命中");
Check("Chart-分段标记 拒绝非 chart", !segRegex.IsMatch("-----table1"), "-----table1 不命中");
Check("Chart-占位符 {%.2f}", phRegex.IsMatch("{%.2f}"), "{%2f} 命中");
Check("Chart-占位符 {自定义:%.2f}", phRegex.IsMatch("{电压:%.2f}"), "{电压:%.2f} 命中");
Check("Chart-占位符 {%d}", phRegex.IsMatch("{%d}"), "{%d} 命中");

// 7b. 同一组内多行数据不能串值：LED 的 current/power 必须取自己那一行
var parseBodyMethod = typeof(MultiSerialTool.Views.ChartView)
    .GetMethod("ParseRuleBody", BindingFlags.NonPublic | BindingFlags.Static)!;
var matchValuesMethod = typeof(MultiSerialTool.Views.ChartView)
    .GetMethod("MatchValues", BindingFlags.NonPublic | BindingFlags.Static)!;

List<(string Column, Regex Regex)> BuildFields(string body)
{
    var list = new List<(string, Regex)>();
    foreach (var r in (System.Collections.IEnumerable)parseBodyMethod.Invoke(null, new object[] { body })!)
    {
        var t = r.GetType();
        list.Add(((string)t.GetProperty("Legend")!.GetValue(r)!,
                  (Regex)t.GetProperty("ValueRegex")!.GetValue(r)!));
    }
    return list;
}

Dictionary<string, double> MatchFields(List<(string Column, Regex Regex)> fields, string text)
{
    var map = new Dictionary<string, double>();
    var anchor = fields.Count > 0 ? fields[0].Regex : null;
    var result = (System.Collections.IEnumerable)matchValuesMethod
        .Invoke(null, new object?[] { anchor, fields, text })!;
    foreach (var item in result)
    {
        var t = item.GetType();
        map[(string)t.GetField("Item1")!.GetValue(item)!] =
            (double)t.GetField("Item2")!.GetValue(item)!;
    }
    return map;
}

const string solarRule = "Solar voltage: {光伏电压:%.2f}/current: {光伏电流:%.2f}/power: {%.2f}\n";
const string ledRule = "LED voltage: {LED电压:%.2f}/current: {LED电流:%.2f}/power: {%.2f}\n";
const string twoLineGroup =
    "Solar voltage: 12.50/current: 3.20/power: 40.00\nLED voltage: 11.80/current: 0.50/power: 5.90";

var solarValues = MatchFields(BuildFields(solarRule), twoLineGroup);
var ledValues = MatchFields(BuildFields(ledRule), twoLineGroup);

Check("Chart-光伏电流取本行",
    solarValues.TryGetValue("光伏电流", out var sv) && Math.Abs(sv - 3.20) < 1e-9,
    $"光伏电流={sv}");
Check("Chart-光伏功率取本行",
    solarValues.TryGetValue("power", out var sp) && Math.Abs(sp - 40.00) < 1e-9,
    $"power={sp}");
Check("Chart-LED电流取本行",
    ledValues.TryGetValue("LED电流", out var lv) && Math.Abs(lv - 0.50) < 1e-9,
    $"LED电流={lv}");
Check("Chart-LED功率取本行",
    ledValues.TryGetValue("power", out var lp) && Math.Abs(lp - 5.90) < 1e-9,
    $"LED power={lp}");

// ============================================================
// 8. AppInfo —— 版本/命名
// ============================================================
Check("AppInfo-三段版本号", AppInfo.Version.Split('.').Length == 3, $"Version={AppInfo.Version}");
Check("AppInfo-完整命名格式", AppInfo.FullName == $"MultiSerialTool-{AppInfo.Version}-hfl", $"FullName={AppInfo.FullName}");

Console.WriteLine($"=== 结果：{pass} 通过 / {fail} 失败 ===");
return fail == 0 ? 0 : 1;
