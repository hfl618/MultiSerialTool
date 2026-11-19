namespace MultiSerialTool.Shared.Find;

/// <summary>文本中的一处命中区间，供高亮渲染使用。</summary>
public readonly record struct MatchRange(int Start, int Length);
