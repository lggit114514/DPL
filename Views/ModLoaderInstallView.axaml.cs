using Avalonia.Controls;

namespace DPL.Views;

/// <summary>
/// “选择模组加载器并安装”页面：为选中的 Minecraft 版本选择 Fabric / Quilt 等加载器，
/// 可选择一起安装 Fabric API，然后开始下载安装。
/// </summary>
public partial class ModLoaderInstallView : UserControl
{
    /// <summary>
    /// 初始化视图。
    /// </summary>
    public ModLoaderInstallView() => InitializeComponent();
}
