using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DPL.Services;
using DPL.ViewModels;
using DPL.Views;

namespace DPL;

/// <summary>
/// Avalonia 应用程序类，负责加载样式并创建主窗口。
/// </summary>
public partial class App : Application
{
    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 组合根：在这里创建服务与 ViewModel，并完成依赖注入。
            // 启动器改名为 DPL 后，先尝试把旧目录（LGLun）里的设置 / 账号 / 登录缓存迁移过来。
            Services.AppPaths.MigrateLegacyData();

            var settingsService = new SettingsService();
            var settings = settingsService.Load();

            // 先应用保存的主题，再创建窗口，避免启动瞬间闪一下深色。
            Services.ThemeManager.Apply(settings.Theme);
            var minecraftService = new MinecraftService(settings.GameDirectory);

            // 先创建窗口（作为微软登录对话框的父窗口），再创建需要窗口的服务。
            var window = new MainWindow();
            var loginUi = MicrosoftLoginUiFactory.Create(window);
            var microsoftAuthService = new MicrosoftAuthService(loginUi);
            var fabricApiService = new FabricApiService();

            var viewModel = new MainWindowViewModel(
                settingsService, minecraftService, microsoftAuthService, fabricApiService);
            window.DataContext = viewModel;

            viewModel.FilePicker = new FilePickerService(window);
            viewModel.MinimizeRequested = () => window.WindowState = WindowState.Minimized;
            viewModel.MaximizeRequested = () => window.WindowState =
                window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            viewModel.CloseRequested = window.Close;

            desktop.MainWindow = window;
            desktop.Exit += (_, _) =>
            {
                minecraftService.Dispose();
                microsoftAuthService.Dispose();
                fabricApiService.Dispose();
            };

#if DEBUG
            // 调试辅助：设置 NOVALAUNCH_LAYOUT_DUMP 环境变量可导出界面布局信息（发布版本不包含该逻辑）。
            Diagnostics.VisualTreeDumper.RunIfRequested(window, viewModel);
#endif
        }

        base.OnFrameworkInitializationCompleted();
    }
}
