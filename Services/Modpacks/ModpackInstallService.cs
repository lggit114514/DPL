using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using DPL.Models;
using DPL.Services.Mods;

namespace DPL.Services.Modpacks;

/// <summary>
/// 整合包安装服务：负责格式解析、版本/加载器安装、模组并发下载与 overrides 覆盖。
/// ViewModel 只负责拖拽交互、命名弹窗和进度显示，本类不依赖任何 Avalonia 类型。
/// </summary>
public sealed class ModpackInstallService : IDisposable
{
    /// <summary>文件级并发下载数。</summary>
    public const int MaxDownloadConcurrency = 8;

    /// <summary>单个文件的最大下载尝试次数。</summary>
    private const int MaxDownloadAttempts = 3;

    /// <summary>文件路径比较：Windows 忽略大小写，macOS/Linux 区分大小写。</summary>
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>版本名 / 路径段中不允许出现的字符（跨平台统一限制）。</summary>
    private static readonly char[] InvalidNameChars =
    {
        '\\', '/', ':', '*', '?', '"', '<', '>', '|', '\0',
    };

    private readonly IMinecraftService _minecraft;
    private readonly ModConfig _config;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly ModHttp _http;
    private readonly CurseForgeModSource _curseForge;
    private readonly ChunkedFileDownloader _chunked;

    /// <summary>日志回调（可能在后台线程触发）。</summary>
    public event Action<string>? LogWritten;

