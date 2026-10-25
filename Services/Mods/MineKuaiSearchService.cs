using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DPL.Models.Mods;
using DPL.Services.Mods.Dtos;

namespace DPL.Services.Mods;

/// <summary>
/// 麦块开放 API 的模组搜索客户端（只负责“搜索列表”，
/// 版本列表 / 下载文件仍然走 Modrinth / CurseForge 的原有逻辑）。
/// 无需 API Key；限流 600 次/分钟，命中 429 时进入限流窗口并在窗口内不再发请求。
/// </summary>
public sealed class MineKuaiSearchService : IDisposable
{
    /// <summary>默认 Base URL。</summary>
    public const string DefaultBaseUrl = "https://api.minekuai.cn";

    /// <summary>搜索端点。</summary>
    private const string SearchPath = "/panel/public/mc-mods";

    /// <summary>默认单次请求超时。</summary>
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly string _baseUrl;
    private readonly TimeSpan _requestTimeout;

    /// <summary>热门列表的内存缓存时长。</summary>
    private static readonly TimeSpan PopularCacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>热门列表缓存（key = 加载器 + 游戏版本）。</summary>
    private readonly Dictionary<string, PopularCacheEntry> _popularCache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _popularCacheLock = new();

    /// <summary>日志回调。</summary>
    public event Action<string>? LogWritten;

    /// <summary>
    /// 当前限流窗口的结束时间；在此时间内不会发请求。
    /// </summary>
    public DateTimeOffset? RateLimitedUntil { get; private set; }

