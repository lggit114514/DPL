using System.Text.Json;
using System.Text.Json.Serialization;
using DPL.Models.ResourcePacks;

namespace DPL.Services.ResourcePacks;

/// <summary>
/// 资源包搜索源（当前只实现 Modrinth；CurseForge 用 classId=12 的源码尚未接入，见 TODO）。
///
/// 与模组链路完全独立：不复用 IModSource / ModSourceRouter，只依赖自己的 HttpClient。
/// API 可以走 MCIM 镜像（100% 兼容官方接口），文件下载走 <see cref="ResourcePackInstallService"/>。
/// </summary>
public sealed class ResourcePackSource : IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly ResourcePackConfig _config;

    /// <summary>
    /// 创建资源包来源。
    /// </summary>
    /// <param name="config">资源包配置。</param>
    /// <param name="client">可注入的 HttpClient（单元测试用）。</param>
    public ResourcePackSource(ResourcePackConfig? config = null, HttpClient? client = null)
    {
        _config = config ?? new ResourcePackConfig();

        if (client is not null)
        {
            _client = client;
            _ownsClient = false;
        }
        else
        {
            _client = new HttpClient { Timeout = _config.ApiTimeout };
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("DPL/1.0 (resourcepack)");
            _ownsClient = true;
        }
    }

    /// <summary>日志回调。</summary>
    public event Action<string>? LogWritten;

    /// <summary>平台显示名称。</summary>
    public string DisplayName => "Modrinth";

    /// <summary>
    /// 搜索资源包：只按游戏版本过滤，不传 loaders。
    /// </summary>
    /// <param name="query">关键字。</param>
    /// <param name="gameVersion">游戏版本（精确补丁版本）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyList<ResourcePackProject>> SearchAsync(
        string query,
        string gameVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<ResourcePackProject>();

        var page = await SearchCoreAsync(
                query.Trim(), gameVersion, "relevance", _config.PageSize, 0, cancellationToken)
            .ConfigureAwait(false);

        return page.Items;
    }

    /// <summary>
    /// 热门资源包：query 为空，按下载量降序（index=downloads），可带游戏版本过滤。
    /// 这是资源包链路独立实现的热门查询，不复用模组页的热门接口与缓存。
    /// </summary>
    /// <param name="gameVersion">当前实例的游戏版本。</param>
    /// <param name="limit">每页数量。</param>
    /// <param name="offset">分页偏移。</param>
    /// <param name="includeGameVersion">是否带 versions facet（为空时可去掉后重试）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task<ResourcePackSearchPage> SearchPopularAsync(
        string? gameVersion,
        int limit,
        int offset,
        bool includeGameVersion = true,
        CancellationToken cancellationToken = default)
        => SearchCoreAsync(
            query: null,
            gameVersion: includeGameVersion ? gameVersion : null,
            index: "downloads",
            limit: limit,
            offset: offset,
            cancellationToken: cancellationToken);

    /// <summary>
    /// 统一的 Modrinth /search 调用：project_type 固定为 resourcepack，
    /// 游戏版本作为可选的 AND facet；query 为空时不带 query 参数。
    /// </summary>
    private async Task<ResourcePackSearchPage> SearchCoreAsync(
        string? query,
        string? gameVersion,
        string index,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        // 类型/版本过滤只能走 facets（JSON 数组的数组），不能传 new_filters。
        var facets = new List<string> { ResourcePackConfig.ModrinthResourcePackFacet };
        var usedGameVersionFilter = !string.IsNullOrWhiteSpace(gameVersion);
        if (usedGameVersionFilter)
            facets.Add($"[\"versions:{gameVersion}\"]");

        var parameters = new List<string>
        {
            "index=" + index,
            $"limit={Math.Clamp(limit, 1, 100)}",
            $"offset={Math.Max(0, offset)}",
            "facets=" + Uri.EscapeDataString($"[{string.Join(",", facets)}]"),
        };

        if (!string.IsNullOrWhiteSpace(query))
            parameters.Insert(0, $"query={Uri.EscapeDataString(query)}");

        var response = await GetJsonAsync<ModrinthSearchResponse>(
                "/search?" + string.Join("&", parameters), cancellationToken)
            .ConfigureAwait(false);

        var items = MapHits(response);
        return new ResourcePackSearchPage(
            items,
            response?.TotalHits ?? items.Count,
            usedGameVersionFilter,
            response?.Hits?.Count ?? 0);
    }

    /// <summary>
    /// 把 Modrinth 搜索命中项映射为资源包模型。
    /// </summary>
    private List<ResourcePackProject> MapHits(ModrinthSearchResponse? response)
    {
        if (response?.Hits is null)
            return new List<ResourcePackProject>();

        return response.Hits
            // 镜像忽略 facets 时不再把模组混进资源包列表：project_type 必须是 resourcepack。
            .Where(h => string.Equals(h.ProjectType, "resourcepack", StringComparison.OrdinalIgnoreCase))
            .Where(h => !string.IsNullOrWhiteSpace(h.ProjectId))
            .Select(h => new ResourcePackProject(
                DisplayName,
                h.ProjectId!,
                h.Slug ?? h.ProjectId!,
                h.Title ?? h.ProjectId!,
                h.Description ?? string.Empty,
                h.DateModified,
                h.Downloads))
            .ToList();
    }

    /// <summary>
    /// 获取资源包的版本列表：只按 game_versions 过滤（不传 loaders）。
    /// </summary>
    /// <param name="projectId">项目 ID。</param>
    /// <param name="gameVersion">游戏版本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyList<ResourcePackVersion>> GetVersionsAsync(
        string projectId,
        string gameVersion,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            query.Add("game_versions=" + Uri.EscapeDataString(
                JsonSerializer.Serialize(new[] { gameVersion })));
        }

        var suffix = $"/project/{Uri.EscapeDataString(projectId)}/version" +
                     (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);

        var versions = await GetJsonAsync<List<ModrinthVersion>>(suffix, cancellationToken)
            .ConfigureAwait(false);

        return (versions ?? new List<ModrinthVersion>())
            .Where(v => !string.IsNullOrWhiteSpace(v.Id))
            .Select(MapVersion)
            .Where(v => v is not null)
            .Select(v => v!)
            .OrderByDescending(v => v.PublishedAt)
            .ToList();
    }

    /// <summary>
    /// 依次尝试镜像与官方 API，任一成功即返回。
    /// </summary>
    private async Task<T?> GetJsonAsync<T>(string suffix, CancellationToken cancellationToken)
        where T : class
    {
        var endpoints = new[]
        {
            _config.ModrinthApiBase + suffix,
            _config.ModrinthApiFallbackBase + suffix,
        };

        Exception? lastError = null;

        foreach (var endpoint in endpoints)
        {
            try
            {
                Log($"[资源包] 请求 {endpoint}");
                using var response = await _client
                    .GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    lastError = new HttpRequestException(
                        $"{(int)response.StatusCode} {response.ReasonPhrase}");
                    Log($"[资源包] {(int)response.StatusCode}，尝试下一个源");
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);

                var value = await JsonSerializer
                    .DeserializeAsync<T>(stream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (value is not null)
                    return value;

                lastError = new InvalidOperationException("响应内容为空");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                Log($"[资源包] 请求失败：{ex.Message}");
            }
        }

        throw new InvalidOperationException(
            $"资源包接口请求失败：{lastError?.Message ?? "未知错误"}", lastError);
    }

    private static ResourcePackVersion? MapVersion(ModrinthVersion version)
    {
        var file = version.Files?.FirstOrDefault(f => !string.IsNullOrWhiteSpace(f.Url));
        if (string.IsNullOrWhiteSpace(version.Id) || file is null)
            return null;

        return new ResourcePackVersion(
            version.ProjectId ?? string.Empty,
            version.Id!,
            version.Name ?? version.VersionNumber ?? version.Id!,
            version.VersionNumber ?? string.Empty,
            version.GameVersions ?? new List<string>(),
            version.DatePublished,
            file.Url,
            string.IsNullOrWhiteSpace(file.Filename)
                ? $"{version.Id}.zip"
                : file.Filename!,
            file.Size);
    }

    private void Log(string message)
    {
        try
        {
            LogWritten?.Invoke(message);
        }
        catch (Exception)
        {
            // 忽略日志订阅者异常。
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();
    }
}

