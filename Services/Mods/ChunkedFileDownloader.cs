using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace DPL.Services.Mods;

/// <summary>
/// 大文件分片下载器（独立类）。
/// 下载流程：候选源健康检查（HEAD 校验状态码 / Content-Type / Accept-Ranges）→
/// 预分配文件 → 按 4 MB 切片、各分片 Seek 到自己的偏移并行写入 → 校验总长度。
/// 分片过程中若某个源返回 text/html 或 403，会把该源标记为“本次下载不可用”，
/// 并切换到下一个候选源重试当前分片（不会把错误页写进文件）。
/// </summary>
public sealed class ChunkedFileDownloader
{
    /// <summary>默认阈值：只有大于 10 MB 的文件才启用分片。</summary>
    public const long DefaultThresholdBytes = 10L * 1024 * 1024;

    private readonly HttpClient _client;
    private readonly ChunkedFileDownloaderOptions _options;

    /// <summary>日志回调（可能在后台线程触发）。</summary>
    public event Action<string>? LogWritten;

    /// <summary>
    /// 创建分片下载器。
    /// </summary>
    /// <param name="client">复用的 HttpClient（超时、UA、代理等配置都沿用现有实例）。</param>
    /// <param name="options">分片参数（阈值、分片大小、并发数）；为 null 时使用默认值。</param>
    public ChunkedFileDownloader(HttpClient client, ChunkedFileDownloaderOptions? options = null)
    {
        _client = client;
        _options = options ?? new ChunkedFileDownloaderOptions();
    }

    /// <summary>当前生效的阈值（字节）。</summary>
    public long ThresholdBytes => _options.ThresholdBytes;

    /// <summary>单个文件最大并发分片数。</summary>
    public int MaxParallelChunks => _options.MaxParallelChunks;

    /// <summary>
    /// 判断某个文件是否应该走分片下载（严格大于阈值）。
    /// </summary>
    /// <param name="contentLength">文件大小。</param>
    public bool ShouldChunk(long contentLength) => contentLength > ThresholdBytes;

