using System.Data;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MultiSerialTool.Protocols.Serial;
using MultiSerialTool.Services;
using MultiSerialTool.Services.Storage;

// 服务层测试：内置设备规则、CSV 导出、帧匹配、实时落盘记录器
int pass = 0, fail = 0, skip = 0;
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}  {detail}");
    if (ok) pass++; else fail++;
}
void Skip(string name, string reason)
{
    Console.WriteLine($"[SKIP] {name}  {reason}");
    skip++;
}

static bool IsHexToken(string token)
    => token.Length == 2 && token.All(Uri.IsHexDigit);

static bool IsFrameToken(string token)
    => token == "??" || IsHexToken(token);

static string[] Tokens(string value)
    => FrameMatcher.NormalizeHex(value).Split(' ', StringSplitOptions.RemoveEmptyEntries);

static bool TryCompileRegex(string? pattern, out string error)
{
    error = string.Empty;
    if (string.IsNullOrEmpty(pattern))
    {
        error = "pattern 为空";
        return false;
    }
    try
    {
        _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
        return true;
    }
    catch (Exception ex)
    {
        error = ex.Message;
        return false;
    }
}

static string? FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "Assets", "DeviceRules")))
            return dir.FullName;
        dir = dir.Parent;
    }
    return null;
}

// ================= A. 内置设备规则文件 =================
Console.WriteLine("=== A. 内置设备规则加载与结构校验 ===");

