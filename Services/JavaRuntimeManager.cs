using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using DPL.Models;

namespace DPL.Services;

/// <summary>
/// Java 运行时管理器：检测本机 / 启动器运行库中的 Java，
/// 缺失时从国内镜像静默下载 ZIP、解压并递归定位可执行文件。
/// </summary>
public sealed class JavaRuntimeManager
{
    private readonly Func<string> _directoryProvider;
    private readonly HttpClient _client;
    private readonly IReadOnlyList<JavaDownloadSource> _sources;

    /// <summary>创建 Java 运行时管理器。</summary>
    /// <param name="directoryProvider">当前 Java 运行库根目录（用户可在设置中修改）。</param>
    /// <param name="client">可注入的 HttpClient（测试用）；为 null 时内部创建。</param>
    public JavaRuntimeManager(Func<string> directoryProvider, HttpClient? client = null)
    {
        _directoryProvider = directoryProvider;

        if (client is not null)
        {
            _client = client;
        }
        else
        {
            _client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("DPL/1.0");
        }

        _sources =
        [
            new JavaDownloadSource("Injdk", ResolveInjdkAsync),
            new JavaDownloadSource("华为云 OpenJDK", ResolveHuaweiAsync),
            new JavaDownloadSource("清华 TUNA Adoptium", ResolveTunaAsync),
        ];
    }

    /// <summary>日志回调。</summary>
    public event Action<string>? LogWritten;

