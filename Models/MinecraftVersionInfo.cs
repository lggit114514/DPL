namespace DPL.Models;

/// <summary>
/// 游戏版本的分类，用于界面筛选与标签显示。
/// </summary>
public enum GameVersionKind
{
    /// <summary>正式版（release）。</summary>
    Release,

    /// <summary>快照版（snapshot）。</summary>
    Snapshot,

    /// <summary>远古版本（old_beta / old_alpha）。</summary>
    Old,

    /// <summary>未知或自定义类型（例如整合包、模组加载器版本）。</summary>
    Unknown,
}

/// <summary>
/// 从 CmlLib 获取并转换后的 Minecraft 版本信息（与 UI 无关的纯数据模型）。
/// </summary>
public sealed class MinecraftVersionInfo
{
    /// <summary>版本名称，例如 <c>1.20.4</c>。</summary>
    public required string Name { get; init; }

    /// <summary>版本分类。</summary>
    public GameVersionKind Kind { get; init; } = GameVersionKind.Unknown;

    /// <summary>该版本的发布时间。</summary>
    public DateTimeOffset ReleaseTime { get; init; }

    /// <summary>该版本是否已经安装到当前游戏目录。</summary>
    public bool IsInstalled { get; init; }
}
