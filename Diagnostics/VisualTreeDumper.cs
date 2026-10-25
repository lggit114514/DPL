#if DEBUG
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DPL.ViewModels;

namespace DPL.Diagnostics;

/// <summary>
/// 仅在 Debug 构建下可用的可视化树导出工具。
/// 设置环境变量 NOVALAUNCH_LAYOUT_DUMP=1 后启动程序，会把三个页面的控件尺寸、位置与文本导出到临时目录，
/// 用于在无图形界面的环境下核对布局。正式发布时该文件不会被编译进程序。
/// </summary>
internal static class VisualTreeDumper
{
    /// <summary>
    /// 若环境变量存在，则在窗口打开后依次切换到三个页面并导出布局信息，随后退出程序。
    /// </summary>
    /// <param name="window">主窗口。</param>
    /// <param name="viewModel">主窗口 ViewModel，用于切换页面。</param>
    public static void RunIfRequested(Window window, MainWindowViewModel viewModel)
    {
        if (Environment.GetEnvironmentVariable("NOVALAUNCH_LAYOUT_DUMP") is not null)
            _ = CaptureAsync(window, viewModel);

        if (Environment.GetEnvironmentVariable("NOVALAUNCH_SELFTEST") is not null)
            _ = SelfTestAsync(viewModel);

        var launchTestVersion = Environment.GetEnvironmentVariable("NOVALAUNCH_LAUNCH_TEST");
        if (!string.IsNullOrWhiteSpace(launchTestVersion))
            _ = LaunchSelfTestAsync(viewModel, launchTestVersion);

        if (Environment.GetEnvironmentVariable("NOVALAUNCH_ACCOUNT_TEST") is not null)
            _ = AccountSelfTestAsync(viewModel);

        if (Environment.GetEnvironmentVariable("NOVALAUNCH_MSLOGIN_TEST") is not null)
            _ = MicrosoftLoginSelfTestAsync(window, viewModel);

        var modBrowserTest = Environment.GetEnvironmentVariable("NOVALAUNCH_MODBROWSER_TEST");
        if (!string.IsNullOrWhiteSpace(modBrowserTest))
            _ = ModBrowserSelfTestAsync(viewModel, modBrowserTest);

        var modLoaderTest = Environment.GetEnvironmentVariable("NOVALAUNCH_MODLOADER_TEST");
        if (!string.IsNullOrWhiteSpace(modLoaderTest))
            _ = ModLoaderInstallSelfTestAsync(viewModel, modLoaderTest);
    }

