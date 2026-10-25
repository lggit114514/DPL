using Avalonia;

namespace DPL;

/// <summary>
/// 应用程序入口，负责创建并启动 Avalonia 桌面应用。
/// </summary>
internal static class Program
{
    /// <summary>
    /// 主线程入口点。STAThread 是 Avalonia 桌面应用的要求（Windows 剪贴板 / 对话框等依赖 COM）。
    /// </summary>
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>
    /// 构建 Avalonia 应用构建器：使用自动平台探测、内置 Inter 字体与调试日志输出。
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
