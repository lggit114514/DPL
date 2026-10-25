using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DPL.ViewModels;

namespace DPL.Views;

/// <summary>
/// 主窗口：承载自定义标题栏、侧边导航与三个功能页面。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// 初始化主窗口。
    /// </summary>
    public MainWindow() => InitializeComponent();

    /// <inheritdoc />
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // 窗口显示后再初始化数据，避免界面卡在首帧。
        if (DataContext is MainWindowViewModel viewModel)
            _ = viewModel.InitializeAsync();
    }

    /// <summary>
    /// 支持拖拽自定义标题栏移动窗口；双击标题栏在最大化与还原之间切换。
    /// </summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        BeginMoveDrag(e);
    }
}
