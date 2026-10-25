namespace DPL.Models;

/// <summary>
/// 模组加载器类型。原版表示不安装任何加载器。
/// </summary>
public enum ModLoaderKind
{
    /// <summary>原版，不安装模组加载器。</summary>
    Vanilla,

    /// <summary>Fabric 加载器。</summary>
    Fabric,

    /// <summary>Quilt 加载器。</summary>
    Quilt,

    /// <summary>Forge 加载器。</summary>
    Forge,

    /// <summary>NeoForge 加载器（Forge 的分支）。</summary>
    NeoForge,
}

/// <summary>
/// 某个 Minecraft 版本可用的模组加载器版本。
/// </summary>
/// <param name="Version">加载器版本号，例如 <c>0.16.10</c> 或 <c>47.1.0</c>。</param>
/// <param name="Build">构建号（Fabric / Quilt 元数据中的 build；Forge 系列为 0）。</param>
/// <param name="IsStable">是否为稳定版 / 推荐版。</param>
/// <param name="Badge">徽标文字，例如“稳定版”“推荐”“最新”；可为空。</param>
public sealed record ModLoaderVersionInfo(string Version, int Build, bool IsStable, string Badge = "")
{
    /// <summary>下拉框里显示的文本，会带上徽标说明。</summary>
    public string DisplayText => string.IsNullOrWhiteSpace(Badge) ? Version : $"{Version}（{Badge}）";
}

/// <summary>
/// 一个 Fabric API 的可用发行版（来自 Modrinth）。
/// </summary>
/// <param name="VersionNumber">版本号，例如 <c>0.119.4+1.21.4</c>。</param>
/// <param name="FileName">下载后的文件名。</param>
/// <param name="DownloadUrl">下载地址（Modrinth CDN）。</param>
/// <param name="FileSize">文件大小（字节）。</param>
public sealed record FabricApiRelease(string VersionNumber, string FileName, string DownloadUrl, long FileSize)
{
    /// <summary>
    /// FabricMC 官方 Maven 上的同一个 jar。
    /// 部分网络环境访问不了 Modrinth 的 CDN，但可以访问 maven.fabricmc.net，因此作为首选/备份地址。
    /// </summary>
    public string MavenDownloadUrl =>
        "https://maven.fabricmc.net/net/fabricmc/fabric-api/fabric-api/" +
        Uri.EscapeDataString(VersionNumber) + "/fabric-api-" + Uri.EscapeDataString(VersionNumber) + ".jar";

    /// <summary>界面显示的说明文本，例如 <c>0.119.4+1.21.4 · 2.1 MB</c>。</summary>
    public string DisplayText => FileSize > 0
        ? $"{VersionNumber} · {FormatSize(FileSize)}"
        : VersionNumber;

    /// <summary>把字节数格式化为便于阅读的字符串。</summary>
    private static string FormatSize(long bytes)
    {
        const double kb = 1024d;
        const double mb = kb * 1024d;

        return bytes switch
        {
            >= (long)mb => $"{bytes / mb:0.0} MB",
            >= (long)kb => $"{bytes / kb:0.0} KB",
            _ => $"{bytes} B",
        };
    }
}

/// <summary>
/// 查询模组加载器版本的结果：失败时 <see cref="ErrorText"/> 会带上可读的原因
/// （用于区分“该版本没有这个加载器”和“网络访问不了加载器官网”）。
/// </summary>
/// <param name="Versions">可用的加载器版本列表。</param>
/// <param name="ErrorText">查询失败时的说明；成功时为 null。</param>
public sealed record ModLoaderQueryResult(IReadOnlyList<ModLoaderVersionInfo> Versions, string? ErrorText);