    /// <summary>
    /// 按给定的源顺序下载同一个文件；健康检查会跳过不可用源。
    /// </summary>
    /// <param name="baseUrls">
    /// 候选源，顺序由调用方决定：
    /// Modrinth 为 官方 CDN → BMCLAPI → MCIM；CurseForge 为 官方 CDN → MCIM。
    /// </param>
    /// <param name="relativePath">相对路径，例如 <c>/data/xxx/versions/1/mod.jar</c>。</param>
    /// <param name="destinationPath">目标文件路径。</param>
    /// <param name="expectedSize">已知文件大小（0 表示以 HEAD 结果为准）。</param>
    /// <param name="progress">进度回调（已下载字节、总字节）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入的字节数。</returns>
    public async Task<long> DownloadAsync(
        IReadOnlyList<string> baseUrls,
        string relativePath,
        string destinationPath,
        long expectedSize,
        Action<long, long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        var candidateUrls = baseUrls
            .Where(baseUrl => !string.IsNullOrWhiteSpace(baseUrl))
            .Select(baseUrl => Combine(baseUrl, relativePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidateUrls.Count == 0)
            throw new ChunkDownloadException("没有可用的下载源", errors);

        // 1) 下载前健康检查：只保留满足 2xx + 二进制 Content-Type + Accept-Ranges 的源。
        var healthySources = _options.HealthCheckEnabled
            ? await CheckSourcesAsync(candidateUrls, cancellationToken).ConfigureAwait(false)
            : candidateUrls.Select(url => new SourceProbe(url, 0)).ToList();

        if (healthySources.Count == 0)
        {
            Log("警告：所有源都未通过健康检查，回退到默认顺序下载");
            healthySources = candidateUrls.Select(url => new SourceProbe(url, 0)).ToList();
        }

        // 2) 本次下载内被判定为不可用的源（text/html、403、不支持 Range 等）。
        var unavailable = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var orderedUrls = healthySources.Select(source => source.Url).ToList();

        // 如果所有源都只是因为“不支持 Range”被跳过，抛出专门异常让调用方回退单线程下载。
        var allRangeUnsupported = true;

        foreach (var source in healthySources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (unavailable.ContainsKey(source.Url))
                continue;

            try
            {
                var total = source.Length > 0
                    ? source.Length
                    : await ProbeAsync(source.Url, expectedSize, cancellationToken).ConfigureAwait(false);

                if (total <= 0)
                    throw new RangeNotSupportedException($"无法确定文件大小：{source.Url}");

                return await DownloadFromSourceAsync(
                        source.Url, orderedUrls, unavailable, destinationPath, total, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ChunkSourceException ex)
            {
                allRangeUnsupported = false;
                errors.Add($"{source.Url} => {ex.Message}");
                Log($"源内容异常，换下一个源：{ex.Message}");
                unavailable[source.Url] = 0;
                TryDelete(destinationPath);
            }
            catch (RangeNotSupportedException ex)
            {
                errors.Add($"{source.Url} => {ex.Message}");
                Log($"源不支持分片，换下一个源：{ex.Message}");
                unavailable[source.Url] = 0;
                TryDelete(destinationPath);
            }
            catch (Exception ex)
            {
                allRangeUnsupported = false;
                errors.Add($"{source.Url} => {ex.GetType().Name}: {ex.Message}");
                Log($"源下载失败，换下一个源：{ex.Message}");
                TryDelete(destinationPath);
            }
        }

        // 全部源都只是“不支持 Range”：交给调用方回退到单线程下载。
        if (allRangeUnsupported)
            throw new RangeNotSupportedException($"所有源都不支持 Range：{string.Join("；", errors)}");

        throw new ChunkDownloadException(
            $"分片下载失败（{errors.Count} 个源均不可用）：{string.Join("；", errors)}", errors);
    }

    /// <summary>
    /// 下载前健康检查：逐个 HEAD 候选源，检查状态码、Content-Type 与 Accept-Ranges。
    /// </summary>
    private async Task<List<SourceProbe>> CheckSourcesAsync(
        IReadOnlyList<string> urls,
        CancellationToken cancellationToken)
    {
        var result = new List<SourceProbe>();

        foreach (var url in urls)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(_options.RequestTimeout);

                using var head = new HttpRequestMessage(HttpMethod.Head, url);
                using var response = await _client
                    .SendAsync(head, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);

                var mediaType = response.Content.Headers.ContentType?.MediaType;
                var length = response.Content.Headers.ContentLength ?? 0;

                if (!response.IsSuccessStatusCode)
                {
                    Log($"健康检查跳过（HTTP {(int)response.StatusCode}）：{url}");
                    continue;
                }

                if (IsHtml(mediaType))
                {
                    Log($"健康检查跳过（返回 text/html，疑似镜像错误页）：{url}");
                    continue;
                }

                if (!IsAcceptableContentType(mediaType))
                {
                    Log($"健康检查跳过（Content-Type={mediaType ?? "未知"}）：{url}");
                    continue;
                }

                if (!AcceptsRanges(response))
                {
                    Log($"健康检查跳过（不支持 Accept-Ranges: bytes）：{url}");
                    continue;
                }

                Log($"健康检查通过：{url}");
                result.Add(new SourceProbe(url, length));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log($"健康检查失败（{url}）：{ex.Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// 探测单个源是否支持 Range 并返回总大小（用于健康检查全部失败后的回退）。
    /// </summary>
    private async Task<long> ProbeAsync(string url, long expectedSize, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_options.RequestTimeout);

            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await _client
                .SendAsync(head, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);

            var contentType = response.Content.Headers.ContentType?.MediaType;

            if (response.IsSuccessStatusCode && IsHtml(contentType))
                throw new ChunkSourceException($"HEAD 返回 {contentType}（疑似错误页）");

            if (response.IsSuccessStatusCode && AcceptsRanges(response))
            {
                var length = response.Content.Headers.ContentLength ?? expectedSize;
                if (length > 0)
                    return length;
            }

            if (response.IsSuccessStatusCode &&
                response.StatusCode is not HttpStatusCode.MethodNotAllowed and not HttpStatusCode.NotImplemented)
            {
                throw new RangeNotSupportedException("服务器未返回 Accept-Ranges: bytes");
            }
        }
        catch (ChunkSourceException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"HEAD 探测失败，改用 Range GET 探测：{ex.Message}");
        }

        using var probeTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeTimeoutCts.CancelAfter(_options.RequestTimeout);

        using var probe = new HttpRequestMessage(HttpMethod.Get, url);
        probe.Headers.Range = new RangeHeaderValue(0, 0);

        using var probeResponse = await _client
            .SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, probeTimeoutCts.Token)
            .ConfigureAwait(false);

        var probeContentType = probeResponse.Content.Headers.ContentType?.MediaType;
        if (IsHtml(probeContentType))
            throw new ChunkSourceException($"Range 探测返回 {probeContentType}（疑似错误页）");

        if (probeResponse.StatusCode != HttpStatusCode.PartialContent)
            throw new RangeNotSupportedException($"Range 探测返回 HTTP {(int)probeResponse.StatusCode}");

        var total = probeResponse.Content.Headers.ContentRange?.Length ?? expectedSize;
        if (total <= 0)
            throw new RangeNotSupportedException("无法从 Content-Range 取得文件大小");

        return total;
    }

