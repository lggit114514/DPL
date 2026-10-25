using XboxAuthNet.OAuth.CodeFlow;

namespace DPL.Services;

/// <summary>
/// 自定义的 <see cref="IWebUI"/> 实现：把登录页交给界面层打开，
/// 再把用户完成的授权结果回传给 CmlLib 的 OAuth 代码流。
/// 之所以需要它：CmlLib 默认的 WebUI 在 .NET 上不可用
/// （<c>createDefaultWebUIForPlatform</c> 会抛 “Current platform does not support to provide default WebUI.”）。
/// </summary>
public sealed class InjectWebUi : IWebUI
{
    private readonly IMicrosoftLoginUi _loginUi;

    /// <summary>
    /// 创建自定义 WebUI。
    /// </summary>
    /// <param name="loginUi">真正负责显示登录界面的实现。</param>
    public InjectWebUi(IMicrosoftLoginUi loginUi) => _loginUi = loginUi;

    /// <inheritdoc />
    public Task<CodeFlowAuthorizationResult> DisplayDialogAndInterceptUri(
        Uri uri,
        ICodeFlowUrlChecker uriChecker,
        CancellationToken cancellationToken)
        => _loginUi.WaitForAuthorizationAsync(uri, uriChecker, cancellationToken);

    /// <inheritdoc />
    public Task DisplayDialogAndNavigateUri(Uri uri, CancellationToken cancellationToken)
        => _loginUi.ShowNavigationAsync(uri, cancellationToken);
}
