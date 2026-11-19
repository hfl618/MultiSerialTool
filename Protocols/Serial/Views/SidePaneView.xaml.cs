using System.Windows;
using System.Windows.Controls.Primitives;

namespace MultiSerialTool.Protocols.Serial
{
    /// <summary>
    /// SidePaneView.xaml 的交互逻辑：统一指令列表侧边栏。
    /// DataContext 继承自父级，为 SerialWorkspaceViewModel。
    /// </summary>
    public partial class SidePaneView : System.Windows.Controls.UserControl
    {
        public SidePaneView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 侧边栏拖宽结束：把实际宽度回写 VM（内部 clamp 180–360 并持久化）。
        /// </summary>
        private void PaneSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (DataContext is SerialWorkspaceViewModel vm && sender is FrameworkElement splitter)
            {
                double paneWidth = splitter.Parent is FrameworkElement parent
                    ? parent.ActualWidth
                    : vm.SidePaneWidth;
                vm.SidePaneWidth = paneWidth;
            }
        }
    }
}
