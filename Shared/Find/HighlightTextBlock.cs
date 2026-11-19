using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace MultiSerialTool.Shared.Find;

/// <summary>
/// 按查找表达式给命中片段加底色的 TextBlock。
/// 复用一个控件即可同时承载"原文 + 当前查找词 + 匹配方式"，
/// 表达式为空时退化为普通文本，不产生任何高亮开销。
/// </summary>
public sealed class HighlightTextBlock : TextBlock
{
    private static readonly Brush DefaultHighlightBrush = CreateDefaultBrush();

    public static readonly DependencyProperty SourceTextProperty = DependencyProperty.Register(
        nameof(SourceText), typeof(string), typeof(HighlightTextBlock),
        new FrameworkPropertyMetadata(string.Empty, OnContentChanged));

    public static readonly DependencyProperty QueryProperty = DependencyProperty.Register(
        nameof(Query), typeof(string), typeof(HighlightTextBlock),
        new FrameworkPropertyMetadata(string.Empty, OnContentChanged));

    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(FindMatchMode), typeof(HighlightTextBlock),
        new FrameworkPropertyMetadata(FindMatchMode.Contains, OnContentChanged));

    public static readonly DependencyProperty MatchCaseProperty = DependencyProperty.Register(
        nameof(MatchCase), typeof(bool), typeof(HighlightTextBlock),
        new FrameworkPropertyMetadata(false, OnContentChanged));

    public static readonly DependencyProperty HighlightBrushProperty = DependencyProperty.Register(
        nameof(HighlightBrush), typeof(Brush), typeof(HighlightTextBlock),
        new FrameworkPropertyMetadata(DefaultHighlightBrush, OnContentChanged));

    /// <summary>日志原文（HEX 或 ASCII，取决于当前视图）。</summary>
    public string SourceText
    {
        get => (string)GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    /// <summary>当前查找表达式；为空则不高亮。</summary>
    public string Query
    {
        get => (string)GetValue(QueryProperty);
        set => SetValue(QueryProperty, value);
    }

    /// <summary>匹配方式，与查找弹窗保持一致。</summary>
    public FindMatchMode Mode
    {
        get => (FindMatchMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    /// <summary>是否区分大小写。</summary>
    public bool MatchCase
    {
        get => (bool)GetValue(MatchCaseProperty);
        set => SetValue(MatchCaseProperty, value);
    }

    /// <summary>命中片段底色。</summary>
    public Brush HighlightBrush
    {
        get => (Brush)GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    private static Brush CreateDefaultBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x2E));
        brush.Freeze();
        return brush;
    }

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((HighlightTextBlock)d).Rebuild();

    /// <summary>把原文拆成普通片段与命中片段，重建 Inlines。</summary>
    private void Rebuild()
    {
        Inlines.Clear();

        string text = SourceText ?? string.Empty;
        if (text.Length == 0)
            return;

        IReadOnlyList<MatchRange> ranges = FindMatcher.FindRanges(
            text, Query ?? string.Empty, Mode, MatchCase);

        if (ranges.Count == 0)
        {
            Inlines.Add(new Run(text));
            return;
        }

        int position = 0;
        foreach (MatchRange range in ranges)
        {
            if (range.Start > position)
                Inlines.Add(new Run(text.Substring(position, range.Start - position)));

            Inlines.Add(new Run(text.Substring(range.Start, range.Length))
            {
                Background = HighlightBrush
            });

            position = range.Start + range.Length;
        }

        if (position < text.Length)
            Inlines.Add(new Run(text.Substring(position)));
    }
}
