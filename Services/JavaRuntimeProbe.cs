using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DPL.Services;

/// <summary>
/// 一个可用的 Java 运行时信息。
/// </summary>
/// <param name="Path">Java 可执行文件路径。</param>
/// <param name="MajorVersion">主版本号，例如 8、17、21、25。</param>
/// <param name="VersionText">原始版本字符串，例如 <c>openjdk version "21.0.1" 2023-10-17 LTS</c>。</param>
public sealed record JavaRuntimeInfo(string Path, int MajorVersion, string VersionText);

/// <summary>
/// Java 运行时探测工具：通过执行 <c>java -version</c> 判断某个 Java 是否真的可用。
/// 这一步非常必要——磁盘上存在 java.exe 并不代表它完整可用（例如运行时文件残缺时
/// 会直接报 "Failed to load jvm.dll"），若不校验就会出现“点了启动但游戏秒退”的现象。
/// </summary>
public static partial class JavaRuntimeProbe
{
    /// <summary>Java 路径比较：Windows 忽略大小写，macOS/Linux 区分大小写。</summary>
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly ConcurrentDictionary<string, JavaRuntimeInfo?> ProbeCache =
        new(PathComparer);

    /// <summary>
    /// 探测指定 Java 是否可用，并解析其主版本号；不可用时返回 null。
    /// </summary>
    /// <param name="javaPath">Java 可执行文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<JavaRuntimeInfo?> ProbeAsync(string? javaPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(javaPath) || !File.Exists(javaPath))
            return null;

        if (ProbeCache.TryGetValue(javaPath, out var cached))
            return cached;

        var info = await ProbeCoreAsync(javaPath, cancellationToken).ConfigureAwait(false);
        ProbeCache[javaPath] = info;
        return info;
    }

    /// <summary>
    /// 清空探测缓存。安装 / 修复 Java 运行时之后必须调用，否则会拿到旧的探测结果。
    /// </summary>
    public static void ClearCache() => ProbeCache.Clear();

    /// <summary>
    /// 执行 <c>java -version</c> 并解析输出。
    /// </summary>
    private static async Task<JavaRuntimeInfo?> ProbeCoreAsync(string javaPath, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo(javaPath, "-version")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return null;

            // 同时读取两个输出流，避免缓冲区写满导致死锁。
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var text = $"{await errorTask.ConfigureAwait(false)}{await outputTask.ConfigureAwait(false)}";
            if (process.ExitCode != 0)
                return null;

            var major = ParseMajorVersion(text);
            if (major <= 0)
                return null;

            var firstLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim()
                            ?? string.Empty;

            return new JavaRuntimeInfo(javaPath, major, firstLine);
        }
        catch (Exception)
        {
            // 进程启动失败、超时或被拒绝等都视为不可用。
            return null;
        }
    }

    /// <summary>
    /// 从 <c>java -version</c> 的输出中解析主版本号（1.8 会解析为 8）。
    /// </summary>
    private static int ParseMajorVersion(string text)
    {
        var match = VersionRegex().Match(text);
        if (!match.Success)
            return 0;

        var first = int.Parse(match.Groups[1].Value);
        if (first != 1)
            return first;

        return match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;
    }

    /// <summary>匹配 <c>version "21.0.1"</c> 形式的版本号。</summary>
    [GeneratedRegex("version\\s+\"(\\d+)(?:\\.(\\d+))?[^\"]*\"")]
    private static partial Regex VersionRegex();
}
