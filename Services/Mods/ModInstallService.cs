using System.Collections.Concurrent;
using DPL.Models.Mods;

namespace DPL.Services.Mods;

/// <summary>
/// 模组文件安装服务：按解析结果逐个下载到实例的 mods 目录，
/// 多文件时按总字节数汇报整体进度；文件下载先直连官方 CDN，失败后才走镜像。
/// </summary>
public sealed class ModInstallService
{
    private readonly ModSourceRouter _router;
    private readonly ModHttp _http;
    private readonly ModConfig _config;

    /// <summary>
    /// 创建安装服务。
    /// </summary>
    /// <param name="router">平台路由器（用于解析下载地址）。</param>
    /// <param name="http">HTTP 封装（下载与镜像回退）。</param>
    /// <param name="config">模组下载配置。</param>
    public ModInstallService(ModSourceRouter router, ModHttp http, ModConfig config)
    {
        _router = router;
        _http = http;
        _config = config;

        // 把 HTTP 层的日志转给界面（例如“直连失败，切换镜像”这类信息）。
        _http.LogWritten += message => LogWritten?.Invoke(message);
    }

    /// <summary>日志回调。</summary>
    public event Action<string>? LogWritten;

    /// <summary>
    /// 下载一组文件到指定 mods 目录。
    /// </summary>
    /// <param name="files">待下载文件（主模组 + 依赖）。</param>
    /// <param name="modsDirectory">目标 mods 目录。</param>
    /// <param name="progress">进度回调。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<ModInstallReport> InstallAsync(
        IReadOnlyList<ModResolvedFile> files,
        string modsDirectory,
        IProgress<ModDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(modsDirectory);

        var saved = new ConcurrentBag<string>();
        var skipped = new ConcurrentBag<string>();
        var failures = new ConcurrentBag<string>();

        // 总字节数未知时按文件数估算进度。
        var totalBytes = files.Sum(f => Math.Max(f.Version.FileSize, 0));

        // [0]=已传输字节（成功下载中的增量累加） [1]=跳过文件的字节 [2]=失败文件的字节
        var counters = new long[3];

        // 文件级并行：多个模组 / 依赖同时下载（并行数见 ModConfig.FileParallelism，默认 32）。
        using var gate = new SemaphoreSlim(Math.Max(1, _config.FileParallelism), Math.Max(1, _config.FileParallelism));

        long Current()
            => Interlocked.Read(ref counters[0]) + Interlocked.Read(ref counters[1]) + Interlocked.Read(ref counters[2]);

        var tasks = files.Select(async (file, index) =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileName = file.Version.FileName;
                var destination = Path.Combine(modsDirectory, fileName);
                var fileIndex = index + 1;
                var fileSize = Math.Max(file.Version.FileSize, 0);

                if (File.Exists(destination))
                {
                    skipped.Add(destination);
                    Interlocked.Add(ref counters[1], fileSize);
                    Log($"{fileName} 已存在，跳过");
                    ReportProgress(progress, fileName, fileIndex, files.Count, Current(), totalBytes, "已存在，跳过");
                    return;
                }

                var stage = file.IsDependency ? $"下载前置依赖 {file.ProjectName}" : $"下载 {file.ProjectName}";
                ReportProgress(progress, fileName, fileIndex, files.Count, Current(), totalBytes, stage);

                var url = file.DownloadUrl;
                if (string.IsNullOrWhiteSpace(url))
                {
                    var source = _router.GetSource(file.Source);
                    url = await source.ResolveDownloadUrlAsync(file.Version.VersionId, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (string.IsNullOrWhiteSpace(url))
                    throw new ModDownloadException($"无法解析 {file.ProjectName} 的下载地址", Array.Empty<string>());

                // 候选链（按平台）：官方 CDN → BMCLAPI → MCIM；CurseForge 为 edge → mediafilez → MCIM。
                var candidates = _config.GetDownloadCandidates(url);
                var primaryUrls = candidates.Take(1).ToList();
                var fallbackUrls = candidates.Skip(1).ToList();

                // 单个文件内部的进度增量（多文件并行时按增量累加到整体进度）。
                var sync = new object();
                long lastWritten = 0;

                var result = await _http.DownloadFileAsync(
                    primaryUrls,
                    fallbackUrls,
                    destination,
                    (written, _) =>
                    {
                        lock (sync)
                        {
                            var delta = written - lastWritten;
                            lastWritten = written;

                            if (delta != 0)
                                Interlocked.Add(ref counters[0], delta);
                        }

                        ReportProgress(progress, fileName, fileIndex, files.Count, Current(),
                            totalBytes > 0 ? totalBytes : files.Count, stage);
                    },
                    cancellationToken,
                    file.Version.FileSize).ConfigureAwait(false);

                saved.Add(result.Path);
                Log($"{fileName} 下载完成{(result.UsedFallback ? "（回退官方源）" : "（MCIM 镜像）")}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var failedName = file.Version.FileName;
                failures.Add($"{file.ProjectName}（{failedName}）：{ex.Message}");
                Log($"{failedName} 下载失败：{ex.Message}");

                // 单个文件失败不影响其它文件，最后统一汇报。
                Interlocked.Add(ref counters[2], Math.Max(file.Version.FileSize, 0));
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var savedList = saved.ToList();
        var skippedList = skipped.ToList();
        var failureList = failures.ToList();

        if (failureList.Count > 0)
        {
            throw new ModDownloadException(
                $"有 {failureList.Count} 个文件下载失败：\n{string.Join("\n", failureList)}",
                failureList);
        }

        var totalDownloaded = Interlocked.Read(ref counters[0]) +
                              Interlocked.Read(ref counters[1]) +
                              Interlocked.Read(ref counters[2]);

        return new ModInstallReport(savedList, skippedList, totalDownloaded);
    }

    /// <summary>
    /// 上报进度（并行下载时由多个线程调用）。
    /// </summary>
    private static void ReportProgress(
        IProgress<ModDownloadProgress>? progress,
        string fileName,
        int fileIndex,
        int fileCount,
        long downloadedBytes,
        long totalBytes,
        string stage)
    {
        progress?.Report(new ModDownloadProgress(
            fileName, fileIndex, fileCount, downloadedBytes, totalBytes, stage));
    }

    /// <summary>
    /// 写日志。
    /// </summary>
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
}