    /// <summary>
    /// 模组下载页自测：切到模组页、执行一次真实搜索并加载版本列表，把结果写进日志。
    /// </summary>
    private static async Task ModBrowserSelfTestAsync(MainWindowViewModel viewModel, string query)
    {
        var directory = Path.Combine(Path.GetTempPath(), "novalaunch-layout");
        Directory.CreateDirectory(directory);
        using var writer = new StreamWriter(Path.Combine(directory, "modbrowser-selftest.txt"), append: false,
            System.Text.Encoding.UTF8) { AutoFlush = true };

        void Log(string message) => writer.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

        for (var i = 0; i < 60 && viewModel.LocalVersions.Count == 0; i++)
            await Task.Delay(500);

        // 选一个带加载器的版本作为目标实例（没有就用第一个）
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var modded = viewModel.LocalVersions.FirstOrDefault(v =>
                v.Name.Contains("fabric", StringComparison.OrdinalIgnoreCase) ||
                v.Name.Contains("forge", StringComparison.OrdinalIgnoreCase) ||
                v.Name.Contains("quilt", StringComparison.OrdinalIgnoreCase));
            viewModel.SelectedLocalVersion = modded ?? viewModel.LocalVersions.FirstOrDefault();
        });

        await Dispatcher.UIThread.InvokeAsync(() => viewModel.ShowModsPageCommand.Execute(null));
        await Task.Delay(2500);

        Log($"页面索引={await Dispatcher.UIThread.InvokeAsync(() => viewModel.CurrentPageIndex)} " +
            $"IsActive={await Dispatcher.UIThread.InvokeAsync(() => viewModel.ModBrowser.IsActive)}");
        Log($"目标实例：{await Dispatcher.UIThread.InvokeAsync(() => viewModel.ModBrowser.TargetText)}");
        Log($"下载源状态：{await Dispatcher.UIThread.InvokeAsync(() => viewModel.ModBrowser.SourceStatusText)}");

        await Dispatcher.UIThread.InvokeAsync(() => viewModel.ModBrowser.SearchText = query);
        await Dispatcher.UIThread.InvokeAsync(() => viewModel.ModBrowser.SearchCommand.Execute(null));

        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(500);
            var done = await Dispatcher.UIThread.InvokeAsync(() => viewModel.ModBrowser.IsIdle);
            if (done)
                break;
        }

        var count = await Dispatcher.UIThread.InvokeAsync(() => viewModel.ModBrowser.Results.Count);
        var status = await Dispatcher.UIThread.InvokeAsync(() => viewModel.ModBrowser.StatusText);
        var error = await Dispatcher.UIThread.InvokeAsync(() => viewModel.ModBrowser.ErrorText);
        Log($"搜索结果数={count}，状态：{status}，错误：{(string.IsNullOrWhiteSpace(error) ? "(无)" : error)}");

        for (var i = 0; i < Math.Min(count, 5); i++)
        {
            var index = i;
            var line = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var item = viewModel.ModBrowser.Results[index];
                return $"{item.Name} | {item.SourceText} | {item.UpdatedText} | {item.ChosenBecause}";
            });
            Log("  " + line);
        }

        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(500);
            var done = await Dispatcher.UIThread.InvokeAsync(() =>
                !viewModel.ModBrowser.IsLoadingVersions && viewModel.ModBrowser.Versions.Count > 0);
            if (done)
                break;
        }

        var versions = await Dispatcher.UIThread.InvokeAsync(() => viewModel.ModBrowser.Versions.Count);
        Log($"版本数={versions}，状态：{await Dispatcher.UIThread.InvokeAsync(() => viewModel.ModBrowser.StatusText)}");
        for (var i = 0; i < Math.Min(versions, 3); i++)
        {
            var index = i;
            Log("  版本 " + await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var v = viewModel.ModBrowser.Versions[index];
                return $"{v.Version.VersionNumber} | {v.GameVersionsText} | {v.LoadersText} | {v.DependencyText}";
            }));
        }

        Log("=== done（未执行下载，避免写入用户实例）===");
        Environment.Exit(0);
    }

    /// <summary>
    /// 模组加载器安装自测：按 <c>版本号:加载器</c>（例如 <c>1.21.10:quilt</c>）驱动界面
    /// 走完“打开安装页 → 查询加载器版本 → 查询 Fabric API → 开始安装”的完整流程，并记录进度。
    /// </summary>
    private static async Task ModLoaderInstallSelfTestAsync(MainWindowViewModel viewModel, string spec)
    {
        var directory = Path.Combine(Path.GetTempPath(), "novalaunch-layout");
        Directory.CreateDirectory(directory);
        using var writer = new StreamWriter(Path.Combine(directory, "modloader-selftest.txt"), append: false,
            System.Text.Encoding.UTF8) { AutoFlush = true };

        void Log(string message) => writer.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

        var pieces = spec.Split(':', 2);
        var mcVersion = pieces[0];
        var loaderName = pieces.Length > 1 ? pieces[1] : "fabric";
        var kind = loaderName switch
        {
            "quilt" => DPL.Models.ModLoaderKind.Quilt,
            "vanilla" => DPL.Models.ModLoaderKind.Vanilla,
            "forge" => DPL.Models.ModLoaderKind.Forge,
            "neoforge" => DPL.Models.ModLoaderKind.NeoForge,
            _ => DPL.Models.ModLoaderKind.Fabric,
        };

        Log($"自测目标：{mcVersion} + {kind}");

        for (var i = 0; i < 60 && viewModel.OnlineVersions.Count == 0; i++)
            await Task.Delay(500);

        var item = await Dispatcher.UIThread.InvokeAsync(() => viewModel.OnlineVersions
            .FirstOrDefault(v => string.Equals(v.Name, mcVersion, StringComparison.OrdinalIgnoreCase)));

        if (item is null)
        {
            Log($"在线列表里找不到 {mcVersion}（共 {viewModel.OnlineVersions.Count} 个版本）");
            Environment.Exit(0);
            return;
        }

        // 1) 点击卡片进入安装页
        await Dispatcher.UIThread.InvokeAsync(() => item.OpenInstallCommand.Execute(null));
        await Task.Delay(300);
        Log($"进入安装页：页面索引={await Dispatcher.UIThread.InvokeAsync(() => viewModel.CurrentPageIndex)} " +
            $"目标版本={await Dispatcher.UIThread.InvokeAsync(() => viewModel.InstallTargetVersionName)} " +
            $"IsInstallPage={await Dispatcher.UIThread.InvokeAsync(() => viewModel.IsInstallPage)}");

        // 2) 等待默认（Fabric）加载器版本与 Fabric API 查询完成
        for (var i = 0; i < 60; i++)
        {
            var ready = await Dispatcher.UIThread.InvokeAsync(() =>
                !viewModel.IsLoaderLoading && !viewModel.IsFabricApiLoading && viewModel.HasLoaderVersions);
            if (ready)
                break;
            await Task.Delay(500);
        }

        Log($"默认加载器：{await Dispatcher.UIThread.InvokeAsync(() => viewModel.SelectedLoaderKind)} " +
            $"版本数={await Dispatcher.UIThread.InvokeAsync(() => viewModel.LoaderVersions.Count)} " +
            $"选中版本={await Dispatcher.UIThread.InvokeAsync(() => viewModel.SelectedLoaderVersion?.Version)}");
        Log($"加载器提示：{await Dispatcher.UIThread.InvokeAsync(() => viewModel.LoaderStatusText)}");
        Log($"Fabric API 卡片可见={await Dispatcher.UIThread.InvokeAsync(() => viewModel.IsFabricApiCardVisible)} " +
            $"推荐版本={await Dispatcher.UIThread.InvokeAsync(() => viewModel.FabricApiVersionText)} " +
            $"一起安装={await Dispatcher.UIThread.InvokeAsync(() => viewModel.IncludeFabricApi)}");
        Log($"Fabric API 提示：{await Dispatcher.UIThread.InvokeAsync(() => viewModel.FabricApiStatusText)}");
        Log($"安装汇总：{await Dispatcher.UIThread.InvokeAsync(() => viewModel.InstallSummaryText)}");

        // 3) 切换到目标加载器
        var option = await Dispatcher.UIThread.InvokeAsync(() =>
            viewModel.LoaderOptions.FirstOrDefault(o => o.Kind == kind));
        if (option is null)
        {
            Log($"找不到加载器选项 {kind}");
            Environment.Exit(0);
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() => option.SelectCommand.Execute(null));

        for (var i = 0; i < 120; i++)
        {
            var ready = await Dispatcher.UIThread.InvokeAsync(() =>
                !viewModel.IsLoaderLoading && viewModel.SelectedLoaderKind == kind);
            if (ready)
            {
                // 查询结束后再等一小会儿，确保状态文字已经写入。
                await Task.Delay(500);
                break;
            }
            await Task.Delay(500);
        }

        Log($"切换后：加载器={await Dispatcher.UIThread.InvokeAsync(() => viewModel.SelectedLoaderKind)} " +
            $"能否开始安装={await Dispatcher.UIThread.InvokeAsync(() => viewModel.CanStartInstall)} " +
            $"加载器提示={await Dispatcher.UIThread.InvokeAsync(() => viewModel.LoaderStatusText)} " +
            $"版本数={await Dispatcher.UIThread.InvokeAsync(() => viewModel.LoaderVersions.Count)} " +
            $"选中版本={await Dispatcher.UIThread.InvokeAsync(() => viewModel.SelectedLoaderVersion?.Version)} " +
            $"Fabric API 卡片可见={await Dispatcher.UIThread.InvokeAsync(() => viewModel.IsFabricApiCardVisible)}");
        Log($"安装汇总：{await Dispatcher.UIThread.InvokeAsync(() => viewModel.InstallSummaryText)}");

        // 4) 开始安装
        await Dispatcher.UIThread.InvokeAsync(() => viewModel.StartInstallCommand.Execute(null));

        var started = DateTime.UtcNow;
        var lastPercent = -10d;
        var lastStage = string.Empty;
        while (DateTime.UtcNow - started < TimeSpan.FromMinutes(15))
        {
            await Task.Delay(2000);
            var percent = await Dispatcher.UIThread.InvokeAsync(() => viewModel.InstallPercent);
            var stage = await Dispatcher.UIThread.InvokeAsync(() => viewModel.InstallStageText);
            var status = await Dispatcher.UIThread.InvokeAsync(() => viewModel.StatusMessage);
            var installing = await Dispatcher.UIThread.InvokeAsync(() => viewModel.IsInstalling);

            if (Math.Abs(percent - lastPercent) >= 10 || stage != lastStage)
            {
                lastPercent = percent;
                lastStage = stage;
                Log($"进度 {percent:0.0}% | {stage} | 状态：{status}");
            }

            if (!installing)
                break;
        }

        await Task.Delay(1500);

        var finalStatus = await Dispatcher.UIThread.InvokeAsync(() => viewModel.StatusMessage);
        var isError = await Dispatcher.UIThread.InvokeAsync(() => viewModel.StatusIsError);
        var pageIndex = await Dispatcher.UIThread.InvokeAsync(() => viewModel.CurrentPageIndex);
        var selected = await Dispatcher.UIThread.InvokeAsync(() => viewModel.SelectedLocalVersion?.Name);
        var localNames = await Dispatcher.UIThread.InvokeAsync(() =>
            string.Join(", ", viewModel.LocalVersions.Select(v => v.Name).Take(15)));

        Log($"最终：错误={isError} 页面={pageIndex} 状态={finalStatus}");

        var mirrorLines = await Dispatcher.UIThread.InvokeAsync(() =>
            string.Join("\n", viewModel.AppLogs.Where(l => l.Contains("[镜像]")).TakeLast(8)));
        Log($"界面日志里的镜像记录（{await Dispatcher.UIThread.InvokeAsync(() => viewModel.AppLogs.Count(l => l.Contains("[镜像]")))} 条）：\n{mirrorLines}");
        Log($"本地选中版本 = {selected}");
        Log($"本地版本列表（前 15）= {localNames}");

        Log("=== done ===");
        Environment.Exit(0);
    }

    /// <summary>
    /// 微软登录自测：点击“登录微软账号”后确认没有出现
    /// “Current platform does not support to provide default WebUI” 之类的错误，
    /// 并且确实弹出了登录对话框；随后模拟取消。
    /// </summary>
    private static async Task MicrosoftLoginSelfTestAsync(Window window, MainWindowViewModel viewModel)
    {
        var directory = Path.Combine(Path.GetTempPath(), "novalaunch-layout");
        Directory.CreateDirectory(directory);
        using var writer = new StreamWriter(Path.Combine(directory, "mslogin-selftest.txt"), append: false,
            System.Text.Encoding.UTF8) { AutoFlush = true };

        void Log(string message) => writer.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

        for (var i = 0; i < 40 && viewModel.LocalVersions.Count == 0; i++)
            await Task.Delay(500);

        Log($"点击前：模式={viewModel.AccountModeIndex} 微软状态={viewModel.MicrosoftLoginStatus}");
        await Dispatcher.UIThread.InvokeAsync(() => viewModel.LoginMicrosoftCommand.Execute(null));

        Window? dialog = null;
        for (var i = 0; i < 30 && dialog is null; i++)
        {
            await Task.Delay(500);
            dialog = await Dispatcher.UIThread.InvokeAsync(() => FindLoginDialog());
        }

        var status = await Dispatcher.UIThread.InvokeAsync(() => viewModel.MicrosoftLoginStatus);
        var statusBar = await Dispatcher.UIThread.InvokeAsync(() => viewModel.StatusMessage);

        Log($"是否弹出登录对话框（Avalonia 降级方案）：{dialog is not null}");
        Log($"微软状态：{status}");
        Log($"状态栏：{statusBar}");
        Log($"是否出现 WebUI 不支持的错误：{status.Contains("WebUI", StringComparison.OrdinalIgnoreCase)}");

#if WINDOWS
        // 等待内嵌 WebView2 初始化（Windows 版走这条路径）
        for (var i = 0; i < 30; i++)
        {
            if (WebView2MicrosoftLoginUi_Ready)
                break;
            await Task.Delay(500);
        }

        Log("--- WebView2 诊断日志 ---");
        Log(string.Join("\n", WebView2MicrosoftLoginUi_Diagnostics()));

        // 模拟“用户在浏览器里完成登录，页面跳回 oauth20_desktop.srf”，
        // 用于验证内嵌窗口能否正确截获授权码并继续登录流程。
        var navigated = WebView2MicrosoftLoginUi_NavigateForTest(
            "https://login.live.com/oauth20_desktop.srf?code=M.R3_BAY.fake-code-for-test");
        Log($"是否成功模拟回调跳转：{navigated}");

        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(1000);
            var current = await Dispatcher.UIThread.InvokeAsync(() => viewModel.MicrosoftLoginStatus);
            if (current.Contains("失败", StringComparison.Ordinal))
                break;
        }

        Log($"模拟回调后微软状态：{await Dispatcher.UIThread.InvokeAsync(() => viewModel.MicrosoftLoginStatus)}");
        Log($"模拟回调后状态栏：{await Dispatcher.UIThread.InvokeAsync(() => viewModel.StatusMessage)}");
        Log($"已保存账号数：{await Dispatcher.UIThread.InvokeAsync(() => viewModel.MicrosoftAccounts.Count)}");
        Log("--- 模拟回调后的诊断日志 ---");
        Log(string.Join("\n", WebView2MicrosoftLoginUi_Diagnostics()));
