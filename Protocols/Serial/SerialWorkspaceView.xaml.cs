using System.ComponentModel;

namespace MultiSerialTool.Protocols.Serial
{
    /// <summary>
    /// SerialWorkspaceView.xaml 的交互逻辑
    /// </summary>
    public partial class SerialWorkspaceView : System.Windows.Controls.UserControl
    {
        public SerialWorkspaceView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        /// <summary>绑定工作区 VM，监听其设备切换通知</summary>
        private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is SerialWorkspaceViewModel oldVm)
                oldVm.PropertyChanged -= OnWorkspacePropertyChanged;

            if (e.NewValue is SerialWorkspaceViewModel newVm)
                newVm.PropertyChanged += OnWorkspacePropertyChanged;
        }

        /// <summary>
        /// 设备切换导致日志集合更换时，把新设备日志滚动到最新位置，
        /// 避免残留上一个设备的滚动位置。
        /// </summary>
        private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(SerialWorkspaceViewModel.CurrentLogEntries))
                return;

            LogList.UpdateLayout();
            if (LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]);
        }

        /// <summary>
        /// 发送框键盘处理：第一行按 ↑ 上翻历史，最后一行按 ↓ 下翻历史；
        /// 其余位置 ↑/↓ 保持正常的多行光标移动。
        /// </summary>
        private void SendBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (sender is not System.Windows.Controls.TextBox box)
                return;
            if (DataContext is not SerialWorkspaceViewModel vm)
                return;

            string text = box.Text ?? string.Empty;
            int caret = System.Math.Min(box.CaretIndex, text.Length);
            string before = text[..caret];
            string after = text[caret..];

            if (e.Key == System.Windows.Input.Key.Up && !before.Contains('\n'))
            {
                if (vm.HistoryUp())
                    e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Down && !after.Contains('\n'))
            {
                if (vm.HistoryDown())
                    e.Handled = true;
            }
        }

        /// <summary>
        /// Ctrl + 鼠标滚轮：缩放日志字体，范围 10~24；不带 Ctrl 时保持正常滚动。
        /// </summary>
        private void LogList_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Control)
                return;

            double next = LogList.FontSize + (e.Delta > 0 ? 1 : -1);
            LogList.FontSize = System.Math.Clamp(next, 10, 24);
            e.Handled = true;
        }
    }
}
