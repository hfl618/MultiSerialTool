using System.Globalization;
using System.Windows.Data;

namespace MultiSerialTool.Protocols.Serial;

/// <summary>
/// 侧边栏宽度转换器：输入 [是否展开, 展开宽度]，
/// 展开返回像素宽度，收合返回 24px 把手宽度。
/// </summary>
public sealed class PaneWidthConverter : IMultiValueConverter
{
    /// <summary>收合态把手宽度</summary>
    public const double CollapsedWidth = 24;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var open = values.Length > 0 && values[0] is true;
        var width = values.Length > 1 && values[1] is double w && w > 0 ? w : 240;
        return new System.Windows.GridLength(open ? width : CollapsedWidth);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
