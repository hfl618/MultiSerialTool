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
        /// 自动发送复选框：阻止点击冒泡到整行（否则会同时触发发送），
        /// 因事件被拦截 CheckEdit 不会自行切换，这里手动翻转 AutoSend，
        /// 绑定会反映新值，随后交给 VM 持久化。
        /// </summary>
        private void AutoSend_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is CommandRule cmd)
                cmd.AutoSend = !cmd.AutoSend;
            if (DataContext is SerialWorkspaceViewModel vm)
                vm.PersistCommandToggle();
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
