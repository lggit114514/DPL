namespace DPL.Services.ResourcePacks;

/// <summary>
/// 资源包下载相关的地址与策略参数（独立于模组下载配置）。
/// 所有 URL、筛选条件、超时都集中在这里。
/// </summary>
public sealed class ResourcePackConfig
{
    /// <summary>Modrinth API 主源（MCIM 镜像，100% 兼容官方接口）。</summary>
    public string ModrinthApiBase { get; init; } = "https://mod.mcimirror.top/modrinth/v2";

    /// <summary>Modrinth API 备源（官方）。</summary>
    public string ModrinthApiFallbackBase { get; init; } = "https://api.modrinth.com/v2";

    /// <summary>Modrinth 文件官方 CDN（资源包优先直连这里）。</summary>
    public string ModrinthCdnOfficialBase { get; init; } = "https://cdn.modrinth.com";

    /// <summary>BMCLAPI 镜像（资源包缓存情况未知，仅作兜底）。</summary>
    public string ModrinthCdnBangbangBase { get; init; } = "https://bmclapi2.bangbang93.com";

    /// <summary>MCIM 镜像（不缓存资源包，最后兜底，失败概率高）。</summary>
    public string ModrinthCdnMirrorBase { get; init; } = "https://mod.mcimirror.top";

    /// <summary>
    /// Modrinth 搜索的资源包筛选 facet：只取 project_type=resourcepack。
    ///
    /// 注意：<c>/v2/search</c> 的参数名是 <c>facets</c>，值是「JSON 数组的数组」；
    /// 传 <c>new_filters={"project_types":[...]}</c> 会被服务端直接拒绝
    /// （HTTP 400，failed to parse filters），因此这里改用官方 facet 语法。
    /// 资源包与模组加载器无关，所以只筛 project_type，不传 categories / loaders。
    /// </summary>
    public const string ModrinthResourcePackFacet = "[\"project_type:resourcepack\"]";

    /// <summary>
    /// CurseForge 的“资源包”classId（12）。
    /// TODO: CurseForge 资源包源尚未实现（需要 API Key），当前只提供 Modrinth 单源。
    /// 接入时热门查询参数：classId=12、sortField=6（TotalDownloads）、sortOrder=desc。
    /// </summary>
    public const int CurseForgeResourcePackClassId = 12;

    /// <summary>CurseForge 按总下载量排序的 sortField（TotalDownloads = 6）。</summary>
    public const int CurseForgeTotalDownloadsSortField = 6;

    /// <summary>CurseForge 排序方向：降序。</summary>
    public const string CurseForgeSortDesc = "desc";

    /// <summary>每页结果数。</summary>
    public int PageSize { get; init; } = 20;

    /// <summary>API 请求超时。</summary>
    public TimeSpan ApiTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>单个文件下载超时。</summary>
    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromMinutes(10);
}
