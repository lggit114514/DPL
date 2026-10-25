using System.Collections.Concurrent;
using System.Globalization;
using DPL.Models.Mods;
using DPL.Services.Mods.Dtos;

namespace DPL.Services.Mods;

/// <summary>
/// CurseForge 的 <see cref="IModSource"/> 实现。
/// 必须提供 API Key（来自设置或环境变量，不在源码里硬编码）。
/// </summary>
public sealed class CurseForgeModSource : IModSource
{
    /// <summary>CurseForge 文件依赖里表示“必须依赖”的 relationType。</summary>
    private const int RequiredDependencyRelation = 3;

    private readonly ModHttp _http;
    private readonly ModConfig _config;

    /// <summary>版本 ID（modId:fileId）到下载地址的缓存，避免重复解析。</summary>
    private readonly ConcurrentDictionary<string, string> _downloadUrlCache = new();

    /// <summary>
    /// 创建 CurseForge 来源。
    /// </summary>
    /// <param name="http">HTTP 封装（含镜像回退）。</param>
    /// <param name="config">模组下载配置。</param>
    public CurseForgeModSource(ModHttp http, ModConfig config)
    {
        _http = http;
        _config = config;
    }

    /// <inheritdoc />
    public ModSourceKind Kind => ModSourceKind.CurseForge;

    /// <inheritdoc />
    public string DisplayName => "CurseForge";

