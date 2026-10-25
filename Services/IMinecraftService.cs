using DPL.Models;

namespace DPL.Services;

/// <summary>
/// Minecraft 核心操作服务：版本列表、下载安装、启动游戏。
/// 内部所有 Minecraft 相关逻辑都通过 CmlLib.Core 完成。
/// </summary>
public interface IMinecraftService
{
    /// <summary>当前使用的游戏目录（.minecraft 目录）。</summary>
    string GameDirectory { get; }

    /// <summary>
    /// 切换游戏目录；目录变化后会重建内部的 CmlLib 启动器实例。
    /// </summary>
    /// <param name="gameDirectory">新的游戏目录绝对路径。</param>
    void UseGameDirectory(string gameDirectory);

    /// <summary>当前实际生效的版本目录（versions）绝对路径。</summary>
    string VersionsDirectory { get; }

    /// <summary>
    /// 切换版本目录；传入 null 或空字符串表示使用游戏目录下的默认 versions 目录。
    /// </summary>
    /// <param name="versionsDirectory">自定义版本目录绝对路径，或 null。</param>
    void UseVersionsDirectory(string? versionsDirectory);

    /// <summary>
    /// 取得指定版本所在文件夹的绝对路径（即 &lt;版本目录&gt;/&lt;版本名&gt;）。
    /// </summary>
    /// <param name="versionName">版本名称。</param>
    string GetVersionDirectory(string versionName);

    /// <summary>
    /// 读取本地已安装的所有版本。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<MinecraftVersionInfo>> GetLocalVersionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 从 Mojang 官方服务器（经由 CmlLib）获取全部可下载的原版版本，并标记哪些已安装。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<MinecraftVersionInfo>> GetOnlineVersionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 下载并安装指定版本。
    /// </summary>
    /// <param name="versionName">版本名称，例如 <c>1.20.4</c>。</param>
    /// <param name="progress">进度回调；可为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task InstallAsync(string versionName, IProgress<InstallProgressInfo>? progress, CancellationToken cancellationToken = default);

    /// <summary>
    /// 构建离线会话并启动指定版本的游戏，等待游戏进程退出。
    /// </summary>
    /// <param name="versionName">版本名称。</param>
    /// <param name="request">启动参数。</param>
    /// <param name="progress">启动前文件校验的进度回调；可为 null。</param>
    /// <param name="gameLog">游戏标准输出日志回调；可为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>游戏进程的退出代码。</returns>
    Task<int> LaunchAsync(
        string versionName,
        LaunchRequest request,
        IProgress<InstallProgressInfo>? progress = null,
        Action<string>? gameLog = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 解析用于启动的 Java 路径：优先使用指定路径，其次使用 CmlLib 管理的运行时，最后扫描本机。
    /// </summary>
    /// <param name="versionName">版本名称。</param>
    /// <param name="preferredPath">用户指定的 Java 路径；可为空。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Java 可执行文件路径；找不到时返回 null。</returns>
    Task<string?> ResolveJavaPathAsync(string versionName, string? preferredPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 解析可用的 Java 运行时（会实际执行一次 <c>java -version</c> 校验，并检查是否满足该版本要求）。
    /// </summary>
    /// <param name="versionName">版本名称。</param>
    /// <param name="preferredPath">用户指定的 Java 路径；可为空。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>可用的 Java 运行时信息；没有任何可用 Java 时抛出 <see cref="InvalidOperationException"/>。</returns>
    Task<JavaRuntimeInfo?> ResolveJavaRuntimeAsync(string versionName, string? preferredPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 判断某个版本文件夹看起来是否是“版本隔离”实例（包含 mods / saves / config / options.txt 等）。
    /// </summary>
    /// <param name="versionName">版本名称。</param>
    /// <returns>看起来是隔离实例时返回 true。</returns>
    bool LooksLikeIsolatedInstance(string versionName);

    /// <summary>
    /// 查询某个 Minecraft 版本可用的模组加载器版本（Fabric / Quilt，由 CmlLib 的对应安装器提供）。
    /// </summary>
    /// <param name="kind">加载器类型。</param>
    /// <param name="minecraftVersion">Minecraft 版本号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>加载器版本列表；不支持或获取失败时返回空列表。</returns>
    Task<IReadOnlyList<ModLoaderVersionInfo>> GetModLoaderVersionsAsync(
        ModLoaderKind kind,
        string minecraftVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 查询模组加载器版本，并在失败时返回可读的原因（用于区分“没有该加载器”和“网络不通”）。
    /// </summary>
    /// <param name="kind">加载器类型。</param>
    /// <param name="minecraftVersion">Minecraft 版本号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<ModLoaderQueryResult> QueryModLoaderVersionsAsync(
        ModLoaderKind kind,
        string minecraftVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 判断该 Minecraft 版本是否支持指定的模组加载器。
    /// </summary>
    /// <param name="kind">加载器类型。</param>
    /// <param name="minecraftVersion">Minecraft 版本号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<bool> IsModLoaderSupportedAsync(
        ModLoaderKind kind,
        string minecraftVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 安装模组加载器并补全该版本所需的文件。
    /// 会先确保原版版本已安装（加载器版本通过 inheritsFrom 引用原版）。
    /// </summary>
    /// <param name="kind">加载器类型。</param>
    /// <param name="minecraftVersion">Minecraft 版本号。</param>
    /// <param name="loaderVersion">加载器版本号；为空表示使用最新版。</param>
    /// <param name="progress">安装进度回调；可为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>安装后的版本名称（例如 <c>fabric-loader-0.16.10-1.21.4</c>）。</returns>
    Task<string> InstallModLoaderAsync(
        ModLoaderKind kind,
        string minecraftVersion,
        string? loaderVersion,
        IProgress<InstallProgressInfo>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 扫描本机可用的 Java 运行时列表。
    /// </summary>
    IReadOnlyList<JavaCandidate> FindJavaCandidates();
}
