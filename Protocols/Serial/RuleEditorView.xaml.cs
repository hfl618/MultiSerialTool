namespace MultiSerialTool.Protocols.Serial;

/// <summary>
/// 规则页独立视图：自带规则编辑器 ViewModel。
/// </summary>
public partial class RuleEditorView : System.Windows.Controls.UserControl
{
    public RuleEditorView()
    {
        InitializeComponent();

        // 设计时（VS WpfSurface/DesignerRunner 进程）不创建带文件 IO 的 VM：
        // 设计器宿主的 BaseDirectory 下不存在 DeviceRules，构造期扫描会产生副作用，
        // 嵌套在 DXTabControl 中时会导致整个选项卡设计时渲染空白。运行时才实例化。
        if (System.ComponentModel.DesignerProperties.GetIsInDesignMode(this))
            return;

        DataContext = new RuleEditorViewModel();
    }
}