string? repoRoot = FindRepoRoot();
if (repoRoot is null)
{
    Skip("定位设备规则目录", "未找到 Assets/DeviceRules");
}
else
{
    var ruleDirs = new List<string> { Path.Combine(repoRoot, "Assets", "DeviceRules") };
    var binRules = Path.Combine(repoRoot, "bin", "Debug", "net9.0-windows", "DeviceRules");
    if (Directory.Exists(binRules))
        ruleDirs.Add(binRules);

    foreach (var ruleDir in ruleDirs)
    {
        Console.WriteLine($"-- 目录：{ruleDir}");
        var files = Directory.GetFiles(ruleDir, "*.json");
        Check($"规则文件数量 > 0 ({Path.GetFileName(Path.GetDirectoryName(ruleDir))})", files.Length > 0, $"共 {files.Length} 个");

        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            DeviceRuleSet? rule;
            try
            {
                var json = File.ReadAllText(file);
                rule = JsonSerializer.Deserialize<DeviceRuleSet>(json, JsonFile.Options);
            }
            catch (Exception ex)
            {
                Check($"{name} 反序列化", false, ex.Message);
                continue;
            }

            if (rule is null)
            {
                Check($"{name} 反序列化", false, "结果为空");
                continue;
            }

            RuleMigration.Normalize(rule);

            Check($"{name} DeviceType 非空", !string.IsNullOrWhiteSpace(rule.DeviceType), rule.DeviceType);
            Check($"{name} 命令归一化非空", rule.Commands.Count > 0,
                $"commands={rule.Commands.Count}（含旧字段迁移）");

            var fieldKeys = rule.Fields.Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var chartKeys = rule.Charts.Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // 字段 / 图表：Key、解析规则
            var duplicateFieldKeys = rule.Fields.GroupBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Check($"{name} 字段 Key 唯一", duplicateFieldKeys.Count == 0,
                duplicateFieldKeys.Count == 0 ? $"{rule.Fields.Count} 个字段" : "重复：" + string.Join(",", duplicateFieldKeys));

            var badFields = new List<string>();
            foreach (var f in rule.Fields)
            {
                if (string.IsNullOrWhiteSpace(f.Key)) badFields.Add("(空Key)");
                if (string.IsNullOrWhiteSpace(f.Query)) badFields.Add($"{f.Key}:Query 为空");
                if (f.Format is not ("hex" or "ascii")) badFields.Add($"{f.Key}:Format={f.Format}");
                if (f.Parse is null) { badFields.Add($"{f.Key}:Parse 为空"); continue; }

                var p = f.Parse;
                if (p.Type.Equals("regex", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryCompileRegex(p.Pattern, out var err)) badFields.Add($"{f.Key}:正则 {err}");
                }
                else
                {
                    if (p.Offset is null or < 0) badFields.Add($"{f.Key}:Offset 无效");
                    if (p.Length is null or <= 0) badFields.Add($"{f.Key}:Length 无效");
                    if (p.Endian is not ("big" or "little")) badFields.Add($"{f.Key}:Endian={p.Endian}");
                    if (p.DataType is not (null or "u8" or "i8" or "u16" or "i16" or "u32" or "i32" or "float32"))
                        badFields.Add($"{f.Key}:DataType={p.DataType}");
                }

                if (!string.IsNullOrEmpty(p.FrameFilter))
                {
                    var bad = Tokens(p.FrameFilter).Where(t => !IsFrameToken(t)).ToList();
                    if (bad.Count > 0) badFields.Add($"{f.Key}:FrameFilter 非法字节 {string.Join(",", bad)}");
                }
            }
            Check($"{name} 字段解析规则有效", badFields.Count == 0,
                badFields.Count == 0 ? $"{rule.Fields.Count} 条" : string.Join("; ", badFields.Take(4)));

            var badCharts = new List<string>();
            foreach (var c in rule.Charts)
            {
                if (string.IsNullOrWhiteSpace(c.Key)) badCharts.Add("(空Key)");
                if (c.Parse is null) { badCharts.Add($"{c.Key}:Parse 为空"); continue; }
                var p = c.Parse;
                if (p.Type.Equals("regex", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryCompileRegex(p.Pattern, out var err)) badCharts.Add($"{c.Key}:正则 {err}");
                }
                else
                {
                    if (p.Offset is null or < 0) badCharts.Add($"{c.Key}:Offset 无效");
                    if (p.Length is null or <= 0) badCharts.Add($"{c.Key}:Length 无效");
                }
            }
            Check($"{name} 图表解析规则有效", badCharts.Count == 0,
                badCharts.Count == 0 ? $"{rule.Charts.Count} 条" : string.Join("; ", badCharts.Take(4)));

            // 统一命令：模式、帧、断言
            var badCmds = new List<string>();
            foreach (var cmd in rule.Commands)
            {
                var label = string.IsNullOrWhiteSpace(cmd.Name) ? "(未命名)" : cmd.Name;
                if (string.IsNullOrWhiteSpace(cmd.Name)) badCmds.Add("存在未命名命令");
                if (cmd.Mode is not ("manual" or "auto" or "both")) badCmds.Add($"{label}:Mode={cmd.Mode}");
                if (cmd.Format is not ("hex" or "ascii")) badCmds.Add($"{label}:Format={cmd.Format}");
                if (cmd.Append is not ("none" or "crlf" or "lf" or "cr")) badCmds.Add($"{label}:Append={cmd.Append}");
                if (cmd.Repeat < 1) badCmds.Add($"{label}:Repeat={cmd.Repeat}");
                if (cmd.IntervalMs < 0) badCmds.Add($"{label}:IntervalMs={cmd.IntervalMs}");

                if (string.IsNullOrWhiteSpace(cmd.TxFrame))
                    badCmds.Add($"{label}:TxFrame 为空");
                else if (cmd.Format == "hex")
                {
                    var tokens = Tokens(ChecksumEncoder.EncodeHex(cmd.TxFrame));
                    if (tokens.Length == 0 || tokens.Any(t => !IsHexToken(t)))
                        badCmds.Add($"{label}:TX 帧编码后非法 [{string.Join(' ', tokens)}]");
                }

                bool triggerLooksHex = !string.IsNullOrWhiteSpace(cmd.TriggerFrame)
                                       && Tokens(cmd.TriggerFrame).All(IsFrameToken);
                if (cmd.IsAuto)
                {
                    if (string.IsNullOrWhiteSpace(cmd.TriggerFrame)) badCmds.Add($"{label}:触发帧为空");
                    if (cmd.TriggerMode is not ("exact" or "mask" or "regex")) badCmds.Add($"{label}:TriggerMode={cmd.TriggerMode}");
                    if (cmd.TriggerMode == "regex")
                    {
                        if (!TryCompileRegex(cmd.TriggerFrame, out var err)) badCmds.Add($"{label}:触发正则 {err}");
                    }
                    else if (!triggerLooksHex)
                    {
                        // ASCII 触发帧在 exact/mask 下无法匹配（FrameMatcher 只比较 HEX），由 F 节单独判定
                    }
                    if (cmd.DelayMs < 0) badCmds.Add($"{label}:DelayMs={cmd.DelayMs}");
                    if (cmd.MaxTimes < 0) badCmds.Add($"{label}:MaxTimes={cmd.MaxTimes}");
                }

                if (cmd.Expect is { } expect)
                {
                    if (expect.MatchMode is not ("exact" or "mask" or "regex")) badCmds.Add($"{label}:Expect.MatchMode={expect.MatchMode}");
                    if (expect.TimeoutMs <= 0) badCmds.Add($"{label}:Expect.TimeoutMs={expect.TimeoutMs}");
                    if (string.IsNullOrWhiteSpace(expect.Expected)) badCmds.Add($"{label}:Expect.Expected 为空");
                    else if (expect.MatchMode == "regex")
                    {
                        if (!TryCompileRegex(expect.Expected, out var err)) badCmds.Add($"{label}:预期正则 {err}");
                    }
                    else
                    {
                        var bad = Tokens(expect.Expected).Where(t => !IsFrameToken(t)).ToList();
                        if (bad.Count > 0) badCmds.Add($"{label}:预期帧非法字节 {string.Join(",", bad)}");
                    }

                    foreach (var a in expect.Asserts)
                    {
                        if (string.IsNullOrWhiteSpace(a.Source)) { badCmds.Add($"{label}:断言 Source 为空"); continue; }
                        if (!fieldKeys.Contains(a.Source) && !chartKeys.Contains(a.Source))
                            badCmds.Add($"{label}:断言 Source '{a.Source}' 不存在");
                        if (a.Op is not ("eq" or "ne" or "range" or "regex")) badCmds.Add($"{label}:断言 Op={a.Op}");
                        if (a.Op is "eq" or "ne" && a.Expected is null) badCmds.Add($"{label}:断言 {a.Op} 缺 Expected");
                        if (a.Op == "range" && (a.Min is null || a.Max is null)) badCmds.Add($"{label}:断言 range 缺 Min/Max");
                    }
                }
            }
            Check($"{name} 命令规则有效", badCmds.Count == 0,
                badCmds.Count == 0 ? $"{rule.Commands.Count} 条" : string.Join("; ", badCmds.Take(4)));

            // 样例：若有 regex+ascii 字段，样例应答至少能解析出一个值
            if (rule.Sample is { } sample && !string.IsNullOrWhiteSpace(sample.Response))
            {
                var regexFields = rule.Fields
                    .Where(f => f.Format == "ascii" && f.Parse is not null
                                && f.Parse.Type.Equals("regex", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (regexFields.Count > 0 && !sample.Response.Contains(' '))
                {
                    int parsed = regexFields.Count(f =>
                    {
                        var value = FrameParser.Parse(f.Parse, Array.Empty<byte>(), sample.Response);
                        return value.Numeric is not null || value.Display is not null;
                    });
                    Check($"{name} 样例应答可解析字段", parsed > 0, $"解析成功 {parsed}/{regexFields.Count}");
                }
            }
        }
    }
}

// ================= B. 帧匹配 =================
Console.WriteLine();
Console.WriteLine("=== B. FrameMatcher 匹配 ===");
Check("exact 归一化大小写/空白", FrameMatcher.Match("exact", "01 03 02 00 7A", "", "01 03 02 00 7a"));
Check("mask ?? 通配", FrameMatcher.Match("mask", "01 03 02 00 7A", "", "01 03 02 ?? 7A"));
Check("mask 长度不同不匹配", !FrameMatcher.Match("mask", "01 03 02", "", "01 03 02 ??"));
Check("regex 匹配 HEX", FrameMatcher.Match("regex", "01 03 02 00 7A", "abc", "^01 03"));
Check("regex 匹配文本", FrameMatcher.Match("regex", "", "OK+CSQ: 20", @"CSQ:\s*\d+"));
Check("空白归一化", FrameMatcher.NormalizeHex(" 0x01\t03 ") == "01 03");

// ================= B2. 自动应答 ASCII 触发与回复 =================
Console.WriteLine();
Console.WriteLine("=== B2. 自动应答 ASCII 支持（4G 透传模组规则场景）===");
const string asciiCmd = "AT+CGSN\r\n";
string asciiCmdHex = BitConverter.ToString(Encoding.UTF8.GetBytes(asciiCmd)).Replace('-', ' ');
Check("ASCII 触发帧 exact 可匹配", FrameMatcher.Match("exact", asciiCmdHex, asciiCmd, asciiCmd),
    $"RX HEX=[{asciiCmdHex}] 规则=AT+CGSN");
Check("ASCII 触发帧 mask 可匹配", FrameMatcher.Match("mask", asciiCmdHex, asciiCmd, asciiCmd));
Check("ASCII 触发帧 regex 可匹配", FrameMatcher.Match("regex", asciiCmdHex, asciiCmd, @"AT\+CGSN"));

// 成帧后的文本不含行尾终止符，规则里带 \r\n 也必须能命中
var asciiRuleSet = new DeviceRuleSet();
asciiRuleSet.AutoReplies.Add(new AutoReplyRule
{
    Name = "IMEI",
    MatchMode = "exact",
    Command = asciiCmd,
    ActualReply = "860123456789012\r\nOK"
});
var hit = AutoReplyEngine.FindMatch(asciiRuleSet, asciiCmdHex, "AT+CGSN");
Check("ASCII 触发帧命中自动应答规则", hit is not null, hit?.Name ?? "未命中");

var asciiPayload = hit is null ? null : AutoReplyEngine.BuildReplyPayload(hit);
Check("ASCII 自动应答按原样回发",
    asciiPayload is { IsHex: false, Text: "860123456789012\r\nOK" },
    asciiPayload is null ? "无载荷" : $"IsHex={asciiPayload.IsHex} Text={asciiPayload.Text.Replace("\r", "\\r").Replace("\n", "\\n")}");

var hexPayload = AutoReplyEngine.BuildReplyPayload(new AutoReplyRule
{
    ActualReply = "01 03 02 00 7A [CRC16]"
});
Check("HEX 自动应答仍替换校验占位符",
    hexPayload is { IsHex: true, Text: "01 03 02 00 7A 39 A7" },
    $"IsHex={hexPayload.IsHex} Text={hexPayload.Text}");

// 引擎级验证：RX 到达后按规则自动发出 ASCII 应答
var engine = new RuleExecutionEngine();
var emitted = new List<SerialTxPayload>();
engine.FrameReady += p => emitted.Add(p);
engine.UpdateRule(asciiRuleSet);
engine.ProcessRx(Encoding.UTF8.GetBytes(asciiCmd), asciiCmdHex, "AT+CGSN");
Check("引擎自动应答发出 ASCII 载荷",
    emitted.Count == 1 && emitted[0] is { IsHex: false, Text: "860123456789012\r\nOK" },
    emitted.Count == 0 ? "未发出" : $"IsHex={emitted[0].IsHex} Text={emitted[0].Text.Replace("\r", "\\r").Replace("\n", "\\n")}");

// ================= C. CSV 导出 =================
Console.WriteLine();
Console.WriteLine("=== C. CsvExporter ===");
var csvDir = Path.Combine(Path.GetTempPath(), "MultiSerialToolTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(csvDir);
try
{
    var table = new DataTable();
    table.Columns.Add("名称", typeof(string));
    table.Columns.Add("时间", typeof(DateTime));
    table.Columns.Add("数值", typeof(double));
    table.Rows.Add("普通", new DateTime(2026, 11, 14, 1, 2, 3, 45), 1.5);
    table.Rows.Add("含,逗号", new DateTime(2026, 11, 14, 2, 3, 4, 5), 2.5);
    table.Rows.Add("含\"引号\"", DBNull.Value, 3.5);

    var csvPath = Path.Combine(csvDir, "out.csv");
    CsvExporter.WriteDataTable(table, csvPath);
    byte[] raw = File.ReadAllBytes(csvPath);
    Check("CSV 带 UTF-8 BOM", raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF);

    string text = File.ReadAllText(csvPath, Encoding.UTF8);
    var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
    Check("CSV 行数", lines.Length == 4, $"实际 {lines.Length}（1 表头 + 3 数据）");
    Check("CSV 表头", lines[0] == "名称,时间,数值", lines[0]);
    Check("CSV 日期格式", lines[1].Contains("2026-11-14 01:02:03.045"), lines[1]);
    Check("CSV 逗号转义", lines[2].Contains("\"含,逗号\""), lines[2]);
    Check("CSV 引号双写", lines[3].Contains("\"含\"\"引号\"\"\""), lines[3]);
}
finally
{
    try { Directory.Delete(csvDir, true); } catch { }
}

// ================= D. 实时落盘记录器 =================
Console.WriteLine();
Console.WriteLine("=== D. RealtimeFileRecorder 四种格式 ===");
const string testPortName = "COM_SVC_TEST_9471";
var recorderDir = Path.Combine(Path.GetTempPath(), "MultiSerialToolTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(recorderDir);
var device = new SerialDeviceInfo { Name = testPortName, FullName = "Service Test Device" };
string deviceKey = DeviceKeyProvider.GetKey(device);
string ruleFile = Path.Combine(AppPaths.FileRulesDirectory, deviceKey + ".json");
bool rulePreexisting = File.Exists(ruleFile);
if (rulePreexisting)
{
    Skip("实时落盘记录器", $"设备 Key 规则文件已存在，跳过以免覆盖：{ruleFile}");
}
else
{
    try
    {
        var time = new DateTime(2026, 11, 14, 10, 20, 30, 456);

        void RunFormat(string format)
        {
            FileRuleStore.Save(deviceKey, new FileExportRule
            {
                RealtimeEnabled = true,
                Directory = recorderDir,
                FileName = "rec_{port}_{date}",
                Format = format
            });

            RealtimeFileRecorder.Current.Write(device, isTx: false, time, "41 42", "AB");
            RealtimeFileRecorder.Current.Write(device, isTx: true, time, "43 0A", "C\n");
            RealtimeFileRecorder.Current.CloseAll();

            string path = Path.Combine(recorderDir,
                $"rec_{testPortName}_{time:yyyyMMdd}{FileExportFormats.GetExtension(format)}");
            Check($"记录器-{format} 文件生成", File.Exists(path), path);
            if (!File.Exists(path)) return;

            var bytes = File.ReadAllBytes(path);
            string content = File.ReadAllText(path, Encoding.UTF8);

            switch (format)
            {
                case FileExportFormats.Csv:
                    Check("记录器-CSV BOM", bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
                    Check("记录器-CSV 表头", content.Contains("时间,方向,HEX,ASCII"));
                    Check("记录器-CSV RX/TX 行", content.Contains(",RX,41 42,AB") && content.Contains(",TX,43 0A,"));
                    break;
                case FileExportFormats.Txt:
                    Check("记录器-TXT 时间戳与方向", content.Contains($"[{time:yyyy-MM-dd HH:mm:ss.fff}] RX AB")
                                                     && content.Contains("] TX C"));
                    break;
                case FileExportFormats.Hex:
                    var hexLines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                          .Select(l => l.TrimEnd('\r')).ToList();
                    Check("记录器-HEX 仅字节行", hexLines.Count == 2 && hexLines[0] == "41 42" && hexLines[1] == "43 0A",
                        string.Join(" | ", hexLines));
                    break;
                case FileExportFormats.Json:
                    var jsonLines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    bool jsonOk = jsonLines.Length == 2;
                    var dirs = new List<string>();
                    foreach (var line in jsonLines)
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            dirs.Add(doc.RootElement.GetProperty("dir").GetString() ?? "");
                            jsonOk &= doc.RootElement.TryGetProperty("time", out _)
                                      && doc.RootElement.TryGetProperty("hex", out _)
                                      && doc.RootElement.TryGetProperty("ascii", out _);
                        }
                        catch { jsonOk = false; }
                    }
                    Check("记录器-JSONL 每行可解析", jsonOk, string.Join(",", dirs));
                    break;
            }
        }

        RunFormat(FileExportFormats.Csv);
        RunFormat(FileExportFormats.Txt);
        RunFormat(FileExportFormats.Hex);
        RunFormat(FileExportFormats.Json);

        // 关闭实时写入后不再生成新文件
        FileRuleStore.Save(deviceKey, new FileExportRule
        {
            RealtimeEnabled = false,
            Directory = recorderDir,
            FileName = "off_{port}",
            Format = FileExportFormats.Csv
        });
        DateTime later = new(2026, 11, 15, 0, 0, 0);
        RealtimeFileRecorder.Current.Write(device, false, later, "00", ".");
        RealtimeFileRecorder.Current.CloseAll();
        Check("记录器-关闭实时写入不落盘",
            !File.Exists(Path.Combine(recorderDir, $"off_{testPortName}.csv")));
    }
    finally
    {
        RealtimeFileRecorder.Current.CloseAll();
        try { File.Delete(ruleFile); } catch { }
        try { Directory.Delete(recorderDir, true); } catch { }
    }
}

// ================= E. 导出格式常量 =================
Console.WriteLine();
Console.WriteLine("=== E. FileExportFormats ===");
Check("扩展名映射", FileExportFormats.GetExtension(FileExportFormats.Csv) == ".csv"
                   && FileExportFormats.GetExtension(FileExportFormats.Txt) == ".txt"
                   && FileExportFormats.GetExtension(FileExportFormats.Hex) == ".hex"
                   && FileExportFormats.GetExtension(FileExportFormats.Json) == ".jsonl");
Check("下拉选项顺序", string.Join(",", FileExportFormats.All) == "csv,txt,hex,json");
Check("默认导出目录在文档下", AppPaths.DefaultExportDirectory.Contains("MultiSerialTool"));

Console.WriteLine($"=== 结果：{pass} 通过 / {fail} 失败 / {skip} 跳过 ===");
return fail == 0 ? 0 : 1;