    /// <inheritdoc />
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_config.CurseForgeApiKey);

    /// <inheritdoc />
    public string? UnavailableReason => IsConfigured
        ? null
        : "未配置 CurseForge API Key（可在「设置」里填写，或设置环境变量 NOVALAUNCH_CURSEFORGE_KEY）";

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModProjectSummary>> SearchAsync(
        string query,
        string gameVersion,
        string? loader,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        var parameters = new List<string>
        {
            $"gameId={_config.CurseForgeMinecraftGameId}",
            $"searchFilter={Uri.EscapeDataString(query)}",
            "pageSize=20",
            "sortField=2",
            "sortOrder=desc",
        };

        if (!string.IsNullOrWhiteSpace(gameVersion))
            parameters.Add($"gameVersion={Uri.EscapeDataString(gameVersion)}");

        var modLoaderType = ToCurseForgeLoaderType(loader);
        if (modLoaderType is not null)
            parameters.Add($"modLoaderType={modLoaderType}");

        var suffix = "/mods/search?" + string.Join("&", parameters);
        var result = await _http
            .GetJsonAsync<CurseForgeSearchResponse>(
                _config.CurseForgeApiBase + suffix,
                _config.CurseForgeApiFallbackBase + suffix,
                "CurseForge",
                _config.CurseForgeApiKey,
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
            throw new ModSourceException($"CurseForge 搜索失败：{result.Error}");

        return result.Value!.Data?
                   .Select(MapProject)
                   .ToList()
               ?? new List<ModProjectSummary>();
    }

    /// <inheritdoc />
    public async Task<ModProjectSummary?> GetProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        var suffix = $"/mods/{Uri.EscapeDataString(projectId)}";
        var result = await _http
            .GetJsonAsync<CurseForgeModResponse>(
                _config.CurseForgeApiBase + suffix,
                _config.CurseForgeApiFallbackBase + suffix,
                "CurseForge",
                _config.CurseForgeApiKey,
                cancellationToken)
            .ConfigureAwait(false);

        return result.Value?.Data is null ? null : MapProject(result.Value.Data);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModVersionInfo>> GetVersionsAsync(
        string projectId,
        string gameVersion,
        string? loader,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        var parameters = new List<string> { "pageSize=50" };
        if (!string.IsNullOrWhiteSpace(gameVersion))
            parameters.Add($"gameVersion={Uri.EscapeDataString(gameVersion)}");

        var modLoaderType = ToCurseForgeLoaderType(loader);
        if (modLoaderType is not null)
            parameters.Add($"modLoaderType={modLoaderType}");

        var suffix = $"/mods/{Uri.EscapeDataString(projectId)}/files?" + string.Join("&", parameters);
        var result = await _http
            .GetJsonAsync<CurseForgeFilesResponse>(
                _config.CurseForgeApiBase + suffix,
                _config.CurseForgeApiFallbackBase + suffix,
                "CurseForge",
                _config.CurseForgeApiKey,
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
            throw new ModSourceException($"CurseForge 获取版本失败：{result.Error}");

        return (result.Value!.Data ?? new List<CurseForgeFile>())
            .Where(f => f.Id > 0)
            .Select(MapVersion)
            .Where(v => IsCompatible(v, gameVersion, loader))
            .OrderByDescending(v => v.PublishedAt)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<string?> ResolveDownloadUrlAsync(string versionId, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        if (!TryParseVersionId(versionId, out var modId, out var fileId))
            throw new ModSourceException($"CurseForge 版本 ID 格式不正确：{versionId}");

        if (_downloadUrlCache.TryGetValue(versionId, out var cached) && !string.IsNullOrWhiteSpace(cached))
            return cached;

        var suffix = $"/mods/{modId}/files/{fileId}";
        var result = await _http
            .GetJsonAsync<CurseForgeFileResponse>(
                _config.CurseForgeApiBase + suffix,
                _config.CurseForgeApiFallbackBase + suffix,
                "CurseForge",
                _config.CurseForgeApiKey,
                cancellationToken)
            .ConfigureAwait(false);

        var file = result.Value?.Data;
        if (file is null)
            return null;

        var url = string.IsNullOrWhiteSpace(file.DownloadUrl)
            ? BuildCdnUrl(file.Id, file.FileName)
            : file.DownloadUrl;

        if (!string.IsNullOrWhiteSpace(url))
            _downloadUrlCache[versionId] = url;

        return url;
    }

    /// <summary>
    /// 判断一个版本是否满足游戏版本与加载器要求（CurseForge 的 gameVersions 会混有加载器名称）。
    /// </summary>
    /// <param name="version">版本信息。</param>
    /// <param name="gameVersion">游戏版本。</param>
    /// <param name="loader">加载器。</param>
    public static bool IsCompatible(ModVersionInfo version, string? gameVersion, string? loader)
    {
        if (!string.IsNullOrWhiteSpace(gameVersion) &&
            !version.GameVersions.Any(v => string.Equals(v, gameVersion, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(loader))
            return true;

        // 解析结果里没有加载器信息时无法判断，按兼容处理（API 查询已经按 modLoaderType 过滤过）。
        if (version.Loaders.Count == 0)
            return true;

        var want = loader.ToLowerInvariant();
        return version.Loaders.Any(l => string.Equals(l, want, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 由文件 ID 与文件名拼出 CurseForge CDN 地址（API 不返回 downloadUrl 时的兜底）。
    /// </summary>
    /// <param name="fileId">文件 ID。</param>
    /// <param name="fileName">文件名。</param>
    public string BuildCdnUrl(long fileId, string? fileName)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? "file.jar" : fileName;
        return $"{_config.CurseForgeCdnBase}/files/{fileId / 1000}/{fileId % 1000}/{name}";
    }

    /// <summary>
    /// 解析 <c>modId:fileId</c> 格式的版本 ID。
    /// </summary>
    private static bool TryParseVersionId(string versionId, out long modId, out long fileId)
    {
        modId = 0;
        fileId = 0;

        var parts = versionId.Split(':');
        return parts.Length == 2
               && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out modId)
               && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out fileId);
    }

    /// <summary>
    /// 把 CurseForge 的模组 DTO 转换为统一模型。
    /// </summary>
    private static ModProjectSummary MapProject(CurseForgeMod mod)
        => new(
            ModSourceKind.CurseForge,
            mod.Id.ToString(CultureInfo.InvariantCulture),
            mod.Slug ?? mod.Id.ToString(CultureInfo.InvariantCulture),
            mod.Name ?? $"CurseForge #{mod.Id}",
            mod.Summary ?? string.Empty,
            string.Empty,
            mod.DateModified,
            mod.DownloadCount);

    /// <summary>
    /// 把 CurseForge 的文件 DTO 转换为统一模型。
    /// </summary>
    private ModVersionInfo MapVersion(CurseForgeFile file)
    {
        var versionId = $"{file.ModId}:{file.Id}";
        var url = string.IsNullOrWhiteSpace(file.DownloadUrl)
            ? null
            : file.DownloadUrl;

        if (!string.IsNullOrWhiteSpace(url))
            _downloadUrlCache[versionId] = url;

        // gameVersions 里既可能包含游戏版本，也可能包含 "Fabric" / "Forge" 这类加载器标记。
        var gameVersions = new List<string>();
        var loaders = new List<string>();

        foreach (var value in file.GameVersions ?? new List<string>())
        {
            if (IsLoaderName(value, out var loader))
                loaders.Add(loader);
            else
                gameVersions.Add(value);
        }

        var dependencies = (file.Dependencies ?? new List<CurseForgeDependency>())
            .Where(d => d.RelationType == RequiredDependencyRelation && d.ModId > 0)
            .Select(d => new ModDependency(
                ModSourceKind.CurseForge,
                d.ModId.ToString(CultureInfo.InvariantCulture),
                null,
                true,
                null))
            .ToList();

        return new ModVersionInfo(
            ModSourceKind.CurseForge,
            file.ModId.ToString(CultureInfo.InvariantCulture),
            versionId,
            file.DisplayName ?? file.FileName ?? versionId,
            file.DisplayName ?? file.FileName ?? versionId,
            gameVersions,
            loaders,
            file.FileDate,
            url,
            file.FileName ?? $"{file.Id}.jar",
            file.FileLength,
            dependencies);
    }

    /// <summary>
    /// 判断一个 gameVersions 元素是否是加载器名称，并返回规范化后的加载器名。
    /// </summary>
    private static bool IsLoaderName(string value, out string loader)
    {
        switch (value.ToLowerInvariant())
        {
            case "fabric":
                loader = "fabric";
                return true;
            case "quilt":
                loader = "quilt";
                return true;
            case "forge":
                loader = "forge";
                return true;
            case "neoforge":
                loader = "neoforge";
                return true;
            default:
                loader = string.Empty;
                return false;
        }
    }

    /// <summary>
    /// 加载器名称映射为 CurseForge 的 modLoaderType 数值。
    /// </summary>
    private static int? ToCurseForgeLoaderType(string? loader)
        => loader?.ToLowerInvariant() switch
        {
            "forge" => 1,
            "fabric" => 4,
            "quilt" => 5,
            "neoforge" => 6,
            _ => null,
        };

    /// <summary>
    /// 未配置 API Key 时给出明确异常。
    /// </summary>
    private void EnsureConfigured()
    {
        if (!IsConfigured)
            throw new ModSourceException(UnavailableReason ?? "CurseForge 不可用");
    }
}