    /// <summary>
    /// 在系统 Java 与启动器运行库中查找满足主版本要求的 Java。
    /// </summary>
    /// <param name="requiredMajor">所需 Java 主版本；0 表示不限制。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<JavaRuntimeInfo?> FindCompatibleAsync(
        int requiredMajor,
        CancellationToken cancellationToken = default)
    {
        JavaRuntimeInfo? bestAbove = null;
        string? root = null;

        try
        {
            root = GetRuntimeRoot();
        }
        catch (Exception)
        {
            // 用户配置的路径暂时不可用时，仍扫描系统 Java。
        }

        foreach (var candidate in JavaLocator.FindCandidates(root))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var info = await JavaRuntimeProbe.ProbeAsync(candidate.Path, cancellationToken)
                .ConfigureAwait(false);
            if (info is null)
                continue;

            if (requiredMajor <= 0 || info.MajorVersion == requiredMajor)
                return info;

            if (info.MajorVersion > requiredMajor &&
                (bestAbove is null || info.MajorVersion < bestAbove.MajorVersion))
            {
                bestAbove = info;
            }
        }

        return bestAbove;
    }

    /// <summary>
    /// 下载并安装指定主版本的 Java 运行时。
    /// 按 Injdk → 华为云 → 清华 TUNA 的顺序尝试，全部失败时抛出异常。
    /// </summary>
    /// <param name="requiredMajor">Java 主版本（8 / 17 / 21 / 25 等）。</param>
    /// <param name="progress">下载 / 解压进度。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<JavaRuntimeInfo> InstallAsync(
        int requiredMajor,
        IProgress<InstallProgressInfo>? progress,
        CancellationToken cancellationToken = default)
    {
        var root = GetRuntimeRoot();
        Directory.CreateDirectory(root);

        var targetDirectory = Path.Combine(root, $"java-{requiredMajor}");
        var existingJava = FindJavaExecutable(targetDirectory);
        if (existingJava is not null)
        {
            var existing = await JavaRuntimeProbe.ProbeAsync(existingJava, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null && existing.MajorVersion >= requiredMajor)
                return existing;
        }

        var errors = new List<string>();

        foreach (var source in _sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var zipPath = Path.Combine(root, $".java-{requiredMajor}-{Guid.NewGuid():N}.zip");
            var stagingDirectory = Path.Combine(root, $".java-{requiredMajor}-{Guid.NewGuid():N}");

            try
            {
                string? url;
                try
                {
                    url = await source.ResolveUrlAsync(requiredMajor, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    errors.Add($"{source.Name} => 解析地址失败：{ex.Message}");
                    Log($"{source.Name} 解析地址失败：{ex.Message}");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(url))
                {
                    errors.Add($"{source.Name} => 没有匹配当前系统的 ZIP");
                    continue;
                }

                Log($"正在从 {source.Name} 下载 Java {requiredMajor}：{url}");

                await DownloadAsync(url, zipPath, requiredMajor, progress, cancellationToken)
                    .ConfigureAwait(false);

                progress?.Report(new InstallProgressInfo
                {
                    Stage = "正在解压 Java 运行时",
                    Ratio = 0,
                });

                Directory.CreateDirectory(stagingDirectory);
                await Task.Run(
                        () => ExtractZip(zipPath, stagingDirectory, progress, cancellationToken),
                        cancellationToken)
                    .ConfigureAwait(false);

                var extractedJava = FindJavaExecutable(stagingDirectory)
                    ?? throw new JavaRuntimeInstallException(
                        "解压完成但没有找到 bin/java.exe 或 bin/java");

                if (Directory.Exists(targetDirectory))
                    Directory.Delete(targetDirectory, recursive: true);

                Directory.Move(stagingDirectory, targetDirectory);

                var installedJava = FindJavaExecutable(targetDirectory)
                    ?? throw new JavaRuntimeInstallException(
                        "安装完成但没有找到 bin/java.exe 或 bin/java");

                JavaRuntimeProbe.ClearCache();
                var info = await JavaRuntimeProbe.ProbeAsync(installedJava, cancellationToken)
                    .ConfigureAwait(false);

                if (info is null || info.MajorVersion < requiredMajor)
                {
                    throw new JavaRuntimeInstallException(
                        $"Java 运行时校验失败（期望 {requiredMajor}，实际 {info?.MajorVersion ?? 0}）");
                }

                progress?.Report(new InstallProgressInfo
                {
                    Stage = "Java 运行时已就绪",
                    Ratio = 1,
                    SpeedText = string.Empty,
                });

                Log($"Java {requiredMajor} 运行时安装完成：{installedJava}");
                return info;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryDeleteDirectory(stagingDirectory);
                TryDeleteFile(zipPath);
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{source.Name} => {ex.GetType().Name}: {ex.Message}");
                Log($"{source.Name} 下载 / 解压失败：{ex.Message}");
                TryDeleteDirectory(stagingDirectory);
                TryDeleteFile(zipPath);

                // 已移动到目标目录但校验失败时，清理掉这个不完整运行时。
                if (Directory.Exists(targetDirectory) && FindJavaExecutable(targetDirectory) is null)
                    TryDeleteDirectory(targetDirectory);
            }
        }

        throw new JavaRuntimeInstallException(
            $"自动下载 Java {requiredMajor} 失败：{string.Join("；", errors)}");
    }

    private async Task DownloadAsync(
        string url,
        string destinationPath,
        int major,
        IProgress<InstallProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(20));

        using var response = await _client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? 0;
        await using var input = await response.Content
            .ReadAsStreamAsync(timeoutCts.Token)
            .ConfigureAwait(false);
        await using var output = new FileStream(
            destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[128 * 1024];
        long written = 0;
        var lastReportAt = 0L;
        var stopwatch = Stopwatch.StartNew();

        int read;
        while ((read = await input.ReadAsync(buffer, timeoutCts.Token).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), timeoutCts.Token).ConfigureAwait(false);
            written += read;

            if (stopwatch.ElapsedMilliseconds - lastReportAt < 200)
                continue;

            lastReportAt = stopwatch.ElapsedMilliseconds;
            var speed = written / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
            progress?.Report(new InstallProgressInfo
            {
                Stage = $"正在下载 Java {major} 运行时…",
                CurrentFile = Path.GetFileName(destinationPath),
                ProgressedBytes = written,
                TotalBytes = totalBytes,
                Ratio = totalBytes > 0 ? Math.Clamp((double)written / totalBytes, 0d, 1d) : 0d,
                SizeText = totalBytes > 0
                    ? $"{FormatSize(written)} / {FormatSize(totalBytes)}"
                    : FormatSize(written),
                SpeedText = DownloadSpeedFormatter.Format(speed),
            });
        }

        progress?.Report(new InstallProgressInfo
        {
            Stage = $"Java {major} 运行时下载完成",
            ProgressedBytes = written,
            TotalBytes = totalBytes,
            Ratio = 1,
            SizeText = totalBytes > 0
                ? $"{FormatSize(written)} / {FormatSize(totalBytes)}"
                : FormatSize(written),
        });
    }

    private static void ExtractZip(
        string zipPath,
        string targetDirectory,
        IProgress<InstallProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(targetDirectory);
        var rootPrefix = root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        using var archive = ZipFile.OpenRead(zipPath);
        var entries = archive.Entries;

        for (var index = 0; index < entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = entries[index];
            var destination = Path.GetFullPath(Path.Combine(root, entry.FullName));

            if (!destination.StartsWith(rootPrefix, comparison))
                throw new JavaRuntimeInstallException("ZIP 包含非法路径，已停止解压");

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }

            if (index % 25 == 0 || index == entries.Count - 1)
            {
                progress?.Report(new InstallProgressInfo
                {
                    Stage = "正在解压 Java 运行时",
                    Ratio = entries.Count > 0 ? (double)(index + 1) / entries.Count : 0d,
                    SizeText = $"{index + 1} / {entries.Count} 个文件",
                });
            }
        }
    }

    private async Task<string?> ResolveInjdkAsync(int major, CancellationToken cancellationToken)
    {
        var directoryUrl = $"https://d10.injdk.cn/openjdk/zulu/{major}/";
        var json = await GetStringAsync(directoryUrl, cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("name", out var nameElement))
                continue;

            var name = nameElement.GetString() ?? string.Empty;
            if (!name.EndsWith(GetZipSuffix(), StringComparison.OrdinalIgnoreCase))
                continue;

            var relativeUrl = item.TryGetProperty("url", out var urlElement)
                ? urlElement.GetString()
                : name;

            if (string.IsNullOrWhiteSpace(relativeUrl))
                relativeUrl = name;

            return new Uri(new Uri(directoryUrl), relativeUrl!).ToString();
        }

        return null;
    }

    private Task<string?> ResolveHuaweiAsync(int major, CancellationToken cancellationToken)
    {
        // 华为云当前提供的是 Oracle OpenJDK 的 Windows x64 ZIP。
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return Task.FromResult<string?>(null);

        return Task.FromResult<string?>(
            $"https://repo.huaweicloud.com/openjdk/{major}/openjdk-{major}_windows-x64_bin.zip");
    }

    private async Task<string?> ResolveTunaAsync(int major, CancellationToken cancellationToken)
    {
        var architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "aarch64"
            : "x64";
        var operatingSystem = OperatingSystem.IsWindows()
            ? "windows"
            : OperatingSystem.IsMacOS()
                ? "mac"
                : "linux";
        var directoryUrl =
            $"https://mirrors.tuna.tsinghua.edu.cn/Adoptium/{major}/jdk/{architecture}/{operatingSystem}/";

        var html = await GetStringAsync(directoryUrl, cancellationToken).ConfigureAwait(false);
        var matches = Regex.Matches(html, "href=\"([^\"]+\\.zip)\"", RegexOptions.IgnoreCase);

        foreach (Match match in matches)
        {
            var href = match.Groups[1].Value;
            if (href.Contains("Parent", StringComparison.OrdinalIgnoreCase))
                continue;

            return new Uri(new Uri(directoryUrl), href).ToString();
        }

        return null;
    }

    private async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));

        using var response = await _client.GetAsync(url, timeoutCts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
    }

    private string GetRuntimeRoot()
    {
        var configured = _directoryProvider();
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DPL",
                "runtime");
        }

        return Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(configured.Trim().Trim('"')));
    }

    private static string GetZipSuffix()
    {
        var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        if (OperatingSystem.IsWindows())
            return arm ? "win_aarch64.zip" : "win_x64.zip";

        if (OperatingSystem.IsMacOS())
            return arm ? "macosx_aarch64.zip" : "macosx_x64.zip";

        return arm ? "linux_aarch64.zip" : "linux_x64.zip";
    }

    private static string? FindJavaExecutable(string rootDirectory)
    {
        try
        {
            if (!Directory.Exists(rootDirectory))
                return null;

            string[] names = OperatingSystem.IsWindows()
                ? ["java.exe", "javaw.exe"]
                : ["java"];
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var comparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

            return Directory
                .EnumerateFiles(rootDirectory, "*", SearchOption.AllDirectories)
                .Where(file =>
                {
                    var fileName = Path.GetFileName(file);
                    if (!names.Contains(fileName, comparer))
                        return false;

                    var parent = Path.GetFileName(Path.GetDirectoryName(file) ?? string.Empty);
                    return string.Equals(parent, "bin", comparison);
                })
                .OrderByDescending(file =>
                    string.Equals(Path.GetFileName(file), "java.exe", comparison))
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string FormatSize(long bytes)
    {
        const double kb = 1024d;
        const double mb = kb * 1024d;
        const double gb = mb * 1024d;

        return bytes switch
        {
            >= (long)gb => $"{bytes / gb:0.00} GB",
            >= (long)mb => $"{bytes / mb:0.0} MB",
            >= (long)kb => $"{bytes / kb:0.0} KB",
            _ => $"{bytes} B",
        };
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception)
        {
            // 清理失败不影响下一次尝试。
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
            // 清理失败不影响下一次尝试。
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
            // 日志订阅者异常不能中断下载。
        }
    }

    private sealed record JavaDownloadSource(
        string Name,
        Func<int, CancellationToken, Task<string?>> ResolveUrlAsync);
}

/// <summary>Java 自动下载 / 解压失败。</summary>
public sealed class JavaRuntimeInstallException : Exception
{
    /// <summary>创建异常。</summary>
    public JavaRuntimeInstallException(string message) : base(message)
    {
    }
}
