namespace MultiSerialTool.Shared.Find;

/// <summary>
/// 查找/过滤的匹配方式。
/// </summary>
public enum FindMatchMode
{
    /// <summary>包含：文本中出现关键字即命中（默认）</summary>
    Contains,

    /// <summary>全词：关键字作为独立单词出现，两侧为非单词边界</summary>
    WholeWord,

    /// <summary>精确：整段文本与关键字完全相等</summary>
    Exact,

    /// <summary>正则：关键字按 .NET 正则表达式解释</summary>
    Regex
}
