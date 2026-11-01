namespace DPL.Models;

/// <summary>
/// 启动器的用户设置，会被序列化为 JSON 保存在本地。
/// </summary>
public sealed class LauncherSettings
{
    /// <summary>游戏目录（即 .minecraft 目录）的绝对路径。</summary>
    public string GameDirectory { get; set; } = string.Empty;

    /// <summary>
    /// 自定义版本目录（versions）的绝对路径；为空表示使用 <see cref="GameDirectory"/> 下的 versions 目录。
    /// </summary>
    public string VersionsDirectory { get; set; } = string.Empty;

    /// <summary>离线登录使用的玩家名称。</summary>
    public string PlayerName { get; set; } = "Steve";

    /// <summary>自定义 Java 可执行文件路径；为空表示自动检测。</summary>
    public string JavaPath { get; set; } = string.Empty;

    /// <summary>
    /// Java 运行库存储目录。自动下载的 Java 会解压到该目录下的 <c>java-&lt;主版本&gt;</c>。
    /// </summary>
    public string JavaRuntimeDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DPL",
        "runtime");

    /// <summary>缺少匹配的 Java 时是否允许自动下载（默认开启）。</summary>
    public bool AutoDownloadJava { get; set; } = true;

    /// <summary>最大内存（单位 MB），对应 JVM 参数 -Xmx。</summary>
    public int MaxMemoryMb { get; set; } = 4096;

    /// <summary>下载线程数（1-32），用于多线程分片下载。</summary>
    public int DownloadThreadCount { get; set; } = 8;

    /// <summary>启动前是否先校验并补全游戏文件（调用 CmlLib 的 InstallAsync）。</summary>
    public bool CheckFilesBeforeLaunch { get; set; } = true;

    /// <summary>
    /// 版本隔离：当版本文件夹内含有 mods / saves / options.txt 等文件时，
    /// 把该文件夹作为游戏目录启动，从而加载该版本独立的模组与存档。
    /// </summary>
    public bool UseVersionIsolation { get; set; } = true;

    /// <summary>是否使用微软正版账号启动（false 表示使用离线账号）。</summary>
    public bool UseMicrosoftAccount { get; set; }

    /// <summary>上次选择的微软正版账号唯一标识；为空表示使用最近使用的账号。</summary>
    public string MicrosoftAccountId { get; set; } = string.Empty;

    /// <summary>用过的离线账号名称历史（最近使用的在最后，供下拉框选择）。</summary>
    public List<string> OfflineNameHistory { get; set; } = new();

    /// <summary>
    /// 收藏的本地版本名称。即使版本已卸载，名称也保留在设置里，
    /// 之后重新安装同名版本时仍会显示为收藏。
    /// </summary>
    public List<string> FavoriteVersions { get; set; } = new();

    /// <summary>
    /// CurseForge 的 API Key（不能硬编码在源码里，由用户填写）。
    /// 也可以设置环境变量 <c>NOVALAUNCH_CURSEFORGE_KEY</c> 覆盖这里。
    /// </summary>
    public string CurseForgeApiKey { get; set; } = string.Empty;

    /// <summary>模组下载源偏好：Auto / Modrinth / CurseForge。</summary>
    public string ModSourcePreference { get; set; } = "Auto";

    /// <summary>界面主题：Dark / White / BlueGlass / PinkGlass。</summary>
    public string Theme { get; set; } = "Dark";
}
