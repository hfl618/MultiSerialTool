using MultiSerialTool.ViewModels;

namespace MultiSerialTool.Views
{
    /// <summary>
    /// DevicePanelView.xaml 的交互逻辑：
    /// Tab1 测试页容器 + Tab2 全局测试数据表。
    /// </summary>
    public partial class DevicePanelView : System.Windows.Controls.UserControl
    {
        public DevicePanelView()
        {
            InitializeComponent();
            DataContext = new DevicePanelViewModel();
        }
    }
}
