using DPL.Models.Mods;

namespace DPL.Services.Mods;

/// <summary>
/// 择优路由器：持有两个平台的实现，负责“探测可达性 → 判断存在性 → 比较更新时间 → 用户覆盖”的决策。
/// 平台实现本身不包含任何择优逻辑。
/// </summary>
public sealed class ModSourceRouter : IDisposable
{
    /// <summary>探测结果的缓存时间，避免每次搜索都重新探测。</summary>
    private static readonly TimeSpan ProbeCacheDuration = TimeSpan.FromMinutes(5);

    private readonly ModConfig _config;
    private readonly ModHttp _http;

    private readonly Dictionary<ModSourceKind, (bool Reachable, DateTime CheckedAt)> _probeCache = new();

    /// <summary>
    /// 创建路由器。
    /// </summary>
    /// <param name="config">模组下载配置。</param>
    /// <param name="http">可注入的 HTTP 封装（单元测试用）。</param>
    public ModSourceRouter(ModConfig config, ModHttp? http = null, MineKuaiSearchService? mineKuai = null)
    {
        _config = config;
        _http = http ?? new ModHttp(config);

        Modrinth = new ModrinthModSource(_http, config);
        CurseForge = new CurseForgeModSource(_http, config);

        // 搜索列表走麦块开放 API（中文关键词）；版本列表 / 下载仍走原逻辑。
        MineKuai = mineKuai ?? new MineKuaiSearchService();
        MineKuai.LogWritten += message => _http.LogMessage("麦块：" + message);
    }

    /// <summary>麦块开放 API 搜索客户端（支持中文关键词）。</summary>
    public MineKuaiSearchService MineKuai { get; }

    /// <summary>Modrinth 来源。</summary>
    public ModrinthModSource Modrinth { get; }

    /// <summary>CurseForge 来源。</summary>
    public CurseForgeModSource CurseForge { get; }

    /// <summary>
    /// 最近一次响应里 MCIM 给出的缓存同步时间（<c>sync_at</c>）；没有则为 null。
    /// </summary>
    public DateTimeOffset? LastSyncAt => _http.LastSyncAt;

    /// <summary>日志回调。</summary>
    public event Action<string>? LogWritten
    {
        add => _http.LogWritten += value;
        remove => _http.LogWritten -= value;
    }

    /// <summary>
    /// 按平台类型取实现。
    /// </summary>
    /// <param name="kind">平台类型。</param>
    public IModSource GetSource(ModSourceKind kind)
        => kind == ModSourceKind.Modrinth ? Modrinth : CurseForge;

    /// <summary>
    /// 探测两个平台是否可达（结果缓存 5 分钟）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<ModSourceStatus> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var modrinthReachable = await ProbeSourceAsync(ModSourceKind.Modrinth, cancellationToken).ConfigureAwait(false);
        var curseForgeReachable = await ProbeSourceAsync(ModSourceKind.CurseForge, cancellationToken).ConfigureAwait(false);

