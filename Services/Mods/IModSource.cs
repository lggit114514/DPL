using DPL.Models.Mods;

namespace DPL.Services.Mods;

/// <summary>
/// 统一的模组来源接口。每个平台（Modrinth / CurseForge）各实现一份，
/// 上层（择优、依赖解析、下载）只依赖这个接口，不感知平台细节。
/// </summary>
public interface IModSource
{
    /// <summary>平台类型。</summary>
    ModSourceKind Kind { get; }

    /// <summary>平台显示名称。</summary>
    string DisplayName { get; }

    /// <summary>当前配置下是否可用（例如 CurseForge 需要 API Key）。</summary>
    bool IsConfigured { get; }

    /// <summary>不可用时的原因说明（可用时为 null）。</summary>
    string? UnavailableReason { get; }

    /// <summary>
    /// 按名称搜索模组。
    /// </summary>
    /// <param name="query">关键字。</param>
    /// <param name="gameVersion">游戏版本，必须是精确补丁版本（例如 1.21.1）。</param>
    /// <param name="loader">模组加载器（fabric / quilt / forge / neoforge）；为空表示不筛选。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<ModProjectSummary>> SearchAsync(
        string query,
        string gameVersion,
        string? loader,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取单个项目详情。
    /// </summary>
    /// <param name="projectId">项目 ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<ModProjectSummary?> GetProjectAsync(string projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取与指定游戏版本 / 加载器兼容的版本列表（按发布时间从新到旧）。
    /// </summary>
    /// <param name="projectId">项目 ID。</param>
    /// <param name="gameVersion">游戏版本。</param>
    /// <param name="loader">模组加载器；为空表示不筛选。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<ModVersionInfo>> GetVersionsAsync(
        string projectId,
        string gameVersion,
        string? loader,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 解析某个版本的下载地址。
    /// </summary>
    /// <param name="versionId">版本 ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<string?> ResolveDownloadUrlAsync(string versionId, CancellationToken cancellationToken = default);
}
