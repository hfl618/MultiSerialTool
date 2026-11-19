using System.Text.RegularExpressions;

namespace MultiSerialTool.Shared.Find;

/// <summary>
/// 查找/过滤匹配引擎：单关键字匹配与复合表达式匹配。
/// 过滤表达式语法：
///   "|" 分隔代表“或”（OR），"\" 分隔代表“与”（AND）。
/// 解析优先级：先按 "|" 拆成若干或分组，组内再按 "\" 拆成若干与条件，
/// 任一或分组内全部条件命中即整体命中（DNF 析取范式）。
/// </summary>
public static class FindMatcher
{
    /// <summary>过滤表达式中的“或”分隔符。</summary>
    public const char OrSeparator = '|';

    /// <summary>过滤表达式中的“与”分隔符。</summary>
    public const char AndSeparator = '\\';

    /// <summary>
    /// 单关键字是否命中文本。
    /// </summary>
    public static bool IsMatch(string text, string pattern,
                               FindMatchMode mode, bool matchCase)
    {
        if (pattern.Length == 0)
            return false;

        var comparison = matchCase
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        switch (mode)
        {
            case FindMatchMode.Contains:
                return text.Contains(pattern, comparison);

            case FindMatchMode.Exact:
                return string.Equals(text, pattern, comparison);

            case FindMatchMode.WholeWord:
                return IsWholeWord(text, pattern, matchCase);

            case FindMatchMode.Regex:
                return TryBuildRegex(pattern, matchCase, out var regex, out _)
                       && regex!.IsMatch(text);

            default:
                return false;
        }
    }

    /// <summary>
    /// 复合过滤表达式是否命中。
    /// 空表达式（全空白）视为不过滤，返回 true；
    /// 正则模式下反斜杠与竖线本身是正则语法，整条表达式当作一条模式，不做与或拆分；
    /// 非法正则通过 <paramref name="error"/> 返回错误。
    /// </summary>
    public static bool FilterMatch(string text, string expression,
                                   FindMatchMode mode, bool matchCase,
                                   out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(expression))
            return true;

        bool isRegex = mode == FindMatchMode.Regex;

        string[] orGroups = isRegex
            ? new[] { expression }
            : expression.Split(OrSeparator);

        foreach (string rawGroup in orGroups)
        {
            // 连续的 "|" 会产生空分组，忽略；空分组不参与命中
            string[] terms = isRegex
                ? new[] { rawGroup }
                : rawGroup.Split(AndSeparator);
            if (terms.All(t => t.Length == 0))
                continue;

            bool groupHit = true;
            foreach (string term in terms)
            {
                if (term.Length == 0)
                {
                    // 空的与条件（如 "A\"、"\A"）不产生约束
                    continue;
                }

                if (mode == FindMatchMode.Regex
                    && !TryBuildRegex(term, matchCase, out _, out var regexError))
                {
                    error = regexError;
                    groupHit = false;
                    break;
                }

                if (!IsMatch(text, term, mode, matchCase))
                {
                    groupHit = false;
                    break;
                }
            }

            if (groupHit)
                return true;
        }

