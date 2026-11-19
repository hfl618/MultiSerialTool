using MultiSerialTool.Shared.Find;

int pass = 0, fail = 0;
void Check(string name, bool actual, bool expected)
{
    bool ok = actual == expected;
    Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}  actual={actual} expected={expected}");
    if (ok) pass++; else fail++;
}

Console.WriteLine("=== FindMatcher 查找/过滤匹配引擎测试 ===");

// ---------- 包含匹配 ----------
Check("包含-默认大小写不敏感命中",
    FindMatcher.IsMatch("RX AT+CSQ ok", "at+csq", FindMatchMode.Contains, false), true);
Check("包含-区分大小写未命中",
    FindMatcher.IsMatch("RX AT+CSQ ok", "at+csq", FindMatchMode.Contains, true), false);
Check("包含-子串命中",
    FindMatcher.IsMatch("01 03 02 00 7A", "00 7A", FindMatchMode.Contains, true), true);

// ---------- 全词匹配 ----------
Check("全词-独立单词命中",
    FindMatcher.IsMatch("RSSI value is -67 dbm", "value", FindMatchMode.WholeWord, false), true);
Check("全词-单词一部分未命中",
    FindMatcher.IsMatch("RSSI value is -67", "val", FindMatchMode.WholeWord, false), false);
Check("全词-符号关键字边界正确",
    FindMatcher.IsMatch("AT+CSQ", "+CSQ", FindMatchMode.WholeWord, true), true);

// ---------- 精确匹配 ----------
Check("精确-完全相等命中",
    FindMatcher.IsMatch("AT+CSQ", "AT+CSQ", FindMatchMode.Exact, true), true);
Check("精确-多余字符未命中",
    FindMatcher.IsMatch("AT+CSQ\r\n", "AT+CSQ", FindMatchMode.Exact, true), false);

// ---------- 正则匹配 ----------
Check("正则-数字模式命中",
    FindMatcher.IsMatch("voltage: 12.34", @"\d+\.\d{2}", FindMatchMode.Regex, true), true);
Check("正则-非法模式不抛异常且未命中",
    FindMatcher.IsMatch("voltage: 12.34", @"\d+(.[0-9", FindMatchMode.Regex, true), false);
Check("正则-编译错误信息可获取",
    !FindMatcher.TryBuildRegex("(", false, out _, out var err) && err is not null, true);

// ---------- 过滤表达式：| 或、\ 与 ----------
Check("过滤-| 或：命中任一即可",
    FindMatcher.FilterMatch("RX battery ok", "TX|battery",
        FindMatchMode.Contains, true, out _), true);
Check("过滤-| 或：两者都不命中",
    FindMatcher.FilterMatch("RX solar ok", "TX|battery",
        FindMatchMode.Contains, true, out _), false);
Check("过滤-\\ 与：两个条件同时命中",
    FindMatcher.FilterMatch("RX battery voltage 26", "battery\\voltage",
        FindMatchMode.Contains, true, out _), true);
Check("过滤-\\ 与：缺一个条件未命中",
    FindMatcher.FilterMatch("RX battery 26", "battery\\voltage",
        FindMatchMode.Contains, true, out _), false);
Check("过滤-与或组合：(A 与 B) 或 C",
    FindMatcher.FilterMatch("solar current 1.2", "battery\\voltage|solar",
        FindMatchMode.Contains, true, out _), true);
Check("过滤-空表达式放行全部",
    FindMatcher.FilterMatch("anything", "   ",
        FindMatchMode.Contains, true, out _), true);
Check("过滤-正则模式下非法条件返回错误",
    FindMatcher.FilterMatch("abc", "(",
        FindMatchMode.Regex, true, out var regexErr), false);
Check("过滤-正则错误信息已回传",
    regexErr is not null && regexErr.Length > 0, true);
Check("过滤-正则或分组命中",
    FindMatcher.FilterMatch("ch1 12.3", @"^\d|ch1",
        FindMatchMode.Regex, true, out _), true);
Check("过滤-正则含反斜杠转义可用",
    FindMatcher.FilterMatch("v 12.34", @"\d+\.\d{2}",
        FindMatchMode.Regex, false, out _), true);
Check("过滤-正则竖线按替代解释",
    FindMatcher.FilterMatch("ch2 ok", "ch1|ch2",
        FindMatchMode.Regex, true, out _), true);

// ---------- 高亮区间 ----------
string Ranges(string text, string expr, FindMatchMode mode, bool caseSensitive)
    => string.Join("|", FindMatcher.FindRanges(text, expr, mode, caseSensitive)
        .Select(r => r.Start + ":" + r.Length));

Check("高亮-包含两处", Ranges("ab ab", "ab", FindMatchMode.Contains, true) == "0:2|3:2", true);
Check("高亮-大小写不敏感", Ranges("AB ab", "ab", FindMatchMode.Contains, false) == "0:2|3:2", true);
Check("高亮-精确整段", Ranges("AT+CSQ", "AT+CSQ", FindMatchMode.Exact, true) == "0:6", true);
Check("高亮-正则数字区间", Ranges("v 12.34", @"\d+\.\d{2}", FindMatchMode.Regex, true) == "2:5", true);
Check("高亮-正则竖线按替代解释",
    Ranges("ch2 ok", "ch1|ch2", FindMatchMode.Regex, true) == "0:3", true);
Check("高亮-全词不匹配子串", Ranges("value valid", "value", FindMatchMode.WholeWord, false) == "0:5", true);
Check("高亮-或表达式两项都高亮",
    Ranges("battery voltage", "battery|voltage", FindMatchMode.Contains, false) == "0:7|8:7", true);
Check("高亮-重叠区间合并",
    Ranges("abc", "ab|bc", FindMatchMode.Contains, true) == "0:3", true);
Check("高亮-空表达式不高亮", Ranges("abc", "", FindMatchMode.Contains, true) == "", true);
Check("高亮-非法正则不抛异常", Ranges("abc", "(", FindMatchMode.Regex, true) == "", true);

Console.WriteLine($"=== 结果：{pass} 通过 / {fail} 失败 ===");
return fail == 0 ? 0 : 1;
