using System.Globalization;
using System.Windows.Data;

namespace MultiSerialTool.Models.Localization;

/// <summary>
/// 绑定值翻译转换器：用于“绑定到动态字符串”的场景（如下拉项、表格单元格）。
/// 把绑定得到的源字符串送进本地化服务翻译；静态 XAML 文本请优先使用 {loc:T}。
/// </summary>
public sealed class LocConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            if (value is string text)
                return LocalizationService.Instance.Translate(text);

            return value;
        }
        catch
        {
            // 转换失败返回原值，避免界面因本地化异常出现空白或崩溃。
            return value;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // 本地化文本仅单向展示，不支持反向转换。
        throw new NotSupportedException("LocConverter 不支持 ConvertBack。");
    }
}