    /// <summary>
    /// 创建整合包安装服务。
    /// </summary>
    /// <param name="minecraftService">版本 / 加载器安装服务。</param>
    /// <param name="config">下载配置（含 CurseForge API Key）。</param>
    /// <param name="client">可注入的 HttpClient（测试用）；为 null 时内部创建并负责释放。</param>
    public ModpackInstallService(
        IMinecraftService minecraftService,
        ModConfig config,
        HttpClient? client = null)
    {
        _minecraft = minecraftService;
        _config = config;

        if (client is null)
        {
            _client = new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(10),
            })
            {
                Timeout = TimeSpan.FromMinutes(10),
            };
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("DPL/1.0");
            _ownsClient = true;
        }
        else
        {
            _client = client;
            _ownsClient = false;
        }

        _http = new ModHttp(config, _client);
        _http.LogWritten += message => Log(message);
        _curseForge = new CurseForgeModSource(_http, config);

        // 整合包里可能包含配置、资源包等非 jar 文件，因此关闭分片前的 Content-Type 健康检查；
        // 不支持 Range 的源会自动回退到单线程下载。
        _chunked = new ChunkedFileDownloader(
            _client,
            new ChunkedFileDownloaderOptions
            {
                ThresholdBytes = config.ChunkThresholdBytes,
                ChunkSizeBytes = config.ChunkSizeBytes,
                MaxParallelChunks = config.DownloadThreadCount,
                HealthCheckEnabled = false,
            });
        _chunked.LogWritten += message => Log(message);
    }

    /// <summary>
    /// 校验实例名称：非空、无非法字符、不与 versions 下已有实例重名。
    /// 返回 null 表示可用；否则返回给用户看的错误提示。
    /// </summary>
    /// <param name="name">用户输入的实例名。</param>
    /// <param name="versionsDirectory">当前版本目录。</param>
    public static string? ValidateInstanceName(string name, string versionsDirectory)
    {
        var trimmed = (name ?? string.Empty).Trim();

        if (trimmed.Length == 0)
            return "实例名称不能为空";

        if (trimmed.IndexOfAny(InvalidNameChars) >= 0)
            return "实例名称不能包含 \\ / : * ? \" < > |";

        if (trimmed is "." or "..")
            return "实例名称无效";

        try
        {
            var directory = Path.Combine(versionsDirectory, trimmed);
            if (Directory.Exists(directory) || File.Exists(directory))
                return "该版本名称已存在，请换一个名字";
        }
        catch (Exception)
        {
            return "实例名称无效";
        }

        return null;
    }

    /// <summary>
    /// 安装一个 Modrinth（.mrpack）或 CurseForge（.zip）整合包。
    /// 失败时会删除本次创建实例目录；已存在的原版 / 加载器依赖不会被删除。
    /// </summary>
    /// <param name="archivePath">整合包文件路径。</param>
    /// <param name="instanceName">用户确认后的实例名称。</param>
    /// <param name="progress">安装进度回调。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<ModpackInstallResult> InstallAsync(
        string archivePath,
        string instanceName,
        IProgress<ModpackInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            throw new FileNotFoundException("找不到整合包文件", archivePath);

        var extension = Path.GetExtension(archivePath);
        if (!extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".mrpack", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("该整合包格式不受支持：只支持 .zip 或 .mrpack");
        }

        var name = (instanceName ?? string.Empty).Trim();
        var nameError = ValidateInstanceName(name, _minecraft.VersionsDirectory);
        if (nameError is not null)
            throw new InvalidOperationException(nameError);

        var instanceDirectory = _minecraft.GetVersionDirectory(name);
        if (Directory.Exists(instanceDirectory) || File.Exists(instanceDirectory))
            throw new InvalidOperationException("该版本名称已存在，请换一个名字");

        var tempDirectory = Path.Combine(
            Path.GetTempPath(), "DPL-modpack-" + Guid.NewGuid().ToString("N"));

        var createdInstance = false;

        try
        {
            Report(progress, "正在解压整合包", 0, null, 0, 0, 0, 0);

            Directory.CreateDirectory(tempDirectory);
            ExtractArchive(archivePath, tempDirectory, cancellationToken);

            Report(progress, "正在解析整合包清单", 2, null, 0, 0, 0, 0);
            var plan = await ParseAsync(tempDirectory, progress, cancellationToken).ConfigureAwait(false);

            // 原版目录同时也是加载器的 inheritsFrom 基础，不能被实例名占用。
            if (string.Equals(name, plan.MinecraftVersion, PathComparison))
                throw new InvalidOperationException("实例名称不能与原版版本号相同，请换一个名字");

            // 解析 / 查询 CurseForge 期间可能有其他安装创建了同名目录，安装前再检查一次。
            if (Directory.Exists(instanceDirectory) || File.Exists(instanceDirectory))
                throw new InvalidOperationException("该版本名称已存在，请换一个名字");

            Report(progress, "正在安装原版与模组加载器", 5, null, 0, 0, 0, 0);

            // 先显式补全原版；InstallModLoaderAsync 内部还会再次校验 / 安装原版，
            // 这样原版依赖与加载器实例的职责都和现有安装流程保持一致。
            var baseProgress = new Progress<InstallProgressInfo>(info =>
            {
                var ratio = ResolveInstallRatio(info);
                Report(
                    progress,
                    string.IsNullOrWhiteSpace(info.Stage) ? "正在安装原版" : info.Stage,
                    5 + Math.Clamp(ratio, 0d, 1d) * 15d,
                    info.CurrentFile,
                    0,
                    0,
                    0,
                    0);
            });

            await _minecraft
                .InstallAsync(plan.MinecraftVersion, baseProgress, cancellationToken)
                .ConfigureAwait(false);

            var installProgress = new Progress<InstallProgressInfo>(info =>
            {
                var ratio = ResolveInstallRatio(info);
                Report(
                    progress,
                    string.IsNullOrWhiteSpace(info.Stage) ? "正在安装模组加载器" : info.Stage,
                    20 + Math.Clamp(ratio, 0d, 1d) * 15d,
                    info.CurrentFile,
                    0,
                    0,
                    0,
                    0);
            });

            // InstallModLoaderAsync 会先把原版装好，再生成 / 补全加载器版本；
            // 自定义名称会强制把最终实例放到 versions/<name>。
            createdInstance = true;
            await _minecraft
                .InstallModLoaderAsync(
                    plan.LoaderKind,
                    plan.MinecraftVersion,
                    plan.LoaderVersion,
                    name,
                    installProgress,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!Directory.Exists(instanceDirectory))
                throw new InvalidOperationException($"安装完成但实例目录不存在：{instanceDirectory}");

            Report(progress, "原版与加载器安装完成，准备下载模组", 35, null, 0, plan.Files.Count, 0, 0);

            await DownloadFilesAsync(plan.Files, instanceDirectory, progress, cancellationToken)
                .ConfigureAwait(false);

            Report(progress, "正在覆盖 config / saves / resourcepacks 等文件", 90, null,
                plan.Files.Count, plan.Files.Count, 0, 0);

            var overridesCopied = CopyOverrides(
                tempDirectory, instanceDirectory, plan.OverrideFolders, progress, cancellationToken);

            Report(progress, "整合包安装完成", 100, null,
                plan.Files.Count, plan.Files.Count, 0, 0);

            Log($"整合包 {name} 安装完成：{plan.Format}，{plan.Files.Count} 个文件，覆盖 {overridesCopied} 个文件");

            return new ModpackInstallResult(
                name,
                plan.MinecraftVersion,
                plan.LoaderKind,
                plan.LoaderVersion,
                plan.Files.Count,
                overridesCopied,
                plan.Format);
        }
        catch (OperationCanceledException)
        {
            if (createdInstance)
            {
                TryDeleteDirectory(instanceDirectory);
                Log($"安装已取消，已回滚实例目录：{instanceDirectory}");
            }

            throw;
        }
        catch (Exception)
        {
            if (createdInstance)
            {
                TryDeleteDirectory(instanceDirectory);
                Log($"安装失败，已回滚实例目录：{instanceDirectory}");
            }

            throw;
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    /// <summary>
    /// 解析临时目录中的整合包清单。
    /// </summary>
    private async Task<ModpackPlan> ParseAsync(
        string tempDirectory,
        IProgress<ModpackInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var modrinthIndex = Path.Combine(tempDirectory, "modrinth.index.json");
            if (File.Exists(modrinthIndex))
                return ParseModrinth(modrinthIndex);

            var curseForgeManifest = Path.Combine(tempDirectory, "manifest.json");
            if (File.Exists(curseForgeManifest))
                return await ParseCurseForgeAsync(curseForgeManifest, progress, cancellationToken).ConfigureAwait(false);

            throw new InvalidOperationException("该整合包格式不受支持（未找到 modrinth.index.json 或 manifest.json）");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("该整合包格式不受支持：清单 JSON 无法解析", ex);
        }
    }

    /// <summary>
    /// 解析 Modrinth 的 modrinth.index.json。
    /// </summary>
    private static ModpackPlan ParseModrinth(string indexPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(indexPath));
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("缺少依赖文件：modrinth.index.json 不是有效的对象");

        var dependencies = GetRequiredObject(root, "dependencies", "modrinth.index.json");
        var minecraftVersion = GetRequiredString(dependencies, "minecraft", "dependencies");

        var loaderKind = ModLoaderKind.Vanilla;
        string? loaderVersion = null;

        if (TryGetOptionalString(dependencies, "fabric-loader", out var fabricLoader))
        {
            loaderKind = ModLoaderKind.Fabric;
            loaderVersion = fabricLoader;
        }
        else if (TryGetOptionalString(dependencies, "quilt-loader", out var quiltLoader))
        {
            loaderKind = ModLoaderKind.Quilt;
            loaderVersion = quiltLoader;
        }
        else if (TryGetOptionalString(dependencies, "neoforge", out var neoForgeLoader))
        {
            loaderKind = ModLoaderKind.NeoForge;
            loaderVersion = neoForgeLoader;
        }
        else if (TryGetOptionalString(dependencies, "forge", out var forgeLoader))
        {
            loaderKind = ModLoaderKind.Forge;
            loaderVersion = forgeLoader;
        }
        else if (dependencies.TryGetProperty("liteloader", out _))
        {
            throw new InvalidOperationException("该整合包格式不受支持：暂不支持 LiteLoader");
        }

        if (!root.TryGetProperty("files", out var filesElement) ||
            filesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("缺少依赖文件：modrinth.index.json 未声明 files 数组");
        }

        var files = new List<ModpackFile>();
        var seenPaths = new HashSet<string>(PathComparer);

        foreach (var item in filesElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("缺少依赖文件：files 中包含无效条目");

            var relativePath = GetRequiredString(item, "path", "files[]");
            var normalizedPath = relativePath.Replace('\\', '/');
            if (!seenPaths.Add(normalizedPath))
                throw new InvalidOperationException($"缺少依赖文件：files 中存在重复路径 {normalizedPath}");

            if (!item.TryGetProperty("downloads", out var downloadsElement) ||
                downloadsElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException($"缺少依赖文件：{normalizedPath} 未声明 downloads");
            }

            string? downloadUrl = null;
            foreach (var download in downloadsElement.EnumerateArray())
            {
                if (download.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(download.GetString()))
                {
                    downloadUrl = download.GetString();
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(downloadUrl))
                throw new InvalidOperationException($"缺少依赖文件：{normalizedPath} 没有可用的下载地址");

            var fileSize = TryGetLong(item, "fileSize") ?? 0;

            files.Add(new ModpackFile(normalizedPath, downloadUrl, fileSize));
        }

        return new ModpackPlan(
            minecraftVersion,
            loaderKind,
            loaderVersion,
            files,
            new[] { "overrides", "client-overrides" },
            "Modrinth");
    }

    /// <summary>
    /// 解析 CurseForge 的 manifest.json，并通过现有 CurseForge API 解析每个文件的真实下载地址。
    /// </summary>
    private async Task<ModpackPlan> ParseCurseForgeAsync(
        string manifestPath,
        IProgress<ModpackInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!_curseForge.IsConfigured)
        {
            throw new InvalidOperationException(
                "缺少依赖文件：未配置 CurseForge API Key，无法解析 CurseForge 整合包");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("缺少依赖文件：manifest.json 不是有效的对象");

        var minecraft = GetRequiredObject(root, "minecraft", "manifest.json");
        var minecraftVersion = GetRequiredString(minecraft, "version", "minecraft");
        var loader = ReadCurseForgeLoader(minecraft);

        if (!root.TryGetProperty("files", out var filesElement) ||
            filesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("缺少依赖文件：manifest.json 未声明 files 数组");
        }

        var files = new List<ModpackFile>();
        var seenPaths = new HashSet<string>(PathComparer);
        var totalFiles = filesElement.GetArrayLength();
        var fileIndex = 0;

        foreach (var item in filesElement.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            fileIndex++;

            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("缺少依赖文件：files 中包含无效条目");

            Report(
                progress,
                $"正在解析 CurseForge 文件 {fileIndex}/{totalFiles}",
                2 + (double)fileIndex / Math.Max(1, totalFiles) * 3,
                null,
                0,
                totalFiles,
                0,
                0);

            var projectId = GetRequiredLong(item, "projectID", "files[]");
            var fileId = GetRequiredLong(item, "fileID", "files[]");
            var versionId = $"{projectId}:{fileId}";

            string? downloadUrl;
            try
            {
                downloadUrl = await _curseForge
                    .ResolveDownloadUrlAsync(versionId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"缺少依赖文件：无法解析 CurseForge 文件 {versionId}（{ex.Message}）", ex);
            }

            if (string.IsNullOrWhiteSpace(downloadUrl))
                throw new InvalidOperationException($"缺少依赖文件：CurseForge 文件 {versionId} 没有下载地址");

            var fileName = GetFileNameFromUrl(downloadUrl, $"{projectId}-{fileId}.jar");
            var relativePath = "mods/" + fileName;

            if (!seenPaths.Add(relativePath))
                throw new InvalidOperationException($"缺少依赖文件：CurseForge 文件路径重复 {relativePath}");

            files.Add(new ModpackFile(relativePath, downloadUrl, 0));
        }

        var overridesFolder = TryGetOptionalString(root, "overrides", out var configuredOverrides) &&
                              !string.IsNullOrWhiteSpace(configuredOverrides)
            ? configuredOverrides
            : "overrides";

        var overrideFolders = new List<string> { overridesFolder };
        if (!string.Equals(overridesFolder, "client-overrides", StringComparison.OrdinalIgnoreCase))
            overrideFolders.Add("client-overrides");

        return new ModpackPlan(
            minecraftVersion,
            loader.Kind,
            loader.Version,
            files,
            overrideFolders,
            "CurseForge");
    }

    /// <summary>
    /// 并发下载整合包文件列表（最多 8 个文件同时下载）。
    /// </summary>
    private async Task DownloadFilesAsync(
        IReadOnlyList<ModpackFile> files,
        string instanceDirectory,
        IProgress<ModpackInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            Report(progress, "整合包没有需要下载的文件", 90, null, 0, 0, 0, 0);
            return;
        }

        var knownSizes = files.All(file => file.Size > 0);
        var totalBytes = knownSizes ? files.Sum(file => file.Size) : 0;

        // 用数组包装计数，方便在多个并发下载任务之间安全累加。
        var downloadedBytes = new long[1];
        var completedFiles = new int[1];
        var failures = new ConcurrentBag<string>();

        using var gate = new SemaphoreSlim(
            MaxDownloadConcurrency, MaxDownloadConcurrency);

        var tasks = files.Select(async (file, index) =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var destination = GetSafePath(instanceDirectory, file.RelativePath);
                var parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrWhiteSpace(parent))
                    Directory.CreateDirectory(parent);

                var lastWritten = 0L;
                var sync = new object();

                void OnFileProgress(long written, long _)
                {
                    lock (sync)
                    {
                        if (written <= lastWritten)
                            return;

                        var delta = written - lastWritten;
                        lastWritten = written;
                        Interlocked.Add(ref downloadedBytes[0], delta);
                    }

                    ReportDownloadProgress(
                        progress,
                        "正在下载 " + file.RelativePath,
                        file.RelativePath,
                        completedFiles[0],
                        files.Count,
                        Interlocked.Read(ref downloadedBytes[0]),
                        totalBytes,
                        knownSizes);
                }

                ReportDownloadProgress(
                    progress,
                    "准备下载 " + file.RelativePath,
                    file.RelativePath,
                    completedFiles[0],
                    files.Count,
                    Interlocked.Read(ref downloadedBytes[0]),
                    totalBytes,
                    knownSizes);

                await DownloadFileWithRetryAsync(
                        file, destination, OnFileProgress, cancellationToken)
                    .ConfigureAwait(false);

                Interlocked.Increment(ref completedFiles[0]);
                ReportDownloadProgress(
                    progress,
                    "已下载 " + file.RelativePath,
                    file.RelativePath,
                    completedFiles[0],
                    files.Count,
                    Interlocked.Read(ref downloadedBytes[0]),
                    totalBytes,
                    knownSizes);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add($"{file.RelativePath}：{ex.Message}");
                Log($"下载失败：{file.RelativePath}（{ex.Message}）");
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);

        if (!failures.IsEmpty)
        {
            var detail = string.Join("\n", failures.ToArray());
            throw new ModpackInstallException($"有 {failures.Count} 个文件下载失败：\n{detail}", failures.ToArray());
        }

        Report(progress, "模组文件下载完成", 90, null, files.Count, files.Count,
            Interlocked.Read(ref downloadedBytes[0]), totalBytes);
    }

    /// <summary>
    /// 下载单个文件，失败时最多重试 3 次。
    /// </summary>
    private async Task DownloadFileWithRetryAsync(
        ModpackFile file,
        string destination,
        Action<long, long> progress,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= MaxDownloadAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await DownloadOnceAsync(file, destination, progress, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                TryDeleteFile(destination);
                Log($"下载第 {attempt}/{MaxDownloadAttempts} 次失败：{file.RelativePath}（{ex.Message}）");

                if (attempt < MaxDownloadAttempts)
                {
                    await Task
                        .Delay(TimeSpan.FromMilliseconds(500 * attempt), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        throw new ModpackInstallException(
            $"{file.RelativePath} 下载失败：{lastError?.Message}", Array.Empty<string>());
    }

    /// <summary>
    /// 优先使用现有的 ChunkedFileDownloader；源不支持 Range 或分片失败时回退到单线程下载。
    /// </summary>
    private async Task DownloadOnceAsync(
        ModpackFile file,
        string destination,
        Action<long, long> progress,
        CancellationToken cancellationToken)
    {
        var candidates = BuildDownloadCandidates(file.Url);
        if (candidates.Count == 0)
            throw new InvalidOperationException($"无法识别下载地址：{file.Url}");

        var first = new Uri(candidates[0]);
        var relativePath = first.PathAndQuery.TrimStart('/');
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidOperationException($"下载地址没有文件路径：{file.Url}");

        var baseUrls = candidates
            .Select(url => new Uri(url).GetLeftPart(UriPartial.Authority))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        try
        {
            await _chunked
                .DownloadAsync(
                    baseUrls,
                    relativePath,
                    destination,
                    file.Size,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"分片下载失败，回退单线程：{file.RelativePath}（{ex.Message}）");
            TryDeleteFile(destination);
        }

        await DownloadSingleThreadAsync(candidates, destination, file.Size, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 单线程下载兜底（支持任意文件类型，不再要求 Range）。
    /// </summary>
    private async Task DownloadSingleThreadAsync(
        IReadOnlyList<string> candidates,
        string destination,
        long expectedSize,
        Action<long, long> progress,
        CancellationToken cancellationToken)
    {
        var partPath = destination + ".part";
        Exception? lastError = null;

        foreach (var url in candidates)
        {
            for (var attempt = 1; attempt <= MaxDownloadAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(_config.DownloadTimeout);

                    using var response = await _client
                        .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                        .ConfigureAwait(false);

                    response.EnsureSuccessStatusCode();

                    var mediaType = response.Content.Headers.ContentType?.MediaType;
                    if (!string.IsNullOrWhiteSpace(mediaType) &&
                        mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"下载地址返回了 HTML 页面：{url}");
                    }

                    var total = response.Content.Headers.ContentLength ?? expectedSize;
                    long written = 0;

                    await using (var source = await response.Content
                                     .ReadAsStreamAsync(timeout.Token)
                                     .ConfigureAwait(false))
                    await using (var target = new FileStream(
                                     partPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[81920];
                        int read;
                        while ((read = await source
                                   .ReadAsync(buffer.AsMemory(0, buffer.Length), timeout.Token)
                                   .ConfigureAwait(false)) > 0)
                        {
                            await target
                                .WriteAsync(buffer.AsMemory(0, read), timeout.Token)
                                .ConfigureAwait(false);
                            written += read;
                            progress(written, total);
                        }
                    }

                    if (written == 0)
                        throw new InvalidOperationException("下载内容为空");

                    if (expectedSize > 0 && written != expectedSize)
                    {
                        throw new InvalidOperationException(
                            $"文件大小不匹配（期望 {expectedSize} 字节，实际 {written} 字节）");
                    }

                    File.Move(partPath, destination, overwrite: true);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    TryDeleteFile(partPath);
                    throw;
                }
                catch (OperationCanceledException ex)
                {
                    lastError = ex;
                    TryDeleteFile(partPath);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    TryDeleteFile(partPath);
                }

                if (attempt < MaxDownloadAttempts)
                {
                    await Task
                        .Delay(TimeSpan.FromMilliseconds(500 * attempt), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        throw new ModpackInstallException(
            $"所有下载源都失败：{lastError?.Message}", Array.Empty<string>());
    }

    /// <summary>
    /// 把 overrides / client-overrides 目录内容覆盖到实例目录。
    /// </summary>
    private static int CopyOverrides(
        string tempDirectory,
        string instanceDirectory,
        IReadOnlyList<string> overrideFolders,
        IProgress<ModpackInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var copied = 0;

        foreach (var folder in overrideFolders.Distinct(PathComparer))
        {
            if (string.IsNullOrWhiteSpace(folder))
                continue;

            string sourceDirectory;
            try
            {
                sourceDirectory = GetSafePath(tempDirectory, folder);
            }
            catch (Exception)
            {
                throw new InvalidOperationException($"整合包 overrides 路径非法：{folder}");
            }

            if (!Directory.Exists(sourceDirectory))
                continue;

            foreach (var sourceFile in Directory.EnumerateFiles(
                         sourceDirectory, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);
                var destination = GetSafePath(instanceDirectory, relativePath);
                var parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrWhiteSpace(parent))
                    Directory.CreateDirectory(parent);

                File.Copy(sourceFile, destination, overwrite: true);
                copied++;
            }
        }

        if (copied > 0)
        {
            Report(progress, $"已覆盖 {copied} 个配置文件", 99, null, copied, copied, 0, 0);
        }

        return copied;
    }

    /// <summary>
    /// 安全解压 zip / mrpack（阻止 ../ 与绝对路径逃逸）。
    /// </summary>
    private static void ExtractArchive(
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(entry.FullName))
                    continue;

                var destination = GetSafePath(destinationDirectory, entry.FullName);

                // 目录条目的 Name 为空。
                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                var parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrWhiteSpace(parent))
                    Directory.CreateDirectory(parent);

                entry.ExtractToFile(destination, overwrite: true);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException("该整合包格式不受支持：不是有效的 .zip / .mrpack 压缩包", ex);
        }
    }

    /// <summary>
    /// 获取安全的相对路径，拒绝绝对路径、../ 与非法路径段。
    /// </summary>
    private static string GetSafePath(string rootDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidOperationException("整合包包含空路径");

        var normalized = relativePath.Replace('\\', '/').Trim();
        if (normalized.StartsWith('/'))
            throw new InvalidOperationException($"整合包包含绝对路径：{relativePath}");

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            throw new InvalidOperationException($"整合包包含空路径：{relativePath}");

        foreach (var segment in segments)
        {
            if (segment is "." or ".." || segment.IndexOfAny(InvalidNameChars) >= 0)
                throw new InvalidOperationException($"整合包包含非法路径：{relativePath}");
        }

        var root = Path.GetFullPath(rootDirectory);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        var combined = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));

        if (!combined.StartsWith(rootWithSeparator, PathComparison) &&
            !string.Equals(combined, root, PathComparison))
        {
            throw new InvalidOperationException($"整合包路径越界：{relativePath}");
        }

        return combined;
    }

    /// <summary>
    /// 根据下载地址构造候选链接：优先镜像，再回退官方 CDN。
    /// </summary>
    private IReadOnlyList<string> BuildDownloadCandidates(string url)
    {
        var candidates = new List<string>();

        void AddRange(IEnumerable<string> urls)
        {
            foreach (var candidate in urls)
            {
                if (!string.IsNullOrWhiteSpace(candidate) &&
                    Uri.TryCreate(candidate, UriKind.Absolute, out _) &&
                    !candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                {
                    candidates.Add(candidate);
                }
            }
        }

        AddRange(_config.GetPrimaryDownloadUrls(url));
        AddRange(_config.GetFallbackDownloadUrls(url));

        if (candidates.Count == 0)
            candidates.Add(url);

        return candidates;
    }

    /// <summary>
    /// 从下载地址提取文件名；失败时使用 projectId-fileId 兜底。
    /// </summary>
    private static string GetFileNameFromUrl(string url, string fallback)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var name = Path.GetFileName(Uri.UnescapeDataString(uri.LocalPath));
                if (!string.IsNullOrWhiteSpace(name) &&
                    name.IndexOfAny(InvalidNameChars) < 0)
                {
                    return name;
                }
            }
        }
        catch (Exception)
        {
            // 使用兜底文件名。
        }

        return fallback;
    }

    /// <summary>读取 CurseForge 的加载器信息。</summary>
    private static (ModLoaderKind Kind, string? Version) ReadCurseForgeLoader(JsonElement minecraft)
    {
        if (!minecraft.TryGetProperty("modLoaders", out var loaders) ||
            loaders.ValueKind != JsonValueKind.Array ||
            loaders.GetArrayLength() == 0)
        {
            return (ModLoaderKind.Vanilla, null);
        }

        var candidates = loaders.EnumerateArray().ToList();
        var selected = candidates.FirstOrDefault(item =>
            item.TryGetProperty("primary", out var primary) &&
            primary.ValueKind == JsonValueKind.True);

        if (selected.ValueKind == JsonValueKind.Undefined)
            selected = candidates[0];

        var id = GetRequiredString(selected, "id", "minecraft.modLoaders[]");
        var separator = id.IndexOf('-');
        if (separator <= 0 || separator >= id.Length - 1)
            throw new InvalidOperationException($"该整合包格式不受支持：无法识别加载器 {id}");

        var prefix = id[..separator].Trim().ToLowerInvariant();
        var version = id[(separator + 1)..].Trim();

        return prefix switch
        {
            "fabric" => (ModLoaderKind.Fabric, version),
            "quilt" => (ModLoaderKind.Quilt, version),
            "forge" => (ModLoaderKind.Forge, version),
            "neoforge" => (ModLoaderKind.NeoForge, version),
            "liteloader" => throw new InvalidOperationException("该整合包格式不受支持：暂不支持 LiteLoader"),
            _ => throw new InvalidOperationException($"该整合包格式不受支持：未知加载器 {prefix}"),
        };
    }

    /// <summary>读取必需的对象字段。</summary>
    private static JsonElement GetRequiredObject(JsonElement parent, string name, string context)
    {
        if (!parent.TryGetProperty(name, out var element) ||
            element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"缺少依赖文件：{context}.{name}");
        }

        return element;
    }

    /// <summary>读取必需的字符串字段。</summary>
    private static string GetRequiredString(JsonElement parent, string name, string context)
    {
        if (!parent.TryGetProperty(name, out var element) ||
            element.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(element.GetString()))
        {
            throw new InvalidOperationException($"缺少依赖文件：{context}.{name}");
        }

        return element.GetString()!;
    }

    /// <summary>读取可选字符串字段。</summary>
    private static bool TryGetOptionalString(JsonElement parent, string name, out string? value)
    {
        value = null;

        if (!parent.TryGetProperty(name, out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = element.GetString();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        value = text;
        return true;
    }

    /// <summary>读取必需的 long 字段（兼容 JSON 数字与字符串）。</summary>
    private static long GetRequiredLong(JsonElement parent, string name, string context)
    {
        if (!parent.TryGetProperty(name, out var element))
            throw new InvalidOperationException($"缺少依赖文件：{context}.{name}");

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number))
            return number;

        if (element.ValueKind == JsonValueKind.String &&
            long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        throw new InvalidOperationException($"缺少依赖文件：{context}.{name} 不是有效数字");
    }

    /// <summary>读取可选 long 字段。</summary>
    private static long? TryGetLong(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element))
            return null;

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number))
            return number;

        return null;
    }

    /// <summary>把 CmlLib 的进度比例换算成 0~1。</summary>
    private static double ResolveInstallRatio(InstallProgressInfo info)
    {
        if (info.Ratio > 0)
            return info.Ratio;

        if (info.TotalBytes > 0)
            return (double)info.ProgressedBytes / info.TotalBytes;

        if (info.TotalTasks > 0)
            return (double)info.ProgressedTasks / info.TotalTasks;

        return 0;
    }

    /// <summary>上报下载阶段进度（35% ~ 90%）。</summary>
    private static void ReportDownloadProgress(
        IProgress<ModpackInstallProgress>? progress,
        string stage,
        string currentFile,
        int completedFiles,
        int totalFiles,
        long downloadedBytes,
        long totalBytes,
        bool knownSizes)
    {
        // 大文件用字节比例平滑推进；同时用“已完成文件数”保证小文件也能前进。
        var byteRatio = knownSizes && totalBytes > 0
            ? (double)downloadedBytes / totalBytes
            : 0;
        var fileRatio = totalFiles > 0 ? (double)completedFiles / totalFiles : 0;
        var ratio = Math.Max(byteRatio, fileRatio);

        Report(
            progress,
            stage,
            35 + Math.Clamp(ratio, 0d, 1d) * 55d,
            currentFile,
            completedFiles,
            totalFiles,
            downloadedBytes,
            totalBytes);
    }

    /// <summary>上报一条进度（订阅者异常不会影响安装）。</summary>
    private static void Report(
        IProgress<ModpackInstallProgress>? progress,
        string stage,
        double percent,
        string? currentFile,
        int completedFiles,
        int totalFiles,
        long downloadedBytes,
        long totalBytes)
    {
        try
        {
            progress?.Report(new ModpackInstallProgress(
                stage,
                Math.Clamp(percent, 0d, 100d),
                currentFile,
                completedFiles,
                totalFiles,
                downloadedBytes,
                totalBytes));
        }
        catch (Exception)
        {
            // 进度订阅者异常不能中断安装。
        }
    }

    /// <summary>写一条日志。</summary>
    private void Log(string message)
    {
        try
        {
            LogWritten?.Invoke(string.IsNullOrWhiteSpace(message) ? "[整合包]" : $"[整合包] {message}");
        }
        catch (Exception)
        {
            // 忽略日志订阅者异常。
        }
    }

    /// <summary>尝试删除实例 / 临时目录。</summary>
    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception)
        {
            // 忽略清理失败。
        }
    }

    /// <summary>尝试删除文件。</summary>
    private static void TryDeleteFile(string path)
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

    /// <inheritdoc />
    public void Dispose()
    {
        _http.Dispose();

        if (_ownsClient)
            _client.Dispose();
    }

    /// <summary>解析后的整合包安装计划。</summary>
    private sealed record ModpackPlan(
        string MinecraftVersion,
        ModLoaderKind LoaderKind,
        string? LoaderVersion,
        IReadOnlyList<ModpackFile> Files,
        IReadOnlyList<string> OverrideFolders,
        string Format);

    /// <summary>一个需要下载的整合包文件。</summary>
    private sealed record ModpackFile(string RelativePath, string DownloadUrl, long Size)
    {
        /// <summary>下载地址。</summary>
        public string Url => DownloadUrl;
    }
}

