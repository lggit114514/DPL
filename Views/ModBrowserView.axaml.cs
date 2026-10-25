using Avalonia.Controls;

namespace DPL.Views;

/// <summary>
/// 模组下载页：搜索 Modrinth / CurseForge，自动择优，解析依赖并下载到实例的 mods 目录。
/// </summary>
public partial class ModBrowserView : UserControl
{
    /// <summary>
    /// 初始化视图。
    /// </summary>
    public ModBrowserView() => InitializeComponent();
}
