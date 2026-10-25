namespace DPL.Models.Mods;

/// <summary>
/// 模组来源平台。
/// </summary>
public enum ModSourceKind
{
    /// <summary>Modrinth。</summary>
    Modrinth,

    /// <summary>CurseForge。</summary>
    CurseForge,
}

/// <summary>
/// 模组下载源偏好（界面上可切换）。
/// </summary>
public enum ModSourcePreference
{
    /// <summary>自动择优（默认）。</summary>
    Auto,

    /// <summary>只使用 Modrinth。</summary>
    Modrinth,

    /// <summary>只使用 CurseForge。</summary>
    CurseForge,
}

/// <summary>
/// 模组搜索 / 项目信息（两个平台的统一视图）。
/// </summary>
/// <param name="Source">来源平台。</param>
/// <param name="ProjectId">平台内的项目 ID。</param>
/// <param name="Slug">平台内的短名称（用于跨平台匹配同名模组）。</param>
/// <param name="Name">模组名称。</param>
/// <param name="Description">简短描述。</param>
/// <param name="Author">作者。</param>
/// <param name="UpdatedAt">最近更新时间（UTC）。</param>
/// <param name="Downloads">总下载量。</param>
public sealed record ModProjectSummary(
    ModSourceKind Source,
    string ProjectId,
    string Slug,
    string Name,
    string Description,
    string Author,
    DateTimeOffset UpdatedAt,
    long Downloads)
{
    /// <summary>更新时间显示文本（本地时间）。</summary>
    public string UpdatedAtText => UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd");

    /// <summary>来源显示名称。</summary>
    public string SourceText => Source == ModSourceKind.Modrinth ? "Modrinth" : "CurseForge";

    /// <summary>下载量显示文本。</summary>
    public string DownloadsText => Downloads >= 1_000_000
        ? $"{Downloads / 1_000_000.0:0.#}M"
        : Downloads >= 1_000
            ? $"{Downloads / 1_000.0:0.#}K"
            : Downloads.ToString();
}

/// <summary>
/// 一个可下载的模组版本。
/// </summary>
/// <param name="Source">来源平台。</param>
/// <param name="ProjectId">所属项目 ID。</param>
/// <param name="VersionId">版本 ID（CurseForge 内部使用 <c>projectId:fileId</c> 形式）。</param>
/// <param name="Name">版本名称（展示用）。</param>
/// <param name="VersionNumber">版本号。</param>
/// <param name="GameVersions">支持的游戏版本列表。</param>
/// <param name="Loaders">支持的模组加载器列表（小写，例如 fabric / forge / neoforge / quilt）。</param>
/// <param name="PublishedAt">发布时间（UTC）。</param>
/// <param name="DownloadUrl">下载地址；CurseForge 可能为空，需要调用解析接口。</param>
/// <param name="FileName">文件名。</param>
/// <param name="FileSize">文件大小（字节，0 表示未知）。</param>
/// <param name="Dependencies">依赖列表。</param>
public sealed record ModVersionInfo(
    ModSourceKind Source,
    string ProjectId,
    string VersionId,
    string Name,
    string VersionNumber,
    IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Loaders,
    DateTimeOffset PublishedAt,
    string? DownloadUrl,
    string FileName,
    long FileSize,
    IReadOnlyList<ModDependency> Dependencies)
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
/// 模组依赖（前置等）。
/// </summary>
/// <param name="Source">依赖所在的平台。</param>
/// <param name="ProjectId">依赖的项目 ID。</param>
/// <param name="VersionId">指定的版本 ID；为空表示“取最新兼容版本”。</param>
/// <param name="Required">是否是必须依赖。</param>
/// <param name="Name">依赖的显示名称（可能为空）。</param>
public sealed record ModDependency(
    ModSourceKind Source,
    string ProjectId,
    string? VersionId,
    bool Required,
    string? Name);

/// <summary>
/// 一次搜索的结果（含两个平台的探测信息）。
/// </summary>
/// <param name="Items">搜索结果（已按择优规则合并）。</param>
/// <param name="Notice">给用户看的提示（例如某个平台不可用）。</param>
public sealed record ModSearchOutcome(
    IReadOnlyList<ModSearchItem> Items,
    string? Notice);

/// <summary>
/// 搜索结果中的一条，携带“自动择优”的结论。
/// </summary>
/// <param name="Project">选中的项目信息。</param>
/// <param name="SelectedSource">择优后选中的平台。</param>
/// <param name="ChosenBecause">选择原因（展示在 UI 上，方便判断）。</param>
/// <param name="Alternative">另一个平台上同名项目（可能为空）。</param>
public sealed record ModSearchItem(
    ModProjectSummary Project,
    ModSourceKind SelectedSource,
    string ChosenBecause,
    ModProjectSummary? Alternative);

/// <summary>
/// 一次依赖解析的结果：需要下载的文件队列。
/// </summary>
/// <param name="Files">按下载顺序排列的文件。</param>
/// <param name="ResolvedProjects">解析到的所有项目（用于日志/界面展示）。</param>
public sealed record ModResolutionResult(
    IReadOnlyList<ModResolvedFile> Files,
    IReadOnlyList<string> ResolvedProjects);

/// <summary>
/// 依赖解析后的一个待下载文件。
/// </summary>
/// <param name="Source">来源平台。</param>
/// <param name="ProjectName">模组名。</param>
/// <param name="Version">版本信息。</param>
/// <param name="DownloadUrl">已知的下载地址（可能为空，需要再解析）。</param>
/// <param name="IsDependency">是否是依赖（非用户直接选择的模组）。</param>
public sealed record ModResolvedFile(
    ModSourceKind Source,
    string ProjectName,
    ModVersionInfo Version,
    string? DownloadUrl,
    bool IsDependency);

/// <summary>
/// 下载进度（多文件时按总字节数计算整体进度）。
/// </summary>
/// <param name="CurrentFile">当前文件名。</param>
/// <param name="FileIndex">当前第几个文件（从 1 开始）。</param>
/// <param name="FileCount">总文件数。</param>
/// <param name="DownloadedBytes">已下载字节。</param>
/// <param name="TotalBytes">总字节（0 表示未知）。</param>
/// <param name="Stage">当前阶段说明。</param>
public sealed record ModDownloadProgress(
    string CurrentFile,
    int FileIndex,
    int FileCount,
    long DownloadedBytes,
    long TotalBytes,
    string Stage)
{
    /// <summary>整体进度（0~1）。</summary>
    public double Ratio => TotalBytes > 0
        ? Math.Clamp((double)DownloadedBytes / TotalBytes, 0d, 1d)
        : 0d;

    /// <summary>百分比（0~100）。</summary>
    public double Percent => Math.Round(Ratio * 100d, 1);
}

/// <summary>
/// 下载完成报告。
/// </summary>
/// <param name="SavedFiles">保存下来的文件完整路径。</param>
/// <param name="SkippedFiles">因为已存在而跳过的文件。</param>
/// <param name="TotalBytes">下载总字节。</param>
public sealed record ModInstallReport(
    IReadOnlyList<string> SavedFiles,
    IReadOnlyList<string> SkippedFiles,
    long TotalBytes);