        return false;
    }

    /// <summary>
    /// 计算文本中与表达式命中的区间（用于高亮），按起点升序且互不重叠。
    /// 非正则模式下拆分规则与过滤一致：| 分组为或，\ 分隔为与；
    /// 正则模式不拆分——反斜杠与竖线本身就是正则语法，整体当作一条模式。
    /// </summary>
    public static IReadOnlyList<MatchRange> FindRanges(
        string text, string expression, FindMatchMode mode, bool matchCase)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(expression))
            return Array.Empty<MatchRange>();

        var ranges = new List<MatchRange>();

        IEnumerable<string> terms = mode == FindMatchMode.Regex
            ? new[] { expression }
            : expression.Split(OrSeparator).SelectMany(g => g.Split(AndSeparator));

        foreach (string term in terms)
        {
            if (term.Length == 0)
                continue;

            CollectRanges(text, term, mode, matchCase, ranges);
        }

        if (ranges.Count == 0)
            return Array.Empty<MatchRange>();

        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));

        var merged = new List<MatchRange>(ranges.Count);
        foreach (MatchRange range in ranges)
        {
            if (merged.Count > 0)
            {
                MatchRange last = merged[^1];
                int lastEnd = last.Start + last.Length;
                if (range.Start <= lastEnd)
                {
                    int end = Math.Max(lastEnd, range.Start + range.Length);
                    merged[^1] = new MatchRange(last.Start, end - last.Start);
                    continue;
                }
            }

            merged.Add(range);
        }

        return merged;
    }

    /// <summary>收集单个条件在文本中的所有命中区间。</summary>
    private static void CollectRanges(string text, string term, FindMatchMode mode,
                                      bool matchCase, List<MatchRange> into)
    {
        var comparison = matchCase
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        switch (mode)
        {
            case FindMatchMode.Contains:
                int index = 0;
                while (index <= text.Length - term.Length)
                {
                    int found = text.IndexOf(term, index, comparison);
                    if (found < 0)
                        break;

                    into.Add(new MatchRange(found, term.Length));
                    index = found + term.Length;
                }
                break;

            case FindMatchMode.Exact:
                if (string.Equals(text, term, comparison))
                    into.Add(new MatchRange(0, text.Length));
                break;

            case FindMatchMode.WholeWord:
                if (TryBuildWholeWordRegex(term, matchCase, out var wordRegex))
                {
                    foreach (Match m in wordRegex!.Matches(text))
                        into.Add(new MatchRange(m.Index, m.Length));
                }
                break;

            case FindMatchMode.Regex:
                if (TryBuildRegex(term, matchCase, out var regex, out _))
                {
                    foreach (Match m in regex!.Matches(text))
                    {
                        if (m.Length > 0)
                            into.Add(new MatchRange(m.Index, m.Length));
                    }
                }
                break;
        }
    }

    /// <summary>
    /// 编译正则；非法模式返回 false 与错误信息，不抛异常。
    /// 正向结果按 (模式, 大小写) 缓存：过滤/高亮会对每条日志调用，
    /// 逐条重建 Regex 是大日志下卡顿的主因。
    /// </summary>
    public static bool TryBuildRegex(string pattern, bool matchCase,
                                     out Regex? regex, out string? error)
    {
        var key = (pattern, matchCase);

        lock (RegexCacheLock)
        {
            if (RegexCache.TryGetValue(key, out var cached))
            {
                regex = cached;
                error = null;
                return true;
            }
        }

        try
        {
            var options = RegexOptions.CultureInvariant;
            if (!matchCase)
                options |= RegexOptions.IgnoreCase;

            regex = new Regex(pattern, options);
            error = null;

            lock (RegexCacheLock)
            {
                // 简单上限，避免用户长时间输入不同模式时无限增长
                if (RegexCache.Count >= RegexCacheLimit)
                    RegexCache.Clear();

                RegexCache[key] = regex;
            }

            return true;
        }
        catch (ArgumentException ex)
        {
            regex = null;
            error = ex.Message;
            return false;
        }
    }

    private const int RegexCacheLimit = 256;
    private static readonly object RegexCacheLock = new();
    private static readonly Dictionary<(string Pattern, bool MatchCase), Regex> RegexCache = new();

    /// <summary>
    /// 全词匹配：对关键字转义后，按首尾字符是否为单词字符施加边界约束。
    /// 单词字符开头/结尾才要求对应侧为非单词边界；
    /// 符号开头（如 "+CSQ"）左侧本身已构成边界，不额外约束，
    /// 避免 "AT+CSQ" 中匹配 "+CSQ" 被误判为非全词。
    /// </summary>
    private static bool IsWholeWord(string text, string pattern, bool matchCase)
        => TryBuildWholeWordRegex(pattern, matchCase, out var regex) && regex!.IsMatch(text);

    /// <summary>按全词语义把关键字编译为带边界约束的正则。</summary>
    private static bool TryBuildWholeWordRegex(string pattern, bool matchCase, out Regex? regex)
    {
        string escaped = Regex.Escape(pattern);

        string left = IsWordChar(pattern[0]) ? @"(?<!\w)" : string.Empty;
        string right = IsWordChar(pattern[^1]) ? @"(?!\w)" : string.Empty;

        return TryBuildRegex(left + escaped + right, matchCase, out regex, out _);
    }

    /// <summary>是否为正则 \w 语义下的单词字符（字母/数字/下划线）。</summary>
    private static bool IsWordChar(char c)
        => c == '_' || char.IsLetterOrDigit(c);
}
