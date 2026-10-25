using DPL.Models;

namespace DPL.Services;

/// <summary>
/// Fabric API 模组下载服务。
/// Fabric API 不在 Mojang / CmlLib 的范围内，这里通过 Modrinth 的公开 API
/// 找到与目标 Minecraft 版本匹配的发行版，再下载到对应版本的 mods 文件夹。
/// </summary>
public interface IFabricApiService
{
    /// <summary>
    /// 查询与指定 Minecraft 版本匹配、且适合 Fabric 加载器的 Fabric API 版本（优先稳定发行版、取最新）。
    /// </summary>
    /// <param name="minecraftVersion">Minecraft 版本号，例如 <c>1.21.4</c>。</param>
    /// <param name="loaderVersion">Fabric 加载器版本（仅用于界面展示，不参与筛选）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>匹配的发行版；没有匹配时返回 null。</returns>
    Task<FabricApiRelease?> GetRecommendedAsync(
        string minecraftVersion,
        string? loaderVersion = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 下载 Fabric API 的 jar 到指定文件夹（通常是 <c>&lt;版本目录&gt;/mods</c>）。
    /// </summary>
    /// <param name="release">要下载的发行版。</param>
    /// <param name="destinationFolder">目标文件夹。</param>
    /// <param name="progress">下载进度回调，参数为 0~1 的比例；可为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>下载后的文件完整路径。</returns>
    Task<string> DownloadAsync(
        FabricApiRelease release,
        string destinationFolder,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}
