namespace DPL.Models;

/// <summary>
/// 下载安装过程中的进度快照，用于向界面反馈下载状态。
/// </summary>
public sealed class InstallProgressInfo
{
    /// <summary>当前阶段的文字说明，例如“正在下载游戏文件”。</summary>
    public string Stage { get; init; } = string.Empty;

    /// <summary>当前正在处理的文件名称。</summary>
    public string? CurrentFile { get; init; }

    /// <summary>已完成的文件数量。</summary>
    public int ProgressedTasks { get; init; }

    /// <summary>需要处理的文件总数。</summary>
    public int TotalTasks { get; init; }

    /// <summary>已完成的总字节数。</summary>
    public long ProgressedBytes { get; init; }

    /// <summary>需要下载的总字节数。</summary>
    public long TotalBytes { get; init; }

    /// <summary>整体进度比例，取值范围 0 ~ 1。</summary>
    public double Ratio { get; init; }

    /// <summary>
    /// 已格式化好的进度描述，例如 <c>12.3 MB / 240.0 MB</c>。
    /// </summary>
    public string SizeText { get; init; } = string.Empty;
}
