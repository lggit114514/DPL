using System.Diagnostics;

namespace DPL.Services;

/// <summary>
/// 应用数据目录的统一入口。
/// 所有需要落盘的配置 / 账号 / 日志都通过这里取路径，
/// 并负责把旧版本（改名前的 LGLun 目录）里的数据迁移过来，避免用户丢失设置与账号。
/// </summary>
public static class AppPaths
{
    /// <summary>改名前的目录名（用于一次性数据迁移）。</summary>
    private const string LegacyDirectoryName = "LGLun";

    /// <summary>当前的目录名。</summary>
    private const string CurrentDirectoryName = "DPL";

    /// <summary>漫游应用数据目录（<c>%AppData%\DPL</c>），放设置与账号。</summary>
    public static string RoamingDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        CurrentDirectoryName);

    /// <summary>本机应用数据目录（<c>%LocalAppData%\DPL</c>），放 WebView2 缓存等大文件。</summary>
    public static string LocalDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        CurrentDirectoryName);

    /// <summary>设置文件路径。</summary>
    public static string SettingsFile => Path.Combine(RoamingDirectory, "settings.json");

    /// <summary>微软账号文件路径。</summary>
    public static string AccountsFile => Path.Combine(RoamingDirectory, "microsoft-accounts.json");

    /// <summary>镜像下载日志文件路径。</summary>
    public static string MirrorLogFile => Path.Combine(RoamingDirectory, "mirror.log");

    /// <summary>
    /// 确保目录存在（失败时忽略，调用方会退化为不落盘）。
    /// </summary>
    /// <param name="directory">要创建的目录。</param>
    public static void EnsureDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception)
        {
            // 目录不可写时由调用方按“读不到配置”处理，不在这里抛异常。
        }
    }

    /// <summary>
    /// 把旧名字（LGLun）目录下的设置与账号迁移到新目录。
    /// 只在“新目录里还没有对应文件”且“旧文件存在”时复制，重复启动不会覆盖新数据。
    /// </summary>
    public static void MigrateLegacyData()
    {
        MigrateFile(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyDirectoryName, "settings.json"),
            SettingsFile);

        MigrateFile(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyDirectoryName, "microsoft-accounts.json"),
            AccountsFile);

        MigrateDirectory(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyDirectoryName, "webview2-data"),
            Path.Combine(LocalDirectory, "webview2-data"));
    }

    /// <summary>
    /// 复制单个文件（目标已存在或源不存在时跳过）。
    /// </summary>
    private static void MigrateFile(string legacyPath, string currentPath)
    {
        try
        {
            if (File.Exists(currentPath) || !File.Exists(legacyPath))
                return;

            EnsureDirectory(Path.GetDirectoryName(currentPath) ?? RoamingDirectory);
            File.Copy(legacyPath, currentPath, overwrite: false);
            Debug.WriteLine($"[DPL] 已从旧目录迁移配置：{legacyPath} -> {currentPath}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DPL] 迁移 {legacyPath} 失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 递归复制目录（目标已存在时跳过）。
    /// </summary>
    private static void MigrateDirectory(string legacyPath, string currentPath)
    {
        try
        {
            if (Directory.Exists(currentPath) || !Directory.Exists(legacyPath))
                return;

            Directory.CreateDirectory(currentPath);

            foreach (var file in Directory.EnumerateFiles(legacyPath, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(legacyPath, file);
                var target = Path.Combine(currentPath, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target) ?? currentPath);
                File.Copy(file, target, overwrite: false);
            }

            Debug.WriteLine($"[DPL] 已从旧目录迁移数据：{legacyPath} -> {currentPath}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DPL] 迁移目录 {legacyPath} 失败：{ex.Message}");
        }
    }
}
