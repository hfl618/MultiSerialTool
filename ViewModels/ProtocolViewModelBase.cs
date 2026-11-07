namespace MultiSerialTool.ViewModels;

/// <summary>
/// 各协议 ViewModel 的公共基类，提供标题与工作区等通用属性。
/// </summary>
public abstract class ProtocolViewModelBase : ViewModelBase
{
    /// <summary>该协议 Tab 上显示的标题</summary>
    public string Title { get; protected set; } = string.Empty;

    /// <summary>该协议对应的右侧工作区 ViewModel</summary>
    public object? Workspace { get; protected set; }
}