        return new ModSourceStatus(
            modrinthReachable,
            curseForgeReachable,
            CurseForge.IsConfigured,
            BuildStatusText(modrinthReachable, curseForgeReachable));
    }

    /// <summary>
    /// 搜索模组（自动模式下同时查询两个平台并按规则择优）。
    /// </summary>
    /// <param name="query">关键字。</param>
    /// <param name="gameVersion">游戏版本（精确补丁版本）。</param>
    /// <param name="loader">加载器。</param>
    /// <param name="preference">用户的下载源偏好。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<ModSearchOutcome> SearchAsync(
        string query,
        string gameVersion,
        string? loader,
        ModSourcePreference preference,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new ModSearchOutcome(Array.Empty<ModSearchItem>(), null);

        // 用户明确指定平台：只用那一个，不做择优。
        if (preference != ModSourcePreference.Auto)
        {
            var kind = preference == ModSourcePreference.Modrinth ? ModSourceKind.Modrinth : ModSourceKind.CurseForge;
            var source = GetSource(kind);

            if (!source.IsConfigured)
                throw new ModSourceException(source.UnavailableReason ?? $"{source.DisplayName} 不可用");

            var items = await source.SearchAsync(query, gameVersion, loader, cancellationToken).ConfigureAwait(false);
            return new ModSearchOutcome(
                items.Select(p => new ModSearchItem(p, kind, $"已按你的选择使用 {source.DisplayName}", null)).ToList(),
                null);
        }

        // 搜索列表优先走麦块开放 API（支持中文关键词）。
        // 429 限流：不重试、不降级，直接抛给界面提示“稍后再试”；
        // 其它失败（网络/接口异常）：回退到原来的 Modrinth/CurseForge 搜索。
        try
        {
            var mineKuaiItems = await MineKuai
                .SearchAsync(query, gameVersion, loader, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return new ModSearchOutcome(
                mineKuaiItems,
                mineKuaiItems.Count == 0
                    ? "麦块搜索没有匹配结果"
                    : "搜索源：麦块开放 API（status=translated，中文结果）");
        }
        catch (MineKuaiRateLimitException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _http.LogMessage($"麦块搜索不可用，回退到 Modrinth/CurseForge：{ex.Message}");
        }

        var status = await ProbeAsync(cancellationToken).ConfigureAwait(false);

        var modrinthTask = status.ModrinthReachable
            ? SafeSearchAsync(Modrinth, query, gameVersion, loader, cancellationToken)
            : Task.FromResult<SourceSearchResult>(new SourceSearchResult(ModSourceKind.Modrinth, null, "Modrinth 不可达"));

        var curseForgeTask = status.CurseForgeReachable && CurseForge.IsConfigured
            ? SafeSearchAsync(CurseForge, query, gameVersion, loader, cancellationToken)
            : Task.FromResult<SourceSearchResult>(new SourceSearchResult(
                ModSourceKind.CurseForge,
                null,
                CurseForge.IsConfigured ? "CurseForge 不可达" : CurseForge.UnavailableReason));

        var results = await Task.WhenAll(modrinthTask, curseForgeTask).ConfigureAwait(false);

        var notices = new List<string>();
        if (!status.ModrinthReachable)
            notices.Add("Modrinth 当前不可达，已自动降级");
        if (!CurseForge.IsConfigured)
            notices.Add(CurseForge.UnavailableReason ?? "CurseForge 未配置");
        else if (!status.CurseForgeReachable)
            notices.Add("CurseForge 当前不可达，已自动降级");

        var modrinthHits = results.FirstOrDefault(r => r.Kind == ModSourceKind.Modrinth)?.Items;
        var curseForgeHits = results.FirstOrDefault(r => r.Kind == ModSourceKind.CurseForge)?.Items;

        if (modrinthHits is null && curseForgeHits is null)
        {
            var reasons = results
                .Where(r => !string.IsNullOrWhiteSpace(r.Error))
                .Select(r => r.Error)
                .ToList();

            throw new ModSourceException($"两个平台都不可用：{string.Join("；", reasons)}");
        }

        var merged = Merge(modrinthHits ?? Array.Empty<ModProjectSummary>(),
            curseForgeHits ?? Array.Empty<ModProjectSummary>());

        return new ModSearchOutcome(merged, notices.Count == 0 ? null : string.Join("；", notices));
    }

    /// <summary>
    /// 获取某个模组的版本列表：自动模式下若首选平台失败，会尝试另一个平台。
    /// </summary>
    /// <param name="item">搜索结果条目。</param>
    /// <param name="gameVersion">游戏版本。</param>
    /// <param name="loader">加载器。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<ModVersionFetchResult> GetVersionsAsync(
        ModSearchItem item,
        string gameVersion,
        string? loader,
        CancellationToken cancellationToken = default)
    {
        var primary = GetSource(item.SelectedSource);

        try
        {
            var versions = await primary
                .GetVersionsAsync(item.Project.ProjectId, gameVersion, loader, cancellationToken)
                .ConfigureAwait(false);

            if (versions.Count > 0 || item.Alternative is null)
                return new ModVersionFetchResult(versions, item.SelectedSource, item.Alternative, null);

            // 首选平台没有兼容版本时，尝试另一个平台。
            var alternativeSource = GetSource(item.Alternative.Source);
            var alternativeVersions = await alternativeSource
                .GetVersionsAsync(item.Alternative.ProjectId, gameVersion, loader, cancellationToken)
                .ConfigureAwait(false);

            return alternativeVersions.Count > 0
                ? new ModVersionFetchResult(alternativeVersions, item.Alternative.Source, item.Project, null)
                : new ModVersionFetchResult(versions, item.SelectedSource, item.Alternative, null);
        }
        catch (Exception ex) when (item.Alternative is not null)
        {
            var alternativeSource = GetSource(item.Alternative.Source);
            var alternativeVersions = await alternativeSource
                .GetVersionsAsync(item.Alternative.ProjectId, gameVersion, loader, cancellationToken)
                .ConfigureAwait(false);

            return new ModVersionFetchResult(
                alternativeVersions,
                item.Alternative.Source,
                item.Project,
                $"{primary.DisplayName} 获取版本失败（{ex.Message}），已切换到 {alternativeSource.DisplayName}");
        }
    }

    /// <summary>
    /// 关闭内部 HttpClient。
    /// </summary>
    public void Dispose()
    {
        MineKuai.Dispose();
        _http.Dispose();
    }

    /// <summary>
    /// 合并两个平台的搜索结果：同名模组按更新时间择优，其余各自保留。
    /// </summary>
    private List<ModSearchItem> Merge(
        IReadOnlyList<ModProjectSummary> modrinthItems,
        IReadOnlyList<ModProjectSummary> curseForgeItems)
    {
        var result = new List<ModSearchItem>();

        var modrinthByKey = modrinthItems
            .GroupBy(p => NormalizeKey(p))
            .ToDictionary(g => g.Key, g => g.First());

        var matchedModrinthKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var curseForge in curseForgeItems)
        {
            var key = NormalizeKey(curseForge);
            if (!modrinthByKey.TryGetValue(key, out var modrinth))
            {
                result.Add(new ModSearchItem(
                    curseForge,
                    ModSourceKind.CurseForge,
                    "仅 CurseForge 有该模组",
                    null));
                continue;
            }

            matchedModrinthKeys.Add(key);
            result.Add(Choose(modrinth, curseForge));
        }

        foreach (var modrinth in modrinthItems)
        {
            if (matchedModrinthKeys.Contains(NormalizeKey(modrinth)))
                continue;

            if (result.Any(i => string.Equals(i.Project.ProjectId, modrinth.ProjectId, StringComparison.Ordinal) &&
                                i.Project.Source == ModSourceKind.Modrinth))
                continue;

            result.Add(new ModSearchItem(
                modrinth,
                ModSourceKind.Modrinth,
                "仅 Modrinth 有该模组",
                null));
        }

        return result
            .GroupBy(i => (i.Project.Source, i.Project.ProjectId))
            .Select(g => g.First())
            .OrderByDescending(i => i.Project.UpdatedAt)
            .ToList();
    }

    /// <summary>
    /// 两个平台都存在时，按更新时间择优（一周内视为同步，优先 Modrinth）。
    /// </summary>
    private ModSearchItem Choose(ModProjectSummary modrinth, ModProjectSummary curseForge)
    {
        var diff = (modrinth.UpdatedAt - curseForge.UpdatedAt).Duration();

        // 一周以内算同步更新：优先 Modrinth（API 稳定、无需 Key、国内通常更快）。
        if (diff <= _config.SyncWindow)
        {
            return new ModSearchItem(
                modrinth,
                ModSourceKind.Modrinth,
                $"两平台一周内同步更新（{modrinth.UpdatedAt:yyyy-MM-dd} / {curseForge.UpdatedAt:yyyy-MM-dd}），优先 Modrinth",
                curseForge);
        }

        return modrinth.UpdatedAt > curseForge.UpdatedAt
            ? new ModSearchItem(
                modrinth,
                ModSourceKind.Modrinth,
                $"Modrinth 更新更晚（{modrinth.UpdatedAt:yyyy-MM-dd} > {curseForge.UpdatedAt:yyyy-MM-dd}）",
                curseForge)
            : new ModSearchItem(
                curseForge,
                ModSourceKind.CurseForge,
                $"CurseForge 更新更晚（{curseForge.UpdatedAt:yyyy-MM-dd} > {modrinth.UpdatedAt:yyyy-MM-dd}）",
                modrinth);
    }

    /// <summary>
    /// 用于跨平台匹配同一模组的键：优先 slug，退化为名称。
    /// </summary>
    private static string NormalizeKey(ModProjectSummary project)
    {
        var slug = string.IsNullOrWhiteSpace(project.Slug) ? project.Name : project.Slug;
        return slug.Trim().ToLowerInvariant().Replace(' ', '-');
    }

    /// <summary>
    /// 探测单个平台。
    /// </summary>
    private async Task<bool> ProbeSourceAsync(ModSourceKind kind, CancellationToken cancellationToken)
    {
        if (kind == ModSourceKind.CurseForge && !CurseForge.IsConfigured)
            return false;

        if (_probeCache.TryGetValue(kind, out var cached) &&
            DateTime.UtcNow - cached.CheckedAt < ProbeCacheDuration)
        {
            return cached.Reachable;
        }

        var apiKey = kind == ModSourceKind.CurseForge ? _config.CurseForgeApiKey : null;

        var primaryUrl = kind == ModSourceKind.Modrinth
            ? $"{_config.ModrinthApiBase}/search?query=a&limit=1"
            : $"{_config.CurseForgeApiBase}/mods/search?gameId={_config.CurseForgeMinecraftGameId}&searchFilter=a&pageSize=1";

        var fallbackUrl = kind == ModSourceKind.Modrinth
            ? $"{_config.ModrinthApiFallbackBase}/search?query=a&limit=1"
            : $"{_config.CurseForgeApiFallbackBase}/mods/search?gameId={_config.CurseForgeMinecraftGameId}&searchFilter=a&pageSize=1";

        // 主源是 MCIM 镜像；镜像探测失败时要再看官方源，避免把“镜像挂了但官方可用”误判为平台不可用。
        var reachable = await _http.ProbeAsync(primaryUrl, apiKey, cancellationToken).ConfigureAwait(false);

        if (!reachable)
        {
            reachable = await _http.ProbeAsync(fallbackUrl, apiKey, cancellationToken).ConfigureAwait(false);

            if (reachable)
                _http.LogMessage("主源（MCIM 镜像）不可达，已改用官方源");
        }

        _probeCache[kind] = (reachable, DateTime.UtcNow);
        return reachable;
    }

    /// <summary>
    /// 状态文案（显示在界面顶部）。
    /// </summary>
    private string BuildStatusText(bool modrinthReachable, bool curseForgeReachable)
    {
        var modrinth = modrinthReachable ? "Modrinth 可用" : "Modrinth 不可达";
        var curseForge = !CurseForge.IsConfigured
            ? "CurseForge 未配置 Key"
            : curseForgeReachable ? "CurseForge 可用" : "CurseForge 不可达";

        return $"{modrinth} · {curseForge}";
    }

    /// <summary>
    /// 包一层搜索：失败返回错误而不是抛出，便于两个平台并行查询。
    /// </summary>
    private static async Task<SourceSearchResult> SafeSearchAsync(
        IModSource source,
        string query,
        string gameVersion,
        string? loader,
        CancellationToken cancellationToken)
    {
        try
        {
            var items = await source.SearchAsync(query, gameVersion, loader, cancellationToken).ConfigureAwait(false);
            return new SourceSearchResult(source.Kind, items, null);
        }
        catch (Exception ex)
        {
            return new SourceSearchResult(source.Kind, null, $"{source.DisplayName} 查询失败：{ex.Message}");
        }
    }

    /// <summary>单个平台的搜索结果（或错误）。</summary>
    private sealed record SourceSearchResult(
        ModSourceKind Kind,
        IReadOnlyList<ModProjectSummary>? Items,
        string? Error);
}

/// <summary>
/// 平台可用性状态。
/// </summary>
/// <param name="ModrinthReachable">Modrinth 是否可达。</param>
/// <param name="CurseForgeReachable">CurseForge 是否可达。</param>
/// <param name="CurseForgeConfigured">CurseForge 是否配置了 API Key。</param>
/// <param name="Text">展示用文案。</param>
public sealed record ModSourceStatus(
    bool ModrinthReachable,
    bool CurseForgeReachable,
    bool CurseForgeConfigured,
    string Text);

/// <summary>
/// 版本列表获取结果（可能发生了平台切换）。
/// </summary>
/// <param name="Versions">版本列表。</param>
/// <param name="Source">实际使用的平台。</param>
/// <param name="Alternative">另一个平台上的同名项目。</param>
/// <param name="Notice">给用户的提示（例如已切换平台）。</param>
public sealed record ModVersionFetchResult(
    IReadOnlyList<ModVersionInfo> Versions,
    ModSourceKind Source,
    ModProjectSummary? Alternative,
    string? Notice);