#else
        // 非 Windows：验证“粘贴回跳网址 → 点完成”的链路
        if (dialog is not null)
        {
            var pasted = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var textBox = dialog.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
                var confirmButton = dialog.GetVisualDescendants().OfType<Button>()
                    .FirstOrDefault(b => (b.Content as string) == "完成");

                if (textBox is null || confirmButton is null)
                    return false;

                textBox.Text = "https://login.live.com/oauth20_desktop.srf?code=M.R3_BAY.fake-code-for-test";
                confirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                return true;
            });

            Log($"是否模拟粘贴并点击「完成」：{pasted}");

            for (var i = 0; i < 20; i++)
            {
                await Task.Delay(1000);
                var current = await Dispatcher.UIThread.InvokeAsync(() => viewModel.MicrosoftLoginStatus);
                if (current.Contains("失败", StringComparison.Ordinal))
                    break;
            }

            Log($"模拟登录后微软状态：{await Dispatcher.UIThread.InvokeAsync(() => viewModel.MicrosoftLoginStatus)}");
            Log($"模拟登录后状态栏：{await Dispatcher.UIThread.InvokeAsync(() => viewModel.StatusMessage)}");
            Log($"已保存账号数：{await Dispatcher.UIThread.InvokeAsync(() => viewModel.MicrosoftAccounts.Count)}");
        }
