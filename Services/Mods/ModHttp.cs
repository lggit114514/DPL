using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DPL.Services.Mods;

/// <summary>
/// 模组下载用的 HTTP 封装：统一处理超时、指数退避重试、官方源 → 镜像源的顺序回退，
/// 并把每次尝试的地址与失败原因写进日志，方便排查网络问题。
/// </summary>
public sealed class ModHttp : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _client;
    private readonly ModConfig _config;
    private readonly bool _ownsClient;
    private readonly ChunkedFileDownloader _chunked;

    /// <summary>日志回调（可能在后台线程触发）。</summary>
    public event Action<string>? LogWritten;

    /// <summary>
    /// 最近一次响应里 MCIM 提供的缓存同步时间（<c>sync_at</c>）；没有该字段时为 null。
    /// </summary>
    public DateTimeOffset? LastSyncAt { get; private set; }

    /// <summary>
    /// 创建 HTTP 封装。
    /// </summary>
    /// <param name="config">模组下载配置。</param>
    /// <param name="client">可注入的 HttpClient（单元测试用）；为 null 时内部创建。</param>
    public ModHttp(ModConfig config, HttpClient? client = null)
    {
        _config = config;

        if (client is not null)
        {
            _client = client;
            _ownsClient = false;
        }
        else
        {
            _client = new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                // 连接超时单独设置：被墙的 CDN 只等 10 秒，而不是每次连接都拖 20 秒。
                ConnectTimeout = TimeSpan.FromSeconds(10),
            })
            {
                // 单次请求超时 30 秒（保留原有重试逻辑；分片每片只有 4 MB，30 秒足够）。
                Timeout = TimeSpan.FromSeconds(30),
            };

            _client.DefaultRequestHeaders.UserAgent.ParseAdd("DPL/1.0");
            _ownsClient = true;
        }

        // 大文件分片下载器（与平台 API 无关，独立类）。
        _chunked = new ChunkedFileDownloader(
            _client,
            new ChunkedFileDownloaderOptions
            {
                ThresholdBytes = config.ChunkThresholdBytes,
                ChunkSizeBytes = config.ChunkSizeBytes,
                MaxParallelChunks = config.ChunkParallelism,
            });
        _chunked.LogWritten += message => Log("[分片] " + message);
    }

    /// <summary>
    /// GET 一个 JSON 资源：先官方地址，失败后尝试镜像地址；每个源内部最多重试 <see cref="ModConfig.MaxAttempts"/> 次。
    /// </summary>
    /// <typeparam name="T">响应类型。</typeparam>
    /// <param name="officialUrl">官方地址。</param>
    /// <param name="mirrorUrl">镜像地址（可为 null 表示没有镜像）。</param>
    /// <param name="sourceName">来源名称，仅用于日志。</param>
    /// <param name="apiKey">需要鉴权的平台传入 API Key。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<ModHttpJsonResult<T>> GetJsonAsync<T>(
        string primaryUrl,
        string? fallbackUrl,
        string sourceName,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        // 主源是 MCIM 镜像，备源是官方；两者请求路径完全一致，只换 Base URL。
        var candidates = new List<(string Kind, string Url)> { ("MCIM 镜像", primaryUrl) };
        if (!string.IsNullOrWhiteSpace(fallbackUrl) && fallbackUrl != primaryUrl)
            candidates.Add(("官方源", fallbackUrl));

        var errors = new List<string>();

        foreach (var (kind, url) in candidates)
        {
            var attempt = await SendWithRetryAsync(
                url,
                sourceName,
                kind,
                apiKey,
                _config.ApiTimeout,
                cancellationToken,
                ReadJsonPayloadAsync<T>).ConfigureAwait(false);

            if (attempt.Value is { } payload && payload.Value is not null)
            {
                if (payload.SyncAt is not null)
                {
                    LastSyncAt = payload.SyncAt;
                    Log($"{sourceName} 缓存同步时间（sync_at）：{payload.SyncAt:yyyy-MM-dd HH:mm:ss}");
                }

                return new ModHttpJsonResult<T>(
                    payload.Value,
                    null,
                    kind == "官方源",
                    attempt.Attempts,
                    payload.SyncAt);
            }

            errors.Add(attempt.Error ?? "未知错误");
        }

        var message = $"获取{sourceName}数据失败：{string.Join("；", errors)}";
        Log(message);
        return new ModHttpJsonResult<T>(default, message, false, 0, null);
    }

    /// <summary>
    /// 探测某个地址是否可达（短超时 GET，允许任意状态码，只要拿到响应就算可达）。
    /// </summary>
    /// <param name="url">探测地址。</param>
    /// <param name="apiKey">需要鉴权的平台传入 API Key。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<bool> ProbeAsync(string url, string? apiKey, CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_config.ProbeTimeout);

            using var request = CreateRequest(HttpMethod.Get, url, apiKey);
            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            // 401/403 说明网络通、只是没有 Key，也算“可达”（由上层决定是否可用）。
            var reachable = response.IsSuccessStatusCode ||
                            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

            Log($"探测 {url} => {(int)response.StatusCode}，{(reachable ? "可达" : "不可达")}");
            return reachable;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log($"探测 {url} => 超时");
            return false;
        }
        catch (Exception ex)
        {
            Log($"探测 {url} => 失败：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 下载文件到指定路径：先试官方 CDN，全部失败后才走镜像（镜像稳定性差，只在必要时用）。
    /// </summary>
    /// <param name="officialUrls">官方候选地址（可能有多个，例如 edge / mediafilez）。</param>
    /// <param name="mirrorUrls">镜像候选地址。</param>
    /// <param name="destinationPath">保存路径。</param>
    /// <param name="progress">进度回调（已下载字节、总字节）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<ModFileDownloadResult> DownloadFileAsync(
        IReadOnlyList<string> primaryUrls,
        IReadOnlyList<string> fallbackUrls,
        string destinationPath,
        Action<long, long>? progress,
        CancellationToken cancellationToken = default,
        long expectedBytes = 0)
    {
        var candidates = new List<(string Kind, string Url)>();
        foreach (var url in primaryUrls)
            candidates.Add((LabelFor(url), url));
        foreach (var url in fallbackUrls)
            candidates.Add((LabelFor(url), url));

        var errors = new List<string>();

        // 大文件优先分片下载（官方 → BMCLAPI → MCIM）；所有源都不支持 Range 时回退到下面的单线程逻辑。
        if (_chunked.ShouldChunk(expectedBytes) && candidates.Count > 0)
        {
            var relativePath = _config.GetCdnRelativePath(candidates[0].Url);
            var baseUrls = _config.GetChunkedBaseUrls(candidates[0].Url);

            try
            {
                var bytes = await _chunked
                    .DownloadAsync(baseUrls, relativePath, destinationPath, expectedBytes, progress, cancellationToken)
                    .ConfigureAwait(false);

                var invalid = ValidateDownloadedFile(destinationPath, expectedBytes);
                if (invalid is null)
                {
                    Log($"分片下载成功：{Path.GetFileName(destinationPath)}");
                    return new ModFileDownloadResult(destinationPath, bytes, false, errors);
                }

                errors.Add($"分片 => {invalid}");
                Log($"分片下载内容校验失败：{invalid}");
                TryDelete(destinationPath);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (RangeNotSupportedException ex)
            {
                errors.Add($"分片 => {ex.Message}");
                Log("所有源都不支持分片或大小未知，改用单线程下载");
            }
            catch (Exception ex)
            {
                errors.Add($"分片 => {ex.GetType().Name}: {ex.Message}");
                Log($"分片下载失败，改用单线程下载：{ex.Message}");
            }
        }

        for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            var (kind, url) = candidates[candidateIndex];
            var usedFallback = candidateIndex > 0;

            for (var attempt = 1; attempt <= _config.MaxAttempts; attempt++)
            {
                var tempPath = destinationPath + ".part";
                try
                {
                    Log($"下载（{kind}，第 {attempt} 次）：{url}");

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(_config.DownloadTimeout);

                    using var request = CreateRequest(HttpMethod.Get, url, null);
                    using var response = await _client
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                        .ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        errors.Add($"{kind} {url} => HTTP {(int)response.StatusCode}");
                        if (IsTransient(response.StatusCode) && attempt < _config.MaxAttempts)
                        {
                            await DelayAsync(attempt, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        break;
                    }

                    var total = response.Content.Headers.ContentLength ?? 0;
                    var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                    long written = 0;
                    var header = new byte[4];
                    var headerLength = 0;

                    await using (var source = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false))
                    await using (var target = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[81920];
                        int read;
                        while ((read = await source.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
                        {
                            if (headerLength < 4)
                            {
                                var copy = Math.Min(4 - headerLength, read);
                                Array.Copy(buffer, 0, header, headerLength, copy);
                                headerLength += copy;
                            }

                            await target.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
                            written += read;
                            progress?.Invoke(written, total);
                        }
                    }

                    // 镜像/被劫持时经常返回 200 + HTML 错误页，这里必须校验内容，避免把错误页当 jar 存下来。
                    var invalidReason = ValidateDownloadedFile(tempPath, header, written, contentType, expectedBytes, kind);
                    if (invalidReason is not null)
                    {
                        errors.Add($"{kind} {url} => {invalidReason}");
                        Log($"下载内容校验失败（{kind}）：{invalidReason}");
                        TryDelete(tempPath);
                        break;
                    }

                    if (File.Exists(destinationPath))
                        File.Delete(destinationPath);
                    File.Move(tempPath, destinationPath);

                    Log($"下载完成（{kind}）：{Path.GetFileName(destinationPath)}，{written / 1024} KB");
                    return new ModFileDownloadResult(destinationPath, written, usedFallback, errors);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    TryDelete(tempPath);
                    throw;
                }
                catch (Exception ex)
                {
                    TryDelete(tempPath);
                    errors.Add($"{kind} {url} => {ex.GetType().Name}: {ex.Message}");

                    if (attempt < _config.MaxAttempts)
                    {
                        Log($"下载失败，准备第 {attempt + 1} 次重试：{ex.Message}");
                        await DelayAsync(attempt, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    Log($"下载失败（{kind}）：{ex.Message}");
                    break;
                }
            }
        }

        throw new ModDownloadException(
            $"下载失败（官方源与镜像都不可用）：{string.Join("；", errors.TakeLast(4))}", errors);
    }

    /// <summary>
    /// 释放内部的 HttpClient（注入的客户端不会释放）。
    /// </summary>
    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();
    }

    /// <summary>
    /// 在单个地址上做“带退避重试”的请求并反序列化。
    /// </summary>
    private async Task<AttemptResult<T>> SendWithRetryAsync<T>(
        string url,
        string sourceName,
        string kind,
        string? apiKey,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Func<Stream, CancellationToken, Task<T?>> read)
    {
        var errors = new List<string>();
        var stopwatch = Stopwatch.StartNew();

        for (var attempt = 1; attempt <= _config.MaxAttempts; attempt++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(timeout);

                Log($"{sourceName}（{kind}）请求：{url}");

                using var request = CreateRequest(HttpMethod.Get, url, apiKey);
                using var response = await _client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var reason = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                    errors.Add(reason);
                    Log($"{sourceName}（{kind}）失败：{reason}");

                    if (IsTransient(response.StatusCode) && attempt < _config.MaxAttempts)
                    {
                        await DelayAsync(attempt, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    return new AttemptResult<T>(default, reason, attempt);
                }

                await using var stream = await response.Content
                    .ReadAsStreamAsync(cts.Token)
                    .ConfigureAwait(false);

                var value = await read(stream, cts.Token).ConfigureAwait(false);

                Log($"{sourceName}（{kind}）成功（{stopwatch.Elapsed.TotalSeconds:0.0} 秒）");
                return new AttemptResult<T>(value, null, attempt);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                errors.Add($"超时（{timeout.TotalSeconds:0} 秒）");
                Log($"{sourceName}（{kind}）超时（{timeout.TotalSeconds:0} 秒）");

                if (attempt < _config.MaxAttempts)
                {
                    await DelayAsync(attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{ex.GetType().Name}: {ex.Message}");
                Log($"{sourceName}（{kind}）失败：{ex.GetType().Name}: {ex.Message}");

                if (attempt < _config.MaxAttempts)
                {
                    await DelayAsync(attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }
        }

        return new AttemptResult<T>(default, string.Join("；", errors), _config.MaxAttempts);
    }

    /// <summary>
    /// 解析 JSON，并读取 MCIM 返回的 <c>sync_at</c>（缓存同步时间）。
    /// </summary>
    /// <typeparam name="T">业务数据类型。</typeparam>
    /// <param name="stream">响应流。</param>
    /// <param name="token">取消令牌。</param>
    private static async Task<JsonPayload<T>?> ReadJsonPayloadAsync<T>(Stream stream, CancellationToken token)
    {
        using var document = await JsonDocument
            .ParseAsync(stream, cancellationToken: token)
            .ConfigureAwait(false);

        DateTimeOffset? syncAt = null;

        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("sync_at", out var syncElement))
        {
            syncAt = ParseSyncAt(syncElement);
        }

        var value = document.RootElement.Deserialize<T>(SerializerOptions);
        return value is null ? null : new JsonPayload<T>(value, syncAt);
    }

    /// <summary>
    /// 解析 <c>sync_at</c>：MCIM 可能返回 ISO 字符串，也可能返回 Unix 时间戳（秒或毫秒）。
    /// </summary>
    /// <param name="element">JSON 元素。</param>
    private static DateTimeOffset? ParseSyncAt(JsonElement element)
    {
        try
        {
            if (element.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(
                    element.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                return parsed;
            }

            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var epoch))
            {
                // 大于 1e12 视为毫秒，否则按秒处理。
                return epoch > 1_000_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                    : DateTimeOffset.FromUnixTimeSeconds(epoch);
            }
        }
        catch (Exception)
        {
            // 解析失败就当没有这个字段。
        }

        return null;
    }

    /// <summary>
    /// 创建请求，并按需附加 API Key。
    /// </summary>
    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string? apiKey)
    {
        var request = new HttpRequestMessage(method, url);

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            // CurseForge 使用 x-api-key；如果以后接入别的平台，可以在这里扩展。
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        }

        return request;
    }

    /// <summary>
    /// 指数退避延迟。
    /// </summary>
    private Task DelayAsync(int attempt, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(_config.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
        return Task.Delay(delay, cancellationToken);
    }

    /// <summary>
    /// 判断状态码是否值得重试（超时、限流、服务端错误）。
    /// </summary>
    private static bool IsTransient(HttpStatusCode status)
        => status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
           or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
           or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    /// <summary>
    /// 校验已经落盘的文件（读文件头 + 核对大小），用于分片合并后的检查。
    /// </summary>
    /// <param name="path">文件路径。</param>
    /// <param name="expectedBytes">期望大小（0 表示未知）。</param>
    private static string? ValidateDownloadedFile(string path, long expectedBytes)
    {
        if (!File.Exists(path))
            return "下载文件不存在";

        var length = new FileInfo(path).Length;
        if (length <= 0)
            return "下载内容为空";

        var header = new byte[(int)Math.Min(4, length)];
        try
        {
            using var stream = File.OpenRead(path);
            var read = stream.Read(header, 0, header.Length);
            if (read < header.Length)
                header = header[..read];
        }
        catch (Exception ex)
        {
            return $"读取下载文件失败：{ex.Message}";
        }

        return ValidateDownloadedFile(path, header, length, string.Empty, expectedBytes, "分片");
    }

    /// <summary>
    /// 校验下载内容是否是有效的 jar（zip），并核对大小。
    /// </summary>
    /// <returns>不合格时返回原因；合格返回 null。</returns>
    private static string? ValidateDownloadedFile(
        string path,
        byte[] header,
        long written,
        string contentType,
        long expectedBytes,
        string kind)
    {
        if (written <= 0)
            return "下载内容为空";

        if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
            contentType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
            contentType.Contains("text", StringComparison.OrdinalIgnoreCase))
        {
            return $"返回的是 {contentType} 而不是 jar（可能是镜像错误页）";
        }

        // zip / jar 的本地文件头是 "PK\x03\x04"（空压缩包是 "PK\x05\x06"）。
        var isZip = header.Length >= 4 &&
                    header[0] == 0x50 && header[1] == 0x4B &&
                    (header[2] == 0x03 || header[2] == 0x05) &&
                    (header[3] == 0x04 || header[3] == 0x06);

        if (!isZip)
            return "下载内容不是有效的 jar（文件头不是 zip）";

        if (expectedBytes > 0 && written != expectedBytes)
            return $"文件大小不匹配（期望 {expectedBytes} 字节，实际 {written} 字节）";

        return null;
    }

    /// <summary>
    /// 尝试删除临时文件。
    /// </summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
            // 忽略临时文件清理失败。
        }
    }

    /// <summary>
    /// 供上层（路由器 / 安装服务）记录一条日志。
    /// </summary>
    /// <param name="message">日志内容。</param>
    public void LogMessage(string message) => Log(message);

    /// <summary>
    /// 根据 URL 判断来源标签（仅用于日志）。
    /// </summary>
    private static string LabelFor(string url)
        => url.Contains("mcimirror", StringComparison.OrdinalIgnoreCase) ? "MCIM 镜像"
            : url.Contains("bmclapi", StringComparison.OrdinalIgnoreCase) ? "BMCLAPI 镜像"
            : "官方源";

    /// <summary>
    /// 写日志：调试输出 + 订阅者。
    /// </summary>
    private void Log(string message)
    {
        Debug.WriteLine($"[模组] {message}");

        try
        {
            LogWritten?.Invoke(message);
        }
        catch (Exception)
        {
            // 日志订阅者异常不能影响下载逻辑。
        }
    }

    /// <summary>单次请求的重试结果。</summary>
    private sealed record AttemptResult<T>(T? Value, string? Error, int Attempts);
}

/// <summary>
/// JSON 请求结果：值、错误信息、是否回退了官方源、尝试次数与缓存同步时间。
/// </summary>
/// <typeparam name="T">响应类型。</typeparam>
/// <param name="Value">反序列化后的结果。</param>
/// <param name="Error">失败原因（成功时为 null）。</param>
/// <param name="UsedFallback">是否因为镜像失败而回退了官方源。</param>
/// <param name="Attempts">尝试次数。</param>
/// <param name="SyncAt">响应中的 <c>sync_at</c>（MCIM 缓存同步时间），没有则为 null。</param>
public sealed record ModHttpJsonResult<T>(
    T? Value,
    string? Error,
    bool UsedFallback,
    int Attempts,
    DateTimeOffset? SyncAt)
{
    /// <summary>是否成功。</summary>
    public bool Success => Value is not null;
}

/// <summary>
/// 带 <c>sync_at</c> 的 JSON 载荷。
/// </summary>
/// <typeparam name="T">业务数据类型。</typeparam>
/// <param name="Value">反序列化结果。</param>
/// <param name="SyncAt">缓存同步时间。</param>
internal sealed record JsonPayload<T>(T? Value, DateTimeOffset? SyncAt);

/// <summary>
/// 文件下载结果。
/// </summary>
/// <param name="Path">保存的完整路径。</param>
/// <param name="Bytes">文件大小。</param>
/// <param name="UsedFallback">是否因为镜像失败而回退了官方 CDN。</param>
/// <param name="Errors">过程中记录的错误。</param>
public sealed record ModFileDownloadResult(string Path, long Bytes, bool UsedFallback, IReadOnlyList<string> Errors);

/// <summary>
/// 下载失败异常，携带每一次尝试的错误，便于界面展示具体原因。
/// </summary>
public sealed class ModDownloadException : Exception
{
    /// <summary>
    /// 创建下载异常。
    /// </summary>
    /// <param name="message">错误说明。</param>
    /// <param name="errors">每次尝试的错误详情。</param>
    public ModDownloadException(string message, IReadOnlyList<string> errors) : base(message)
        => Errors = errors;

    /// <summary>每次尝试的错误详情。</summary>
    public IReadOnlyList<string> Errors { get; }
}
