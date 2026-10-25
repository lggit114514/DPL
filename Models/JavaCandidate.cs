namespace DPL.Models;

/// <summary>
/// 本机检测到的一个 Java 运行时候选。
/// 除路径外还记录“是否真的可用”与主版本号，方便界面提示（磁盘上有 java.exe 不代表它能跑起来）。
/// </summary>
public sealed class JavaCandidate
{
    /// <summary>
    /// 创建一个尚未校验的 Java 候选（可用性与版本号后续通过探测补充）。
    /// </summary>
    /// <param name="path">Java 可执行文件路径。</param>
    /// <param name="displayName">界面上显示的名称。</param>
    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
    public JavaCandidate(string path, string displayName)
    {
        Path = path;
        DisplayName = displayName;
    }

    /// <summary>Java 可执行文件的绝对路径。</summary>
    public required string Path { get; init; }

    /// <summary>用于界面显示的简短名称，例如“jdk-21 · Z:\jdk-21\bin\javaw.exe”。</summary>
    public required string DisplayName { get; init; }

    /// <summary>该 Java 是否可用（已实际执行过 <c>java -version</c> 校验）。</summary>
    public bool IsUsable { get; init; }

    /// <summary>主版本号（例如 8、17、21、25）；不可用时为 0。</summary>
    public int MajorVersion { get; init; }
}
