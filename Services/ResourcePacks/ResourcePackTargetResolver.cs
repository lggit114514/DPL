using System.Text.Json;
using System.Text.RegularExpressions;
using DPL.Services;

namespace DPL.Services.ResourcePacks;

/// <summary>
/// 资源包下载的目标实例：游戏版本 + resourcepacks 目录。
/// </summary>
/// <param name="VersionName">实例（版本）名称。</param>
/// <param name="GameVersion">游戏版本，例如 1.21.1。</param>
/// <param name="ResourcePacksDirectory">该实例的 resourcepacks 目录。</param>
public sealed record ResourcePackTarget(string VersionName, string GameVersion, string ResourcePacksDirectory)
{
    /// <summary>界面展示文本。</summary>
    public string DisplayText => $"{VersionName} · {GameVersion}";
}

/// <summary>
/// 从本地已安装的版本推导出资源包下载所需的目标信息（与模组链路独立）。
/// </summary>
public static class ResourcePackTargetResolver
{
    /// <summary>匹配 1.21.1 / 26.3 这类版本号。</summary>
    private static readonly Regex GameVersionPattern = new(@"\d+\.\d+(?:\.\d+)?", RegexOptions.Compiled);

    /// <summary>
    /// 解析目标实例。
    /// </summary>
    /// <param name="minecraftService">Minecraft 服务（用于取版本目录）。</param>
    /// <param name="versionName">版本名称；为空表示没有选中版本。</param>
    /// <param name="gameDirectory">游戏目录（非隔离实例的 resourcepacks 目录）。</param>
    public static ResourcePackTarget? Resolve(
        IMinecraftService minecraftService,
        string? versionName,
        string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(versionName))
            return null;

        var versionDirectory = minecraftService.GetVersionDirectory(versionName);
        var gameVersion = string.Empty;

        // 1) 读版本 JSON 的 inheritsFrom（modded 版本的真实游戏版本）。
        var jsonPath = Path.Combine(versionDirectory, versionName + ".json");
        if (File.Exists(jsonPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
                if (document.RootElement.TryGetProperty("inheritsFrom", out var inherits) &&
                    inherits.ValueKind == JsonValueKind.String)
                {
                    gameVersion = inherits.GetString() ?? string.Empty;
                }
            }
            catch (Exception)
            {
                // JSON 解析失败时退回解析版本名。
            }
        }

        // 2) 版本名兜底：fabric-loader-0.19.5-1.21.10 / 1.21.10-forge-60.1.18 / 26.3
        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            var matches = GameVersionPattern.Matches(versionName)
                .Select(m => m.Value)
                .Where(v => !v.StartsWith("0.", StringComparison.Ordinal))
                .ToList();

            gameVersion = matches.LastOrDefault() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(gameVersion))
            return null;

        // 3) resourcepacks 目录：隔离实例放版本目录，原版放游戏目录（与实例目录一致）。
        var isolated = minecraftService.LooksLikeIsolatedInstance(versionName);
        var directory = isolated
            ? Path.Combine(versionDirectory, "resourcepacks")
            : Path.Combine(gameDirectory, "resourcepacks");

        return new ResourcePackTarget(versionName, gameVersion, directory);
    }
}
