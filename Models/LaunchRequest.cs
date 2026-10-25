namespace DPL.Models;

/// <summary>
/// 一次游戏启动请求的参数集合，由界面收集后交给启动服务。
/// </summary>
public sealed class LaunchRequest
{
    /// <summary>离线会话使用的玩家名称。</summary>
    public string PlayerName { get; set; } = "Steve";

    /// <summary>希望使用的 Java 可执行文件路径；为空时由服务自动解析。</summary>
    public string? JavaPath { get; set; }

    /// <summary>最大堆内存（MB）。</summary>
    public int MaxMemoryMb { get; set; } = 4096;

    /// <summary>最小堆内存（MB）；为 0 时不显式设置。</summary>
    public int MinimumMemoryMb { get; set; }

    /// <summary>游戏窗口宽度；为 0 表示由游戏自行决定。</summary>
    public int WindowWidth { get; set; } = 1280;

    /// <summary>游戏窗口高度；为 0 表示由游戏自行决定。</summary>
    public int WindowHeight { get; set; } = 720;

    /// <summary>是否以全屏模式启动游戏。</summary>
    public bool FullScreen { get; set; }

    /// <summary>启动前是否先校验并补全游戏文件。</summary>
    public bool CheckFilesBeforeLaunch { get; set; } = true;

    /// <summary>
    /// 版本隔离目录：不为空时把该目录作为游戏目录（用于每个版本独立的 mods / saves / config）。
    /// </summary>
    public string? InstanceDirectory { get; set; }

    /// <summary>追加的 JVM 参数，例如 <c>-Dfile.encoding=UTF-8</c>。</summary>
    public IReadOnlyList<string>? ExtraJvmArguments { get; set; }

    /// <summary>
    /// 指定的游戏会话（微软正版登录时由认证服务提供）。
    /// 为 null 时服务会用 <see cref="PlayerName"/> 创建离线会话。
    /// </summary>
    public CmlLib.Core.Auth.MSession? Session { get; set; }
}