#endif

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var target = FindLoginDialog();
            if (target is not null)
                target.Close(false);
        });

        await Task.Delay(800);
        Log($"结束前微软状态：{await Dispatcher.UIThread.InvokeAsync(() => viewModel.MicrosoftLoginStatus)}");

        Log("=== done ===");
        Environment.Exit(0);
    }

    /// <summary>
    /// 在当前应用窗口中查找微软登录对话框。
    /// </summary>
    private static Window? FindLoginDialog()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return null;

        return desktop.Windows.FirstOrDefault(w => w.Title == "微软账号登录");
    }

#if WINDOWS
    /// <summary>内嵌 WebView2 是否已初始化完成。</summary>
    private static bool WebView2MicrosoftLoginUi_Ready =>
        WebView2MicrosoftLoginUi_Diagnostics().Any(line => line.StartsWith("WebView2 已初始化", StringComparison.Ordinal));

    /// <summary>读取内嵌登录窗口的诊断日志。</summary>
    private static string[] WebView2MicrosoftLoginUi_Diagnostics()
        => DPL.Services.WebView2MicrosoftLoginUi.Diagnostics.ToArray();

    /// <summary>把内嵌登录窗口导航到指定地址（模拟 OAuth 回调）。</summary>
    private static bool WebView2MicrosoftLoginUi_NavigateForTest(string url)
        => DPL.Services.WebView2MicrosoftLoginUi.TestNavigator?.Invoke(url) ?? false;
