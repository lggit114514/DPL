using System.Diagnostics;

namespace DPL.Services;

/// <summary>
/// 与操作系统交互的辅助方法。
/// </summary>
public static class SystemIntegration
{
    /// <summary>
    /// 使用系统文件管理器打开指定目录。
    /// </summary>
    /// <param name="directory">需要打开的目录路径。</param>
    /// <returns>是否成功发起打开请求。</returns>
    public static bool OpenDirectory(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
                return false;

            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open", $"\"{directory}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("xdg-open", $"\"{directory}\"") { UseShellExecute = true });

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 使用系统默认浏览器打开网址。
    /// </summary>
    /// <param name="url">需要打开的网址。</param>
    /// <returns>是否成功发起打开请求。</returns>
    public static bool OpenUrl(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open", $"\"{url}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("xdg-open", $"\"{url}\"") { UseShellExecute = true });

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
