using Avalonia.Controls;
using Avalonia.Threading;
using DPL.Views;
using XboxAuthNet.OAuth.CodeFlow;

namespace DPL.Services;

/// <summary>
/// 基于 Avalonia 的微软登录界面实现：
/// 用系统默认浏览器打开 Microsoft 登录页，然后弹出一个窗口让用户粘贴浏览器跳转后的网址，
/// 由 <see cref="ICodeFlowUrlChecker"/> 解析出授权码后交给 CmlLib 继续完成登录。
/// </summary>
public sealed class AvaloniaMicrosoftLoginUi : IMicrosoftLoginUi
{
    private readonly Window _owner;

    /// <summary>
    /// 创建登录界面实现。
    /// </summary>
    /// <param name="owner">主窗口，作为对话框的父窗口。</param>
    public AvaloniaMicrosoftLoginUi(Window owner) => _owner = owner;

    /// <inheritdoc />
    public async Task<CodeFlowAuthorizationResult> WaitForAuthorizationAsync(
        Uri loginUri,
        ICodeFlowUrlChecker urlChecker,
        CancellationToken cancellationToken)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var authorized = false;
            var authorizedResult = default(CodeFlowAuthorizationResult);

            void OpenLoginPage() => SystemIntegration.OpenUrl(loginUri.ToString());

            // 先自动打开一次登录页
            OpenLoginPage();

            // 校验用户粘贴的网址：返回 null 表示解析成功
            string? Validate(string text)
            {
                if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
                    return "网址格式不正确，请粘贴以 https:// 开头的完整地址";

                try
                {
                    var result = urlChecker.GetAuthCodeResult(uri);

                    if (result.IsEmpty)
                        return "这个网址里没有授权码。请确认复制的是登录跳转后地址栏里的完整网址（含 code=…）。";

                    if (!result.IsSuccess)
                    {
                        var detail = string.IsNullOrWhiteSpace(result.ErrorDescription)
                            ? result.Error
                            : $"{result.Error} - {result.ErrorDescription}";
                        return $"微软返回了错误：{detail}";
                    }

                    authorized = true;
                    authorizedResult = result;
                    return null;
                }
                catch (Exception ex)
                {
                    return $"解析网址失败：{ex.Message}";
                }
            }

            var dialog = new MicrosoftLoginDialog(Validate, OpenLoginPage);

            using var registration = cancellationToken.Register(() =>
                Dispatcher.UIThread.Post(() =>
                {
                    if (dialog.IsVisible)
                        dialog.Close(false);
                }));

            var confirmed = await dialog.ShowDialog<bool>(_owner);

            if (!confirmed || !authorized)
            {
                // 用户取消：抛 OperationCanceledException，让上层显示“已取消登录”而不是一条原始异常。
                throw new OperationCanceledException("用户取消了微软登录");
            }

            return authorizedResult;
        });
    }

    /// <inheritdoc />
    public Task ShowNavigationAsync(Uri navigationUri, CancellationToken cancellationToken)
    {
        // 这类页面（例如账户注销页）只需要在浏览器中完成，不需要回传结果。
        return Dispatcher.UIThread.InvokeAsync(() =>
        {
            SystemIntegration.OpenUrl(navigationUri.ToString());
        }).GetTask();
    }
}
