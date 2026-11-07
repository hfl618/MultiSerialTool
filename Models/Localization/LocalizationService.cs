using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace MultiSerialTool.Models.Localization;

/// <summary>
/// 本地化服务（单例）。
/// 采用“源字符串即键”模式：源码中直接写中文，由本服务按当前语言返回对应译文。
/// 语言包为嵌入程序集的静态资源（Assets/Languages 下的 JSON），随程序发布，不依赖外部文件。
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    /// <summary>单例实例。</summary>
    public static LocalizationService Instance { get; } = new();

    private const string DefaultCulture = "zh-CN";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly object _sync = new();
    private Dictionary<string, string> _texts = new(StringComparer.Ordinal);

    private LocalizationService()
    {
        // 构造函数只初始化自身字段，不依赖其他静态成员，避免静态初始化顺序问题。
    }

    /// <summary>语言变更事件；切换语言时以空参数名触发，刷新所有绑定。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>当前语言代码，如 zh-CN、en-US。</summary>
    public string CurrentCulture { get; private set; } = DefaultCulture;

    /// <summary>语言偏好文件（%LocalAppData%\MultiSerialTool\current.txt）。</summary>
    private static string PreferenceFile
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MultiSerialTool");
            return Path.Combine(dir, "current.txt");
        }
    }

    /// <summary>
    /// 启动时调用：读取上次语言偏好并加载；无有效记录则使用默认语言。
    /// </summary>
    public void Initialize()
    {
        var culture = DefaultCulture;
        try
        {
            if (File.Exists(PreferenceFile))
            {
                var saved = File.ReadAllText(PreferenceFile).Trim();
                if (!string.IsNullOrEmpty(saved))
                    culture = saved;
            }
        }
        catch
        {
            // 偏好读取失败不影响启动，回退默认语言。
        }

        Switch(culture);
    }

    /// <summary>
    /// 切换语言：加载对应嵌入资源，缺失时回退默认语言，然后通知所有绑定刷新。
    /// </summary>
    public void Switch(string culture)
    {
        lock (_sync)
        {
            _texts = LoadCulture(culture) ?? LoadCulture(DefaultCulture)
                     ?? new Dictionary<string, string>(StringComparer.Ordinal);
            CurrentCulture = culture;
            SavePreference(culture);
        }

        // 空字符串或 null 的属性名表示所有属性均已更改，索引器绑定随之全局刷新。
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    /// <summary>
    /// 翻译入口：返回源字符串在当前语言下的译文；找不到则原样返回，保证不显示空白。
    /// </summary>
    public string Translate(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        lock (_sync)
        {
            return _texts.TryGetValue(text, out var value) && !string.IsNullOrEmpty(value)
                ? value
                : text;
        }
    }

    /// <summary>供 XAML 绑定使用的索引器。</summary>
    public string this[string key] => Translate(key);

    /// <summary>列出嵌入资源中可用的语言代码（依据 Assets/Languages 下的 *.json）。</summary>
    public static IReadOnlyList<string> GetAvailableCultures()
    {
        try
        {
            // 程序集资源无法直接列目录，用资源管理器枚举 g.resources 中的键
            var asm = typeof(LocalizationService).Assembly;
            var manager = new System.Resources.ResourceManager(
                asm.GetName().Name + ".g", asm);
            using var set = manager.GetResourceSet(
                System.Globalization.CultureInfo.InvariantCulture, true, true);
            return set!.Cast<System.Collections.DictionaryEntry>()
                .Select(e => e.Key as string)
                .Where(k => k is not null && k.StartsWith("assets/languages/", StringComparison.OrdinalIgnoreCase)
                            && k.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .Select(k => Path.GetFileNameWithoutExtension(k!))
                .OrderBy(n => n)
                .ToList();
        }
        catch
        {
            return new[] { DefaultCulture };
        }
    }

    /// <summary>从嵌入资源加载语言包，资源不存在时返回 null。</summary>
    private static Dictionary<string, string>? LoadCulture(string culture)
    {
        try
        {
            var uri = new Uri(
                $"pack://application:,,,/Assets/Languages/{culture}.json", UriKind.Absolute);
            var info = System.Windows.Application.GetResourceStream(uri);
            if (info is null)
                return null;

            using var reader = new StreamReader(info.Stream);
            var json = reader.ReadToEnd();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
        }
        catch
        {
            // 语言包缺失或损坏不能拖垮主程序，返回 null 由调用方回退。
            return null;
        }
    }

    private static void SavePreference(string culture)
    {
        try
        {
            var file = PreferenceFile;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, culture);
        }
        catch
        {
            // 偏好写入失败不影响语言切换本身。
        }
    }
}
