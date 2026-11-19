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

        public CommandEditorWindow()
        {
            InitializeComponent();
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
            Result = new CommandRule
            {
                Name = name.Length == 0 ? Loc.T("新命令") : name,
                TxFrame = frame,
                Format = FormatBox.SelectedValue as string ?? "hex",
                Mode = AutoSendBox.IsChecked == true
                    ? CommandRuleMode.Auto
                    : CommandRuleMode.Manual
            };
            DialogResult = true;
        }
    }
}
