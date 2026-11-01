using DPL.Models;

namespace DPL.Services;

/// <summary>
/// 启动器设置的读写服务。
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// 最近一次 <see cref="Load"/> 产生的警告（例如便携游戏目录创建失败并已回退），
    /// 供界面写入启动器日志；没有警告时为 null。
    /// </summary>
    string? LastWarning { get; }

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
