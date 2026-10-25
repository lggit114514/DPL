using System.Text.Json.Serialization;

namespace DPL.Services.Mods.Dtos;

// ---------------------------------------------------------------------------
// 麦块开放 API（https://api.minekuai.cn/panel/public/mc-mods）的搜索响应 DTO。
// 该接口无需 API Key，统一 JSON，时间字段为 UTC RFC 3339。
// ---------------------------------------------------------------------------

/// <summary>麦块模组搜索响应。</summary>
internal sealed class MineKuaiSearchResponse
{
    [JsonPropertyName("items")]
    public List<MineKuaiSearchItem>? Items { get; set; }

    [JsonPropertyName("limit")]
    public int Limit { get; set; }

    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }
}

/// <summary>麦块搜索结果中的一条模组。</summary>
internal sealed class MineKuaiSearchItem
{
    /// <summary>对应的 Modrinth 项目 ID（版本列表与下载仍沿用原有逻辑）。</summary>
    [JsonPropertyName("project_id")]
    public string? ProjectId { get; set; }

    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>中文标题（status=translated 时通常存在）。</summary>
    [JsonPropertyName("title_zh")]
    public string? TitleZh { get; set; }

    [JsonPropertyName("description_en")]
    public string? DescriptionEn { get; set; }

    /// <summary>中文描述。</summary>
    [JsonPropertyName("description_zh")]
    public string? DescriptionZh { get; set; }

    [JsonPropertyName("categories")]
    public List<string>? Categories { get; set; }

    [JsonPropertyName("downloads")]
    public long Downloads { get; set; }

    [JsonPropertyName("follows")]
    public long Follows { get; set; }

    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; set; }

    [JsonPropertyName("game_versions")]
    public List<string>? GameVersions { get; set; }

    [JsonPropertyName("loaders")]
    public List<string>? Loaders { get; set; }

    [JsonPropertyName("translation_status")]
    public string? TranslationStatus { get; set; }

    /// <summary>翻译时间（UTC RFC 3339）。</summary>
    [JsonPropertyName("translation_at")]
    public DateTimeOffset? TranslationAt { get; set; }
}
