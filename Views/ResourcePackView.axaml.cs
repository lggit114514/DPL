using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DPL.Views;

/// <summary>
/// 资源包（材质包）下载页：独立页面，不与模组下载页共用列表或状态。
/// </summary>
public partial class ResourcePackView : UserControl
{
    /// <summary>
    /// 初始化资源包下载页。
    /// </summary>
    public ResourcePackView() => InitializeComponent();
}