    /// <summary>
    /// 预分配文件并按偏移并行写入各分片；每个分片可在候选源之间切换。
    /// </summary>
    private async Task<long> DownloadFromSourceAsync(
        string preferredUrl,
        IReadOnlyList<string> candidateUrls,
        ConcurrentDictionary<string, byte> unavailable,
        string destinationPath,
        long total,
        Action<long, long>? progress,
        CancellationToken cancellationToken)
    {
        var chunks = BuildChunks(total);
        Log($"分片下载：{Path.GetFileName(destinationPath)}，{total / 1024 / 1024.0:0.0} MB，" +
            $"{chunks.Count} 片 × {_options.ChunkSizeBytes / 1024 / 1024.0:0.0} MB，并发 {_options.MaxParallelChunks}，" +
            $"首选源 {preferredUrl}");

        await using (var preallocate = new FileStream(
                         destinationPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            preallocate.SetLength(total);
        }

        var counter = new ByteCounter();
        using var gate = new SemaphoreSlim(_options.MaxParallelChunks, _options.MaxParallelChunks);

        var tasks = chunks.Select(async chunk =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await DownloadChunkAsync(
                        chunk, candidateUrls, unavailable, destinationPath, total, counter, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception)
        {
            TryDelete(destinationPath);
            throw;
        }

        var length = new FileInfo(destinationPath).Length;
        if (length != total)
        {
            TryDelete(destinationPath);
            throw new InvalidOperationException($"分片合并后大小不一致（期望 {total}，实际 {length}）");
        }

        Log($"分片下载完成：{Path.GetFileName(destinationPath)}（{length / 1024} KB）");
        return length;
    }

    /// <summary>
    /// 下载单个分片：按候选源顺序尝试，遇到 text/html / 403 立刻换源重试当前分片。
    /// </summary>
    private async Task DownloadChunkAsync(
        ChunkRange chunk,
        IReadOnlyList<string> candidateUrls,
        ConcurrentDictionary<string, byte> unavailable,
        string destinationPath,
        long total,
        ByteCounter counter,
        Action<long, long>? progress,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        foreach (var url in candidateUrls)
        {
            if (unavailable.ContainsKey(url))
                continue;

            for (var attempt = 1; attempt <= _options.MaxRetriesPerChunk; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await DownloadChunkOnceAsync(url, chunk, destinationPath, counter, total, progress, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (ChunkSourceException ex)
                {
                    // 源不可用：标记本次下载不可用，立即换下一个源重试当前分片。
                    lastError = ex;
                    unavailable[url] = 0;
                    Log($"分片 {chunk.Index} 源不可用（{url}），切换下一个源：{ex.Message}");
                    break;
                }
                catch (RangeNotSupportedException ex)
                {
                    lastError = ex;
                    unavailable[url] = 0;
                    Log($"分片 {chunk.Index} 源不支持 Range（{url}），切换下一个源：{ex.Message}");
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    Log($"分片 {chunk.Index} 第 {attempt} 次失败（{url}）：{ex.Message}");

                    if (attempt < _options.MaxRetriesPerChunk)
                    {
                        await Task.Delay(
                                TimeSpan.FromMilliseconds(_options.RetryDelay.TotalMilliseconds * attempt),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }
        }

        throw new IOException(
            $"分片 {chunk.Index} 在所有候选源上都失败：{lastError?.Message}",
            lastError);
    }

    /// <summary>
    /// 用 Range 请求下载一个分片并写入对应偏移。
    /// </summary>
    private async Task DownloadChunkOnceAsync(
        string url,
        ChunkRange chunk,
        string destinationPath,
        ByteCounter counter,
        long total,
        Action<long, long>? progress,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.RequestTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(chunk.Start, chunk.End);

        using var response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
            .ConfigureAwait(false);

        var contentType = response.Content.Headers.ContentType?.MediaType;

        // 镜像常见的故障表现：200/403 + text/html 错误页 → 判定该源本次下载不可用。
        if (IsHtml(contentType))
            throw new ChunkSourceException($"分片 {chunk.Index} 返回 {contentType}（疑似错误页）");

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            throw new ChunkSourceException($"分片 {chunk.Index} 返回 HTTP {(int)response.StatusCode}");

        if (response.StatusCode != HttpStatusCode.PartialContent)
            throw new RangeNotSupportedException(
                $"分片 {chunk.Index} 返回 HTTP {(int)response.StatusCode}（期望 206）");

        await using var source = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
        await using var target = new FileStream(
            destinationPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        target.Seek(chunk.Start, SeekOrigin.Begin);

        var buffer = new byte[81920];
        long written = 0;
        int read;

        while (written < chunk.Length &&
               (read = await source.ReadAsync(buffer, timeoutCts.Token).ConfigureAwait(false)) > 0)
        {
            var size = (int)Math.Min(read, chunk.Length - written);
            await target.WriteAsync(buffer.AsMemory(0, size), timeoutCts.Token).ConfigureAwait(false);
            written += size;
            counter.Add(size);
            ReportProgress(progress, counter, total);
        }

        if (written != chunk.Length)
            throw new IOException($"分片 {chunk.Index} 长度不匹配（期望 {chunk.Length}，实际 {written}）");
    }

    /// <summary>
    /// 按固定分片大小切分（默认 4 MB）。
    /// </summary>
    private List<ChunkRange> BuildChunks(long total)
    {
        var chunks = new List<ChunkRange>();
        var size = _options.ChunkSizeBytes;
        var index = 0;

        for (long start = 0; start < total; start += size)
        {
            var end = Math.Min(start + size, total) - 1;
            chunks.Add(new ChunkRange(index++, start, end));
        }

        return chunks;
    }

    /// <summary>
    /// 进度上报（限流，避免高频跨线程刷新 UI）。
    /// </summary>
    private void ReportProgress(Action<long, long>? progress, ByteCounter counter, long total)
    {
        if (progress is null)
            return;

        if (DateTime.UtcNow - _lastReport < _options.ProgressThrottle)
            return;

        _lastReport = DateTime.UtcNow;
        progress(counter.Value, total);
    }

    private DateTime _lastReport = DateTime.UtcNow;

    /// <summary>拼接 Base URL 与相对路径。</summary>
    private static string Combine(string baseUrl, string relativePath)
        => baseUrl.TrimEnd('/') + "/" + relativePath.TrimStart('/');

    /// <summary>判断响应是否声明支持 Range。</summary>
    private static bool AcceptsRanges(HttpResponseMessage response)
        => response.Headers.AcceptRanges.Any(v => v.Equals("bytes", StringComparison.OrdinalIgnoreCase));

    /// <summary>判断响应是否是 HTML 错误页。</summary>
    private static bool IsHtml(string? mediaType)
        => !string.IsNullOrWhiteSpace(mediaType) &&
           mediaType.Contains("html", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 判断 Content-Type 是否是“可下载的二进制内容”。
    /// </summary>
    private static bool IsAcceptableContentType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
            return false;

        return mediaType.ToLowerInvariant() switch
        {
            "application/octet-stream" => true,
            "application/java-archive" => true,
            "application/x-java-archive" => true,
            "application/zip" => true,
            "binary/octet-stream" => true,
            _ => false,
        };
    }

    /// <summary>尝试删除文件。</summary>
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

    /// <summary>写日志。</summary>
    private void Log(string message)
    {
        Debug.WriteLine($"[分片] {message}");

        try
        {
            LogWritten?.Invoke(message);
        }
        catch (Exception)
        {
            // 忽略订阅者异常。
        }
    }

    /// <summary>一个通过健康检查的源。</summary>
    private sealed record SourceProbe(string Url, long Length);

    /// <summary>跨线程累加的字节计数器。</summary>
    private sealed class ByteCounter
    {
        private long _value;

        /// <summary>累加并返回当前值。</summary>
        public long Add(long delta) => Interlocked.Add(ref _value, delta);

        /// <summary>当前值。</summary>
        public long Value => Interlocked.Read(ref _value);
    }

    /// <summary>一个分片：序号 + 起止偏移（含）。</summary>
    private sealed record ChunkRange(int Index, long Start, long End)
    {
        /// <summary>分片长度。</summary>
        public long Length => End - Start + 1;
    }
}

/// <summary>
/// 分片下载参数。
/// </summary>
public sealed class ChunkedFileDownloaderOptions
{
    /// <summary>启用分片的文件大小阈值（默认 10 MB，严格大于才启用）。</summary>
    public long ThresholdBytes { get; init; } = ChunkedFileDownloader.DefaultThresholdBytes;

    /// <summary>固定分片大小（默认 4 MB）。</summary>
    public long ChunkSizeBytes { get; init; } = 4L * 1024 * 1024;

    /// <summary>最大并发分片数（默认 6）。</summary>
    public int MaxParallelChunks { get; init; } = 6;

    /// <summary>单次请求（HEAD / 分片 GET）的超时时间（默认 30 秒）。</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>单个分片的最大重试次数（含首次）。</summary>
    public int MaxRetriesPerChunk { get; init; } = 3;

    /// <summary>重试的基础延迟。</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>进度回调的最小间隔（默认 100ms，测试里可设为 0）。</summary>
    public TimeSpan ProgressThrottle { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>是否在下载前做源健康检查（默认开启）。</summary>
    public bool HealthCheckEnabled { get; init; } = true;
}

/// <summary>
/// 源返回了不可用内容（例如镜像返回 text/html 错误页、403/404）；不重试，直接换下一个源。
/// </summary>
public sealed class ChunkSourceException : Exception
{
    /// <summary>
    /// 创建异常。
    /// </summary>
    /// <param name="message">说明。</param>
    public ChunkSourceException(string message) : base(message)
    {
    }
}

/// <summary>
/// 服务器不支持 Range 时抛出；调用方据此回退到单线程下载。
/// </summary>
public sealed class RangeNotSupportedException : Exception
{
    /// <summary>
    /// 创建异常。
    /// </summary>
    /// <param name="message">说明。</param>
    public RangeNotSupportedException(string message) : base(message)
    {
    }
}

/// <summary>
/// 所有分片源都失败时抛出，携带每个源的失败原因。
/// </summary>
public sealed class ChunkDownloadException : Exception
{
    /// <summary>
    /// 创建异常。
    /// </summary>
    /// <param name="message">说明。</param>
    /// <param name="errors">各源失败原因。</param>
    public ChunkDownloadException(string message, IReadOnlyList<string> errors) : base(message)
        => Errors = errors;

    /// <summary>各源失败原因。</summary>
    public IReadOnlyList<string> Errors { get; }
}
