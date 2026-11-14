namespace MultiSerialTool.Services.Storage;

/// <summary>
/// 工作区 UI 状态：跨重启保留界面选择与侧边栏形态。
/// 持久化在 workspace.json。
/// </summary>
public class WorkspaceState
{
    /// <summary>缓存结构版本，后续不兼容升级时按此迁移</summary>
    public int Version { get; set; } = 1;

    /// <summary>上次选中的设备规则类型（规则文件名，不含扩展名）</summary>
    public string? SelectedDeviceType { get; set; }

    /// <summary>HEX 显示开关</summary>
    public bool IsHexDisplay { get; set; } = true;

    /// <summary>左侧侧边栏是否展开（默认收缩）</summary>
    public bool SidePaneOpen { get; set; } = false;

    /// <summary>侧边栏宽度</summary>
    public double SidePaneWidth { get; set; } = 240;

    /// <summary>工作区上次选中的 Tab 索引</summary>
    public int SelectedTabIndex { get; set; }

    /// <summary>从磁盘加载；缺失或损坏时返回默认实例</summary>
    public static WorkspaceState Load()
        => JsonFile.Load(AppPaths.WorkspaceFile, () => new WorkspaceState());

    /// <summary>保存到磁盘</summary>
    public void Save() => JsonFile.Save(AppPaths.WorkspaceFile, this);
}
