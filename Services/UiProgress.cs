using Avalonia.Threading;

namespace DPL.Services;

/// <summary>
/// 把回调切换到 UI 线程执行的 <see cref="IProgress{T}"/> 实现。
/// CmlLib 的下载进度来自后台线程，直接更新界面会抛出线程异常，因此统一通过该类转发。
/// </summary>
/// <typeparam name="T">进度数据的类型。</typeparam>
public sealed class UiProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;

    /// <summary>
    /// 创建 UI 线程进度转发器。
    /// </summary>
    /// <param name="handler">在 UI 线程上执行的进度处理回调。</param>
    public UiProgress(Action<T> handler) => _handler = handler;

    /// <inheritdoc />
    public void Report(T value)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            _handler(value);
            return;
        }

        // 使用 Background 优先级，避免下载进度抢占界面交互的响应。
        Dispatcher.UIThread.Post(() => _handler(value), DispatcherPriority.Background);
    }
}
