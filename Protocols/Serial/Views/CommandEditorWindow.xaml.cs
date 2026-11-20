using System.Windows;
using MultiSerialTool.Models.Localization;

namespace MultiSerialTool.Protocols.Serial
{
    /// <summary>
    /// 新增自定义命令弹窗：填写名称、发送帧、格式与是否自动发送。
    /// 确定后通过 <see cref="Result"/> 把新命令交给调用方写入当前规则。
    /// </summary>
    public partial class CommandEditorWindow : Window
    {
        /// <summary>确定后生成的自定义命令；取消时为 null</summary>
        public CommandRule? Result { get; private set; }

        /// <summary>编辑模式下的原命令；null 表示新增</summary>
        private readonly CommandRule? _existing;

        public CommandEditorWindow()
        {
            InitializeComponent();
        }

        /// <summary>编辑已有命令：字段预填，确定后回写到同一实例。</summary>
        public CommandEditorWindow(CommandRule existing) : this()
        {
            _existing = existing;
            Title = Loc.T("编辑自定义命令");
            NameBox.Text = existing.Name;
            FrameBox.Text = existing.TxFrame;
            TriggerBox.Text = existing.TriggerFrame;
            FormatBox.SelectedValue = string.Equals(existing.Format, "ascii",
                System.StringComparison.OrdinalIgnoreCase) ? "ascii" : "hex";
            AutoSendBox.IsChecked = existing.IsAuto;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            var frame = (FrameBox.Text ?? string.Empty).Trim();
            if (frame.Length == 0)
            {
                System.Windows.MessageBox.Show(this, Loc.T("发送帧不能为空"), Loc.T("错误"),
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                FrameBox.Focus();
                return;
            }

            var name = (NameBox.Text ?? string.Empty).Trim();
            var trigger = (TriggerBox.Text ?? string.Empty).Trim();
            bool auto = AutoSendBox.IsChecked == true;
            if (auto && trigger.Length == 0)
            {
                System.Windows.MessageBox.Show(this, Loc.T("自动发送需要填写触发帧"),
                    Loc.T("错误"),
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                TriggerBox.Focus();
                return;
            }

            var format = FormatBox.SelectedValue as string ?? "hex";
            var mode = auto ? CommandRuleMode.Auto : CommandRuleMode.Manual;

            if (_existing is not null)
            {
                _existing.Name = name.Length == 0 ? _existing.Name : name;
                _existing.TxFrame = frame;
                _existing.TriggerFrame = trigger;
                _existing.Format = format;
                _existing.Mode = mode;
                Result = _existing;
            }
            else
            {
                Result = new CommandRule
                {
                    Name = name.Length == 0 ? Loc.T("新命令") : name,
                    TxFrame = frame,
                    TriggerFrame = trigger,
                    Format = format,
                    Mode = mode
                };
            }
            DialogResult = true;
        }
    }
}
