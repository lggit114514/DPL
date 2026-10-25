using System.Diagnostics;
using System.Net;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Installer.Forge;
using CmlLib.Core.Installer.NeoForge;
using CmlLib.Core.Installer.Forge.Versions;
using CmlLib.Core.Installers;
using CmlLib.Core.ModLoaders.FabricMC;
using CmlLib.Core.ModLoaders.QuiltMC;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.Version;
using CmlLib.Core.VersionLoader;
using CmlLib.Core.VersionMetadata;
using DPL.Models;

namespace DPL.Services;

/// <summary>
/// <see cref="IMinecraftService"/> 的默认实现。
/// 所有与 Mojang 通信、文件下载、进程构建的逻辑都委托给 CmlLib.Core，本类不自行调用任何 Mojang API。
/// </summary>
public sealed class MinecraftService : IMinecraftService, IDisposable
{
    /// <summary>文件路径比较：Windows 忽略大小写，macOS/Linux 区分大小写。</summary>
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>CmlLib 文件级并行下载数。</summary>
    private const int DownloadParallelism = 32;

    private readonly HttpClient _httpClient;
    private MinecraftPath _minecraftPath;
    private MinecraftLauncher _launcher;
    private string _gameDirectory;
    private string? _customVersionsDirectory;

    /// <summary>
    /// 使用指定游戏目录创建服务实例。
    /// </summary>
    /// <param name="gameDirectory">游戏目录（.minecraft）绝对路径。</param>
    public MinecraftService(string gameDirectory)
    {
        _httpClient = CreateHttpClient();
        _gameDirectory = gameDirectory;
        _minecraftPath = CreateMinecraftPath();
        _launcher = CreateLauncher(_minecraftPath);
    }

    /// <inheritdoc />
    public string GameDirectory => _gameDirectory;

    /// <inheritdoc />
    public string VersionsDirectory => _minecraftPath.Versions;

    /// <inheritdoc />
    public void UseGameDirectory(string gameDirectory)
    {
        if (string.Equals(gameDirectory, _gameDirectory, PathComparison))
            return;

        _gameDirectory = gameDirectory;
        Rebuild();
    }

    /// <inheritdoc />
    public void UseVersionsDirectory(string? versionsDirectory)
    {
        var normalized = string.IsNullOrWhiteSpace(versionsDirectory)
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(versionsDirectory.Trim().Trim('"')));

        if (string.Equals(normalized, _customVersionsDirectory, PathComparison))
            return;

