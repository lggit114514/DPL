using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace DPL.Services;

/// <summary>
/// 一个可选主题。
/// </summary>
/// <param name="Key">主题标识（持久化用）。</param>
/// <param name="DisplayName">界面上显示的名称。</param>
public sealed record ThemeOption(string Key, string DisplayName);

/// <summary>
/// 主题管理：集中定义四套配色，并在运行时整体替换应用资源，实现即时切换。
/// 深色主题沿用 <c>App.axaml</c> 里的默认资源，这里只负责另外三套的覆盖。
/// </summary>
public static class ThemeManager
{
    /// <summary>深色主题标识（默认）。</summary>
    public const string DarkKey = "Dark";

    /// <summary>白色主题标识。</summary>
    public const string WhiteKey = "White";

    /// <summary>浅蓝毛玻璃主题标识。</summary>
    public const string BlueGlassKey = "BlueGlass";

    /// <summary>浅粉毛玻璃主题标识。</summary>
    public const string PinkGlassKey = "PinkGlass";

    /// <summary>可选主题列表（顺序即下拉框顺序）。</summary>
    public static IReadOnlyList<ThemeOption> Options { get; } =
    [
        new(DarkKey, "深色主题（默认）"),
        new(WhiteKey, "白色主题"),
        new(BlueGlassKey, "浅蓝色主题"),
        new(PinkGlassKey, "浅粉色主题"),
    ];

    /// <summary>当前主题标识。</summary>
    public static string CurrentKey { get; private set; } = DarkKey;

    /// <summary>当前是否是毛玻璃主题（用于显示模糊光晕层）。</summary>
    public static bool IsGlass => CurrentKey is BlueGlassKey or PinkGlassKey;

    private static readonly Dictionary<string, ThemePalette> Palettes = new(StringComparer.OrdinalIgnoreCase)
    {
        [WhiteKey] = new ThemePalette(
            Light: true,
            AppBackground: "#F5F7FA",
            SidebarBackground: "#FFFFFF",
            CardBackground: "#FFFFFF",
            CardHover: "#F1F5F9",
            CardSelected: "#E8F5EC",
            InputBackground: "#F8FAFC",
            NavActive: "#E8F5EC",
            Border: "#E2E8F0",
            BorderHover: "#CBD5E1",
            GlassHighlight: "#00000000",
            TextPrimary: "#0F172A",
            TextSecondary: "#475569",
            TextMuted: "#7B8AA0",
            Accent: "#16A34A",
            AccentHover: "#15803D",
            AccentPressed: "#166534",
            AccentSoft: "#DCFCE7",
            AccentForeground: "#FFFFFF",
            Danger: "#DC2626",
            Warning: "#B45309",
            Info: "#2563EB",
            CodeBackground: "#F1F5F9",
            UseWallpaper: false,
            Gradient: null,
            Bloom: "#00000000",
            BloomBlur: 0,
            VersionReleaseBg: "#DCFCE7", VersionReleaseText: "#15803D", VersionReleaseBorder: "#86EFAC",
            VersionSnapshotBg: "#DBEAFE", VersionSnapshotText: "#1D4ED8", VersionSnapshotBorder: "#93C5FD",
            VersionOldBg: "#F1F5F9", VersionOldText: "#475569", VersionOldBorder: "#CBD5E1",
            VersionUnknownBg: "#FEF3C7", VersionUnknownText: "#B45309", VersionUnknownBorder: "#FCD34D"),
        [BlueGlassKey] = new ThemePalette(
            Light: true,
            AppBackground: "#EAF6FC",
            SidebarBackground: "#A6D8ECFA",
            CardBackground: "#BFE1F2FC",
            CardHover: "#D9EAF7FF",
            CardSelected: "#B7DFF3FF",
            InputBackground: "#CCFFFFFF",
            NavActive: "#99A5E3F7",
            Border: "#CCFFFFFF",
            BorderHover: "#E6FFFFFF",
            GlassHighlight: "#99FFFFFF",
            TextPrimary: "#0E2A3D",
            TextSecondary: "#275270",
            TextMuted: "#5A7E96",
            Accent: "#0E7490",
            AccentHover: "#0891B2",
            AccentPressed: "#155E75",
            AccentSoft: "#CDEFF7",
            AccentForeground: "#FFFFFF",
            Danger: "#B91C1C",
            Warning: "#B45309",
            Info: "#1D4ED8",
            CodeBackground: "#E6F4FBFF",
            UseWallpaper: true,
            Gradient: null,
            Bloom: "#33BFE9FF",
            BloomBlur: 36,
            VersionReleaseBg: "#CDEFDF", VersionReleaseText: "#14532D", VersionReleaseBorder: "#8FD3A9",
            VersionSnapshotBg: "#CFE7FB", VersionSnapshotText: "#14456B", VersionSnapshotBorder: "#9CC9EE",
            VersionOldBg: "#E0EBF3", VersionOldText: "#40596B", VersionOldBorder: "#B7CEDD",
            VersionUnknownBg: "#FBEBCF", VersionUnknownText: "#8A5A12", VersionUnknownBorder: "#E9C98A"),
        [PinkGlassKey] = new ThemePalette(
            Light: true,
            AppBackground: "#FCE7F3",
            SidebarBackground: "#A6FBE3EE",
            CardBackground: "#BFFCE7F1",
            CardHover: "#D9F7DCE8",
            CardSelected: "#B7F9D7E6",
            InputBackground: "#CCFFFFFF",
            NavActive: "#99F7C9DC",
            Border: "#CCFFFFFF",
            BorderHover: "#E6FFFFFF",
            GlassHighlight: "#99FFFFFF",
            TextPrimary: "#4A1F33",
            TextSecondary: "#7A3A54",
            TextMuted: "#9C6B7F",
            Accent: "#DB2777",
            AccentHover: "#EC4899",
            AccentPressed: "#BE185D",
            AccentSoft: "#FBD5E6",
            AccentForeground: "#FFFFFF",
            Danger: "#BE123C",
            Warning: "#B45309",
            Info: "#7C3AED",
            CodeBackground: "#E6FCEFF5",
            UseWallpaper: false,
            Gradient: ("#FFF7FB", "#FBE3EF"),
            Bloom: "#33FFC9DE",
            BloomBlur: 36,
            VersionReleaseBg: "#D6F2E3", VersionReleaseText: "#1B5E42", VersionReleaseBorder: "#9FD9BC",
            VersionSnapshotBg: "#E8DDF9", VersionSnapshotText: "#4A2A73", VersionSnapshotBorder: "#C5B0E8",
            VersionOldBg: "#F3E1EA", VersionOldText: "#6B4457", VersionOldBorder: "#DDB9C9",
            VersionUnknownBg: "#FBEBCF", VersionUnknownText: "#8A5A12", VersionUnknownBorder: "#E9C98A"),
    };

