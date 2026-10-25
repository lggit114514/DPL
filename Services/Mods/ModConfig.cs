using DPL.Models;

namespace DPL.Services.Mods;

/// <summary>
/// 模组下载相关的全部外部地址与策略参数。
/// 默认**主源为 MCIM 镜像**（国内可达），官方地址作为备源（镜像失败时回退）。
/// 所有 URL、镜像地址、超时、重试次数都集中在这里，避免散落在业务代码中。
/// </summary>
public sealed class ModConfig
{
    /// <summary>Modrinth API 主源（MCIM 镜像，国内直连更快）。</summary>
    public string ModrinthApiBase { get; init; } = "https://mod.mcimirror.top/modrinth/v2";

    /// <summary>Modrinth API 备源（官方，镜像不可用时回退）。</summary>
    public string ModrinthApiFallbackBase { get; init; } = "https://api.modrinth.com/v2";

    /// <summary>Modrinth 文件下载主源（MCIM 镜像，保持官方路径结构）。</summary>
    public string ModrinthCdnBase { get; init; } = "https://mod.mcimirror.top";

    /// <summary>Modrinth 文件下载备源（官方 CDN）。</summary>
    public string ModrinthCdnFallbackBase { get; init; } = "https://cdn.modrinth.com";

    /// <summary>CurseForge API 主源（MCIM 镜像）。</summary>
    public string CurseForgeApiBase { get; init; } = "https://mod.mcimirror.top/curseforge/v1";

    /// <summary>CurseForge API 备源（官方）。</summary>
    public string CurseForgeApiFallbackBase { get; init; } = "https://api.curseforge.com/v1";

    /// <summary>CurseForge 文件下载主源（MCIM 镜像）。</summary>
    public string CurseForgeCdnBase { get; init; } = "https://mod.mcimirror.top";

    /// <summary>CurseForge 文件下载备源（官方 edge 节点）。</summary>
    public string CurseForgeCdnFallbackBase { get; init; } = "https://edge.forgecdn.net";

    /// <summary>CurseForge 文件下载备源的备用节点（mediafilez）。</summary>
    public string CurseForgeCdnFallbackAltBase { get; init; } = "https://mediafilez.forgecdn.net";

    /// <summary>CurseForge 中“Minecraft”这个游戏的 gameId。</summary>
    public int CurseForgeMinecraftGameId { get; init; } = 432;


    /// <summary>CurseForge API Key（来自设置或环境变量，不硬编码）。</summary>
    public string? CurseForgeApiKey { get; init; }

    /// <summary>API 请求的单次超时。</summary>
    public TimeSpan ApiTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>探测平台可达性的超时（要短，避免拖慢搜索）。</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>文件下载的单次超时。</summary>
    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>单个源的最大尝试次数（含第一次）。</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>指数退避的基准延迟。</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// 模组文件的并行下载数（文件级并行）。大文件内部还会再分片并行，见 <see cref="ChunkParallelism"/>。
    /// </summary>
    public int FileParallelism { get; init; } = 32;

    /// <summary>单个大文件分片下载时，同时下载的分片数（默认 6）。</summary>
    public int ChunkParallelism { get; init; } = 6;

    /// <summary>超过该大小的文件才启用分片下载（默认 10 MB）。</summary>
    public long ChunkThresholdBytes { get; init; } = ChunkedFileDownloader.DefaultThresholdBytes;

    /// <summary>每个分片的目标大小（默认 4 MB）。</summary>
    public long ChunkSizeBytes { get; init; } = 4L * 1024 * 1024;

    /// <summary>依赖解析的最大深度。</summary>
    public int MaxDependencyDepth { get; init; } = 5;

    /// <summary>“同步更新”的判定窗口：两个平台更新时间差在这个范围内时优先 Modrinth。</summary>
    public TimeSpan SyncWindow { get; init; } = TimeSpan.FromDays(7);

    /// <summary>
    /// 从启动器设置构造配置，并支持用环境变量临时覆盖 CurseForge Key。
    /// </summary>
    /// <param name="settings">启动器设置。</param>
    public static ModConfig FromSettings(LauncherSettings settings)
    {
        var key = Environment.GetEnvironmentVariable("NOVALAUNCH_CURSEFORGE_KEY");
        if (string.IsNullOrWhiteSpace(key))
            key = settings.CurseForgeApiKey;

        return new ModConfig
        {
            CurseForgeApiKey = string.IsNullOrWhiteSpace(key) ? null : key.Trim(),
        };
    }

    /// <summary>
    /// 分片下载时可切换的 Base URL 列表（顺序：官方源 → BMCLAPI → MCIM）。
    /// CurseForge 文件没有 BMCLAPI 镜像，顺序为官方 edge → mediafilez → MCIM。
    /// </summary>
    /// <param name="url">原始下载地址（用于判断平台）。</param>
    public IReadOnlyList<string> GetChunkedBaseUrls(string url)
    {
        // CurseForge：官方 edge → mediafilez → MCIM（BMCLAPI 不镜像 CurseForge，不纳入）。
        if (IsCurseForgeUrl(url))
            return new[] { CurseForgeCdnFallbackBase, CurseForgeCdnFallbackAltBase, CurseForgeCdnBase };

        // Modrinth：官方 CDN → BMCLAPI → MCIM。
        return new[]
        {
            ModrinthCdnFallbackBase,
            "https://bmclapi2.bangbang93.com",
            ModrinthCdnBase,
        };
    }

