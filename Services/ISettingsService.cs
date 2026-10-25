using DPL.Models;

namespace DPL.Services;

/// <summary>
/// 启动器设置的读写服务。
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// 读取本地设置；若不存在或读取失败，则返回默认设置。
    /// </summary>
    LauncherSettings Load();

    /// <summary>
    /// 将设置保存到本地磁盘。
    /// </summary>
    /// <param name="settings">需要保存的设置对象。</param>
    void Save(LauncherSettings settings);
}