    /// <summary>
    /// 创建麦块搜索客户端。
    /// </summary>
    /// <param name="client">可注入的 HttpClient（单元测试用）；为 null 时内部创建。</param>
    /// <param name="baseUrl">Base URL；默认 <see cref="DefaultBaseUrl"/>。</param>
    /// <param name="requestTimeout">单次请求超时；默认 30 秒。</param>
    public MineKuaiSearchService(HttpClient? client = null, string? baseUrl = null, TimeSpan? requestTimeout = null)
    {
        _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;

        if (client is not null)
        {
            _client = client;
            _ownsClient = false;
            return;
        }

        _client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        })
        {
            Timeout = _requestTimeout,
        };

        _client.DefaultRequestHeaders.UserAgent.ParseAdd("DPL/1.0");
        _ownsClient = true;
    }

    /// <summary>
    /// 构造热门列表的缓存 key：加载器 + 游戏版本（不区分大小写）。
    /// </summary>
    /// <param name="loader">加载器；为空时用 <c>-</c> 占位。</param>
    /// <param name="gameVersion">游戏版本；为空时用 <c>-</c> 占位。</param>
    public static string BuildPopularCacheKey(string? loader, string gameVersion)
        => "popular|" +
           (string.IsNullOrWhiteSpace(loader) ? "-" : loader.Trim().ToLowerInvariant()) + "|" +
           (string.IsNullOrWhiteSpace(gameVersion) ? "-" : gameVersion.Trim());

    /// <summary>
    /// 获取“热门 / 常用模组”列表（浏览模式：不带 q，按下载量排序）。
    /// 同一「加载器 + 游戏版本」的结果会缓存 10 分钟。
    /// </summary>
    /// <param name="gameVersion">当前实例的游戏版本。</param>
    /// <param name="loader">当前实例的加载器。</param>
    /// <param name="limit">每页条数（默认 30）。</param>
    /// <param name="offset">分页偏移（“加载更多”时使用）。</param>
    /// <param name="includeGameVersion">是否带 game_version 过滤（首页可能因过滤过严而为空）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<PopularModsResult> GetPopularModsAsync(
        string gameVersion,
        string? loader,
        int limit = 30,
        int offset = 0,
        bool includeGameVersion = true,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = BuildPopularCacheKey(loader, gameVersion);

        if (offset == 0)
        {
            lock (_popularCacheLock)
            {
                if (_popularCache.TryGetValue(cacheKey, out var cached) &&
                    DateTimeOffset.UtcNow - cached.FetchedAt < PopularCacheTtl)
                {
                    Log($"热门列表命中缓存（{cacheKey}，{cached.Items.Count} 条）");
                    return new PopularModsResult(
                        cached.Items, cached.Total, cached.UsedGameVersionFilter, FromCache: true);
                }
            }
        }

        var usedGameVersionFilter = includeGameVersion && !string.IsNullOrWhiteSpace(gameVersion);
        var page = await RequestAsync(
                BuildPopularUrl(loader, gameVersion, limit, offset, usedGameVersionFilter), cancellationToken)
            .ConfigureAwait(false);

        // 边界情况：带 game_version 过滤后为空（例如很新的版本）→ 去掉 game_version 再试一次。
        if (offset == 0 && page.Items.Count == 0 && usedGameVersionFilter)
        {
            Log("带 game_version 的热门列表为空，去掉 game_version 再试一次");
            usedGameVersionFilter = false;

            page = await RequestAsync(
                    BuildPopularUrl(loader, gameVersion, limit, offset, includeGameVersion: false),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (offset == 0)
        {
            lock (_popularCacheLock)
            {
                _popularCache[cacheKey] = new PopularCacheEntry(
                    page.Items, page.Total, usedGameVersionFilter, DateTimeOffset.UtcNow);
            }
        }

        return new PopularModsResult(page.Items, page.Total, usedGameVersionFilter, FromCache: false);
    }

    /// <summary>
    /// 构造热门列表请求 URL：不带 q、不带 status，按下载量排序。
    /// </summary>
    private string BuildPopularUrl(
        string? loader,
        string gameVersion,
        int limit,
        int offset,
        bool includeGameVersion)
    {
        var parts = new List<string>
        {
            "order=downloads",
            "limit=" + Math.Clamp(limit, 1, 1000),
        };

        if (offset > 0)
            parts.Add("offset=" + offset);

        if (!string.IsNullOrWhiteSpace(loader))
            parts.Add("loader[]=" + Uri.EscapeDataString(loader));

        if (includeGameVersion && !string.IsNullOrWhiteSpace(gameVersion))
            parts.Add("game_version[]=" + Uri.EscapeDataString(gameVersion));

        return $"{_baseUrl}{SearchPath}?" + string.Join("&", parts);
    }

    /// <summary>
    /// 搜索模组（支持中文关键词）。
    /// </summary>
    /// <param name="query">关键词，可以直接是中文，例如“机械动力”。</param>
    /// <param name="gameVersion">游戏版本过滤（可选）。</param>
    /// <param name="loader">加载器过滤（可选，例如 fabric / forge / neoforge）。</param>
    /// <param name="limit">返回条数，默认 20，最大 1000。</param>
    /// <param name="offset">分页偏移。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyList<ModSearchItem>> SearchAsync(
        string query,
        string gameVersion,
        string? loader,
        int limit = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        // 429 之后处于限流窗口：直接抛出，不再发请求（避免疯狂重试）。
        EnsureNotRateLimited();

        // 第一遍：只搜“已翻译”条目（status=translated）。
        var translated = await RequestAsync(
                BuildUrl(query, gameVersion, loader, limit, offset, translatedOnly: true), cancellationToken)
            .ConfigureAwait(false);

        if (translated.Items.Count > 0)
            return translated.Items;

        // 兜底：去掉 status 参数再搜一次（其它参数完全不变）。
        // 麦块的翻译库可能还没收录某些模组（例如 AppleSkin 搜“苹果皮”），
        // 这时不能直接判为“没有结果”。
        Log("已翻译条目为空，去掉 status 参数进行兜底搜索");

        var fallback = await RequestAsync(
                BuildUrl(query, gameVersion, loader, limit, offset, translatedOnly: false), cancellationToken)
            .ConfigureAwait(false);

        if (fallback.Items.Count == 0)
            Log("兜底搜索仍没有结果");

        return fallback.Items;
    }

    /// <summary>
    /// 发起一次搜索请求并解析结果（含 429 处理）。
    /// </summary>
    /// <param name="url">完整请求地址。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<MineKuaiPage> RequestAsync(string url, CancellationToken cancellationToken)
    {
        Log($"麦块搜索：{url}");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_requestTimeout);

        using var response = await _client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = ParseRetryAfter(response) ?? TimeSpan.FromSeconds(60);
            RateLimitedUntil = DateTimeOffset.UtcNow + retryAfter;

            var message = $"麦块 API 触发限流（429），请 {Math.Ceiling(retryAfter.TotalSeconds):0} 秒后再试";
            Log(message);
            throw new MineKuaiRateLimitException(retryAfter, message);
        }

        if (!response.IsSuccessStatusCode)
            throw new ModSourceException($"麦块搜索失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

        var payload = await response.Content
            .ReadFromJsonAsync<MineKuaiSearchResponse>(SerializerOptions, cts.Token)
            .ConfigureAwait(false);

        if (payload?.Items is null || payload.Items.Count == 0)
            return new MineKuaiPage(Array.Empty<ModSearchItem>(), payload?.Total ?? 0);

        var items = new List<ModSearchItem>(payload.Items.Count);
        foreach (var item in payload.Items)
        {
            if (string.IsNullOrWhiteSpace(item.ProjectId))
                continue;

            items.Add(MapItem(item));
        }

        Log($"麦块搜索返回 {items.Count} 条（total={payload.Total}）");
        return new MineKuaiPage(items, payload.Total);
    }

    /// <summary>热门列表缓存条目。</summary>
    /// <param name="Items">结果。</param>
    /// <param name="Total">总数。</param>
    /// <param name="UsedGameVersionFilter">是否带 game_version 过滤。</param>
    /// <param name="FetchedAt">写入时间。</param>
    private sealed record PopularCacheEntry(
        IReadOnlyList<ModSearchItem> Items,
        int Total,
        bool UsedGameVersionFilter,
        DateTimeOffset FetchedAt);

    /// <summary>一次搜索请求的结果。</summary>
    /// <param name="Items">映射后的结果。</param>
    /// <param name="Total">接口返回的总数。</param>
    private sealed record MineKuaiPage(IReadOnlyList<ModSearchItem> Items, int Total);

    /// <summary>
    /// 把麦块结果映射成统一的搜索结果模型：项目 ID 直接使用其 Modrinth 项目 ID，
    /// 因此后续「版本列表 / 下载」仍然走原有 Modrinth 逻辑。
    /// </summary>
    private static ModSearchItem MapItem(MineKuaiSearchItem item)
    {
        var name = !string.IsNullOrWhiteSpace(item.TitleZh) ? item.TitleZh!
            : item.Title ?? item.Slug ?? item.ProjectId!;

        var description = !string.IsNullOrWhiteSpace(item.DescriptionZh) ? item.DescriptionZh!
            : item.DescriptionEn ?? string.Empty;

        var project = new ModProjectSummary(
            ModSourceKind.Modrinth,
            item.ProjectId!,
            item.Slug ?? item.ProjectId!,
            name,
            description,
            string.Empty,
            item.TranslationAt ?? DateTimeOffset.MinValue,
            item.Downloads);

        return new ModSearchItem(
            project,
            ModSourceKind.Modrinth,
            "麦块中文搜索（版本与下载沿用 Modrinth 逻辑）",
            null);
    }

    /// <summary>
    /// 构造搜索 URL：q / status=translated / limit / offset / loader[] / game_version[]。
    /// </summary>
    private string BuildUrl(
        string query,
        string gameVersion,
        string? loader,
        int limit,
        int offset,
        bool translatedOnly)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(query))
            parts.Add("q=" + Uri.EscapeDataString(query));

        // 第一遍只要已翻译条目；兜底请求去掉该参数，避免翻译库未收录时漏结果。
        if (translatedOnly)
            parts.Add("status=translated");
        parts.Add("limit=" + Math.Clamp(limit, 1, 1000));

        if (offset > 0)
            parts.Add("offset=" + offset);

        // 多值参数必须带 []，裸重复同名参数只有最后一个生效。
        if (!string.IsNullOrWhiteSpace(loader))
            parts.Add("loader[]=" + Uri.EscapeDataString(loader));

        if (!string.IsNullOrWhiteSpace(gameVersion))
            parts.Add("game_version[]=" + Uri.EscapeDataString(gameVersion));

        // 纯浏览（无关键词）时按下载量排序；带 q 时由后端按相关度排序。
        if (string.IsNullOrWhiteSpace(query))
            parts.Add("order=downloads");

        return $"{_baseUrl}{SearchPath}?" + string.Join("&", parts);
    }

    /// <summary>
    /// 解析 429 响应里的 Retry-After（秒数或 HTTP 日期）。
    /// </summary>
    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
            return null;

        if (retryAfter.Delta is { } delta && delta > TimeSpan.Zero)
            return delta;

        if (retryAfter.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : null;
        }

        return null;
    }

    /// <summary>
    /// 如果仍在限流窗口内，直接抛出异常（不发送任何请求）。
    /// </summary>
    private void EnsureNotRateLimited()
    {
        var until = RateLimitedUntil;
        if (until is null || DateTimeOffset.UtcNow >= until)
            return;

        var wait = until.Value - DateTimeOffset.UtcNow;
        var message = $"麦块 API 仍在限流窗口内，请 {Math.Ceiling(wait.TotalSeconds):0} 秒后再试";
        Log(message);
        throw new MineKuaiRateLimitException(wait, message);
    }

    /// <summary>写日志。</summary>
    private void Log(string message)
    {
        try
        {
            LogWritten?.Invoke(message);
        }
        catch (Exception)
        {
            // 忽略订阅者异常。
        }
    }

    /// <summary>释放内部 HttpClient（注入的客户端不释放）。</summary>
    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();
    }
}

/// <summary>
/// 热门模组列表的返回结果。
/// </summary>
/// <param name="Items">热门模组。</param>
/// <param name="Total">接口返回的总数。</param>
/// <param name="UsedGameVersionFilter">本次结果是否带 game_version 过滤（为空时会自动降级）。</param>
/// <param name="FromCache">是否来自内存缓存。</param>
public sealed record PopularModsResult(
    IReadOnlyList<ModSearchItem> Items,
    int Total,
    bool UsedGameVersionFilter,
    bool FromCache);

/// <summary>
/// 麦块 API 限流（429）时抛出：携带建议等待时间，调用方应提示用户稍后再试。
/// </summary>
public sealed class MineKuaiRateLimitException : Exception
{
    /// <summary>
    /// 创建限流异常。
    /// </summary>
    /// <param name="retryAfter">建议等待时间。</param>
    /// <param name="message">给用户看的提示。</param>
    public MineKuaiRateLimitException(TimeSpan retryAfter, string message) : base(message)
        => RetryAfter = retryAfter;

    /// <summary>建议等待时间。</summary>
    public TimeSpan RetryAfter { get; }
}
