using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DPL.ViewModels;

namespace DPL.Views;

/// <summary>
/// 主窗口：承载自定义标题栏、侧边导航与三个功能页面。
/// 同时作为整合包拖拽入口：整个窗口都接受 .zip / .mrpack。
/// </summary>
public partial class MainWindow : Window
{
    private bool _isModpackDialogOpen;

    /// <summary>
    /// 初始化主窗口并开启全局拖拽。
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();

        // XAML 里已通过 DragDrop.DragOver / DragDrop.Drop 绑定事件；
        // 这里再显式打开允许拖拽，并注册路由事件作为兜底（处理器里有 e.Handled 防重复）。
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
    }

    /// <inheritdoc />
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // 窗口显示后再初始化数据，避免界面卡在首帧。
        if (DataContext is MainWindowViewModel viewModel)
            _ = viewModel.InitializeAsync();
    }

    /// <summary>
    /// 拖入文件时只接受 .zip / .mrpack；其它类型显示禁止光标。
    /// </summary>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.Handled)
            return;

        e.DragEffects = GetSupportedModpackFile(e) is null
            ? DragDropEffects.None
            : DragDropEffects.Copy;
        e.Handled = true;
    }

    /// <summary>
    /// 放下整合包后先弹窗让用户确认实例名，再交给 ViewModel 执行安装。
    /// </summary>
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.Handled)
            return;

        e.Handled = true;

        if (DataContext is not MainWindowViewModel viewModel)
            return;

        if (viewModel.IsInstalling)
        {
            viewModel.StatusIsError = true;
            viewModel.StatusMessage = "已有安装任务正在进行，请等待完成或先取消";
            return;
        }

        if (_isModpackDialogOpen)
            return;

        var file = GetSupportedModpackFile(e);
        var archivePath = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            viewModel.NotifyUnsupportedModpackDrop();
            return;
        }

        var defaultName = Path.GetFileNameWithoutExtension(archivePath);
        if (string.IsNullOrWhiteSpace(defaultName))
            defaultName = "整合包实例";

        _isModpackDialogOpen = true;
        try
        {
            var dialog = new ModpackNameDialog(defaultName, viewModel.ValidateModpackInstanceName);
            var name = await dialog.ShowDialog<string?>(this);

            if (!string.IsNullOrWhiteSpace(name))
                await viewModel.InstallModpackAsync(archivePath, name);
        }
        catch (Exception ex)
        {
            viewModel.StatusIsError = true;
            viewModel.StatusMessage = $"无法开始整合包安装：{ex.Message}";
        }
        finally
        {
            _isModpackDialogOpen = false;
        }
    }

    /// <summary>
    /// 从拖拽数据中取第一个受支持的本地整合包文件。
    /// </summary>
    private static IStorageFile? GetSupportedModpackFile(DragEventArgs e)
    {
        var items = e.DataTransfer.TryGetFiles();

        return items?
            .OfType<IStorageFile>()
            .FirstOrDefault(file =>
            {
                var path = file.TryGetLocalPath();
                if (string.IsNullOrWhiteSpace(path))
                    return false;

                var extension = Path.GetExtension(path);
                return extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
                       extension.Equals(".mrpack", StringComparison.OrdinalIgnoreCase);
            });
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
