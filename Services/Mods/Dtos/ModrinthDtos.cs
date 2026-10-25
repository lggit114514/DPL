using System.Text.Json.Serialization;

namespace DPL.Services.Mods.Dtos;

// ---------------------------------------------------------------------------
// Modrinth 专用 DTO（字段名与官方 JSON 一一对应，不与 CurseForge 共用）
// ---------------------------------------------------------------------------

/// <summary>Modrinth 搜索响应。</summary>
internal sealed class ModrinthSearchResponse
{
    [JsonPropertyName("hits")]
    public List<ModrinthSearchHit>? Hits { get; set; }
}

/// <summary>Modrinth 搜索结果条目。</summary>
internal sealed class ModrinthSearchHit
{
    [JsonPropertyName("project_id")]
    public string? ProjectId { get; set; }

    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("downloads")]
    public long Downloads { get; set; }

    [JsonPropertyName("date_modified")]
    public DateTimeOffset DateModified { get; set; }
}

/// <summary>Modrinth 项目详情。</summary>
internal sealed class ModrinthProject
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("updated")]
    public DateTimeOffset Updated { get; set; }

    [JsonPropertyName("downloads")]
    public long Downloads { get; set; }
}

/// <summary>Modrinth 版本列表项。</summary>
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

    [JsonPropertyName("loaders")]
    public List<string>? Loaders { get; set; }

    [JsonPropertyName("date_published")]
    public DateTimeOffset DatePublished { get; set; }

    [JsonPropertyName("files")]
    public List<ModrinthFile>? Files { get; set; }

    [JsonPropertyName("dependencies")]
    public List<ModrinthDependency>? Dependencies { get; set; }
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

    [JsonPropertyName("primary")]
    public bool Primary { get; set; }
}

/// <summary>Modrinth 依赖。</summary>
internal sealed class ModrinthDependency
{
    [JsonPropertyName("version_id")]
    public string? VersionId { get; set; }

    [JsonPropertyName("project_id")]
    public string? ProjectId { get; set; }

    [JsonPropertyName("dependency_type")]
    public string? DependencyType { get; set; }

    [JsonPropertyName("file_name")]
    public string? FileName { get; set; }
}