    /// <summary>
    /// 由主题覆盖的资源键（深色默认值会在首次切换前缓存，切回深色时还原）。
    /// </summary>
    private static readonly string[] ThemeKeys =
    [
        "SystemAccentColor", "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3",
        "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
        "AppBackgroundBrush", "SidebarBackgroundBrush", "CardBackgroundBrush", "CardHoverBrush",
        "CardSelectedBrush", "InputBackgroundBrush", "NavActiveBrush",
        "BorderBrush", "BorderHoverBrush", "GlassHighlightBrush",
        "TextPrimaryBrush", "TextSecondaryBrush", "TextMutedBrush",
        "AccentBrush", "AccentHoverBrush", "AccentPressedBrush", "AccentSoftBrush", "AccentForegroundBrush",
        "DangerBrush", "WarningBrush", "InfoBrush", "CodeBackgroundBrush",
        "GlassBloomBrush", "GlassBloomEffect",

        "VersionReleaseBackgroundBrush", "VersionReleaseTextBrush", "VersionReleaseBorderBrush",
        "VersionSnapshotBackgroundBrush", "VersionSnapshotTextBrush", "VersionSnapshotBorderBrush",
        "VersionOldBackgroundBrush", "VersionOldTextBrush", "VersionOldBorderBrush",
        "VersionUnknownBackgroundBrush", "VersionUnknownTextBrush", "VersionUnknownBorderBrush",
    ];

    /// <summary>深色主题的原始资源值（首次切换前缓存，用于切回深色时还原）。</summary>
    private static Dictionary<string, object?>? _darkDefaults;

