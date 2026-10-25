namespace DPL.Services;

/// <summary>
/// 文件 / 目录选择服务，用于隔离 ViewModel 与具体的顶部窗口实现。
/// </summary>
public interface IFilePickerService
{
    /// <summary>
    /// 选择游戏目录（.minecraft）。
    /// </summary>
    /// <param name="title">对话框标题。</param>
    /// <returns>选中的目录路径；用户取消时返回 null。</returns>
    Task<string?> PickFolderAsync(string title);

    /// <summary>
    /// 选择 Java 可执行文件。
    /// </summary>
    /// <returns>选中的 Java 路径；用户取消时返回 null。</returns>
    Task<string?> PickJavaExecutableAsync();
}
