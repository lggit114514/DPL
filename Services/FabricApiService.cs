using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DPL.Models;

namespace DPL.Services;

/// <summary>
/// 通过 Modrinth 公开 API 获取并下载 Fabric API 的实现。
/// </summary>
public sealed class FabricApiService : IFabricApiService, IDisposable
{
    private const string ModrinthProjectApi = "https://api.modrinth.com/v2/project/fabric-api/version";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;

    /// <summary>
    /// 创建 Fabric API 服务。
    /// </summary>
    public FabricApiService()
    {
        _httpClient = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromMinutes(5),
        };

        // Modrinth 建议带上可识别的 User-Agent。
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DPL-Launcher/1.0 (Minecraft launcher)");
    }

    /// <inheritdoc />
    public async Task<FabricApiRelease?> GetRecommendedAsync(
        string minecraftVersion,
        string? loaderVersion = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion))
            return null;

        var url = $"{ModrinthProjectApi}?loaders=%5B%22fabric%22%5D&game_versions=%5B%22" +
                  Uri.EscapeDataString(minecraftVersion) + "%22%5D";

        using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        var versions = await response.Content
            .ReadFromJsonAsync<List<ModrinthVersion>>(SerializerOptions, cancellationToken)
            .ConfigureAwait(false);

        if (versions is null || versions.Count == 0)
            return null;

        // 优先稳定发行版（release），没有的话用最新的测试版。
        var picked = versions
                         .Where(v => string.Equals(v.VersionType, "release", StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(v => v.DatePublished)
                         .FirstOrDefault()
                     ?? versions.OrderByDescending(v => v.DatePublished).FirstOrDefault();

        if (picked is null)
            return null;

        var file = picked.Files?
                       .FirstOrDefault(f => f.Primary)
                   ?? picked.Files?.FirstOrDefault();

        if (file is null || string.IsNullOrWhiteSpace(file.Url))
            return null;

        return new FabricApiRelease(
            picked.VersionNumber ?? picked.Name ?? "未知版本",
            file.Filename ?? $"fabric-api-{picked.VersionNumber}.jar",
            file.Url,
            file.Size);
    }

    /// <inheritdoc />
    public async Task<string> DownloadAsync(
        FabricApiRelease release,
        string destinationFolder,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationFolder);

        var fileName = string.IsNullOrWhiteSpace(release.FileName)
            ? $"fabric-api-{release.VersionNumber}.jar"
            : release.FileName;

        var targetPath = Path.Combine(destinationFolder, fileName);

        // 依次尝试多个下载源：先 maven.fabricmc.net（国内网络通常可访问），再 Modrinth CDN。
        var sources = new List<string> { release.MavenDownloadUrl };
        if (!string.IsNullOrWhiteSpace(release.DownloadUrl) &&
            !string.Equals(release.DownloadUrl, release.MavenDownloadUrl, StringComparison.OrdinalIgnoreCase))
        {
            sources.Add(release.DownloadUrl);
        }

        var errors = new List<string>();

        foreach (var url in sources)
        {
            try
            {
                await DownloadFromUrlAsync(url, targetPath, release.FileSize, progress, cancellationToken)
                    .ConfigureAwait(false);

                progress?.Report(1d);
                return targetPath;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{new Uri(url).Host}: {ex.Message}");
            }
        }

        throw new InvalidOperationException(
            "下载 Fabric API 失败（可能无法访问 cdn.modrinth.com / maven.fabricmc.net）。" +
            "可以稍后手动下载并放进该版本的 mods 文件夹。详情：" + string.Join("；", errors));
    }

    /// <summary>
    /// 从单个地址下载文件，带进度回调。
    /// </summary>
    private async Task DownloadFromUrlAsync(
        string url,
        string targetPath,
        long expectedSize,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? expectedSize;
        var buffer = new byte[81920];

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            long written = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;

                if (totalBytes > 0)
                    progress?.Report(Math.Clamp((double)written / totalBytes, 0d, 1d));
            }

            // 简单校验：文件太小说明下载到了错误页面。
            if (written < 100 * 1024)
                throw new InvalidOperationException($"下载内容异常（仅 {written} 字节）");
        }
    }

    /// <summary>
    /// 释放内部 HttpClient。
    /// </summary>
    public void Dispose() => _httpClient.Dispose();

    /// <summary>Modrinth 版本条目（只声明用到的字段）。</summary>
    private sealed class ModrinthVersion
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("version_number")]
        public string? VersionNumber { get; set; }

        [JsonPropertyName("version_type")]
        public string? VersionType { get; set; }

        [JsonPropertyName("date_published")]
        public DateTimeOffset DatePublished { get; set; }

        [JsonPropertyName("files")]
        public List<ModrinthFile>? Files { get; set; }
    }

    /// <summary>Modrinth 文件条目。</summary>
    private sealed class ModrinthFile
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("filename")]
        public string? Filename { get; set; }

        [JsonPropertyName("primary")]
        public bool Primary { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}
