using System.Windows.Input;

namespace MultiSerialTool.Commands;

/// <summary>
/// 带参数的通用命令，把带参操作绑定到 ViewModel 中的方法。
/// </summary>
/// <typeparam name="T">命令参数类型。</typeparam>
public class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Predicate<T?>? _canExecute;

    public RelayCommand(Action<T?> execute, Predicate<T?>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) =>
        _canExecute?.Invoke(Cast(parameter)) ?? true;

    public void Execute(object? parameter) => _execute(Cast(parameter));

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    private static T? Cast(object? parameter)
    {
        if (parameter is null)
            return default;

        if (parameter is T typed)
            return typed;

        try
        {
            return (T)Convert.ChangeType(parameter, typeof(T));
        }
        catch
        {
            return default;
        }
    }
}
