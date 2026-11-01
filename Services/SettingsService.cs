using System.Diagnostics;
using System.Text.Json;
using CmlLib.Core;
using DPL.Models;

namespace DPL.Services;

/// <summary>
/// 基于 JSON 文件的设置存储实现，配置保存在 <c>%AppData%/DPL/settings.json</c>。
/// 首次启动时优先把游戏目录放在程序目录下的 <c>.minecraft</c>（便携模式），
/// 创建失败则回退到 CmlLib 提供的系统默认目录。
/// </summary>
public sealed class SettingsService : ISettingsService
{
    /// <summary>便携目录创建失败时记录到日志的提示。</summary>
    public const string PortableFallbackMessage = "无法在程序目录创建游戏文件夹，已回退到系统默认目录";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _settingsFilePath;

    /// <inheritdoc />
    public string? LastWarning { get; private set; }

    /// <summary>
    /// 创建设置服务，并确定配置文件路径。
    /// </summary>
    public SettingsService()
    {
        AppPaths.EnsureDirectory(AppPaths.RoamingDirectory);
        _settingsFilePath = AppPaths.SettingsFile;
    }

    /// <inheritdoc />
    public LauncherSettings Load()
    {
        LastWarning = null;

        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                var settings = JsonSerializer.Deserialize<LauncherSettings>(json, SerializerOptions);
                if (settings is not null)
                    return Normalize(settings);
            }
        }
        catch (Exception)
        {
            // 配置文件损坏时不应阻止启动器运行，直接回退到默认设置。
        }

        return CreateDefault();
    }

    /// <inheritdoc />
    public void Save(LauncherSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(Normalize(settings), SerializerOptions);
            File.WriteAllText(_settingsFilePath, json);
        }
        catch (Exception)
        {
            // 保存失败不影响运行，用户下次修改时会再次尝试写入。
        }
    }

    /// <summary>
    /// 修正非法或缺失的字段，保证后续逻辑拿到的设置可用。
    /// 只在路径为空时补默认值，绝不覆盖用户已经保存的路径。
    /// </summary>
    private LauncherSettings Normalize(LauncherSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.GameDirectory))
            settings.GameDirectory = ResolvePortableGameDirectory();

        if (string.IsNullOrWhiteSpace(settings.PlayerName))
            settings.PlayerName = "Steve";

        // 离线会话名只允许字母数字下划线，长度 3~16，避免与游戏校验冲突。
        settings.PlayerName = new string(settings.PlayerName
            .Where(c => char.IsLetterOrDigit(c) || c == '_')
            .ToArray());
        if (settings.PlayerName.Length is 0)
            settings.PlayerName = "Steve";
        if (settings.PlayerName.Length > 16)
            settings.PlayerName = settings.PlayerName[..16];

        if (settings.MaxMemoryMb < 512)
            settings.MaxMemoryMb = 512;

        settings.DownloadThreadCount = Math.Clamp(settings.DownloadThreadCount, 1, 32);

        settings.JavaPath ??= string.Empty;
        settings.JavaRuntimeDirectory = string.IsNullOrWhiteSpace(settings.JavaRuntimeDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DPL",
                "runtime")
            : settings.JavaRuntimeDirectory.Trim().Trim('"');
        settings.VersionsDirectory = settings.VersionsDirectory?.Trim() ?? string.Empty;
        settings.FavoriteVersions ??= new List<string>();

        return settings;
    }

    /// <summary>
    /// 生成一份默认设置：游戏目录放在程序目录下的 <c>.minecraft</c>，
    /// 版本目录留空以跟随游戏目录（即 <c>&lt;游戏目录&gt;/versions</c>）。
    /// </summary>
    private LauncherSettings CreateDefault() => new()
    {
        GameDirectory = ResolvePortableGameDirectory(),
        VersionsDirectory = string.Empty,
        PlayerName = "Steve",
        JavaPath = string.Empty,
        JavaRuntimeDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DPL",
            "runtime"),
        AutoDownloadJava = true,
        MaxMemoryMb = 4096,
        DownloadThreadCount = 8,
        CheckFilesBeforeLaunch = true,
        UseVersionIsolation = true,
    };

    /// <summary>
    /// 解析首次启动的默认游戏目录：优先程序目录下的 <c>.minecraft</c>；
    /// 如果目录无法创建（Program Files、只读盘等），回退到 CmlLib 的系统默认目录。
    /// </summary>
    private string ResolvePortableGameDirectory()
    {
        try
        {
            var baseDirectory = AppContext.BaseDirectory;
            if (string.IsNullOrWhiteSpace(baseDirectory))
                throw new InvalidOperationException("无法确定程序所在目录");

            var portableDirectory = Path.Combine(baseDirectory, ".minecraft");
            Directory.CreateDirectory(portableDirectory);
            return portableDirectory;
        }
        catch (Exception ex)
        {
            LastWarning = $"{PortableFallbackMessage}（{ex.Message}）";
            Debug.WriteLine($"[DPL] {LastWarning}");
            return MinecraftPath.GetOSDefaultPath();
        }
    }
}
