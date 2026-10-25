using System.Windows.Input;

namespace DPL.ViewModels;

/// <summary>
/// 同步命令的简单实现。
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    /// <summary>
    /// 创建同步命令。
    /// </summary>
    /// <param name="execute">命令执行逻辑。</param>
    /// <param name="canExecute">命令是否可用的判断逻辑；为 null 时始终可用。</param>
    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    /// <inheritdoc />
    public void Execute(object? parameter) => _execute(parameter);

    /// <summary>
    /// 手动触发 <see cref="CanExecuteChanged"/>，用于让按钮可用状态及时刷新。
    /// </summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// 异步命令实现：执行期间自动禁用自身，避免重复点击。
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private bool _isExecuting;

    /// <summary>
    /// 创建异步命令。
    /// </summary>
    /// <param name="execute">命令执行逻辑。</param>
    /// <param name="canExecute">命令是否可用的判断逻辑；为 null 时始终可用。</param>
    public AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <summary>
    /// 未被捕获的异常处理回调，避免异步命令中的异常导致程序崩溃。
    /// </summary>
    public Action<Exception>? ErrorHandler { get; set; }

    /// <summary>
    /// 命令当前是否正在执行。
    /// </summary>
    public bool IsExecuting => _isExecuting;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke(parameter) ?? true);

    /// <inheritdoc />
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
            return;

        _isExecuting = true;
        RaiseCanExecuteChanged();

        try
        {
            await _execute(parameter);
        }
        catch (Exception ex)
        {
            ErrorHandler?.Invoke(ex);
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 手动触发 <see cref="CanExecuteChanged"/>，用于让按钮可用状态及时刷新。
    /// </summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
