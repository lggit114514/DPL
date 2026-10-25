using DPL.Models.ResourcePacks;

namespace DPL.Services.ResourcePacks;

/// <summary>
/// 资源包安装服务：把选中的版本原样下载到实例的 resourcepacks 目录（不解压、不解析依赖）。
///
/// 下载顺序固定为：官方 CDN → BMCLAPI → MCIM。
/// MCIM 不缓存资源包（仅缓存模组），所以它只作为最后的兜底；失败时给出明确提示。
/// </summary>
public sealed class ResourcePackInstallService : IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly ResourcePackConfig _config;

    /// <summary>
    /// 创建安装服务。
    /// </summary>
    /// <param name="config">资源包配置。</param>
    /// <param name="client">可注入的 HttpClient（单元测试用）。</param>
    public ResourcePackInstallService(ResourcePackConfig? config = null, HttpClient? client = null)
    {
        _config = config ?? new ResourcePackConfig();

        if (client is not null)
        {
            _client = client;
            _ownsClient = false;
        }
        else
        {
            _client = new HttpClient { Timeout = _config.DownloadTimeout };
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("DPL/1.0 (resourcepack)");
            _ownsClient = true;
        }
    }

    /// <summary>日志回调。</summary>
    public event Action<string>? LogWritten;

    /// <summary>
    /// 下载一个资源包到指定目录。
    /// </summary>
    /// <param name="version">版本信息（含下载地址）。</param>
    /// <param name="resourcePacksDirectory">resourcepacks 目录。</param>
    /// <param name="progress">进度回调。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<ResourcePackInstallReport> InstallAsync(
        ResourcePackVersion version,
        string resourcePacksDirectory,
        IProgress<ResourcePackProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(resourcePacksDirectory);

        var fileName = SanitizeFileName(version.FileName, version.VersionId);
        var destination = Path.Combine(resourcePacksDirectory, fileName);

        if (File.Exists(destination))
        {
            Log($"{fileName} 已存在，跳过");
            return new ResourcePackInstallReport(
                Array.Empty<string>(), new[] { destination }, 0);
        }

        var candidates = BuildCandidates(version.DownloadUrl);
        if (candidates.Count == 0)
            throw new InvalidOperationException("没有可用的资源包下载地址（版本缺少文件 URL）");

        var tempPath = destination + ".part";
        var errors = new List<string>();

        foreach (var (label, url) in candidates)
        {
            try
            {
                progress?.Report(new ResourcePackProgress($"正在从 {label} 下载", 0, version.FileSize));
                Log($"[资源包] 尝试 {label}：{url}");

                var bytes = await DownloadToFileAsync(
                        url, tempPath, version.FileSize, label, progress, cancellationToken)
                    .ConfigureAwait(false);

                if (!IsZipFile(tempPath))
                {
                    TryDelete(tempPath);
                    errors.Add($"{label} => 返回的内容不是 zip（可能是不缓存资源包的错误页）");
                    Log($"[资源包] {label} 返回非 zip 内容，尝试下一个源");
                    continue;
                }

                File.Move(tempPath, destination, overwrite: true);
                Log($"[资源包] {label} 下载完成：{fileName}（{bytes / 1024.0:0} KB）");

                progress?.Report(new ResourcePackProgress("下载完成", bytes, bytes));
                return new ResourcePackInstallReport(
                    new[] { destination }, Array.Empty<string>(), bytes);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryDelete(tempPath);
                throw;
            }
            catch (Exception ex)
            {
                TryDelete(tempPath);
                errors.Add($"{label} => {ex.Message}");
                Log($"[资源包] {label} 下载失败：{ex.Message}");
            }
        }

        throw new InvalidOperationException(
            "资源包下载失败。官方 CDN 与镜像均不可用；" +
            "其中 MCIM 镜像不缓存资源包（只缓存模组），请检查网络后重试。\n" +
            string.Join("\n", errors));
    }

    /// <summary>
    /// 下载候选：官方 CDN → BMCLAPI → MCIM（顺序固定）。
    /// </summary>
    private List<(string Label, string Url)> BuildCandidates(string? downloadUrl)
    {
        var list = new List<(string Label, string Url)>();
        var relative = GetCdnRelativePath(downloadUrl);

        if (string.IsNullOrWhiteSpace(relative))
            return list;

        list.Add(("官方 CDN", _config.ModrinthCdnOfficialBase + relative));
        list.Add(("BMCLAPI 镜像", _config.ModrinthCdnBangbangBase + relative));
        list.Add(("MCIM 镜像（不缓存资源包）", _config.ModrinthCdnMirrorBase + relative));
        return list;
    }

    /// <summary>
    /// 从下载地址里取相对 CDN 根的路径。
    /// </summary>
    private string? GetCdnRelativePath(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        foreach (var basePart in new[]
                 {
                     _config.ModrinthCdnOfficialBase,
                     _config.ModrinthCdnBangbangBase,
                     _config.ModrinthCdnMirrorBase,
                 })
        {
            if (url.StartsWith(basePart, StringComparison.OrdinalIgnoreCase))
                return url[basePart.Length..];
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.PathAndQuery : null;
    }

    /// <summary>
    /// 流式下载到临时文件，并按块上报进度。
    /// </summary>
    private async Task<long> DownloadToFileAsync(
        string url,
        string tempPath,
        long fallbackTotal,
        string label,
        IProgress<ResourcePackProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}");

        var total = response.Content.Headers.ContentLength ?? fallbackTotal;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var target = new FileStream(
            tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long written = 0;
        int read;
        var lastReport = DateTime.UtcNow;

        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            written += read;

            // 每 200ms 上报一次，避免刷爆 UI 线程。
            if ((DateTime.UtcNow - lastReport).TotalMilliseconds >= 200)
            {
                lastReport = DateTime.UtcNow;
                progress?.Report(new ResourcePackProgress($"正在从 {label} 下载", written, total));
            }
        }

        await target.FlushAsync(cancellationToken).ConfigureAwait(false);

        if (written == 0)
            throw new InvalidOperationException("下载内容为空");

        return written;
    }

    /// <summary>判断文件是否是 zip（PK 文件头）。</summary>
    private static bool IsZipFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[4];
            return stream.Read(header) >= 4 &&
                   header[0] == 0x50 && header[1] == 0x4B &&
                   (header[2] == 0x03 || header[2] == 0x05 || header[2] == 0x07);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>清理非法文件名，避免写出目录。</summary>
    private static string SanitizeFileName(string? fileName, string fallback)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? fallback : fileName!.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');

        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            name += ".zip";

        return name;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
            // 忽略清理失败。
        }
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