    /// <summary>
    /// 单线程（小文件 / 分片不可用）下载时的候选地址：官方 CDN → BMCLAPI → MCIM
    /// （CurseForge 为 官方 edge → mediafilez → MCIM）。
    /// </summary>
    /// <param name="url">原始下载地址（官方或镜像都可以）。</param>
    public IReadOnlyList<string> GetDownloadCandidates(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return Array.Empty<string>();

        var relative = GetCdnRelativePath(url).TrimStart('/');
        if (relative.Length == 0)
            return new[] { url };

        if (IsCurseForgeUrl(url))
        {
            return new[]
            {
                $"{CurseForgeCdnFallbackBase}/{relative}",
                $"{CurseForgeCdnFallbackAltBase}/{relative}",
                $"{CurseForgeCdnBase}/{relative}",
            };
        }

        return new[]
        {
            $"{ModrinthCdnFallbackBase}/{relative}",
            $"https://bmclapi2.bangbang93.com/{relative}",
            $"{ModrinthCdnBase}/{relative}",
        };
    }

    /// <summary>
    /// 取下载地址中相对 CDN 根的部分（例如 <c>/data/xxx/versions/1/mod.jar</c>）。
    /// </summary>
    /// <param name="url">原始下载地址。</param>
    public string GetCdnRelativePath(string url)
    {
        foreach (var basePart in new[]
                 {
                     ModrinthCdnFallbackBase,
                     ModrinthCdnBase,
                     CurseForgeCdnFallbackBase,
                     CurseForgeCdnFallbackAltBase,
                     CurseForgeCdnBase,
                 })
        {
            if (url.StartsWith(basePart, StringComparison.OrdinalIgnoreCase))
                return url[basePart.Length..];
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.PathAndQuery : url;
    }

    /// <summary>
    /// 根据下载地址得到“主源候选地址”：镜像地址优先，保持路径不变。
    /// </summary>
    /// <param name="url">API 返回的下载地址（可能是官方，也可能是镜像）。</param>
    public IReadOnlyList<string> GetPrimaryDownloadUrls(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return Array.Empty<string>();

        // 已经是镜像地址（Modrinth 走 /data/，CurseForge 走 /files/）：直接用。
        if (IsMirrorUrl(url))
            return new[] { url };

        // 官方地址：换成对应的镜像地址作为主源。
        var swapped = ToMirrorUrl(url);
        return swapped is null ? new[] { url } : new[] { swapped };
    }

    /// <summary>
    /// 根据下载地址得到“备源候选地址”：镜像失败后回退官方 CDN（CurseForge 再补一个备用节点）。
    /// </summary>
    /// <param name="url">API 返回的下载地址（可能是官方，也可能是镜像）。</param>
    public IReadOnlyList<string> GetFallbackDownloadUrls(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return Array.Empty<string>();

        // 注意：MCIM 的 Modrinth 与 CurseForge 共用同一个域名，靠路径区分：
        // Modrinth: https://mod.mcimirror.top/data/...   CurseForge: https://mod.mcimirror.top/files/...
        if (StartsWith(url, ModrinthCdnBase) && url.Contains("/data/", StringComparison.OrdinalIgnoreCase))
            return new[] { ModrinthCdnFallbackBase + url[ModrinthCdnBase.Length..] };

        if (StartsWith(url, CurseForgeCdnBase) && url.Contains("/files/", StringComparison.OrdinalIgnoreCase))
        {
            // MCIM 的 CurseForge 文件路径与官方一致：/files/<a>/<b>/<name>
            var tail = url[CurseForgeCdnBase.Length..];
            return new[] { CurseForgeCdnFallbackBase + tail, CurseForgeCdnFallbackAltBase + tail };
        }

        // 已经是官方地址：把它作为备源（CurseForge 再补一个备用节点）。
        if (StartsWith(url, CurseForgeCdnFallbackBase))
            return new[] { url, CurseForgeCdnFallbackAltBase + url[CurseForgeCdnFallbackBase.Length..] };

        if (StartsWith(url, CurseForgeCdnFallbackAltBase))
            return new[] { url, CurseForgeCdnFallbackBase + url[CurseForgeCdnFallbackAltBase.Length..] };

        return new[] { url };
    }

    /// <summary>
    /// 判断地址是否属于 CurseForge 的 CDN（路径 /files/ 或 forgecdn 域名）。
    /// </summary>
    private static bool IsCurseForgeUrl(string url)
        => url.Contains("/files/", StringComparison.OrdinalIgnoreCase) ||
           url.Contains("forgecdn", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 判断地址是否已经是 MCIM 镜像地址（两个平台共用域名，用路径前缀区分）。
    /// </summary>
    private static bool IsMirrorUrl(string url)
        => url.StartsWith("https://mod.mcimirror.top/", StringComparison.OrdinalIgnoreCase) &&
           (url.Contains("/data/", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("/files/", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 官方地址 → 镜像地址（无法识别时返回 null）。
    /// </summary>
    private string? ToMirrorUrl(string url)
    {
        if (StartsWith(url, ModrinthCdnFallbackBase))
            return ModrinthCdnBase + url[ModrinthCdnFallbackBase.Length..];

        if (StartsWith(url, CurseForgeCdnFallbackBase))
            return CurseForgeCdnBase + url[CurseForgeCdnFallbackBase.Length..];

        if (StartsWith(url, CurseForgeCdnFallbackAltBase))
            return CurseForgeCdnBase + url[CurseForgeCdnFallbackAltBase.Length..];

        return null;
    }

    /// <summary>
    /// 前缀比较（忽略大小写）。
    /// </summary>
    private static bool StartsWith(string url, string prefix)
        => url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}