/// <summary>整合包安装进度。</summary>
/// <param name="Stage">当前阶段文字。</param>
/// <param name="Percent">总进度（0 ~ 100）。</param>
/// <param name="CurrentFile">当前文件。</param>
/// <param name="CompletedFiles">已完成文件数。</param>
/// <param name="TotalFiles">总文件数。</param>
/// <param name="DownloadedBytes">已下载字节数。</param>
/// <param name="TotalBytes">已知总字节数。</param>
public sealed record ModpackInstallProgress(
    string Stage,
    double Percent,
    string? CurrentFile = null,
    int CompletedFiles = 0,
    int TotalFiles = 0,
    long DownloadedBytes = 0,
    long TotalBytes = 0);

/// <summary>整合包安装结果。</summary>
/// <param name="InstanceName">实例名称。</param>
/// <param name="MinecraftVersion">原版版本号。</param>
/// <param name="LoaderKind">加载器类型。</param>
/// <param name="LoaderVersion">加载器版本号。</param>
/// <param name="DownloadedFiles">下载的文件数量。</param>
/// <param name="OverridesCopied">覆盖的文件数量。</param>
/// <param name="Format">整合包格式（Modrinth / CurseForge）。</param>
public sealed record ModpackInstallResult(
    string InstanceName,
    string MinecraftVersion,
    ModLoaderKind LoaderKind,
    string? LoaderVersion,
    int DownloadedFiles,
    int OverridesCopied,
    string Format);

/// <summary>整合包安装失败（携带具体失败项）。</summary>
public sealed class ModpackInstallException : Exception
{
    /// <summary>创建异常。</summary>
    /// <param name="message">错误说明。</param>
    /// <param name="failures">失败项详情。</param>
    public ModpackInstallException(string message, IReadOnlyList<string> failures) : base(message)
        => Failures = failures;

    /// <summary>失败项详情。</summary>
    public IReadOnlyList<string> Failures { get; }
}
