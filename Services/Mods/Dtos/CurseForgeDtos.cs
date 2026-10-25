using System.Text.Json.Serialization;

namespace DPL.Services.Mods.Dtos;

// ---------------------------------------------------------------------------
// CurseForge 专用 DTO（字段名与官方 JSON 一一对应，不与 Modrinth 共用）
// ---------------------------------------------------------------------------

/// <summary>CurseForge 搜索响应（外层包一个 data 数组）。</summary>
internal sealed class CurseForgeSearchResponse
{
    [JsonPropertyName("data")]
    public List<CurseForgeMod>? Data { get; set; }
}

/// <summary>CurseForge 单个模组响应。</summary>
internal sealed class CurseForgeModResponse
{
    [JsonPropertyName("data")]
    public CurseForgeMod? Data { get; set; }
}

/// <summary>CurseForge 文件列表响应。</summary>
internal sealed class CurseForgeFilesResponse
{
    [JsonPropertyName("data")]
    public List<CurseForgeFile>? Data { get; set; }
}

/// <summary>CurseForge 单个文件响应。</summary>
internal sealed class CurseForgeFileResponse
{
    [JsonPropertyName("data")]
    public CurseForgeFile? Data { get; set; }
}

/// <summary>CurseForge 模组（项目）。</summary>
internal sealed class CurseForgeMod
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("dateModified")]
    public DateTimeOffset DateModified { get; set; }

    [JsonPropertyName("downloadCount")]
    public long DownloadCount { get; set; }

    [JsonPropertyName("latestFiles")]
    public List<CurseForgeFile>? LatestFiles { get; set; }
}

/// <summary>CurseForge 文件（模组版本）。</summary>
internal sealed class CurseForgeFile
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("modId")]
    public long ModId { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("fileName")]
    public string? FileName { get; set; }

    [JsonPropertyName("fileDate")]
    public DateTimeOffset FileDate { get; set; }

    [JsonPropertyName("fileLength")]
    public long FileLength { get; set; }

    [JsonPropertyName("gameVersions")]
    public List<string>? GameVersions { get; set; }

    [JsonPropertyName("downloadUrl")]
    public string? DownloadUrl { get; set; }

    [JsonPropertyName("dependencies")]
    public List<CurseForgeDependency>? Dependencies { get; set; }
}

/// <summary>CurseForge 文件依赖。</summary>
internal sealed class CurseForgeDependency
{
    /// <summary>依赖的模组 ID。</summary>
    [JsonPropertyName("modId")]
    public long ModId { get; set; }

    /// <summary>依赖关系：3 = RequiredDependency（必须）。</summary>
    [JsonPropertyName("relationType")]
    public int RelationType { get; set; }
}
