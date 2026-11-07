using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MultiSerialTool.ViewModels;

/// <summary>
/// 所有 ViewModel 的基类，实现属性变更通知。
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 设置属性值；值变化时触发 PropertyChanged 并返回 true。
    /// </summary>
    protected bool SetProperty<T>(ref T field, T value,
                                 [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    /// <summary>
    /// 手动触发指定属性的变更通知（供派生类使用）。
    /// </summary>
    protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
