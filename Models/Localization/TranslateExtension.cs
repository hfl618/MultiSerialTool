using System.Windows.Data;
using System.Windows.Markup;

namespace MultiSerialTool.Models.Localization;

/// <summary>
/// XAML 静态文本本地化标记：{loc:Translate 源文本}。
/// 内部绑定到 LocalizationService 的索引器，运行时切换语言可自动刷新。
/// </summary>
public sealed class TranslateExtension : MarkupExtension
{
    /// <summary>源字符串（即语言包中的键）</summary>
    public string Key { get; }

    /// <summary>XAML 特性语法：{loc:Translate 文本}</summary>
    public TranslateExtension(string key)
    {
        Key = key;
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        // 绑定服务索引器：语言切换时服务以空属性名通知，绑定自动重新取值
        var binding = new System.Windows.Data.Binding
        {
            Source = LocalizationService.Instance,
            Path = new System.Windows.PropertyPath("[" + Key + "]"),
            Mode = System.Windows.Data.BindingMode.OneWay
        };
        return binding.ProvideValue(serviceProvider);
    }
}
