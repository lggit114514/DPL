using System.Text;

namespace DPL.Services;

/// <summary>
/// 把启动器运行日志静默写入 &lt;启动器目录&gt;/logs/latest.log。
/// 界面不再显示日志窗口，排查问题时由用户从设置页打开日志文件夹。
/// </summary>
public static class LauncherLogWriter
{
    private static readonly object Gate = new();

    /// <summary>日志目录。</summary>
    public static string LogDirectory => AppPaths.LogsDirectory;

    /// <summary>当前会话日志文件。</summary>
    public static string LogFilePath => AppPaths.LatestLogFile;

    /// <summary>
    /// 开始新的日志会话（覆盖上一次的 latest.log）。
    /// </summary>
    public static void StartSession()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var header = new StringBuilder()
                .AppendLine("============================================================")
                .AppendLine($"DPL 启动器日志  {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
                .AppendLine($"OS: {Environment.OSVersion}")
                .AppendLine($"Runtime: {Environment.Version}")
                .AppendLine("============================================================")
                .ToString();

            File.WriteAllText(LogFilePath, header, Encoding.UTF8);
        }
        catch (Exception)
        {
            // 日志写入失败不应影响启动。
        }
    }

    /// <summary>
    /// 追加一行日志；会自动创建目录。
    /// </summary>
    /// <param name="line">日志内容。</param>
    public static void Write(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(
                    LogFilePath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {line}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // 日志写入失败不应影响运行。
        }
    }
}
