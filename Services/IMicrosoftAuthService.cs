using CmlLib.Core.Auth;
using DPL.Models;

namespace DPL.Services;

/// <summary>
/// 微软正版账号登录服务（基于 CmlLib.Core.Auth.Microsoft）。
/// 所有 OAuth / Xbox / Minecraft 认证流程都由该库完成，本服务只负责账号列表、持久化与并发控制。
/// </summary>
public interface IMicrosoftAuthService
{
    /// <summary>账号列表文件路径（用于在界面上展示保存位置）。</summary>
    string AccountsFilePath { get; }

    /// <summary>
    /// 读取已保存的微软账号列表（只读取本地文件，不访问网络）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<AccountProfile>> GetSavedAccountsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 交互式登录一个新的微软账号（Windows 下会弹出微软登录窗口）。
    /// 登录成功后账号会被写入账号列表文件。
    /// </summary>
    /// <param name="statusProgress">状态回调，用于告知界面当前登录阶段。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>登录成功后的账号信息。</returns>
    Task<AccountProfile> LoginInteractivelyAsync(
        Action<string>? statusProgress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 使用已保存的账号获取（必要时刷新）Minecraft 会话，供启动游戏使用。
    /// 该过程不需要用户交互；若令牌已失效会抛出 <see cref="InvalidOperationException"/>。
    /// </summary>
    /// <param name="accountIdentifier">账号唯一标识；为 null 时使用最近一次使用的账号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>可直接用于 <c>MLaunchOption.Session</c> 的会话。</returns>
    Task<MSession> AuthenticateAsync(string? accountIdentifier, CancellationToken cancellationToken = default);

    /// <summary>
    /// 退出指定账号（清除其保存的令牌）。账号条目仍会保留在列表中。
    /// </summary>
    /// <param name="accountIdentifier">账号唯一标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task SignOutAsync(string? accountIdentifier, CancellationToken cancellationToken = default);
}
