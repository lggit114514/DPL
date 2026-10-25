using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DPL.Services;

namespace DPL.Services.Mods;

/// <summary>
/// 模组下载的“目标实例”上下文：游戏版本、加载器、mods 目录。
/// </summary>
/// <param name="VersionName">实例（版本）名称。</param>
/// <param name="GameVersion">游戏版本，例如 1.21.10。</param>
/// <param name="Loader">加载器（fabric / quilt / forge / neoforge）；原版为 null。</param>
/// <param name="ModsDirectory">该实例的 mods 目录。</param>
public sealed record ModTarget(string VersionName, string GameVersion, string? Loader, string ModsDirectory)
{
    /// <summary>界面展示文本。</summary>
    public string DisplayText => Loader is null
        ? $"{VersionName} · {GameVersion} · 原版（未安装加载器）"
        : $"{VersionName} · {GameVersion} · {Loader}";

    /// <summary>加载器显示名。</summary>
    public string LoaderText => Loader switch
    {
        "fabric" => "Fabric",
        "quilt" => "Quilt",
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        _ => "原版",
    };
}

/// <summary>
/// 从本地已安装的版本推导出模组下载所需的目标信息。
/// 优先读取版本 JSON（modded 版本通常带 inheritsFrom 与加载器库），读不到时退回解析版本名。
/// </summary>
public static class ModTargetResolver
{
    /// <summary>匹配 1.21.10 / 26.3 这类版本号。</summary>
    private static readonly Regex GameVersionPattern = new(@"\d+\.\d+(?:\.\d+)?", RegexOptions.Compiled);

    /// <summary>
    /// 解析目标实例。
    /// </summary>
    /// <param name="minecraftService">Minecraft 服务（用于取版本目录）。</param>
    /// <param name="versionName">版本名称；为空表示没有选中版本。</param>
    /// <param name="gameDirectory">游戏目录（原版的 mods 目录回退）。</param>
    public static ModTarget? Resolve(IMinecraftService minecraftService, string? versionName, string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(versionName))
            return null;

        var versionDirectory = minecraftService.GetVersionDirectory(versionName);

        var gameVersion = string.Empty;
        string? loader = null;

        // 1) 读版本 JSON：inheritsFrom 给出真实游戏版本，libraries 里能看出加载器。
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

                if (document.RootElement.TryGetProperty("libraries", out var libraries) &&
                    libraries.ValueKind == JsonValueKind.Array)
                {
                    foreach (var library in libraries.EnumerateArray())
                    {
                        if (!library.TryGetProperty("name", out var nameElement) ||
                            nameElement.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        var name = nameElement.GetString() ?? string.Empty;
                        if (name.Contains("fabric-loader", StringComparison.OrdinalIgnoreCase))
                            loader = "fabric";
                        else if (name.Contains("quilt-loader", StringComparison.OrdinalIgnoreCase))
                            loader = "quilt";
                        else if (name.Contains("net.neoforged:neoforge", StringComparison.OrdinalIgnoreCase))
                            loader = "neoforge";
                        else if (name.Contains("net.minecraftforge:forge", StringComparison.OrdinalIgnoreCase))
                            loader = "forge";

                        if (loader is not null)
                            break;
                    }
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

        if (loader is null)
        {
            var lower = versionName.ToLowerInvariant();
            if (lower.Contains("neoforge"))
                loader = "neoforge";
            else if (lower.Contains("quilt"))
                loader = "quilt";
            else if (lower.Contains("fabric"))
                loader = "fabric";
            else if (lower.Contains("forge"))
                loader = "forge";
        }

        if (string.IsNullOrWhiteSpace(gameVersion))
            return null;

        // 3) mods 目录：有加载器的版本放到版本文件夹（配合版本隔离），原版放到游戏目录。
        var modsDirectory = loader is not null || minecraftService.LooksLikeIsolatedInstance(versionName)
            ? Path.Combine(versionDirectory, "mods")
            : Path.Combine(gameDirectory, "mods");

        return new ModTarget(versionName, gameVersion, loader, modsDirectory);
    }

    /// <summary>
    /// 从下载的 CurseForge gameVersions 里挑出真正的游戏版本号（排除加载器名）。
    /// </summary>
    /// <param name="values">gameVersions 列表。</param>
    public static IReadOnlyList<string> FilterGameVersions(IEnumerable<string> values)
        => values
            .Where(v => GameVersionPattern.IsMatch(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
