#if WINDOWS
using System.Collections.Concurrent;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using Avalonia.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using XboxAuthNet.OAuth.CodeFlow;
// Avalonia 与 WinForms 都有 Label，这里显式指定使用 WinForms 的
using Label = System.Windows.Forms.Label;

namespace DPL.Services;

/// <summary>
/// Windows 版微软登录界面：使用内嵌的 WebView2 窗口显示 Microsoft 登录页，
/// 在页面跳转到 <c>oauth20_desktop.srf</c> 回调地址时立刻截获授权码并关闭窗口
/// （与官方启动器的行为一致，用户不需要复制任何网址）。
/// 若本机缺少 WebView2 运行时，会自动退化为“浏览器 + 粘贴回跳网址”的方式。
/// </summary>
public sealed class WebView2MicrosoftLoginUi : IMicrosoftLoginUi
{
    /// <summary>调试/诊断信息（供自测脚本读取，也方便排查登录问题）。</summary>
    internal static readonly ConcurrentQueue<string> Diagnostics = new();

    /// <summary>自测入口：把当前登录窗口导航到指定网址（用于模拟回调跳转）。</summary>
    internal static Func<string, bool>? TestNavigator { get; set; }

    private readonly IMicrosoftLoginUi _fallback;

    /// <summary>
    /// 创建 Windows 内嵌登录界面。
    /// </summary>
    /// <param name="owner">主窗口（仅用于降级方案的对话框父窗口）。</param>
    public WebView2MicrosoftLoginUi(Window owner) => _fallback = new AvaloniaMicrosoftLoginUi(owner);

    /// <inheritdoc />
    public async Task<CodeFlowAuthorizationResult> WaitForAuthorizationAsync(
        Uri loginUri,
        ICodeFlowUrlChecker urlChecker,
        CancellationToken cancellationToken)
    {
        if (!IsWebView2RuntimeAvailable())
        {
            Diagnostics.Enqueue("未检测到 WebView2 运行时，退化为浏览器 + 粘贴方式");
            return await _fallback.WaitForAuthorizationAsync(loginUri, urlChecker, cancellationToken);
        }

        try
        {
            return await RunEmbeddedAsync(loginUri, urlChecker, cancellationToken);
        }
        catch (WebView2UnavailableException ex)
        {
            Diagnostics.Enqueue($"WebView2 初始化失败，退化为粘贴方式：{ex.Message}");
            return await _fallback.WaitForAuthorizationAsync(loginUri, urlChecker, cancellationToken);
        }
    }

    /// <inheritdoc />
    public Task ShowNavigationAsync(Uri navigationUri, CancellationToken cancellationToken)
        => _fallback.ShowNavigationAsync(navigationUri, cancellationToken);

