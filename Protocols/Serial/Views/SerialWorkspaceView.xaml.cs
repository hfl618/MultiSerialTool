using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using MultiSerialTool.Shared;
using MultiSerialTool.ViewModels;
using MultiSerialTool.Views;

namespace MultiSerialTool.Protocols.Serial
{
    /// <summary>
    /// SerialWorkspaceView.xaml 的交互逻辑
    /// </summary>
    public partial class SerialWorkspaceView : System.Windows.Controls.UserControl
    {
        // ===== 公共查找弹窗接入状态 =====
        private FindDialog? _findDialog;
        private FindDialogViewModel? _findVm;
        private INotifyCollectionChanged? _findSubscription;

        public SerialWorkspaceView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            PreviewKeyDown += OnPreviewKeyDown;
        }

        /// <summary>Ctrl + F 快捷打开查找弹窗</summary>
        private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.F
                && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
            {
                OpenFindDialog();
                e.Handled = true;
            }
        }

        /// <summary>查找按钮：打开（或激活已打开的）公共查找弹窗</summary>
        private void BtnFind_Click(object sender, System.Windows.RoutedEventArgs e)
            => OpenFindDialog();

        /// <summary>复制反馈提示的自动清除计时</summary>
        private System.Windows.Threading.DispatcherTimer? _copyHintTimer;

        /// <summary>
        /// 日志框快捷键：Ctrl+C 复制选中的多行（支持 Extended 多选），
        /// 按当前 HEX/文本视图决定复制内容，拼接为 [时间] TX/RX 内容。
        /// </summary>
        private void LogList_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.C
                || System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Control)
            {
                return;
            }

            if (LogList.SelectedItems.Count == 0)
            {
                return;
            }

            CopyEntries(LogList.SelectedItems.OfType<LogEntry>());
            e.Handled = true;
        }

        /// <summary>右键菜单：复制当前选中的日志行</summary>
        private void CopySelected_Click(object sender, System.Windows.RoutedEventArgs e)
            => CopyEntries(LogList.SelectedItems.OfType<LogEntry>());

        /// <summary>右键菜单：复制当前设备全部日志</summary>
        private void CopyAll_Click(object sender, System.Windows.RoutedEventArgs e)
            => CopyEntries(LogList.Items.OfType<LogEntry>());

        /// <summary>
        /// 复制指定日志条目到剪贴板：按当前 HEX/文本视图取值，拼为 [时间] TX/RX 内容，
        /// 复制结果在底部状态区给出临时反馈。
        /// </summary>
        private void CopyEntries(IEnumerable<LogEntry> entries)
        {
            var list = entries.ToList();
            if (list.Count == 0)
            {
                ShowCopyHint("没有可复制的内容");
                return;
            }

            bool hex = (DataContext as SerialWorkspaceViewModel)?.IsHexDisplay ?? false;
            var sb = new System.Text.StringBuilder();

            foreach (var entry in list)
            {
                var content = hex && entry.HexText != null ? entry.HexText : entry.Text;
                sb.Append('[').Append(entry.Time.ToString("yyyy-MM-dd HH:mm:ss"))
                  .Append("] ").Append(entry.Prefix).Append(' ').AppendLine(content);
            }

            try
            {
                System.Windows.Clipboard.SetText(sb.ToString());
                ShowCopyHint($"已复制 {list.Count} 条");
            }
            catch (Exception ex)
            {
                ShowCopyHint($"复制失败：{ex.Message}");
            }
        }

        /// <summary>在底部状态区显示复制反馈，1.5 秒后自动清除</summary>
        private void ShowCopyHint(string message)
        {
            CopyHint.Text = message;

            _copyHintTimer ??= new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1.5)
            };
            _copyHintTimer.Tick -= OnCopyHintTimerTick;
            _copyHintTimer.Tick += OnCopyHintTimerTick;
            _copyHintTimer.Start();
        }

        private void OnCopyHintTimerTick(object? sender, System.EventArgs e)
        {
            _copyHintTimer?.Stop();
            CopyHint.Text = string.Empty;
        }

        /// <summary>右键按下时先选中命中的条目，避免右键复制仍按旧选区</summary>
        private void LogList_PreviewMouseRightButtonDown(
            object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var container = FindAncestor<System.Windows.Controls.ListBoxItem>(
                e.OriginalSource as System.Windows.DependencyObject);
            if (container is not null)
            {
                container.Focus();
                LogList.SelectedItem = container.DataContext;
            }
        }

        /// <summary>沿可视化树向上查找指定类型的祖先节点</summary>
        private static T? FindAncestor<T>(System.Windows.DependencyObject? node)
            where T : System.Windows.DependencyObject
        {
            while (node is not null)
            {
                if (node is T match)
                    return match;
                node = System.Windows.Media.VisualTreeHelper.GetParent(node);
            }
            return null;
        }

        /// <summary>
        /// 创建并显示置顶查找弹窗；重复点击时仅激活已有弹窗。
        /// </summary>
        private void OpenFindDialog()
        {
            if (DataContext is not SerialWorkspaceViewModel workspace)
                return;

            if (_findDialog is not null)
            {
                if (_findDialog.WindowState == System.Windows.WindowState.Minimized)
                    _findDialog.WindowState = System.Windows.WindowState.Normal;
                _findDialog.Activate();
                return;
            }

            _findVm = new FindDialogViewModel(
                itemsProvider: () =>
                {
                    var items = new List<object?>();
                    if (LogList.ItemsSource is IEnumerable<object?> source)
                        items.AddRange(source);
                    return items;
                },
                textSelector: item => SelectSearchText(workspace, item),
                locateCallback: LocateItem);

            _findVm.FilterStateChanged += ApplyFindFilter;

            _findDialog = new FindDialog(_findVm)
            {
                Owner = System.Windows.Window.GetWindow(this)
            };
            _findDialog.Closed += OnFindDialogClosed;

            BindCollectionSubscription(LogList.ItemsSource as INotifyCollectionChanged);
            ApplyFindFilter();
            _findDialog.Show();
        }

        /// <summary>
        /// 搜索文本取界面实际显示内容：HEX 视图取 HexText，ASCII 视图取 Text。
        /// </summary>
        private static string SelectSearchText(
            SerialWorkspaceViewModel workspace, object? item)
        {
            if (item is not LogEntry entry)
                return item?.ToString() ?? string.Empty;

            return workspace.IsHexDisplay && entry.HexText is not null
                ? entry.HexText
                : entry.Text;
        }

        /// <summary>命中定位：选中并滚动到目标日志项</summary>
        private void LocateItem(object? item)
        {
            if (item is null)
                return;

            LogList.SelectedItem = item;
            LogList.UpdateLayout();
            LogList.ScrollIntoView(item);
        }

        /// <summary>
        /// 把查找 VM 的谓词挂到集合默认视图：
        /// 查找模式谓词恒为 true（不隐藏），过滤模式按表达式隐藏不命中项。
        /// </summary>
        private void ApplyFindFilter()
        {
            if (LogList.ItemsSource is null || _findVm is null)
                return;

            var view = System.Windows.Data.CollectionViewSource
                .GetDefaultView(LogList.ItemsSource);
            view.Filter = _findVm.Matches;
        }

        /// <summary>订阅/退订日志集合变化，新增或清空后重新计算命中</summary>
        private void BindCollectionSubscription(INotifyCollectionChanged? collection)
        {
            if (_findSubscription is not null)
                _findSubscription.CollectionChanged -= OnFindCollectionChanged;

            _findSubscription = collection;

            if (_findSubscription is not null)
                _findSubscription.CollectionChanged += OnFindCollectionChanged;
        }

        private void OnFindCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            _findVm?.Refresh();
        }

        /// <summary>弹窗关闭：移除过滤器并退订事件，恢复列表完整显示</summary>
        private void OnFindDialogClosed(object? sender, System.EventArgs e)
        {
            BindCollectionSubscription(null);

            if (LogList.ItemsSource is not null)
            {
                System.Windows.Data.CollectionViewSource
                    .GetDefaultView(LogList.ItemsSource).Filter = null;
            }

            if (_findDialog is not null)
                _findDialog.Closed -= OnFindDialogClosed;

            _findDialog = null;
            _findVm = null;
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
        /// 避免残留上一个设备的滚动位置；查找弹窗打开时同步重绑过滤器。
        /// </summary>
        private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(SerialWorkspaceViewModel.CurrentLogEntries))
                return;

            LogList.UpdateLayout();
            if (LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]);

            if (_findVm is not null)
            {
                BindCollectionSubscription(LogList.ItemsSource as INotifyCollectionChanged);
                ApplyFindFilter();
                _findVm.Refresh();
            }
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

        private void SidePaneView_Loaded(object sender, RoutedEventArgs e)
        {

        }
    }
}
