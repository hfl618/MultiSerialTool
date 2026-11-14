using System.Windows.Input;

namespace MultiSerialTool.Commands;

/// <summary>
/// 通用命令，把按钮操作绑定到 ViewModel 中的方法。
/// </summary>
public class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    /// <summary>手动请求重新判定 CanExecute（状态变化不在 UI 交互链上时使用）</summary>
    public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
