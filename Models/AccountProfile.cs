namespace DPL.Models;

/// <summary>
/// 账号类型：离线账号或微软正版账号。
/// </summary>
public enum AccountKind
{
    /// <summary>离线账号（本地生成 UUID，仅用于单机与离线服务器）。</summary>
    Offline,

    /// <summary>微软正版账号（通过 CmlLib.Core.Auth.Microsoft 登录）。</summary>
    Microsoft,
}

/// <summary>
/// 一个可用的账号信息，供界面显示与启动时选择。
/// </summary>
public sealed class AccountProfile
{
    /// <summary>账号类型。</summary>
    public required AccountKind Kind { get; init; }

    /// <summary>账号显示名称：离线账号为玩家名，正版账号为 Minecraft 用户名。</summary>
    public required string Name { get; init; }

    /// <summary>正版账号的唯一标识（CmlLib 账号管理器中用于定位账号）；离线账号为 null。</summary>
    public string? Identifier { get; init; }

    /// <summary>正版账号的 UUID；离线账号为 null。</summary>
    public string? Uuid { get; init; }

    /// <summary>Xbox 玩家代号（正版账号可能为空）。</summary>
    public string? Gamertag { get; init; }

    /// <summary>最近一次使用时间。</summary>
    public DateTime? LastAccess { get; init; }

    /// <summary>账号类型的显示文本。</summary>
    public string KindText => Kind == AccountKind.Microsoft ? "正版" : "离线";

    /// <summary>下拉框中显示的完整文本，例如 <c>正版 · Notch</c>。</summary>
    public string DisplayName => $"{KindText} · {Name}";

    /// <summary>账号详情说明，例如“最近登录 2026-01-02 13:04”。</summary>
    public string DetailText => LastAccess is null
        ? (Kind == AccountKind.Microsoft ? "微软正版账号" : "离线账号")
        : $"最近使用 {LastAccess:yyyy-MM-dd HH:mm}";
}
