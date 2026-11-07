using System.Windows;
using System.Windows.Input;
using MultiSerialTool.ViewModels;

namespace MultiSerialTool.Views;

/// <summary>
/// 公共查找弹窗：置顶、非模态，宿主传入 <see cref="FindDialogViewModel"/>。
/// 回车=下一个，Shift+回车=上一个，Esc=关闭。
/// </summary>
public partial class FindDialog : Window
{
    public FindDialog(FindDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        Loaded += OnLoaded;
    }

    /// <summary>打开即聚焦输入框并把光标放到末尾，便于直接键入。</summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        KeywordBox.Focus();
        KeywordBox.CaretIndex = KeywordBox.Text?.Length ?? 0;
        Keyboard.Focus(KeywordBox);
    }

    /// <summary>
    /// 输入框快捷键：
    /// Enter 下一个（Shift+Enter 上一个），Esc 关闭。
    /// </summary>
    private void KeywordBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                if (FindPreviousCommandCanRun())
                    ((FindDialogViewModel)DataContext).FindPreviousCommand.Execute(null);
            }
            else
            {
                if (FindNextCommandCanRun())
                    ((FindDialogViewModel)DataContext).FindNextCommand.Execute(null);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private bool FindNextCommandCanRun()
        => ((FindDialogViewModel)DataContext).FindNextCommand.CanExecute(null);

    private bool FindPreviousCommandCanRun()
        => ((FindDialogViewModel)DataContext).FindPreviousCommand.CanExecute(null);

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