    /// <summary>
    /// 在独立 STA 线程上运行 WinForms + WebView2 登录窗口。
    /// </summary>
    private static async Task<CodeFlowAuthorizationResult> RunEmbeddedAsync(
        Uri loginUri,
        ICodeFlowUrlChecker urlChecker,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<CodeFlowAuthorizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            LoginForm? form = null;
            try
            {
                Application.EnableVisualStyles();
                form = new LoginForm(loginUri, urlChecker, cancellationToken, tcs);
                Application.Run(form);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                form?.Dispose();
                TestNavigator = null;
            }
        })
        {
            IsBackground = true,
            Name = "DPL-MicrosoftLogin",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        using var registration = cancellationToken.Register(() =>
            tcs.TrySetCanceled(cancellationToken));

        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            if (thread.IsAlive)
                thread.Join(TimeSpan.FromSeconds(3));
        }
    }

    /// <summary>
    /// 检测 WebView2 运行时是否可用（注册表 Evergreen 运行时版本）。
    /// </summary>
    private static bool IsWebView2RuntimeAvailable()
    {
        try
        {
            var coreWebView2 = CoreWebView2Environment.GetAvailableBrowserVersionString(null);
            return !string.IsNullOrWhiteSpace(coreWebView2);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 登录窗口：一个铺满 WebView2 的 WinForms 窗体。
    /// </summary>
    private sealed class LoginForm : Form
    {
        private readonly Uri _loginUri;
        private readonly ICodeFlowUrlChecker _urlChecker;
        private readonly TaskCompletionSource<CodeFlowAuthorizationResult> _tcs;
        private readonly WebView2 _webView = new();
        private readonly Label _status = new();
        private bool _finished;

        public LoginForm(
            Uri loginUri,
            ICodeFlowUrlChecker urlChecker,
            CancellationToken cancellationToken,
            TaskCompletionSource<CodeFlowAuthorizationResult> tcs)
        {
            _loginUri = loginUri;
            _urlChecker = urlChecker;
            _tcs = tcs;

            Text = "登录 Microsoft 账号 - DPL";
            ClientSize = new Size(520, 680);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(420, 520);
            Icon = SystemIcons.Application;

            _status.Text = "正在加载 Microsoft 登录页…";
            _status.Dock = DockStyle.Bottom;
            _status.Height = 46;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.Padding = new Padding(12, 0, 12, 0);
            _status.Font = new Font("Microsoft YaHei UI", 9F);

            _webView.Dock = DockStyle.Fill;

            Controls.Add(_webView);
            Controls.Add(_status);

            Load += OnFormLoad;

            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() =>
                {
                    try
                    {
                        if (!IsDisposed)
                            BeginInvoke(new Action(Close));
                    }
                    catch
                    {
                        // 窗口已关闭时忽略
                    }
                });
            }
        }

        /// <summary>
        /// 初始化 WebView2 并导航到 Microsoft 登录页。
        /// </summary>
        private async void OnFormLoad(object? sender, EventArgs e)
        {
            try
            {
                var environment = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: ResolveUserDataFolder());

                await _webView.EnsureCoreWebView2Async(environment);

                var core = _webView.CoreWebView2;
                core.NavigationStarting += OnNavigationStarting;
                core.NavigationCompleted += OnNavigationCompleted;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;

                Diagnostics.Enqueue($"WebView2 已初始化：{core.Environment.BrowserVersionString}");
                _status.Text = "请在此窗口中登录 Microsoft 账号（登录成功后窗口会自动关闭）";

                TestNavigator = url =>
                {
                    try
                    {
                        // WebView2 只能在它自己的 UI 线程上操作，这里统一投递过去。
                        _webView.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                core.Navigate(url);
                            }
                            catch (Exception ex)
                            {
                                Diagnostics.Enqueue($"测试导航失败：{ex.Message}");
                            }
                        }));
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Diagnostics.Enqueue($"测试导航投递失败：{ex.Message}");
                        return false;
                    }
                };

                core.Navigate(_loginUri.ToString());
            }
            catch (Exception ex)
            {
                Diagnostics.Enqueue($"WebView2 初始化异常：{ex}");
                _tcs.TrySetException(new WebView2UnavailableException("无法初始化内嵌浏览器（WebView2）", ex));
                Close();
            }
        }

        /// <summary>
        /// 页面开始跳转：如果跳转目标就是 OAuth 回调地址，直接截获并阻止继续加载。
        /// </summary>
        private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (TryComplete(e.Uri))
                e.Cancel = true;
        }

        /// <summary>
        /// 页面加载完成：再检查一次当前地址（有些回调会先完成加载再读取地址）。
        /// </summary>
        private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            var source = _webView.CoreWebView2?.Source;
            if (!TryComplete(source) && !string.IsNullOrEmpty(source))
                Diagnostics.Enqueue($"当前页面：{source}");
        }

        /// <summary>
        /// 尝试从网址中解析授权码；成功（或失败）时关闭窗口。
        /// </summary>
        private bool TryComplete(string? url)
        {
            if (_finished || string.IsNullOrWhiteSpace(url))
                return false;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return false;

            CodeFlowAuthorizationResult result;
            try
            {
                result = _urlChecker.GetAuthCodeResult(uri);
            }
            catch (Exception ex)
            {
                Diagnostics.Enqueue($"解析回调地址失败：{ex.Message}");
                return false;
            }

            if (result.IsEmpty)
                return false;

            _finished = true;
            Diagnostics.Enqueue(result.IsSuccess
                ? "已截获授权码，登录继续"
                : $"微软返回错误：{result.Error} {result.ErrorDescription}");

            if (result.IsSuccess)
                _tcs.TrySetResult(result);
            else
                _tcs.TrySetException(new InvalidOperationException(
                    $"微软返回错误：{result.Error}{(string.IsNullOrWhiteSpace(result.ErrorDescription) ? string.Empty : " - " + result.ErrorDescription)}"));

            Close();
            return true;
        }

        /// <summary>
        /// WebView2 的数据目录：优先放在程序目录（Z 盘等），不可写时退回本地应用数据目录。
        /// </summary>
        private static string ResolveUserDataFolder()
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "webview2-data"),
                Path.Combine(AppPaths.LocalDirectory, "webview2-data"),
            };

            foreach (var candidate in candidates)
            {
                try
                {
                    Directory.CreateDirectory(candidate);
                    return candidate;
                }
                catch (Exception)
                {
                    // 尝试下一个候选目录
                }
            }

            return Path.GetTempPath();
        }
    }
}

/// <summary>
/// 表示内嵌 WebView2 浏览器不可用（缺少运行时或初始化失败），调用方可据此降级。
/// </summary>
public sealed class WebView2UnavailableException : Exception
{
    /// <summary>
    /// 创建异常实例。
    /// </summary>
    /// <param name="message">错误说明。</param>
    /// <param name="innerException">内部异常。</param>
    public WebView2UnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
#endif
