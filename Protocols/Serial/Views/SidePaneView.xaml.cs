using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

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
        /// Ctrl+单击=切换该行选中，Shift+单击=从锚点连选；两者都不发送。
        /// 在隧道阶段截断，避免冒泡到行内命令按钮触发一次发送。
        /// </summary>
        private void Row_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not SerialWorkspaceViewModel vm
                || sender is not FrameworkElement row
                || row.DataContext is not CommandRule cmd)
                return;

            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                vm.ExtendSelection(cmd);
                e.Handled = true;
            }
            else if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                vm.ToggleSelection(cmd);
                e.Handled = true;
            }
        }

        /// <summary>右键：先让该行进入选中，再照常弹出上下文菜单</summary>
        private void Row_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is SerialWorkspaceViewModel vm
                && sender is FrameworkElement row
                && row.DataContext is CommandRule cmd)
                vm.SelectOnly(cmd);
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
