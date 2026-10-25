using System.Diagnostics;
using System.Globalization;
using System.Net;

namespace DPL.Services;

/// <summary>
/// 下载镜像处理器：对 <c>maven.neoforged.net</c> 的请求按顺序尝试官方源与国内可达的镜像，
/// 官方源超时或失败后自动切换到镜像，并把每次尝试的地址与失败原因写进日志。
/// 其它域名（Mojang / Fabric 等）的请求原样放行，保持流式下载不受影响。
/// </summary>
public sealed class MirrorHttpHandler : DelegatingHandler
{
    /// <summary>NeoForge 官方 Maven 的域名。</summary>
    private const string NeoForgeHost = "maven.neoforged.net";

    /// <summary>NeoForge 官方 Maven 中存放构件的路径前缀。</summary>
    private const string NeoForgeReleasesPrefix = "/releases/";

    private static readonly object LogLock = new();

    /// <summary>
    /// 镜像尝试日志（可能在后台线程触发），界面可订阅后显示到日志面板。
    /// </summary>
    public static event Action<string>? LogWritten;

    /// <summary>
    /// 一次镜像尝试的超时时间：官方源给的时间短一些，避免国内网络卡在超时上；
    /// 镜像给足时间，避免大文件被误判为失败。
    /// </summary>
    /// <summary>
    /// NeoForge 官方 Maven 源：命中率低（经常超时），只作为兜底，超时压到 3 秒。
    /// </summary>
    private static readonly MirrorSource NeoForgeOfficialSource =
        new("官方源", "https://maven.neoforged.net/", "/releases/", TimeSpan.FromSeconds(3));

    /// <summary>
    /// NeoForge 下载源顺序：BMCLAPI 优先（实测 ~0.8 秒），官方源兜底，再往后是其它镜像。
    /// </summary>
    private static readonly MirrorSource[] NeoForgeSources =
    [
        new("BMCLAPI 镜像", "https://bmclapi2.bangbang93.com/", "/maven/", TimeSpan.FromSeconds(45)),
        NeoForgeOfficialSource,
        new("ForgeCDN 镜像", "https://neoforged.forgecdn.net/", "/releases/", TimeSpan.FromSeconds(45)),
        new("CreeperHost 镜像", "https://maven.creeperhost.net/", "/", TimeSpan.FromSeconds(45)),
    ];

    private readonly object _preferenceLock = new();

    /// <summary>记住上一次成功的源，后续文件直接走它，避免每次都先等官方源超时。</summary>
    private int _preferredSourceIndex = -1;

    /// <summary>
    /// 创建镜像处理器。
    /// </summary>
    /// <param name="innerHandler">真正负责发请求的处理器。</param>
    public MirrorHttpHandler(HttpMessageHandler innerHandler) : base(innerHandler)
    {
    }