#endif

    /// <summary>
    /// 账号功能自测：验证离线名称历史、账号快速切换列表、账号模式切换，
    /// 以及“未登录微软账号却选择正版模式”时的错误提示。
    /// </summary>
    private static async Task AccountSelfTestAsync(MainWindowViewModel viewModel)
    {
        var directory = Path.Combine(Path.GetTempPath(), "novalaunch-layout");
        Directory.CreateDirectory(directory);
        using var writer = new StreamWriter(Path.Combine(directory, "account-selftest.txt"), append: false,
            System.Text.Encoding.UTF8) { AutoFlush = true };

        void Log(string message) => writer.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

        // 等待初始数据（本地版本 + 账号列表）加载完成
        for (var i = 0; i < 40 && viewModel.LocalVersions.Count == 0; i++)
            await Task.Delay(500);

        var initial = await Dispatcher.UIThread.InvokeAsync(() => new
        {
            viewModel.AccountModeIndex,
            viewModel.PlayerName,
            History = viewModel.OfflineNameHistory.Count,
            Microsoft = viewModel.MicrosoftAccounts.Count,
            QuickSwitch = viewModel.Accounts.Count,
            viewModel.CurrentAccountText,
            viewModel.CurrentAccountDetail,
            viewModel.MicrosoftLoginStatus,
        });

        Log($"初始状态：模式={initial.AccountModeIndex}（0=离线 1=正版） 离线名={initial.PlayerName} " +
            $"历史数={initial.History} 微软账号数={initial.Microsoft} 快速切换项={initial.QuickSwitch}");
        Log($"当前账号显示：{initial.CurrentAccountText} / {initial.CurrentAccountDetail}");
        Log($"微软状态：{initial.MicrosoftLoginStatus}");
        Log($"历史条目：{string.Join(", ", await Dispatcher.UIThread.InvokeAsync(() => viewModel.OfflineNameHistory.ToList()))}");
        Log($"快速切换条目：{string.Join(", ", await Dispatcher.UIThread.InvokeAsync(() => viewModel.Accounts.Select(a => a.DisplayName).ToList()))}");

        // ---------- 1. 点击历史名称应直接填入离线输入框 ----------
        var historyName = await Dispatcher.UIThread.InvokeAsync(() => viewModel.OfflineNameHistory.FirstOrDefault());
        if (historyName is not null)
        {
            await Dispatcher.UIThread.InvokeAsync(() => viewModel.SelectedHistoryName = historyName);
            var afterSelect = await Dispatcher.UIThread.InvokeAsync(() => new { viewModel.PlayerName, viewModel.CurrentAccountText });
            Log($"1) 选择历史名称 [{historyName}] 后：PlayerName={afterSelect.PlayerName} 当前账号={afterSelect.CurrentAccountText}");
        }
        else
        {
            Log("1) 历史为空，跳过“点击历史名称”测试");
        }

        // ---------- 2. 快速切换下拉框选择账号 ----------
        var quickItems = await Dispatcher.UIThread.InvokeAsync(() => viewModel.Accounts.ToList());
        if (quickItems.Count > 0)
        {
            await Dispatcher.UIThread.InvokeAsync(() => viewModel.SelectedAccount = quickItems[0]);
            var picked = await Dispatcher.UIThread.InvokeAsync(() => new
            {
                viewModel.PlayerName,
                viewModel.IsOfflineAccountMode,
                viewModel.CurrentAccountText,
            });
            Log($"2) 快速切换选择 [{quickItems[0].DisplayName}] 后：模式={(picked.IsOfflineAccountMode ? "离线" : "正版")} " +
                $"PlayerName={picked.PlayerName} 当前账号={picked.CurrentAccountText}");
        }

        // ---------- 3. 切换到正版模式但未登录：启动应给出明确错误 ----------
        await Dispatcher.UIThread.InvokeAsync(() => viewModel.AccountModeIndex = 1);
        await Task.Delay(300);
        var premiumState = await Dispatcher.UIThread.InvokeAsync(() => new
        {
            viewModel.IsMicrosoftAccountMode,
            viewModel.CurrentAccountDetail,
        });
        Log($"3) 切换正版模式：IsMicrosoftAccountMode={premiumState.IsMicrosoftAccountMode} 说明={premiumState.CurrentAccountDetail}");

        var item = await Dispatcher.UIThread.InvokeAsync(() => viewModel.LocalVersions.FirstOrDefault());
        if (item is not null)
        {
            await Dispatcher.UIThread.InvokeAsync(() => viewModel.SelectedLocalVersion = item);
            await Dispatcher.UIThread.InvokeAsync(() => viewModel.LaunchSelectedCommand.Execute(null));
            await Task.Delay(2500);

            var summary = await Dispatcher.UIThread.InvokeAsync(() => new
            {
                viewModel.StatusMessage,
                viewModel.StatusIsError,
                viewModel.IsGameRunning,
                viewModel.MicrosoftLoginStatus,
            });
            Log($"4) 未登录正版账号时启动：出错={summary.StatusIsError} 运行中={summary.IsGameRunning}");
            Log($"   状态栏：{summary.StatusMessage}");
            Log($"   微软状态：{summary.MicrosoftLoginStatus}");
        }

        Log("=== done ===");
        Environment.Exit(0);
    }

    /// <summary>
    /// 自动化自测：通过界面的启动命令启动指定版本，并逐秒记录界面状态
    /// （校验进度是否可见、是否出现错误提示、是否进入“游戏运行中”），用于验证启动链路。
    /// </summary>
    private static async Task LaunchSelfTestAsync(MainWindowViewModel viewModel, string versionName)
    {
        var directory = Path.Combine(Path.GetTempPath(), "novalaunch-layout");
        Directory.CreateDirectory(directory);
        using var writer = new StreamWriter(Path.Combine(directory, "launch-selftest.txt"), append: false,
            System.Text.Encoding.UTF8) { AutoFlush = true };

        void Log(string message) => writer.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

        // 等待本地版本列表加载完成。
        for (var i = 0; i < 60 && viewModel.LocalVersions.Count == 0; i++)
            await Task.Delay(500);

        var item = await Dispatcher.UIThread.InvokeAsync(() =>
            viewModel.LocalVersions.FirstOrDefault(v => v.Name == versionName));

        if (item is null)
        {
            Log($"未找到版本：{versionName}（当前共 {viewModel.LocalVersions.Count} 个本地版本）");
            Environment.Exit(0);
            return;
        }

        Log($"本地版本 = {viewModel.LocalVersions.Count}，目标版本 = {versionName}");
        Log($"Java 候选 = {string.Join(" | ", viewModel.JavaCandidates.Select(c => c.DisplayName))}");
        Log($"Java 提示 = {viewModel.JavaDetectedText}");
        Log($"启动前校验开关 = {viewModel.CheckFilesBeforeLaunch}，版本隔离开关 = {viewModel.UseVersionIsolation}");

        await Dispatcher.UIThread.InvokeAsync(() => viewModel.SelectedLocalVersion = item);
        await Dispatcher.UIThread.InvokeAsync(() => viewModel.LaunchSelectedCommand.Execute(null));

        var started = DateTime.UtcNow;
        var lastMessage = string.Empty;

        while ((DateTime.UtcNow - started).TotalSeconds < 180)
        {
            await Task.Delay(2000);

            var snapshot = await Dispatcher.UIThread.InvokeAsync(() => new
            {
                viewModel.StatusMessage,
                viewModel.StatusIsError,
                viewModel.IsInstalling,
                viewModel.InstallPercent,
                viewModel.InstallStageText,
                viewModel.IsGameRunning,
                LogCount = viewModel.GameLogs.Count,
                ItemStatus = item.StatusText,
            });

            if (snapshot.StatusMessage != lastMessage)
            {
                lastMessage = snapshot.StatusMessage;
                Log($"状态：{snapshot.StatusMessage}");
            }

            Log($"  进度可见={snapshot.IsInstalling} {snapshot.InstallPercent:0.0}% [{snapshot.InstallStageText}] " +
                $"运行中={snapshot.IsGameRunning} 出错={snapshot.StatusIsError} 日志行={snapshot.LogCount} 条目={snapshot.ItemStatus}");

            // 出现失败提示，或进入“游戏运行中”后再等几秒，即可判定结果。
            if (snapshot.StatusIsError)
            {
                Log("检测到失败提示，结束自测");
                break;
            }

            if (!snapshot.IsInstalling && !snapshot.IsGameRunning && snapshot.LogCount > 0 &&
                (DateTime.UtcNow - started).TotalSeconds > 15)
            {
                Log("启动流程结束（游戏已退出）");
                break;
            }

            if (snapshot.IsGameRunning && (DateTime.UtcNow - started).TotalSeconds > 40)
            {
                Log("游戏进程已在运行，结束自测");
                break;
            }
        }

        var finalLogs = await Dispatcher.UIThread.InvokeAsync(() =>
            string.Join(" / ", viewModel.GameLogs.TakeLast(6)));
        Log($"最后日志：{finalLogs}");
        Environment.Exit(0);
    }

    /// <summary>
    /// 自动化自测：通过 ViewModel 的命令完成“选择一个未安装的正式版 → 下载安装 → 刷新本地列表”的完整流程，
    /// 用于在没有人工点击的情况下验证界面层的下载链路（含进度回调切回 UI 线程）。
    /// </summary>
    private static async Task SelfTestAsync(MainWindowViewModel viewModel)
    {
        var directory = Path.Combine(Path.GetTempPath(), "novalaunch-layout");
        Directory.CreateDirectory(directory);
        var logPath = Path.Combine(directory, "selftest.txt");
        using var writer = new StreamWriter(logPath, append: false, System.Text.Encoding.UTF8) { AutoFlush = true };

        void Log(string message) => writer.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

        Log("等待版本列表加载…");
        for (var i = 0; i < 40 && (viewModel.OnlineVersions.Count == 0 || viewModel.IsBusy); i++)
            await Task.Delay(500);

        Log($"在线版本 {viewModel.OnlineVersions.Count} 个，本地版本 {viewModel.LocalVersions.Count} 个");

        var target = await Dispatcher.UIThread.InvokeAsync(() => viewModel.OnlineVersions
            .FirstOrDefault(v => !v.IsInstalled && v.Kind == DPL.Models.GameVersionKind.Release));

        if (target is null)
        {
            Log("没有找到可用于测试的未安装正式版，测试结束");
            Environment.Exit(0);
            return;
        }

        Log($"目标版本：{target.Name}（{target.TypeText} {target.ReleaseTimeText}）");

        await Dispatcher.UIThread.InvokeAsync(() => target.InstallCommand.Execute(null));

        var started = DateTime.UtcNow;
        var maxWait = TimeSpan.FromMinutes(10);
        var lastPercent = -1d;

        while (DateTime.UtcNow - started < maxWait)
        {
            await Task.Delay(1000);
            var percent = await Dispatcher.UIThread.InvokeAsync(() => viewModel.InstallPercent);
            var installing = await Dispatcher.UIThread.InvokeAsync(() => viewModel.IsInstalling);
            var stage = await Dispatcher.UIThread.InvokeAsync(() => viewModel.InstallStageText);
            var size = await Dispatcher.UIThread.InvokeAsync(() => viewModel.InstallSizeText);
            var status = await Dispatcher.UIThread.InvokeAsync(() => viewModel.StatusMessage);

            if (Math.Abs(percent - lastPercent) >= 10)
            {
                lastPercent = percent;
                Log($"安装进度 {percent:0.0}% | {stage} | {size} | 状态：{status}");
            }

            if (!installing && percent <= 0)
                break;
        }

        await Task.Delay(1500);

        var status1 = await Dispatcher.UIThread.InvokeAsync(() => viewModel.StatusMessage);
        var inLocal = await Dispatcher.UIThread.InvokeAsync(() =>
            viewModel.LocalVersions.Any(v => v.Name == target.Name));
        var badgeInstalled = await Dispatcher.UIThread.InvokeAsync(() => target.IsInstalled);

        Log($"最终状态：{status1}");
        Log($"本地列表包含 {target.Name} = {inLocal}；在线列表已标记安装 = {badgeInstalled}");

        Log("测试结束");
        Environment.Exit(0);
    }

    /// <summary>
    /// 依次截图三个页面（导出布局），完成后关闭程序。
    /// 设置页会分别导出“离线账号模式”与“微软正版模式”两套布局。
    /// </summary>
    private static async Task CaptureAsync(Window window, MainWindowViewModel viewModel)
    {
        var directory = Path.Combine(Path.GetTempPath(), "novalaunch-layout");
        Directory.CreateDirectory(directory);

        // 等待首屏数据（本地版本、在线清单、账号列表）加载完成。
        await Task.Delay(TimeSpan.FromSeconds(8));

        string[] names = ["01-local.txt", "02-download.txt", "03-settings-offline.txt"];
        for (var i = 0; i < names.Length; i++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => viewModel.CurrentPageIndex = i);
            await Task.Delay(600);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var builder = new StringBuilder();
                Dump(window, builder, 0);
                File.WriteAllText(Path.Combine(directory, names[i]), builder.ToString());
            });
        }

        // 额外导出一份“微软正版账号”模式下的设置页布局
        await Dispatcher.UIThread.InvokeAsync(() => viewModel.AccountModeIndex = 1);
        await Task.Delay(600);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var builder = new StringBuilder();
            Dump(window, builder, 0);
            File.WriteAllText(Path.Combine(directory, "04-settings-microsoft.txt"), builder.ToString());
        });

        await Dispatcher.UIThread.InvokeAsync(() => viewModel.AccountModeIndex = 0);

        // 额外导出“模组加载器安装页”（先点开一个在线版本，等待加载器版本与 Fabric API 查询完成）
        var onlineItem = await Dispatcher.UIThread.InvokeAsync(() => viewModel.FilteredOnlineVersions.FirstOrDefault());
        if (onlineItem is not null)
        {
            await Dispatcher.UIThread.InvokeAsync(() => onlineItem.OpenInstallCommand.Execute(null));

            for (var i = 0; i < 60; i++)
            {
                var ready = await Dispatcher.UIThread.InvokeAsync(() =>
                    !viewModel.IsLoaderLoading && !viewModel.IsFabricApiLoading && viewModel.HasLoaderVersions);
                if (ready)
                    break;
                await Task.Delay(500);
            }

            await Task.Delay(800);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var builder = new StringBuilder();
                Dump(window, builder, 0);
                File.WriteAllText(Path.Combine(directory, "05-install-fabric.txt"), builder.ToString());
            });

            // 再看一下“原版”选项下的布局
            var vanillaOption = await Dispatcher.UIThread.InvokeAsync(() =>
                viewModel.LoaderOptions.FirstOrDefault(o => o.Kind == DPL.Models.ModLoaderKind.Vanilla));
            if (vanillaOption is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => vanillaOption.SelectCommand.Execute(null));
                await Task.Delay(600);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var builder = new StringBuilder();
                    Dump(window, builder, 0);
                    File.WriteAllText(Path.Combine(directory, "06-install-vanilla.txt"), builder.ToString());
                });
            }

            // 再看一下 Forge / NeoForge 选项下的布局（会运行官方安装器，这里只查询版本）
            foreach (var (kind, name) in new[]
                     {
                         (DPL.Models.ModLoaderKind.Forge, "forge"),
                         (DPL.Models.ModLoaderKind.NeoForge, "neoforge"),
                     })
            {
                var option = await Dispatcher.UIThread.InvokeAsync(() =>
                    viewModel.LoaderOptions.FirstOrDefault(o => o.Kind == kind));
                if (option is null)
                    continue;

                await Dispatcher.UIThread.InvokeAsync(() => option.SelectCommand.Execute(null));

                for (var i = 0; i < 40; i++)
                {
                    var ready = await Dispatcher.UIThread.InvokeAsync(() => !viewModel.IsLoaderLoading);
                    if (ready)
                        break;
                    await Task.Delay(500);
                }

                await Task.Delay(600);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var builder = new StringBuilder();
                    Dump(window, builder, 0);
                    File.WriteAllText(Path.Combine(directory, $"07-install-{name}.txt"), builder.ToString());
                });
            }
        }

        // 额外导出“模组下载”页面（自动选中当前实例并探测下载源）
        await Dispatcher.UIThread.InvokeAsync(() => viewModel.ShowModsPageCommand.Execute(null));
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            var ready = await Dispatcher.UIThread.InvokeAsync(() =>
                viewModel.IsModsPage && !viewModel.ModBrowser.IsSearching);
            if (ready)
                break;
        }

        await Task.Delay(1200);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var builder = new StringBuilder();
            Dump(window, builder, 0);
            File.WriteAllText(Path.Combine(directory, "08-mods.txt"), builder.ToString());
        });

        Environment.Exit(0);
    }

    /// <summary>
    /// 递归导出可视化树。
    /// </summary>
    private static void Dump(Visual visual, StringBuilder builder, int depth)
    {
        var indent = new string(' ', depth * 2);
        var bounds = visual.Bounds;
        var name = (visual as StyledElement)?.Name;
        var visible = visual.IsVisible ? "" : " [hidden]";

        var extra = string.Empty;
        switch (visual)
        {
            case TextBlock textBlock:
                extra = $" text=\"{Shorten(textBlock.Text)}\"";
                break;
            case TextBox textBox:
                extra = $" value=\"{Shorten(textBox.Text)}\" watermark=\"{Shorten(textBox.Watermark as string)}\"";
                break;
            case ContentControl contentControl when contentControl.Content is string content:
                extra = $" content=\"{Shorten(content)}\"";
                break;
            case Avalonia.Controls.Shapes.Path path:
                extra = $" geometry=\"{(path.Data is null ? string.Empty : "…")}\"";
                break;
        }

        builder.Append(indent)
            .Append(visual.GetType().Name)
            .Append(string.IsNullOrEmpty(name) ? string.Empty : $" #{name}")
            .Append($" [{bounds.X:0.#},{bounds.Y:0.#} {bounds.Width:0.#}x{bounds.Height:0.#}]")
            .Append(visible)
            .Append(extra)
            .AppendLine();

        foreach (var child in visual.GetVisualChildren())
            Dump(child, builder, depth + 1);
    }

    /// <summary>
    /// 截断过长文本，避免导出文件过大。
    /// </summary>
    private static string Shorten(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        value = value.Replace("\r", " ").Replace("\n", " ");
        return value.Length <= 48 ? value : value[..48] + "…";
    }
}
#endif
