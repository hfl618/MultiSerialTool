namespace MultiSerialTool.Protocols.Serial;

/// <summary>
/// 规则页视图：DataContext 由 XAML 绑定到 SerialWorkspaceViewModel.RuleEditor
/// （单一实例，随工作区 VM 生命周期），设计时为 null，不再自建带文件 IO 的 VM。
/// </summary>
public partial class RuleEditorView : System.Windows.Controls.UserControl
{
    public RuleEditorView()
    {
        InitializeComponent();
    }

    /// <summary>单元格编辑结束：通知 VM 调度防抖保存</summary>
    private void AutoReplyGrid_CellEditEnding(object sender,
                                              System.Windows.Controls.DataGridCellEditEndingEventArgs e)
    {
        if (DataContext is RuleEditorViewModel vm && e.EditAction == System.Windows.Controls.DataGridEditAction.Commit)
            vm.NotifyEdited();
    }
}
