using Downloader;

namespace DPL.Services.Mods;

/// <summary>
/// 基于 bezzad/Downloader 的多线程分片下载器。
/// 负责在多个候选 URL 之间回退，并把实时速度回传给调用方。
/// </summary>
public sealed class DownloaderFileDownloader
{
    private readonly HttpClient _client;
    private readonly int _threadCount;

    /// <summary>创建下载器。</summary>
    /// <param name="client">复用的 HttpClient（测试时可注入 mock handler）。</param>
    /// <param name="threadCount">分片线程数，自动限制在 1-32。</param>
    public DownloaderFileDownloader(HttpClient client, int threadCount)
    {
        _client = client;
        _threadCount = Math.Clamp(threadCount, 1, 32);
    }

    /// <summary>日志回调（可能在后台线程触发）。</summary>
    public event Action<string>? LogWritten;

    /// <summary>
    /// 依次尝试候选 URL 下载同一个文件；某个源失败时自动切换下一个。
    /// </summary>
    /// <param name="urls">候选下载地址（按优先级排列）。</param>
    /// <param name="destinationPath">最终保存路径。</param>
    /// <param name="expectedBytes">已知文件大小；0 表示未知。</param>
    /// <param name="progress">进度回调（已接收字节、总字节）。</param>
    /// <param name="speedProgress">实时速度回调（字节/秒）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>下载结果（文件大小 + 是否使用了备用源）。</returns>
    public async Task<DownloaderFileResult> DownloadAsync(
        IReadOnlyList<string> urls,
        string destinationPath,
        long expectedBytes,
        Action<long, long>? progress,
        Action<double>? speedProgress,
        CancellationToken cancellationToken)
    {
        var candidates = urls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0)
            throw new ChunkDownloadException("没有可用的下载源", Array.Empty<string>());

        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (string.IsNullOrWhiteSpace(directory))
            directory = AppContext.BaseDirectory;

        Directory.CreateDirectory(directory);
        var fileName = Path.GetFileName(destinationPath);
        var errors = new List<string>();
        var candidateIndex = 0;

        foreach (var url in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var usedFallback = candidateIndex++ > 0;
            DeleteFile(destinationPath);
            DeleteFile(destinationPath + ".download");

            try
            {
                var configuration = new DownloadConfiguration
                {
                    // 用户设置直接映射为分片数；ParallelDownload 打开后各分片并发下载。
                    ChunkCount = _threadCount,
                    ParallelDownload = true,
                    ParallelCount = _threadCount,
                    MaxTryAgainOnFailure = 2,
                    HttpClientTimeout = 30_000,
                    CheckDiskSizeBeforeDownload = false,
                    ClearPackageOnCompletionWithFailure = true,
                    // 复用现有 HttpClient：保留 UA、代理、超时与测试注入的 handler。
                    CustomHttpClientFactory = () => _client,
                };

                var download = DownloadBuilder.New()
                    .WithUrl(url)
                    .WithDirectory(directory)
                    .WithFileName(fileName)
                    .WithConfiguration(configuration)
                    .Build();

                Exception? failure = null;
                var cancelled = false;

                download.DownloadProgressChanged += (_, e) =>
                {
                    var total = e.TotalBytesToReceive > 0
                        ? e.TotalBytesToReceive
                        : expectedBytes;

                    progress?.Invoke(e.ReceivedBytesSize, total);
                    speedProgress?.Invoke(e.BytesPerSecondSpeed);
                };

                download.DownloadFileCompleted += (_, e) =>
                {
                    failure = e.Error;
                    cancelled = e.Cancelled;
                };

                using var registration = cancellationToken.Register(() =>
                {
                    try
                    {
                        download.Stop();
                    }
                    catch (Exception)
                    {
                        // 取消期间停止失败不影响最终状态判断。
                    }
                });

                Log($"使用 Downloader（{_threadCount} 线程）：{url}");
                await download.StartAsync().ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();

                if (cancelled)
                    throw new OperationCanceledException("下载已取消", cancellationToken);

                if (failure is not null)
                    throw failure;

                if (!File.Exists(destinationPath))
                    throw new IOException("下载完成后找不到目标文件");

                var length = new FileInfo(destinationPath).Length;
                if (expectedBytes > 0 && length != expectedBytes)
                {
                    throw new IOException(
                        $"文件大小不匹配（期望 {expectedBytes} 字节，实际 {length} 字节）");
                }

                return new DownloaderFileResult(length, usedFallback);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                DeleteFile(destinationPath);
                DeleteFile(destinationPath + ".download");
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{url} => {ex.GetType().Name}: {ex.Message}");
                Log($"源下载失败，切换下一个：{ex.Message}");
                DeleteFile(destinationPath);
                DeleteFile(destinationPath + ".download");
            }
        }

        throw new ChunkDownloadException(
            $"所有下载源都失败（{errors.Count} 个）：{string.Join("；", errors)}",
            errors);
    }

    private void Log(string message)
    {
        try
        {
            LogWritten?.Invoke(message);
        }
        catch (Exception)
        {
            // 日志订阅者异常不能中断下载。
        }
    }

    private static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
            // 清理失败时留给下一次覆盖。
        }
    }
}

/// <summary>
/// Downloader 下载结果：文件大小 + 是否使用了备用源。
/// </summary>
/// <param name="Bytes">写入文件的字节数。</param>
/// <param name="UsedFallback">是否在第一个源失败后使用了备用源。</param>
public sealed record DownloaderFileResult(long Bytes, bool UsedFallback);