    /// <summary>
    /// 镜像日志文件的完整路径（用于排查问题）。
    /// </summary>
    public static string LogFilePath => AppPaths.MirrorLogFile;

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!IsNeoForgeRequest(request.RequestUri) || request.Content is not null)
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var absolutePath = request.RequestUri!.AbsolutePath;
        var candidates = BuildCandidates(absolutePath);

        // 没有可用的镜像（例如 /api/ 接口）时直接走官方源。
        if (candidates.Count <= 1)
        {
            Log($"「{absolutePath}」不在镜像范围内，直接请求官方源");
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var failures = new List<string>();
        var stopwatch = Stopwatch.StartNew();

        for (var i = 0; i < candidates.Count; i++)
        {
            var (source, uri) = candidates[i];
            cancellationToken.ThrowIfCancellationRequested();

            var attemptWatch = Stopwatch.StartNew();
            Log($"尝试「{source.Name}」：{uri}");

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(source.Timeout);

            try
            {
                using var response = await base
                    .SendAsync(CloneRequest(request, uri), attemptCts.Token)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var reason = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                    failures.Add($"{source.Name}: {reason}");
                    Log($"「{source.Name}」失败（{Describe(attemptWatch)}）：{reason}");
                    continue;
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(attemptCts.Token).ConfigureAwait(false);

                MarkPreferred(source);
                Log($"「{source.Name}」成功（{Describe(attemptWatch)}，{FormatSize(bytes.Length)}）");

                return BuildBufferedResponse(request, response, bytes);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                failures.Add($"{source.Name}: 超时（{source.Timeout.TotalSeconds:0} 秒）");
                Log($"「{source.Name}」失败（{Describe(attemptWatch)}）：超时（{source.Timeout.TotalSeconds:0} 秒）");
            }
            catch (Exception ex)
            {
                failures.Add($"{source.Name}: {ex.GetType().Name}: {ex.Message}");
                Log($"「{source.Name}」失败（{Describe(attemptWatch)}）：{ex.GetType().Name}: {ex.Message}");
            }
        }

        Log($"所有源都失败（总耗时 {Describe(stopwatch)}）：{string.Join("；", failures)}");
        throw new HttpRequestException(
            $"无法下载 NeoForge 文件（官方源与镜像均失败）：{string.Join("；", failures)}");
    }

    /// <summary>
    /// 判断请求是否指向 NeoForge 官方 Maven。
    /// </summary>
    private static bool IsNeoForgeRequest(Uri? uri)
        => uri is not null && string.Equals(uri.Host, NeoForgeHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 构造候选下载地址：优先使用上次成功的源，其余按「官方 → BMCLAPI → ForgeCDN → CreeperHost」顺序。
    /// </summary>
    private List<(MirrorSource Source, Uri Uri)> BuildCandidates(string absolutePath)
    {
        var list = new List<(MirrorSource, Uri)>();

        // 只有 /releases/ 下的构件和元数据有镜像；其它路径（如 /api/...）保持原样。
        if (!absolutePath.StartsWith(NeoForgeReleasesPrefix, StringComparison.Ordinal))
        {
            list.Add((NeoForgeOfficialSource, new Uri($"https://{NeoForgeHost}{absolutePath}")));
            return list;
        }

        var relativePath = absolutePath[NeoForgeReleasesPrefix.Length..];

        foreach (var index in OrderedSourceIndexes())
        {
            var source = NeoForgeSources[index];
            var prefix = source.PathPrefix;
            var builder = new UriBuilder(new Uri(source.BaseUrl))
            {
                Path = prefix + relativePath,
            };

            list.Add((source, builder.Uri));
        }

        return list;
    }

    /// <summary>
    /// 返回尝试顺序：如果之前有成功的源，则把它排到最前面。
    /// </summary>
    private IEnumerable<int> OrderedSourceIndexes()
    {
        int preferred;
        lock (_preferenceLock)
            preferred = _preferredSourceIndex;

        if (preferred >= 0 && preferred < NeoForgeSources.Length)
            yield return preferred;

        for (var i = 0; i < NeoForgeSources.Length; i++)
        {
            if (i != preferred)
                yield return i;
        }
    }

    /// <summary>
    /// 记录成功的源，供后续请求优先使用。
    /// </summary>
    private void MarkPreferred(MirrorSource source)
    {
        var index = Array.IndexOf(NeoForgeSources, source);
        if (index < 0)
            return;

        lock (_preferenceLock)
        {
            if (_preferredSourceIndex == index)
                return;

            _preferredSourceIndex = index;
        }

        Log($"后续 NeoForge 下载将优先使用「{source.Name}」");
    }

    /// <summary>
    /// 为每个候选源复制一份请求（方法、版本与请求头），避免复用已发送的请求对象。
    /// </summary>
    private static HttpRequestMessage CloneRequest(HttpRequestMessage template, Uri uri)
    {
        var clone = new HttpRequestMessage(template.Method, uri)
        {
            Version = template.Version,
            VersionPolicy = template.VersionPolicy,
        };

        foreach (var header in template.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        return clone;
    }

    /// <summary>
    /// 把已经读进内存的内容重新包装成响应，避免调用方在超时取消后读到断掉的流。
    /// </summary>
    private static HttpResponseMessage BuildBufferedResponse(
        HttpRequestMessage request,
        HttpResponseMessage source,
        byte[] bytes)
    {
        var result = new HttpResponseMessage(source.StatusCode)
        {
            RequestMessage = request,
            Version = source.Version,
            ReasonPhrase = source.ReasonPhrase,
        };

        var content = new ByteArrayContent(bytes);
        foreach (var header in source.Content.Headers)
            content.Headers.TryAddWithoutValidation(header.Key, header.Value);

        result.Content = content;

        foreach (var header in source.Headers)
            result.Headers.TryAddWithoutValidation(header.Key, header.Value);

        return result;
    }

    /// <summary>
    /// 把尝试耗时格式化成便于阅读的文本。
    /// </summary>
    private static string Describe(Stopwatch stopwatch)
        => stopwatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " 秒";

    /// <summary>
    /// 字节数格式化。
    /// </summary>
    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? (bytes / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
        : (bytes / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " KB";

    /// <summary>
    /// 写一条镜像日志：同时写调试输出、日志文件，并通知订阅者。
    /// </summary>
    private static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] [镜像] {message}";
        Debug.WriteLine(line);
        WriteToFile(line);

        try
        {
            LogWritten?.Invoke(message);
        }
        catch (Exception)
        {
            // 日志订阅者出错不能影响下载。
        }
    }

    /// <summary>
    /// 追加写入镜像日志文件（超过 2 MB 时重新开始，避免无限增长）。
    /// </summary>
    private static void WriteToFile(string line)
    {
        lock (LogLock)
        {
            try
            {
                var path = LogFilePath;
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                var info = new FileInfo(path);
                if (info.Exists && info.Length > 2 * 1024 * 1024)
                    File.Delete(path);

                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (Exception)
            {
                // 日志写失败不影响下载。
            }
        }
    }

    /// <summary>
    /// 一个镜像源：名称、基地址、路径前缀与尝试超时。
    /// </summary>
    private sealed record MirrorSource(string Name, string BaseUrl, string PathPrefix, TimeSpan Timeout);
}