        _customVersionsDirectory = normalized;
        Rebuild();
    }

    /// <inheritdoc />
    public string GetVersionDirectory(string versionName)
        => Path.Combine(VersionsDirectory, versionName);

    /// <summary>
    /// 按当前游戏目录与版本目录重建 CmlLib 的路径对象与启动器实例。
    /// </summary>
    private void Rebuild()
    {
        _minecraftPath = CreateMinecraftPath();
        _launcher = CreateLauncher(_minecraftPath);
    }

    /// <summary>
    /// 根据当前设置创建 <see cref="MinecraftPath"/>，并在存在自定义版本目录时覆盖 versions 路径。
    /// </summary>
    private MinecraftPath CreateMinecraftPath()
    {
        var path = new MinecraftPath(_gameDirectory);

        if (!string.IsNullOrWhiteSpace(_customVersionsDirectory))
        {
            // 仅替换版本目录，其他目录（libraries / assets / runtime）仍然位于游戏目录下。
            path.Versions = _customVersionsDirectory;
        }

        try
        {
            Directory.CreateDirectory(path.Versions);
        }
        catch (Exception)
        {
            // 目录不可用时保持原样，后续读取列表会返回空结果而不会崩溃。
        }

        return path;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MinecraftVersionInfo>> GetLocalVersionsAsync(CancellationToken cancellationToken = default)
    {
        var launcher = _launcher;
        try
        {
            // LocalJsonVersionLoader 只扫描 <游戏目录>/versions 下的本地版本。
            var loader = new LocalJsonVersionLoader(launcher.MinecraftPath);
            var metadata = await loader.GetVersionMetadatasAsync(cancellationToken);
            return await BuildLocalVersionListAsync(metadata, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 版本目录可能被其他启动器写入不完整的 JSON，读取失败时返回空列表而不是崩溃。
            return Array.Empty<MinecraftVersionInfo>();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MinecraftVersionInfo>> GetOnlineVersionsAsync(CancellationToken cancellationToken = default)
    {
        var launcher = _launcher;

        // 先取本地已安装版本，用于在线列表中的“已安装”标记。
        var installedNames = new HashSet<string>(PathComparer);
        try
        {
            var localLoader = new LocalJsonVersionLoader(launcher.MinecraftPath);
            var localMetadata = await localLoader.GetVersionMetadatasAsync(cancellationToken);
            foreach (var item in localMetadata)
                installedNames.Add(item.Name);
        }
        catch (Exception)
        {
            // 本地列表读取失败不影响在线列表展示。
        }

        // MojangJsonVersionLoaderV2 通过 CmlLib 拉取 Mojang 官方的版本清单。
        var mojangLoader = new MojangJsonVersionLoaderV2(launcher.MinecraftPath, _httpClient);
        var onlineMetadata = await mojangLoader.GetVersionMetadatasAsync(cancellationToken);
        return BuildVersionList(onlineMetadata, installedNames);
    }

    /// <inheritdoc />
    public async Task InstallAsync(
        string versionName,
        IProgress<InstallProgressInfo>? progress,
        CancellationToken cancellationToken = default)
    {
        var launcher = _launcher;
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report(new InstallProgressInfo
        {
            Stage = $"正在准备安装 {versionName}",
            Ratio = 0,
        });

        // 文件进度事件会触发数千次，这里只记录字段，避免频繁分配对象和刷新界面。
        var progressedTasks = 0;
        var totalTasks = 0;
        string? currentFile = null;

        var fileProgress = new SyncProgress<InstallerProgressChangedEventArgs>(e =>
        {
            progressedTasks = e.ProgressedTasks;
            totalTasks = e.TotalTasks;
            currentFile = e.Name;
        });

        // 字节进度每秒触发数次，在这里统一向界面汇报，避免界面被高频刷新拖慢。
        var byteProgress = new SyncProgress<ByteProgress>(e =>
        {
            var totalBytes = e.TotalBytes;
            var ratio = totalBytes > 0 ? Math.Clamp(e.ToRatio(), 0d, 1d) : 0d;

            progress?.Report(new InstallProgressInfo
            {
                Stage = "正在校验并下载游戏文件",
                CurrentFile = currentFile,
                ProgressedTasks = progressedTasks,
                TotalTasks = totalTasks,
                ProgressedBytes = e.ProgressedBytes,
                TotalBytes = totalBytes,
                Ratio = ratio,
                SizeText = totalBytes > 0
                    ? $"{FormatSize(e.ProgressedBytes)} / {FormatSize(totalBytes)}"
                    : $"{FormatSize(e.ProgressedBytes)}",
            });
        });

        // InstallAsync 会先校验文件完整性，只下载缺失或损坏的文件，因此每次启动前调用也是安全的。
        await launcher.InstallAsync(versionName, fileProgress, byteProgress, cancellationToken)
            .ConfigureAwait(false);

        // 文件补全可能修复了残缺的 Java 运行时，必须清空探测缓存，避免继续使用旧的“不可用”结论。
        JavaRuntimeProbe.ClearCache();

        progress?.Report(new InstallProgressInfo
        {
            Stage = $"{versionName} 已安装完成",
            Ratio = 1,
        });
    }

    /// <inheritdoc />
    public async Task<int> LaunchAsync(
        string versionName,
        LaunchRequest request,
        IProgress<InstallProgressInfo>? progress = null,
        Action<string>? gameLog = null,
        CancellationToken cancellationToken = default)
    {
        var launcher = _launcher;

        // 启动前先补齐文件，确保游戏本体的 jar、库文件、资源文件以及 Java 运行时完整。
        if (request.CheckFilesBeforeLaunch)
            await InstallAsync(versionName, progress, cancellationToken).ConfigureAwait(false);

        var javaPath = await ResolveJavaPathAsync(versionName, request.JavaPath, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(javaPath))
        {
            throw new InvalidOperationException(
                "未找到可用的 Java 运行时。请在“设置”中选择 Java 路径，或勾选“启动前校验并补全游戏文件”让启动器修复官方运行时。");
        }

        // 使用调用方提供的会话（微软正版账号），否则创建离线会话：
        // 离线会话不连接 Mojang 认证服务器，仅本地生成用户名与 UUID。
        var session = request.Session ?? MSession.CreateOfflineSession(request.PlayerName);

        var launchOption = new MLaunchOption
        {
            Session = session,
            Path = launcher.MinecraftPath,
            JavaPath = javaPath,
            MaximumRamMb = Math.Max(512, request.MaxMemoryMb),
            ScreenWidth = Math.Max(0, request.WindowWidth),
            ScreenHeight = Math.Max(0, request.WindowHeight),
            FullScreen = request.FullScreen,
            VersionType = "DPL",
            GameLauncherName = "DPL",
            GameLauncherVersion = "1.0.0",
        };

        // 最小堆内存必须不大于最大堆内存，否则 CmlLib 会抛出参数异常。
        if (request.MinimumMemoryMb > 0 && request.MinimumMemoryMb < launchOption.MaximumRamMb)
            launchOption.MinimumRamMb = request.MinimumMemoryMb;

        // 版本隔离：把某个目录（通常是版本文件夹）当作游戏目录，用于各版本独立的 mods / saves。
        if (!string.IsNullOrWhiteSpace(request.InstanceDirectory) && Directory.Exists(request.InstanceDirectory))
            launchOption.Path = CreateInstancePath(request.InstanceDirectory);

        // 额外 JVM 参数（例如强制指定编码）。
        if (request.ExtraJvmArguments is { Count: > 0 })
            launchOption.ExtraJvmArguments = request.ExtraJvmArguments
                .Select(argument => new MArgument(argument))
                .ToArray();

        // BuildProcessAsync 由 CmlLib 解析版本 JSON、拼接启动参数并创建进程对象。
        var process = await launcher.BuildProcessAsync(versionName, launchOption, cancellationToken)
            .ConfigureAwait(false);

        // 同时捕获标准错误输出：JVM 自身报错（例如 jvm.dll 加载失败）只会写到 stderr。
        try
        {
            process.StartInfo.RedirectStandardError = true;
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    gameLog?.Invoke(e.Data);
            };
        }
        catch (Exception)
        {
            // 部分平台不允许再次修改重定向设置，忽略即可。
        }

        var wrapper = new ProcessWrapper(process);
        if (gameLog is not null)
            wrapper.OutputReceived += (_, line) => gameLog(line);

        wrapper.StartWithEvents();

        try
        {
            process.BeginErrorReadLine();
        }
        catch (Exception)
        {
            // 未成功重定向时忽略。
        }

        // 等待游戏进程退出，避免启动器提前释放资源。
        return await wrapper.WaitForExitTaskAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string?> ResolveJavaPathAsync(
        string versionName,
        string? preferredPath,
        CancellationToken cancellationToken = default)
        => (await ResolveJavaRuntimeAsync(versionName, preferredPath, cancellationToken).ConfigureAwait(false))?.Path;

    /// <inheritdoc />
    public async Task<JavaRuntimeInfo?> ResolveJavaRuntimeAsync(
        string versionName,
        string? preferredPath,
        CancellationToken cancellationToken = default)
    {
        var launcher = _launcher;

        IVersion? version = null;
        try
        {
            version = await launcher.GetVersionAsync(versionName, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 版本 JSON 读取失败时，退化为“不校验 Java 版本”的探测流程。
        }

        // 该版本要求的最低 Java 主版本（来自版本 JSON 的 javaVersion.majorVersion）。
        var requiredMajor = int.TryParse(version?.JavaVersion?.MajorVersion, out var parsedMajor)
            ? parsedMajor
            : 0;

        // 按优先级收集候选 Java 路径。
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(preferredPath))
            candidates.Add(preferredPath);

        // CmlLib 运行时目录中与该版本 javaVersion.component 对应的 Java。
        try
        {
            if (version is not null)
            {
                var componentJava = launcher.GetJavaPath(version);
                if (!string.IsNullOrWhiteSpace(componentJava))
                    candidates.Add(componentJava);
            }
        }
        catch (Exception)
        {
            // 忽略。
        }

        // CmlLib 运行时目录中任意一个 Java。
        try
        {
            var defaultJava = launcher.GetDefaultJavaPath();
            if (!string.IsNullOrWhiteSpace(defaultJava))
                candidates.Add(defaultJava);
        }
        catch (Exception)
        {
            // 忽略。
        }

        // 运行时目录中所有 Java（避免组件名与版本 JSON 不一致时漏检）。
        candidates.AddRange(EnumerateRuntimeJavaPaths());

        // 本机安装的 Java。
        candidates.AddRange(FindJavaCandidates().Select(candidate => candidate.Path));

        var distinctCandidates = candidates
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(PathComparer)
            .ToList();

        if (distinctCandidates.Count == 0)
            return null;

        var details = new List<string>();
        JavaRuntimeInfo? bestBelowRequirement = null;

        foreach (var candidate in distinctCandidates)
        {
            var info = await JavaRuntimeProbe.ProbeAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (info is null)
            {
                details.Add($"{candidate}（无法启动）");
                continue;
            }

            // 用户手动指定的 Java 视为明确意图，即使版本偏低也直接使用。
            if (!string.IsNullOrWhiteSpace(preferredPath) &&
                string.Equals(candidate, preferredPath, PathComparison))
            {
                return info;
            }

            if (requiredMajor <= 0 || info.MajorVersion >= requiredMajor)
                return info;

            details.Add($"{candidate}（Java {info.MajorVersion}）");
            if (bestBelowRequirement is null || info.MajorVersion > bestBelowRequirement.MajorVersion)
                bestBelowRequirement = info;
        }

        // 没有任何 Java 满足版本要求：给出可执行的错误提示，而不是让游戏静默秒退。
        var requirementText = requiredMajor > 0 ? $"该版本需要 Java {requiredMajor} 或更高版本。" : string.Empty;
        var detailText = details.Count > 0 ? $"已检测到的 Java：{string.Join("；", details)}。" : string.Empty;

        throw new InvalidOperationException(
            $"未找到可用的 Java 运行时。{requirementText}{detailText}" +
            "请在“设置”中指定一个可用的 Java，或勾选“启动前校验并补全游戏文件”让启动器自动修复官方运行时。");
    }

    /// <inheritdoc />
    public IReadOnlyList<JavaCandidate> FindJavaCandidates() => JavaLocator.FindCandidates();

    /// <inheritdoc />
    public bool LooksLikeIsolatedInstance(string versionName)
    {
        var directory = GetVersionDirectory(versionName);
        if (!Directory.Exists(directory))
            return false;

        // PCL2 / HMCL 的“版本隔离”会把 mods、saves、options.txt 等放进版本文件夹，
        // 而官方启动器的版本文件夹里只有 json / jar / natives，据此判断。
        string[] markers = ["mods", "saves", "options.txt", "config", ".fabric", ".hmcl"];
        var matched = 0;

        foreach (var marker in markers)
        {
            var path = Path.Combine(directory, marker);
            if (Directory.Exists(path) || File.Exists(path))
                matched++;
        }

        return matched >= 1 && (Directory.Exists(Path.Combine(directory, "mods")) ||
                                File.Exists(Path.Combine(directory, "options.txt")) ||
                                Directory.Exists(Path.Combine(directory, "saves")));
    }

    /// <inheritdoc />
    public async Task<bool> IsModLoaderSupportedAsync(
        ModLoaderKind kind,
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        if (kind == ModLoaderKind.Vanilla)
            return true;

        var versions = await GetModLoaderVersionsAsync(kind, minecraftVersion, cancellationToken)
            .ConfigureAwait(false);
        return versions.Count > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModLoaderVersionInfo>> GetModLoaderVersionsAsync(
        ModLoaderKind kind,
        string minecraftVersion,
        CancellationToken cancellationToken = default)
        => (await QueryModLoaderVersionsAsync(kind, minecraftVersion, cancellationToken).ConfigureAwait(false)).Versions;

    /// <inheritdoc />
    public async Task<ModLoaderQueryResult> QueryModLoaderVersionsAsync(
        ModLoaderKind kind,
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        if (kind == ModLoaderKind.Vanilla || string.IsNullOrWhiteSpace(minecraftVersion))
            return new ModLoaderQueryResult(Array.Empty<ModLoaderVersionInfo>(), null);

        cancellationToken.ThrowIfCancellationRequested();

        // 每种加载器官网的地址，用于在失败时给出可执行的提示。
        var host = kind switch
        {
            ModLoaderKind.Fabric => "meta.fabricmc.net",
            ModLoaderKind.Quilt => "meta.quiltmc.org",
            ModLoaderKind.Forge => "files.minecraftforge.net",
            ModLoaderKind.NeoForge => "maven.neoforged.net",
            _ => "加载器官网",
        };

        try
        {
            if (kind == ModLoaderKind.Fabric)
            {
                // FabricInstaller 从 meta.fabricmc.net 拉取该游戏版本可用的加载器列表。
                var installer = new FabricInstaller(_httpClient);
                var loaders = await installer.GetLoaders(minecraftVersion).ConfigureAwait(false);

                var fabricList = loaders
                    .Where(loader => !string.IsNullOrWhiteSpace(loader.Version))
                    .Select(loader => new ModLoaderVersionInfo(
                        loader.Version!, loader.Build, loader.Stable,
                        loader.Stable ? "稳定版" : "测试版"))
                    .ToList();

                return new ModLoaderQueryResult(fabricList, null);
            }

            if (kind == ModLoaderKind.Quilt)
            {
                var installer = new QuiltInstaller(_httpClient);
                var loaders = await installer.GetLoaders(minecraftVersion).ConfigureAwait(false);

                var quiltList = loaders
                    .Where(loader => !string.IsNullOrWhiteSpace(loader.Version))
                    .Select(loader => new ModLoaderVersionInfo(
                        loader.Version!, loader.Build ?? 0, loader.Stable,
                        loader.Stable ? "稳定版" : "测试版"))
                    .ToList();

                return new ModLoaderQueryResult(quiltList, null);
            }

            if (kind == ModLoaderKind.Forge)
            {
                // ForgeInstaller 从 files.minecraftforge.net 拉取该游戏版本可用的 Forge 列表。
                var installer = new ForgeInstaller(_launcher, _httpClient);
                var versions = await installer.GetForgeVersions(minecraftVersion).ConfigureAwait(false);

                var forgeList = versions
                    .Where(v => !string.IsNullOrWhiteSpace(v.ForgeVersionName))
                    .Select(v => new ModLoaderVersionInfo(
                        v.ForgeVersionName,
                        0,
                        v.IsRecommendedVersion,
                        v.IsRecommendedVersion ? "推荐" : v.IsLatestVersion ? "最新" : string.Empty))
                    .ToList();

                return new ModLoaderQueryResult(forgeList, null);
            }

            if (kind == ModLoaderKind.NeoForge)
            {
                // NeoForge 官方只提供 /api/maven/versions 接口（镜像没有），因此这里改为解析
                // maven-metadata.xml —— 它既在官方源上，也在各个镜像上，配合镜像处理器就能国内直连。
                var list = await GetNeoForgeVersionsAsync(minecraftVersion, cancellationToken).ConfigureAwait(false);
                return new ModLoaderQueryResult(list, null);
            }

            return new ModLoaderQueryResult(Array.Empty<ModLoaderVersionInfo>(), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 网络不可用或该版本没有对应加载器时返回空列表，并把原因带回去由界面提示用户。
            return new ModLoaderQueryResult(
                Array.Empty<ModLoaderVersionInfo>(),
                $"无法获取加载器版本列表：{ex.Message}（请检查网络或代理是否能访问 {host}）");
        }
    }

    /// <summary>
    /// NeoForge 官方 Maven 的 metadata 地址（镜像处理器会把请求自动重定向到国内可达的镜像）。
    /// </summary>
    private const string NeoForgeMetadataUrl =
        "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml";

    /// <summary>NeoForge 版本列表缓存的写入时间。</summary>
    private DateTime _neoForgeVersionCacheTime;

    /// <summary>
    /// 从 maven-metadata.xml 解析出指定 Minecraft 版本可用的 NeoForge 版本（按版本号从新到旧）。
    /// 元数据请求走镜像处理器，因此官方源不可达时会自动切换镜像。
    /// </summary>
    /// <param name="minecraftVersion">Minecraft 版本号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<IReadOnlyList<ModLoaderVersionInfo>> GetNeoForgeVersionsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var all = await GetAllNeoForgeVersionsAsync(cancellationToken).ConfigureAwait(false);
        if (all.Count == 0)
            return Array.Empty<ModLoaderVersionInfo>();

        var wanted = NormalizeMinecraftVersion(minecraftVersion);
        return all
            .Where(v => string.Equals(v.MinecraftVersionName, wanted, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => v.SortKey)
            .ThenByDescending(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .Select(v => v.Info)
            .ToList();
    }

    /// <summary>
    /// 下载并解析 NeoForge 的 maven-metadata.xml，结果缓存 10 分钟。
    /// </summary>
    private async Task<IReadOnlyList<NeoForgeEntry>> GetAllNeoForgeVersionsAsync(CancellationToken cancellationToken)
    {
        // 缓存里只保存“全部版本”的解析结果。
        if (_neoForgeAllVersions is { Count: > 0 } cached &&
            DateTime.UtcNow - _neoForgeVersionCacheTime < TimeSpan.FromMinutes(10))
        {
            return cached;
        }

        var xml = await _httpClient.GetStringAsync(NeoForgeMetadataUrl, cancellationToken).ConfigureAwait(false);

        var document = System.Xml.Linq.XDocument.Parse(xml);
        var entries = new List<NeoForgeEntry>();

        foreach (var element in document.Descendants("version"))
        {
            var name = element.Value?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var mapped = MapNeoForgeMinecraftVersion(name);
            if (mapped is null)
                continue;

            entries.Add(new NeoForgeEntry(
                Name: name,
                MinecraftVersionName: mapped,
                SortKey: BuildNeoForgeSortKey(name),
                Info: new ModLoaderVersionInfo(name, 0, false, string.Empty)));
        }

        // 标记每个 Minecraft 版本下最新的一个（列表按版本号倒序展示，界面里会把它标成“最新”）。
        var latestByMinecraft = entries
            .GroupBy(e => e.MinecraftVersionName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(e => e.SortKey).ThenByDescending(e => e.Name).First().Name,
                StringComparer.OrdinalIgnoreCase);

        entries = entries
            .Select(e => e with
            {
                Info = e.Info with { Badge = string.Equals(e.Name, latestByMinecraft[e.MinecraftVersionName],
                    StringComparison.OrdinalIgnoreCase) ? "最新" : IsNeoForgeBeta(e.Name) ? "测试版" : string.Empty },
            })
            .ToList();

        _neoForgeAllVersions = entries;
        _neoForgeVersionCacheTime = DateTime.UtcNow;

        return entries;
    }

    /// <summary>全部 NeoForge 版本（含映射出的 Minecraft 版本）。</summary>
    private List<NeoForgeEntry>? _neoForgeAllVersions;

    /// <summary>
    /// 把 NeoForge 版本名映射到它支持的 Minecraft 版本（规则与 CmlLib 的 NeoForgeVersionMapper 一致）。
    /// </summary>
    /// <param name="name">NeoForge 版本名，例如 <c>21.1.80</c>、<c>26.1.2.122</c>、<c>0.25w14craftmine.5-beta</c>。</param>
    private static string? MapNeoForgeMinecraftVersion(string name)
    {
        if (name.StartsWith("neoforge-", StringComparison.Ordinal))
            name = name["neoforge-".Length..];

        // 快照版本：0.<快照 ID>.<构建号>
        if (name.StartsWith("0.", StringComparison.Ordinal))
        {
            var snapshotParts = name.Split('.');
            if (snapshotParts.Length >= 3 && snapshotParts[1].Any(char.IsLetter))
            {
                var build = new string(snapshotParts[2].TakeWhile(char.IsDigit).ToArray());
                if (int.TryParse(build, out _))
                    return NormalizeMinecraftVersion(snapshotParts[1]);
            }

            return null;
        }

        var numberLength = 0;
        while (numberLength < name.Length &&
               (char.IsAsciiDigit(name[numberLength]) || name[numberLength] == '.'))
        {
            numberLength++;
        }

        if (numberLength == 0)
            return null;

        var parts = name[..numberLength].Split('.');
        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out numbers[i]))
                return null;
        }

        // 20.x.y / 21.x.y：NeoForge 版本号前两段对应 MC 1.x.y
        if (numbers.Length >= 3 && numbers[0] is 20 or 21)
            return NormalizeMinecraftVersion($"1.{numbers[0]}.{numbers[1]}");

        // 26.x.y.z：NeoForge 版本号前三段对应 MC 26.x.y
        if (numbers.Length >= 4 && numbers[0] >= 26)
            return NormalizeMinecraftVersion($"{numbers[0]}.{numbers[1]}.{numbers[2]}");

        return null;
    }

    /// <summary>
    /// 去掉形如 <c>1.21.0</c> 末尾的 <c>.0</c>，与 CmlLib 的版本映射保持一致。
    /// </summary>
    private static string NormalizeMinecraftVersion(string version)
    {
        var parts = version.Split('.');
        return parts.Length == 3 && parts[2] == "0"
            ? string.Join(".", parts.Take(2))
            : version;
    }

    /// <summary>
    /// 生成用于排序的数值键（忽略 -beta 之类的后缀）。
    /// </summary>
    private static long BuildNeoForgeSortKey(string name)
    {
        if (name.StartsWith("neoforge-", StringComparison.Ordinal))
            name = name["neoforge-".Length..];

        var numberLength = 0;
        while (numberLength < name.Length &&
               (char.IsAsciiDigit(name[numberLength]) || name[numberLength] == '.'))
        {
            numberLength++;
        }

        if (numberLength == 0)
            return 0;

        var numbers = name[..numberLength].Split('.')
            .Select(p => int.TryParse(p, out var n) ? n : 0)
            .Take(4)
            .ToArray();

        long key = 0;
        for (var i = 0; i < 4; i++)
            key = key * 1000 + (i < numbers.Length ? Math.Clamp(numbers[i], 0, 999) : 0);

        return key;
    }

    /// <summary>判断是否是 NeoForge 的测试版（名称里带 beta / alpha / snapshot）。</summary>
    private static bool IsNeoForgeBeta(string name)
        => name.Contains("beta", StringComparison.OrdinalIgnoreCase)
           || name.Contains("alpha", StringComparison.OrdinalIgnoreCase)
           || name.Contains("snapshot", StringComparison.OrdinalIgnoreCase);

    /// <summary>NeoForge 版本条目：名称、映射出的 Minecraft 版本、排序键与展示信息。</summary>
    private sealed record NeoForgeEntry(
        string Name,
        string MinecraftVersionName,
        long SortKey,
        ModLoaderVersionInfo Info);

    /// <inheritdoc />
    public async Task<string> InstallModLoaderAsync(
        ModLoaderKind kind,
        string minecraftVersion,
        string? loaderVersion,
        IProgress<InstallProgressInfo>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (kind == ModLoaderKind.Vanilla)
        {
            await InstallAsync(minecraftVersion, progress, cancellationToken).ConfigureAwait(false);
            return minecraftVersion;
        }

        var path = _minecraftPath;

        // 加载器版本通过 inheritsFrom 引用原版版本，因此先把原版装好（已安装时只会做校验）。
        progress?.Report(new InstallProgressInfo
        {
            Stage = $"正在准备原版 {minecraftVersion} 的文件",
            Ratio = 0,
        });
        await InstallAsync(minecraftVersion, progress, cancellationToken).ConfigureAwait(false);

        // 未指定加载器版本时，让 CmlLib 选择它认为最合适的一个。
        var resolvedLoader = loaderVersion;

        string versionName;
        try
        {
            if (kind == ModLoaderKind.Fabric)
            {
                var installer = new FabricInstaller(_httpClient);

                if (string.IsNullOrWhiteSpace(resolvedLoader))
                {
                    var first = await installer.GetFirstLoader(minecraftVersion).ConfigureAwait(false);
                    resolvedLoader = first?.Version;
                }

                progress?.Report(new InstallProgressInfo
                {
                    Stage = $"正在生成 Fabric 启动配置（加载器 {resolvedLoader ?? "最新版"}）",
                    Ratio = 0,
                });

                versionName = string.IsNullOrWhiteSpace(resolvedLoader)
                    ? await installer.Install(minecraftVersion, path).ConfigureAwait(false)
                    : await installer.Install(minecraftVersion, resolvedLoader, path).ConfigureAwait(false);
            }
            else if (kind == ModLoaderKind.Quilt)
            {
                var installer = new QuiltInstaller(_httpClient);

                if (string.IsNullOrWhiteSpace(resolvedLoader))
                {
                    var first = await installer.GetFirstLoader(minecraftVersion).ConfigureAwait(false);
                    resolvedLoader = first?.Version;
                }

                progress?.Report(new InstallProgressInfo
                {
                    Stage = $"正在生成 Quilt 启动配置（加载器 {resolvedLoader ?? "最新版"}）",
                    Ratio = 0,
                });

                versionName = string.IsNullOrWhiteSpace(resolvedLoader)
                    ? await installer.Install(minecraftVersion, path).ConfigureAwait(false)
                    : await installer.Install(minecraftVersion, resolvedLoader, path).ConfigureAwait(false);
            }
            else if (kind is ModLoaderKind.Forge or ModLoaderKind.NeoForge)
            {
                // Forge / NeoForge 的安装器是一个需要 Java 运行的 jar，因此必须先解析可用的 Java。
                var javaRuntime = await ResolveJavaRuntimeAsync(minecraftVersion, null, cancellationToken)
                    .ConfigureAwait(false);

                if (javaRuntime is null)
                {
                    throw new InvalidOperationException(
                        "安装 Forge / NeoForge 需要可用的 Java，但当前没有检测到。请先在“设置”中指定 Java。");
                }

                var loaderName = kind == ModLoaderKind.Forge ? "Forge" : "NeoForge";

                // 没指定加载器版本时，让安装器自己挑（优先推荐版、其次最新版）。
                if (string.IsNullOrWhiteSpace(resolvedLoader) && kind == ModLoaderKind.Forge)
                {
                    var candidates = await new ForgeInstaller(_launcher, _httpClient)
                        .GetForgeVersions(minecraftVersion)
                        .ConfigureAwait(false);

                    resolvedLoader = candidates
                        .FirstOrDefault(v => v.IsRecommendedVersion)?.ForgeVersionName
                        ?? candidates.FirstOrDefault(v => v.IsLatestVersion)?.ForgeVersionName
                        ?? candidates.FirstOrDefault()?.ForgeVersionName;
                }

                progress?.Report(new InstallProgressInfo
                {
                    Stage = $"正在运行 {loaderName} 安装器（需要几分钟，请勿关闭启动器）",
                    Ratio = 0,
                });

                // 安装器事件 → 界面进度。
                var lastOutput = string.Empty;
                var progressedTasks = 0;
                var totalTasks = 0;
                string? currentFile = null;

                var options = new ForgeInstallOptions
                {
                    JavaPath = javaRuntime.Path,
                    CancellationToken = cancellationToken,
                    SkipIfAlreadyInstalled = true,
                    InstallerOutput = new SyncProgress<string>(line =>
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            lastOutput = line.Trim();
                    }),
                    FileProgress = new SyncProgress<InstallerProgressChangedEventArgs>(e =>
                    {
                        progressedTasks = e.ProgressedTasks;
                        totalTasks = e.TotalTasks;
                        currentFile = e.Name;
                    }),
                    ByteProgress = new SyncProgress<ByteProgress>(e =>
                    {
                        var total = e.TotalBytes;
                        progress?.Report(new InstallProgressInfo
                        {
                            Stage = string.IsNullOrWhiteSpace(lastOutput)
                                ? $"正在安装 {loaderName}…"
                                : lastOutput,
                            CurrentFile = currentFile,
                            ProgressedTasks = progressedTasks,
                            TotalTasks = totalTasks,
                            ProgressedBytes = e.ProgressedBytes,
                            TotalBytes = total,
                            Ratio = total > 0 ? Math.Clamp(e.ToRatio(), 0d, 1d) : 0d,
                            SizeText = total > 0
                                ? $"{e.ProgressedBytes / 1024 / 1024} MB / {total / 1024 / 1024} MB"
                                : string.Empty,
                        });
                    }),
                };

                if (kind == ModLoaderKind.Forge)
                {
                    var installer = new ForgeInstaller(_launcher, _httpClient);
                    versionName = string.IsNullOrWhiteSpace(resolvedLoader)
                        ? await installer.Install(minecraftVersion, options).ConfigureAwait(false)
                        : await installer.Install(minecraftVersion, resolvedLoader, options).ConfigureAwait(false);
                }
                else
                {
                    var installer = new NeoForgeInstaller(_launcher, _httpClient);

                    if (string.IsNullOrWhiteSpace(resolvedLoader))
                    {
                        // 未指定版本时，从（可镜像的）元数据里挑最新一个。
                        var available = await GetNeoForgeVersionsAsync(minecraftVersion, cancellationToken)
                            .ConfigureAwait(false);
                        resolvedLoader = available.FirstOrDefault()?.Version;
                    }

                    if (string.IsNullOrWhiteSpace(resolvedLoader))
                        throw new InvalidOperationException($"找不到 Minecraft {minecraftVersion} 对应的 NeoForge 版本");

                    // 注意：Install(mcVersion, version) 重载内部要访问官方 /api/maven/versions 接口，
                    // 国内常常连不上；直接构造 NeoForgeVersion 走 Install(NeoForgeVersion, options) 可绕开它。
                    var neoForgeVersion = new NeoForgeVersion(minecraftVersion, resolvedLoader);
                    versionName = await installer.Install(neoForgeVersion, options).ConfigureAwait(false);
                }
            }
            else
            {
                throw new InvalidOperationException($"不支持的模组加载器：{kind}");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"安装模组加载器失败：{ex.Message}", ex);
        }

        // 加载器版本自身的库文件（fabric-loader / intermediary 等）需要按版本 JSON 补全。
        progress?.Report(new InstallProgressInfo
        {
            Stage = $"正在补全 {versionName} 的库文件",
            Ratio = 0,
        });
        await InstallAsync(versionName, progress, cancellationToken).ConfigureAwait(false);

        progress?.Report(new InstallProgressInfo
        {
            Stage = $"{versionName} 安装完成",
            Ratio = 1,
        });

        return versionName;
    }

    /// <summary>
    /// 枚举 CmlLib 运行时目录（&lt;游戏目录&gt;/runtime）中所有已存在的 Java 可执行文件。
    /// </summary>
    private IEnumerable<string> EnumerateRuntimeJavaPaths()
    {
        var runtimeRoot = _minecraftPath.Runtime;
        if (string.IsNullOrWhiteSpace(runtimeRoot) || !Directory.Exists(runtimeRoot))
            yield break;

        var fileName = OperatingSystem.IsWindows() ? "javaw.exe" : "java";

        IEnumerable<string> executables;
        try
        {
            executables = Directory.EnumerateFiles(runtimeRoot, fileName, SearchOption.AllDirectories);
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var executable in executables)
            yield return executable;
    }

    /// <summary>
    /// 为“版本隔离”创建路径对象：把游戏目录指向版本文件夹，
    /// 同时保持 assets / libraries / runtime 仍然指向原来的游戏目录，保证游戏能找到资源与依赖。
    /// </summary>
    /// <param name="instanceDirectory">作为游戏目录的文件夹。</param>
    private MinecraftPath CreateInstancePath(string instanceDirectory)
    {
        var instancePath = new MinecraftPath(instanceDirectory)
        {
            Assets = _minecraftPath.Assets,
            Library = _minecraftPath.Library,
            Resource = _minecraftPath.Resource,
            Runtime = _minecraftPath.Runtime,
            Versions = _minecraftPath.Versions,
        };

        return instancePath;
    }

    /// <summary>
    /// 释放内部使用的 HttpClient。
    /// </summary>
    public void Dispose() => _httpClient.Dispose();

    /// <summary>
    /// 创建用于下载资源的 HttpClient（启用自动解压，保持足够的超时时间）。
    /// </summary>
    private static HttpClient CreateHttpClient()
    {
        // 最内层是真正的网络处理器，外面套一层镜像处理器：
        // NeoForge 官方 Maven 在国内经常超时，需要自动切换到镜像源。
        var handler = new MirrorHttpHandler(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        });

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(5),
        };

        // 必须带 User-Agent：BMCLAPI 的镜像会跳转到 CERNET 镜像，而 CERNET 对没有 UA 的请求直接返回 403。
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DPL-Launcher/1.0 (Minecraft launcher)");

        return client;
    }

    /// <summary>
    /// 依据 CmlLib 的默认参数创建启动器实例，并启用多线程下载以加快安装速度。
    /// </summary>
    private MinecraftLauncher CreateLauncher(MinecraftPath path)
    {
        var parameters = MinecraftLauncherParameters.CreateDefault(path, _httpClient);

        // CmlLib.Core 4.0.6 里没有 AsyncParallelDownloader，等价能力是 ParallelGameInstaller。
        // 之前用 CreateAsCoreCount 跟随 CPU 核心数（通常 8~16），这里显式把文件级并行数设为 32。
        parameters.GameInstaller = new ParallelGameInstaller(
            maxChecker: DownloadParallelism,
            maxDownloader: DownloadParallelism,
            boundedCapacity: DownloadParallelism * 2,
            _httpClient);

        return new MinecraftLauncher(parameters);
    }

    /// <summary>
    /// 把 CmlLib 的版本元数据转换成界面使用的模型，并按发布时间倒序排列。
    /// </summary>
    /// <param name="metadata">CmlLib 返回的版本集合。</param>
    /// <param name="installedVersions">已安装版本名称集合；为 null 表示按“全部已安装”处理。</param>
    private static IReadOnlyList<MinecraftVersionInfo> BuildVersionList(
        VersionMetadataCollection metadata,
        HashSet<string>? installedVersions)
    {
        var list = new List<MinecraftVersionInfo>(64);

        foreach (var item in metadata)
        {
            list.Add(new MinecraftVersionInfo
            {
                Name = item.Name,
                Kind = MapVersionKind(item.GetVersionType()),
                ReleaseTime = item.ReleaseTime,
                IsInstalled = installedVersions is null || installedVersions.Contains(item.Name),
            });
        }

        return SortVersions(list);
    }

    /// <summary>
    /// 构建本地版本列表。
    /// LocalJsonVersionLoader 给出的元数据只包含版本名称（类型为 local、无发布时间），
    /// 因此这里再通过 CmlLib 解析一次版本 JSON，补充真实的版本类型与发布时间。
    /// </summary>
    private static async Task<IReadOnlyList<MinecraftVersionInfo>> BuildLocalVersionListAsync(
        VersionMetadataCollection metadata,
        CancellationToken cancellationToken)
    {
        var list = new List<MinecraftVersionInfo>(64);

        foreach (var item in metadata)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var kind = GameVersionKind.Unknown;
            var releaseTime = default(DateTimeOffset);

            try
            {
                var version = await item.GetVersionAsync(cancellationToken).ConfigureAwait(false);
                kind = MapVersionKind(version.GetVersionType());
                releaseTime = version.ReleaseTime;
            }
            catch (Exception)
            {
                // 版本 JSON 缺失或损坏（例如父版本未安装）时保持未知类型，但仍允许显示与启动。
            }

            list.Add(new MinecraftVersionInfo
            {
                Name = item.Name,
                Kind = kind,
                ReleaseTime = releaseTime,
                IsInstalled = true,
            });
        }

        return SortVersions(list);
    }

    /// <summary>
    /// 按发布时间倒序排列版本，发布时间缺失时退化到按名称倒序。
    /// </summary>
    private static IReadOnlyList<MinecraftVersionInfo> SortVersions(List<MinecraftVersionInfo> list)
        => list
            .OrderByDescending(v => v.ReleaseTime)
            .ThenByDescending(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// 把 CmlLib 的版本类型枚举映射为界面使用的分类枚举。
    /// </summary>
    private static GameVersionKind MapVersionKind(MVersionType versionType) => versionType switch
    {
        MVersionType.Release => GameVersionKind.Release,
        MVersionType.Snapshot => GameVersionKind.Snapshot,
        MVersionType.OldAlpha or MVersionType.OldBeta => GameVersionKind.Old,
        _ => GameVersionKind.Unknown,
    };

    /// <summary>
    /// 把字节数格式化为便于阅读的字符串。
    /// </summary>
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
}
