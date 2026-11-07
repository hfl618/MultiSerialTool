namespace MultiSerialTool.Models.Localization;

/// <summary>
/// C# 代码侧本地化入口。用法：Loc.T("串口打开失败")。
/// 名称刻意取短，降低在 ViewModel、弹窗、提示中的使用成本。
/// </summary>
public static class Loc
{
    /// <summary>返回源字符串在当前语言下的译文。</summary>
    public static string T(string text)
        => LocalizationService.Instance.Translate(text);

    /// <summary>
    /// 带占位符的翻译。语言包中用 {0}、{1} 占位，避免字符串拼接破坏语序。
    /// 用法：Loc.T("已接收 {0} 字节", count)。
    /// </summary>
    public static string T(string format, params object[] args)
        => string.Format(LocalizationService.Instance.Translate(format), args);
}
