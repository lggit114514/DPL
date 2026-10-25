using DPL.Models;

namespace DPL.Services;

/// <summary>
/// 本机 Java 运行时探测器。
/// CmlLib 只管理它自己下载到 <c>&lt;游戏目录&gt;/runtime</c> 的 Java，
/// 因此这里额外扫描系统中已安装的 Java，作为“自动检测”的补充。
/// </summary>
public static class JavaLocator
{
    /// <summary>
    /// 扫描本机所有可用的 Java 可执行文件。
    /// </summary>
    /// <returns>去重后的 Java 候选列表，优先返回带 <c>javaw</c> 的窗口版可执行文件。</returns>
    public static IReadOnlyList<JavaCandidate> FindCandidates()
    {
        var executables = new List<string>();
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // 1. JAVA_HOME 环境变量指向的 JDK / JRE
        AddFromJavaHome(executables, Environment.GetEnvironmentVariable("JAVA_HOME"));

        if (OperatingSystem.IsWindows())
        {
            // 2. Windows 常见 JDK 安装目录
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            AddFromChildDirectories(executables, Path.Combine(programFiles, "Java"));
            AddFromChildDirectories(executables, Path.Combine(programFiles, "Eclipse Adoptium"));
            AddFromChildDirectories(executables, Path.Combine(programFiles, "Eclipse Foundation"));
            AddFromChildDirectories(executables, Path.Combine(programFiles, "Zulu"));
            AddFromChildDirectories(executables, Path.Combine(programFiles, "Microsoft"));
            AddFromChildDirectories(executables, Path.Combine(programFiles, "BellSoft"));
            AddFromChildDirectories(executables, Path.Combine(programFiles, "Semeru"));
            AddFromChildDirectories(executables, Path.Combine(programFiles, "Amazon Corretto"));
            AddFromChildDirectories(executables, Path.Combine(programFilesX86, "Java"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            // 3. macOS：系统级 / 用户级 JavaVirtualMachines、Homebrew、SDKMAN
            AddFromChildDirectories(executables, "/Library/Java/JavaVirtualMachines", "Contents/Home/bin");

            if (!string.IsNullOrWhiteSpace(userHome))
                AddFromChildDirectories(
                    executables,
                    Path.Combine(userHome, "Library/Java/JavaVirtualMachines"),
                    "Contents/Home/bin");

            AddExecutable(executables, "/opt/homebrew/opt/openjdk/bin");
            AddExecutable(executables, "/usr/local/opt/openjdk/bin");

            if (!string.IsNullOrWhiteSpace(userHome))
                AddExecutable(executables, Path.Combine(userHome, ".sdkman/candidates/java/current/bin"));
        }
        else
        {
            // 4. Linux：发行版 JVM 目录 + SDKMAN
            AddFromChildDirectories(executables, "/usr/lib/jvm");

            if (!string.IsNullOrWhiteSpace(userHome))
                AddExecutable(executables, Path.Combine(userHome, ".sdkman/candidates/java/current/bin"));
        }

        // 5. PATH 环境变量中的 java
        AddFromPathVariable(executables);

        return BuildCandidates(executables);
    }

    /// <summary>
    /// 从某个 JDK 根目录中追加 <c>bin/java(w).exe</c>。
    /// </summary>
    private static void AddFromJavaHome(List<string> executables, string? home)
    {
        if (string.IsNullOrWhiteSpace(home))
            return;

        AddExecutable(executables, Path.Combine(home, "bin"));
    }

    /// <summary>
    /// 遍历某个父目录下的所有子目录，并尝试从 <c>子目录/bin</c> 中找到 java。
    /// </summary>
    /// <param name="executables">收集结果的可变列表。</param>
    /// <param name="parentDirectory">父目录。</param>
    /// <param name="relativeBinDirectory">相对于子目录的可执行文件目录，默认为 bin。</param>
    private static void AddFromChildDirectories(
        List<string> executables,
        string parentDirectory,
        string relativeBinDirectory = "bin")
    {
        try
        {
            if (!Directory.Exists(parentDirectory))
                return;

            foreach (var child in Directory.EnumerateDirectories(parentDirectory))
                AddExecutable(executables, Path.Combine(child, relativeBinDirectory));
        }
        catch (Exception)
        {
            // 无权限访问某些目录时忽略即可。
        }
    }

    /// <summary>
    /// 从 PATH 环境变量中查找 java 可执行文件。
    /// </summary>
    private static void AddFromPathVariable(List<string> executables)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVariable))
            return;

        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            AddExecutable(executables, directory);
    }

    /// <summary>
    /// 在指定目录中查找 java 可执行文件并加入结果列表。
    /// Windows 下优先使用 javaw.exe（不会弹出控制台窗口）。
    /// </summary>
    private static void AddExecutable(List<string> executables, string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
                return;

            string[] candidates = OperatingSystem.IsWindows()
                ? ["javaw.exe", "java.exe"]
                : ["java"];

            foreach (var fileName in candidates)
            {
                var fullPath = Path.Combine(directory, fileName);
                if (File.Exists(fullPath))
                {
                    executables.Add(fullPath);
                    return;
                }
            }
        }
        catch (Exception)
        {
            // 忽略不可访问的目录。
        }
    }

    /// <summary>
    /// 将可执行文件路径集合转换为界面可用的候选对象（去重并生成显示名）。
    /// </summary>
    private static IReadOnlyList<JavaCandidate> BuildCandidates(List<string> executables)
    {
        var result = new List<JavaCandidate>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

        foreach (var executable in executables)
        {
            var fullPath = Path.GetFullPath(executable);
            if (!seen.Add(fullPath))
                continue;

            result.Add(new JavaCandidate(fullPath, BuildDisplayName(fullPath)));
        }

        return result;
    }

    /// <summary>
    /// 生成 Java 候选的显示名称：优先使用 JDK 安装目录名，否则退回完整路径。
    /// </summary>
    private static string BuildDisplayName(string executablePath)
    {
        var binDirectory = Path.GetDirectoryName(executablePath);
        var homeDirectory = binDirectory is null ? null : Path.GetDirectoryName(binDirectory);

        if (!string.IsNullOrWhiteSpace(homeDirectory))
        {
            var name = Path.GetFileName(homeDirectory);
            if (!string.IsNullOrWhiteSpace(name))
                return $"{name}  ·  {executablePath}";
        }

        return executablePath;
    }
}