    /// <summary>
    /// 应用主题：直接改写 Application.Resources 里的画刷值，DynamicResource 会即时刷新。
    /// </summary>
    /// <param name="key">主题标识；无法识别时回退到深色。</param>
    public static void Apply(string? key)
    {
        var resolved = ResolveKey(key);
        CurrentKey = resolved;

        if (Application.Current is not { } app)
            return;

        var resources = app.Resources;

        // 首次调用时先记录深色默认值（此时还没有被覆盖过）。
        _darkDefaults ??= CaptureDefaults(resources);

        if (resolved == DarkKey)
        {
            foreach (var pair in _darkDefaults)
            {
                if (pair.Value is not null)
                    resources[pair.Key] = pair.Value;
            }

            app.RequestedThemeVariant = ThemeVariant.Dark;
            return;
        }

        var palette = Palettes[resolved];
        foreach (var pair in BuildValues(palette))
            resources[pair.Key] = pair.Value;

        app.RequestedThemeVariant = palette.Light ? ThemeVariant.Light : ThemeVariant.Dark;
    }

    /// <summary>
    /// 记录深色主题的原始资源值。
    /// </summary>
    private static Dictionary<string, object?> CaptureDefaults(IResourceDictionary resources)
    {
        var defaults = new Dictionary<string, object?>(ThemeKeys.Length);
        foreach (var key in ThemeKeys)
            defaults[key] = resources.TryGetValue(key, out var value) ? value : null;

        return defaults;
    }

    /// <summary>
    /// 把主题标识规范化为已知值。
    /// </summary>
    public static string ResolveKey(string? key)
        => Options.Any(option => string.Equals(option.Key, key, StringComparison.OrdinalIgnoreCase))
            ? Options.First(option => string.Equals(option.Key, key, StringComparison.OrdinalIgnoreCase)).Key
            : DarkKey;

    /// <summary>
    /// 按主题索引应用（供设置页下拉框使用）。
    /// </summary>
    public static string KeyAt(int index)
        => index >= 0 && index < Options.Count ? Options[index].Key : DarkKey;

