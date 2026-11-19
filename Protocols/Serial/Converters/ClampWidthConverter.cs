using System.Globalization;
using System.Windows.Data;

namespace MultiSerialTool.Protocols.Serial;

/// <summary>
/// 宽度上限转换器：把控件宽度限制为「视口宽度 − 参数指定的内边距」。
/// 表格按内容测量出的宽度大于视口时，用它钳制，避免整页被撑宽而裁掉右侧控件。
/// </summary>
public sealed class ClampWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double viewport = value is double v && v > 0 ? v : 0;
        double inset = parameter is string s
            && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                ? parsed
                : 0;
        return Math.Max(0, viewport - inset);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
