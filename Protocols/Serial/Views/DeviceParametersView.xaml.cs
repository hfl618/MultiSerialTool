namespace MultiSerialTool.Protocols.Serial;

/// <summary>
/// 设备参数视图：根元素直接为 LayoutGroup（GroupBox），
/// 与 SerialWorkspaceView 中的端口信息组同构，保证两组边缘自动对齐。
/// 后续字段直接在该组内添加。
/// </summary>
public partial class DeviceParametersView : DevExpress.Xpf.LayoutControl.LayoutGroup
{
    public DeviceParametersView()
    {
        InitializeComponent();
    }
}