/// <summary>Modrinth 搜索响应。</summary>
internal sealed class ModrinthSearchResponse
{
    [JsonPropertyName("hits")]
    public List<ModrinthHit>? Hits { get; set; }

    /// <summary>命中的总条数，用于“加载更多”判断是否还有下一页。</summary>
    [JsonPropertyName("total_hits")]
    public int TotalHits { get; set; }
}

/// <summary>Modrinth 搜索结果条目。</summary>
internal sealed class ModrinthHit
{
    [JsonPropertyName("project_id")]
    public string? ProjectId { get; set; }

    /// <summary>项目类型（资源包应为 resourcepack）。</summary>
    [JsonPropertyName("project_type")]
    public string? ProjectType { get; set; }

    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("downloads")]
    public long Downloads { get; set; }

    [JsonPropertyName("date_modified")]
    public DateTimeOffset DateModified { get; set; }
}

/// <summary>Modrinth 版本。</summary>
internal sealed class ModrinthVersion
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("project_id")]
    public string? ProjectId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version_number")]
    public string? VersionNumber { get; set; }

    [JsonPropertyName("game_versions")]
    public List<string>? GameVersions { get; set; }

    [JsonPropertyName("date_published")]
    public DateTimeOffset DatePublished { get; set; }

    [JsonPropertyName("files")]
    public List<ModrinthFile>? Files { get; set; }
}

/// <summary>Modrinth 版本文件。</summary>
internal sealed class ModrinthFile
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("filename")]
    public string? Filename { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }
}
