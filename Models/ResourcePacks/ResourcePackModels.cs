namespace DPL.Models.ResourcePacks;

/// <summary>
/// 资源包项目（Modrinth 统一视图）。
/// </summary>
/// <param name="Source">来源平台显示名。</param>
/// <param name="ProjectId">平台内的项目 ID。</param>
/// <param name="Slug">短名称。</param>
/// <param name="Name">资源包名称。</param>
/// <param name="Description">简短描述。</param>
/// <param name="UpdatedAt">最近更新时间（UTC）。</param>
/// <param name="Downloads">总下载量。</param>
public sealed record ResourcePackProject(
    string Source,
    string ProjectId,
    string Slug,
    string Name,
    string Description,
    DateTimeOffset UpdatedAt,
    long Downloads)
{
    /// <summary>来源显示名称。</summary>
    public string SourceText => Source;

    /// <summary>更新时间显示文本（本地时间）。</summary>
    public string UpdatedAtText => UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd");

    /// <summary>下载量显示文本。</summary>
    public string DownloadsText => Downloads >= 1_000_000
        ? $"{Downloads / 1_000_000.0:0.#}M"
        : Downloads >= 1_000
            ? $"{Downloads / 1_000.0:0.#}K"
            : Downloads.ToString();
}

/// <summary>
/// 一次资源包搜索（热门 / 普通）的分页结果。
/// </summary>
/// <param name="Items">当前页的项目（已按资源包类型过滤）。</param>
/// <param name="TotalHits">命中的总条数（用于“加载更多”）。</param>
/// <param name="UsedGameVersionFilter">本次结果是否带了游戏版本过滤（为空时会被放宽）。</param>
/// <param name="RawCount">接口原始返回条数：过滤可能丢弃条目，offset 必须按它推进。</param>
public sealed record ResourcePackSearchPage(
    IReadOnlyList<ResourcePackProject> Items,
    int TotalHits,
    bool UsedGameVersionFilter,
    int RawCount);

/// <summary>
/// 资源包的一个可下载版本。
/// </summary>
/// <param name="ProjectId">所属项目 ID。</param>
/// <param name="VersionId">版本 ID。</param>
/// <param name="Name">版本名称。</param>
/// <param name="VersionNumber">版本号。</param>
/// <param name="GameVersions">支持的游戏版本。</param>
/// <param name="PublishedAt">发布时间（UTC）。</param>
/// <param name="DownloadUrl">下载地址（官方 CDN 或镜像）。</param>
/// <param name="FileName">文件名。</param>
/// <param name="FileSize">文件大小（字节，0 表示未知）。</param>
public sealed record ResourcePackVersion(
    string ProjectId,
    string VersionId,
    string Name,
    string VersionNumber,
    IReadOnlyList<string> GameVersions,
    DateTimeOffset PublishedAt,
    string? DownloadUrl,
    string FileName,
    long FileSize)
{
    /// <summary>版本显示文本。</summary>
    public string DisplayText => string.IsNullOrWhiteSpace(VersionNumber)
        ? Name
        : $"{VersionNumber} · {PublishedAt.ToLocalTime():yyyy-MM-dd}";

    /// <summary>文件大小显示文本。</summary>
    public string FileSizeText => FileSize > 0
        ? FileSize >= 1024 * 1024
            ? $"{FileSize / 1024.0 / 1024.0:0.0} MB"
            : $"{FileSize / 1024.0:0.0} KB"
        : string.Empty;
}

/// <summary>
/// 资源包下载进度。
/// </summary>
/// <param name="Stage">阶段说明。</param>
/// <param name="DownloadedBytes">已下载字节。</param>
/// <param name="TotalBytes">总字节（0 表示未知）。</param>
public sealed record ResourcePackProgress(string Stage, long DownloadedBytes, long TotalBytes)
{
    /// <summary>百分比（0~100）。</summary>
    public double Percent => TotalBytes > 0
        ? Math.Round(Math.Clamp((double)DownloadedBytes / TotalBytes, 0d, 1d) * 100d, 1)
        : 0d;
}

/// <summary>
/// 资源包安装结果。
/// </summary>
/// <param name="SavedFiles">保存的文件完整路径。</param>
/// <param name="SkippedFiles">因为已存在而跳过的文件。</param>
/// <param name="TotalBytes">下载总字节。</param>
public sealed record ResourcePackInstallReport(
    IReadOnlyList<string> SavedFiles,
    IReadOnlyList<string> SkippedFiles,
    long TotalBytes);
