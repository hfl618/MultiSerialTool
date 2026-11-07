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
    /// 正则模式下任一条件为非法正则时通过 <paramref name="error"/> 返回错误。
    /// </summary>
    public static bool FilterMatch(string text, string expression,
                                   FindMatchMode mode, bool matchCase,
                                   out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(expression))
            return true;

        string[] orGroups = expression.Split(OrSeparator);
        foreach (string rawGroup in orGroups)
        {
            // 连续的 "|" 会产生空分组，忽略；空分组不参与命中
            string[] terms = rawGroup.Split(AndSeparator);
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
    /// 编译正则；非法模式返回 false 与错误信息，不抛异常。
    /// </summary>
    public static bool TryBuildRegex(string pattern, bool matchCase,
                                     out Regex? regex, out string? error)
    {
        try
        {
            var options = RegexOptions.CultureInvariant;
            if (!matchCase)
                options |= RegexOptions.IgnoreCase;

            regex = new Regex(pattern, options);
            error = null;
            return true;
        }
        catch (ArgumentException ex)
        {
            regex = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 全词匹配：对关键字转义后，按首尾字符是否为单词字符施加边界约束。
    /// 单词字符开头/结尾才要求对应侧为非单词边界；
    /// 符号开头（如 "+CSQ"）左侧本身已构成边界，不额外约束，
    /// 避免 "AT+CSQ" 中匹配 "+CSQ" 被误判为非全词。
    /// </summary>
    private static bool IsWholeWord(string text, string pattern, bool matchCase)
    {
        string escaped = Regex.Escape(pattern);

        string left = IsWordChar(pattern[0]) ? @"(?<!\w)" : string.Empty;
        string right = IsWordChar(pattern[^1]) ? @"(?!\w)" : string.Empty;

        string patternWithBounds = left + escaped + right;

        return TryBuildRegex(patternWithBounds, matchCase, out var regex, out _)
               && regex!.IsMatch(text);
    }

    /// <summary>是否为正则 \w 语义下的单词字符（字母/数字/下划线）。</summary>
    private static bool IsWordChar(char c)
        => c == '_' || char.IsLetterOrDigit(c);
}
