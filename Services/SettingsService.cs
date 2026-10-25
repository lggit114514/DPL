using System.Text.Json;
using CmlLib.Core;
using DPL.Models;

namespace DPL.Services;

/// <summary>
/// 基于 JSON 文件的设置存储实现，配置保存在 <c>%AppData%/DPL/settings.json</c>。
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _settingsFilePath;

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
    /// </summary>
    private static LauncherSettings Normalize(LauncherSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.GameDirectory))
            settings.GameDirectory = MinecraftPath.GetOSDefaultPath();

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

        settings.JavaPath ??= string.Empty;
        settings.VersionsDirectory = settings.VersionsDirectory?.Trim() ?? string.Empty;

        return settings;
    }

    /// <summary>
    /// 生成一份默认设置（游戏目录使用 CmlLib 提供的各系统默认路径）。
    /// </summary>
    private static LauncherSettings CreateDefault() => new()
    {
        GameDirectory = MinecraftPath.GetOSDefaultPath(),
        PlayerName = "Steve",
        JavaPath = string.Empty,
        MaxMemoryMb = 4096,
        CheckFilesBeforeLaunch = true,
        UseVersionIsolation = true,
    };
}
