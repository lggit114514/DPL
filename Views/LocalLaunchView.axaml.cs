using Avalonia.Controls;
using DPL.ViewModels;

namespace DPL.Views;

/// <summary>
/// “本地启动”页面：展示本地版本列表并提供启动入口。
/// </summary>
public partial class LocalLaunchView : UserControl
{
    /// <summary>
    /// 初始化视图。
    /// </summary>
    public LocalLaunchView() => InitializeComponent();

    /// <summary>
    /// 收藏 / 全部两个列表共用同一个选中项。这里只处理“新选中”的条目，
    /// 另一个列表因为条目移出集合而清空选择时不会把 SelectedLocalVersion 置空。
    /// </summary>
    private void OnLocalVersionSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        if (e.AddedItems.Count == 0)
            return;

        if (e.AddedItems[0] is VersionItemViewModel item)
            viewModel.SelectedLocalVersion = item;
    }
}
