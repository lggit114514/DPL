using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace DPL.Services;

/// <summary>
/// 基于 Avalonia <see cref="IStorageProvider"/> 的文件选择实现。
/// </summary>
public sealed class FilePickerService : IFilePickerService
{
    private readonly Window _window;

    /// <summary>
    /// 创建文件选择服务。
    /// </summary>
    /// <param name="window">作为对话框父级的主窗口。</param>
    public FilePickerService(Window window) => _window = window;

    /// <inheritdoc />
    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await _window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    /// <inheritdoc />
    public async Task<string?> PickJavaExecutableAsync()
    {
        var files = await _window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 Java 可执行文件",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Java 可执行文件")
                {
                    Patterns = ["java.exe", "javaw.exe", "java"],
                },
            ],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
