using System.Collections.ObjectModel;

namespace MultiSerialTool.Protocols.Serial;

/// <summary>
/// 设备信息界面的分组定义：对应界面上一个 GroupBox。
/// 后续由配置文件解析生成，当前先用占位数据构造。
/// </summary>
public class DeviceFieldGroup
{
    /// <summary>分组标题（配置文件中可写多语言 Key，由界面层翻译）</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>组内字段定义，界面按集合动态生成</summary>
    public ObservableCollection<DeviceField> Fields { get; set; } = new();
}

/// <summary>
/// 设备信息界面的单个字段定义：对应一个 LayoutItem + 编辑器。
/// </summary>
public class DeviceField
{
    /// <summary>字段唯一标识，配置解析与后续数据回填的 Key</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>显示标签（后续可替换为 loc 多语言 Key）</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>当前值，占位阶段为空，后续由设备上报/配置回填</summary>
    public string? Value { get; set; }

    /// <summary>是否显示刷新按钮（单项主动读取）</summary>
    public bool HasRefreshButton { get; set; }

    /// <summary>是否只读，展示型字段默认只读</summary>
    public bool IsReadOnly { get; set; } = true;

    /// <summary>项宽度（像素），水平排列时使用，后续可由配置覆盖</summary>
    public double ItemWidth { get; set; } = 220;
}
