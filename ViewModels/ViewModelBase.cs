using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DPL.ViewModels;

/// <summary>
/// 所有 ViewModel 的基类，提供属性变更通知的基础实现。
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 触发属性变更通知。
    /// </summary>
    /// <param name="propertyName">发生变更的属性名，默认由编译器自动填充。</param>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// 设置字段值并在值发生变化时触发通知。
    /// </summary>
    /// <typeparam name="T">字段类型。</typeparam>
    /// <param name="field">字段引用。</param>
    /// <param name="value">新值。</param>
    /// <param name="propertyName">属性名，默认由编译器自动填充。</param>
    /// <returns>值是否发生变化。</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