    /// <summary>主题在列表中的下标（找不到时为 0）。</summary>
    public static int IndexOf(string? key)
    {
        var resolved = ResolveKey(key);
        for (var i = 0; i < Options.Count; i++)
        {
            if (string.Equals(Options[i].Key, resolved, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return 0;
    }

    /// <summary>
    /// 构造主题需要覆盖的资源值（毛玻璃的透明度、光晕、模糊半径都集中在这里）。
    /// </summary>
    private static Dictionary<string, object> BuildValues(ThemePalette p)
    {
        var accent = Color.Parse(p.Accent);

        return new Dictionary<string, object>
        {
            // Fluent 控件的强调色（复选框、滑块、进度条等）
            ["SystemAccentColor"] = accent,
            ["SystemAccentColorDark1"] = Blend(accent, Colors.Black, 0.15),
            ["SystemAccentColorDark2"] = Blend(accent, Colors.Black, 0.25),
            ["SystemAccentColorDark3"] = Blend(accent, Colors.Black, 0.35),
            ["SystemAccentColorLight1"] = Blend(accent, Colors.White, 0.15),
            ["SystemAccentColorLight2"] = Blend(accent, Colors.White, 0.25),
            ["SystemAccentColorLight3"] = Blend(accent, Colors.White, 0.4),

            // 背景
            ["AppBackgroundBrush"] = p.Gradient is { } gradient
                ? CreateGradientBrush(gradient.Start, gradient.End)
                : Brush(p.AppBackground),
            ["SidebarBackgroundBrush"] = Brush(p.SidebarBackground),
            ["CardBackgroundBrush"] = Brush(p.CardBackground),
            ["CardHoverBrush"] = Brush(p.CardHover),
            ["CardSelectedBrush"] = Brush(p.CardSelected),
            ["InputBackgroundBrush"] = Brush(p.InputBackground),
            ["NavActiveBrush"] = Brush(p.NavActive),

            // 边框 / 高光
            ["BorderBrush"] = Brush(p.Border),
            ["BorderHoverBrush"] = Brush(p.BorderHover),
            ["GlassHighlightBrush"] = Brush(p.GlassHighlight),

            // 文字（浅色背景上用同色系深色，保证对比度）
            ["TextPrimaryBrush"] = Brush(p.TextPrimary),
            ["TextSecondaryBrush"] = Brush(p.TextSecondary),
            ["TextMutedBrush"] = Brush(p.TextMuted),

            // 强调色与语义色
            ["AccentBrush"] = Brush(p.Accent),
            ["AccentHoverBrush"] = Brush(p.AccentHover),
            ["AccentPressedBrush"] = Brush(p.AccentPressed),
            ["AccentSoftBrush"] = Brush(p.AccentSoft),
            ["AccentForegroundBrush"] = Brush(p.AccentForeground),
            ["DangerBrush"] = Brush(p.Danger),
            ["WarningBrush"] = Brush(p.Warning),
            ["InfoBrush"] = Brush(p.Info),
            ["CodeBackgroundBrush"] = Brush(p.CodeBackground),

            // 毛玻璃的柔和光晕（半透明斑点 + BlurEffect）
            ["GlassBloomBrush"] = CreateBloomBrush(p.Bloom),
            ["GlassBloomEffect"] = new BlurEffect { Radius = p.BloomBlur },

            // 版本类型标签（正式版 / 快照 / 远古版本 / 未知）
            ["VersionReleaseBackgroundBrush"] = Brush(p.VersionReleaseBg),
            ["VersionReleaseTextBrush"] = Brush(p.VersionReleaseText),
            ["VersionReleaseBorderBrush"] = Brush(p.VersionReleaseBorder),
            ["VersionSnapshotBackgroundBrush"] = Brush(p.VersionSnapshotBg),
            ["VersionSnapshotTextBrush"] = Brush(p.VersionSnapshotText),
            ["VersionSnapshotBorderBrush"] = Brush(p.VersionSnapshotBorder),
            ["VersionOldBackgroundBrush"] = Brush(p.VersionOldBg),
            ["VersionOldTextBrush"] = Brush(p.VersionOldText),
            ["VersionOldBorderBrush"] = Brush(p.VersionOldBorder),
            ["VersionUnknownBackgroundBrush"] = Brush(p.VersionUnknownBg),
            ["VersionUnknownTextBrush"] = Brush(p.VersionUnknownText),
            ["VersionUnknownBorderBrush"] = Brush(p.VersionUnknownBorder),
        };
    }

    /// <summary>把颜色向目标色混合，用于生成强调色的深浅变体。</summary>
    private static Color Blend(Color source, Color target, double ratio)
        => Color.FromArgb(
            source.A,
            (byte)Math.Round(source.R + (target.R - source.R) * ratio),
            (byte)Math.Round(source.G + (target.G - source.G) * ratio),
            (byte)Math.Round(source.B + (target.B - source.B) * ratio));

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    private static IBrush CreateGradientBrush(string start, string end)
        => new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(Color.Parse(start), 0),
                new GradientStop(Color.Parse(end), 1),
            },
        };

    /// <summary>
    /// 柔和光晕：中心带色、边缘透明的径向渐变，配合 BlurEffect 形成毛玻璃的光感。
    /// </summary>
    private static IBrush CreateBloomBrush(string color)
    {
        var solid = Color.Parse(color);
        var transparent = Color.FromArgb(0, solid.R, solid.G, solid.B);

        return new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.35, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.35, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.75, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(solid, 0),
                new GradientStop(transparent, 1),
            },
        };
    }

    /// <summary>一套主题的配色与毛玻璃参数。</summary>
    private sealed record ThemePalette(
        bool Light,
        string AppBackground,
        string SidebarBackground,
        string CardBackground,
        string CardHover,
        string CardSelected,
        string InputBackground,
        string NavActive,
        string Border,
        string BorderHover,
        string GlassHighlight,
        string TextPrimary,
        string TextSecondary,
        string TextMuted,
        string Accent,
        string AccentHover,
        string AccentPressed,
        string AccentSoft,
        string AccentForeground,
        string Danger,
        string Warning,
        string Info,
        string CodeBackground,
        bool UseWallpaper,
        (string Start, string End)? Gradient,
        string Bloom,
        double BloomBlur,
        string VersionReleaseBg,
        string VersionReleaseText,
        string VersionReleaseBorder,
        string VersionSnapshotBg,
        string VersionSnapshotText,
        string VersionSnapshotBorder,
        string VersionOldBg,
        string VersionOldText,
        string VersionOldBorder,
        string VersionUnknownBg,
        string VersionUnknownText,
        string VersionUnknownBorder);
}
