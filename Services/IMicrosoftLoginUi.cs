using XboxAuthNet.OAuth.CodeFlow;

namespace DPL.Services;

/// <summary>
/// 微软登录界面抽象。
/// CmlLib 的默认 OAuth 需要一个 <c>IWebUI</c> 来显示 Microsoft 登录页并截获跳转地址，
/// 但 .NET 版库并不提供该实现（默认实现会抛 “does not support to provide default WebUI”）。
/// 因此这里把“显示登录界面”抽象出来，由界面层（Avalonia）实现。
/// </summary>
public interface IMicrosoftLoginUi
{
    /// <summary>
    /// 打开 Microsoft 登录页，并等待用户完成登录后回传跳转结果。
    /// </summary>
    /// <param name="loginUri">需要打开的 Microsoft 登录地址。</param>
    /// <param name="urlChecker">库提供的跳转地址校验器，用于从用户粘贴的网址中解析授权码。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>授权结果；用户取消时返回空结果（IsEmpty）。</returns>
    Task<CodeFlowAuthorizationResult> WaitForAuthorizationAsync(
        Uri loginUri,
        ICodeFlowUrlChecker urlChecker,
        CancellationToken cancellationToken);

    /// <summary>
    /// 仅打开一个网页并提示用户在浏览器中完成操作（例如退出登录页面）。
    /// </summary>
    /// <param name="navigationUri">需要打开的地址。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task ShowNavigationAsync(Uri navigationUri, CancellationToken cancellationToken);
}
