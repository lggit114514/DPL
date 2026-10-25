using System.Globalization;
using System.Net;
using System.Text.Json;
using DPL.Models.Mods;
using DPL.Services.Mods.Dtos;

namespace DPL.Services.Mods;

/// <summary>
/// Modrinth 的 <see cref="IModSource"/> 实现。
/// API 公开、无需 Key；游戏版本筛选必须传精确补丁版本。
/// </summary>
public sealed class ModrinthModSource : IModSource
{
    private readonly ModHttp _http;
    private readonly ModConfig _config;

    /// <summary>
    /// 创建 Modrinth 来源。
    /// </summary>
    /// <param name="http">HTTP 封装（含镜像回退）。</param>
    /// <param name="config">模组下载配置。</param>
    public ModrinthModSource(ModHttp http, ModConfig config)
    {
        _http = http;
        _config = config;
    }

    /// <inheritdoc />
    public ModSourceKind Kind => ModSourceKind.Modrinth;

    /// <inheritdoc />
    public string DisplayName => "Modrinth";

    /// <inheritdoc />
    public bool IsConfigured => true;

    /// <inheritdoc />
    public string? UnavailableReason => null;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModProjectSummary>> SearchAsync(
        string query,
        string gameVersion,
        string? loader,
        CancellationToken cancellationToken = default)
    {
        var facets = new List<string> { "[\"project_type:mod\"]" };
        if (!string.IsNullOrWhiteSpace(loader))
            facets.Add($"[\"categories:{loader}\"]");
        if (!string.IsNullOrWhiteSpace(gameVersion))
            facets.Add($"[\"versions:{gameVersion}\"]");

        var primary = $"{_config.ModrinthApiBase}/search?query={Uri.EscapeDataString(query)}" +
                       $"&limit=20&index=relevance&facets={Uri.EscapeDataString("[" + string.Join(",", facets) + "]")}";
        var fallback = $"{_config.ModrinthApiFallbackBase}/search?query={Uri.EscapeDataString(query)}" +
                       $"&limit=20&index=relevance&facets={Uri.EscapeDataString("[" + string.Join(",", facets) + "]")}";

        var result = await _http.GetJsonAsync<ModrinthSearchResponse>(primary, fallback, "Modrinth", null, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
            throw new ModSourceException($"Modrinth 搜索失败：{result.Error}");

        return result.Value!.Hits?
                   .Where(h => !string.IsNullOrWhiteSpace(h.ProjectId))
                   .Select(h => new ModProjectSummary(
                       ModSourceKind.Modrinth,
                       h.ProjectId!,
                       h.Slug ?? h.ProjectId!,
                       h.Title ?? h.ProjectId!,
                       h.Description ?? string.Empty,
                       h.Author ?? string.Empty,
                       h.DateModified,
                       h.Downloads))
                   .ToList()
               ?? new List<ModProjectSummary>();
    }

    /// <inheritdoc />
    public async Task<ModProjectSummary?> GetProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var primary = $"{_config.ModrinthApiBase}/project/{Uri.EscapeDataString(projectId)}";
        var fallback = $"{_config.ModrinthApiFallbackBase}/project/{Uri.EscapeDataString(projectId)}";

        var result = await _http.GetJsonAsync<ModrinthProject>(primary, fallback, "Modrinth", null, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success || result.Value is null)
            return null;

        var project = result.Value;
        return new ModProjectSummary(
            ModSourceKind.Modrinth,
            project.Id ?? projectId,
            project.Slug ?? projectId,
            project.Title ?? projectId,
            project.Description ?? string.Empty,
            string.Empty,
            project.Updated,
            project.Downloads);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModVersionInfo>> GetVersionsAsync(
        string projectId,
        string gameVersion,
        string? loader,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(gameVersion))
            query.Add($"game_versions={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { gameVersion }))}");
        if (!string.IsNullOrWhiteSpace(loader))
            query.Add($"loaders={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader }))}");

        var suffix = query.Count > 0 ? "?" + string.Join("&", query) : string.Empty;

        var primary = $"{_config.ModrinthApiBase}/project/{Uri.EscapeDataString(projectId)}/version{suffix}";
        var fallback = $"{_config.ModrinthApiFallbackBase}/project/{Uri.EscapeDataString(projectId)}/version{suffix}";

        var result = await _http
            .GetJsonAsync<List<ModrinthVersion>>(primary, fallback, "Modrinth", null, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
            throw new ModSourceException($"Modrinth 获取版本失败：{result.Error}");

        return (result.Value ?? new List<ModrinthVersion>())
            .Where(v => !string.IsNullOrWhiteSpace(v.Id))
            .Select(MapVersion)
            .Where(v => v is not null)
            .Select(v => v!)
            .OrderByDescending(v => v.PublishedAt)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<string?> ResolveDownloadUrlAsync(string versionId, CancellationToken cancellationToken = default)
    {
        var primary = $"{_config.ModrinthApiBase}/version/{Uri.EscapeDataString(versionId)}";
        var fallback = $"{_config.ModrinthApiFallbackBase}/version/{Uri.EscapeDataString(versionId)}";

        var result = await _http
            .GetJsonAsync<ModrinthVersion>(primary, fallback, "Modrinth", null, cancellationToken)
            .ConfigureAwait(false);

        var file = result.Value?.Files?
            .FirstOrDefault(f => f.Primary && !string.IsNullOrWhiteSpace(f.Url))
            ?? result.Value?.Files?.FirstOrDefault(f => !string.IsNullOrWhiteSpace(f.Url));

        return file?.Url;
    }

    /// <summary>
    /// 把 Modrinth 的版本 DTO 转换成统一模型。
    /// </summary>
    private static ModVersionInfo? MapVersion(ModrinthVersion version)
    {
        if (string.IsNullOrWhiteSpace(version.Id))
            return null;

        var file = version.Files?
                       .FirstOrDefault(f => f.Primary && !string.IsNullOrWhiteSpace(f.Url))
                   ?? version.Files?.FirstOrDefault(f => !string.IsNullOrWhiteSpace(f.Url));

        var dependencies = version.Dependencies?
            .Where(d => string.Equals(d.DependencyType, "required", StringComparison.OrdinalIgnoreCase))
            .Where(d => !string.IsNullOrWhiteSpace(d.ProjectId) || !string.IsNullOrWhiteSpace(d.VersionId))
            .Select(d => new ModDependency(
                ModSourceKind.Modrinth,
                d.ProjectId ?? string.Empty,
                string.IsNullOrWhiteSpace(d.VersionId) ? null : d.VersionId,
                true,
                d.FileName))
            .ToList() ?? new List<ModDependency>();

        return new ModVersionInfo(
            ModSourceKind.Modrinth,
            version.ProjectId ?? string.Empty,
            version.Id!,
            version.Name ?? version.VersionNumber ?? version.Id!,
            version.VersionNumber ?? version.Name ?? version.Id!,
            version.GameVersions ?? new List<string>(),
            (version.Loaders ?? new List<string>()).Select(l => l.ToLowerInvariant()).ToList(),
            version.DatePublished,
            file?.Url,
            file?.Filename ?? $"{version.Id}.jar",
            file?.Size ?? 0,
            dependencies);
    }
}

/// <summary>
/// 模组来源请求失败（区别于网络层错误，便于界面展示可读信息）。
/// </summary>
public sealed class ModSourceException : Exception
{
    /// <summary>
    /// 创建来源异常。
    /// </summary>
    /// <param name="message">错误说明。</param>
    public ModSourceException(string message) : base(message)
    {
    }
}
