using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Threading;
using DPL.Models;
using DPL.Services;
using DPL.Services.Mods;
using DPL.Services.ResourcePacks;

namespace DPL.ViewModels;

/// <summary>
/// 主窗口的 ViewModel：负责页面切换、版本列表、下载进度与启动流程的协调。
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;
    private readonly IMinecraftService _minecraftService;
    private readonly IMicrosoftAuthService _microsoftAuthService;
    private readonly IFabricApiService _fabricApiService;
    private readonly LauncherSettings _settings;
    private readonly IProgress<InstallProgressInfo> _installProgress;

    private CancellationTokenSource? _activeCts;

    private int _currentPageIndex;
    private string _gameDirectory;
    private string _versionsDirectory;
    private string _playerName;
    private string _javaPath;
    private int _maxMemoryMb;
    private bool _checkFilesBeforeLaunch;
    private bool _useVersionIsolation;
    private bool _statusIsError;
    private JavaCandidate? _selectedJava;
    private VersionItemViewModel? _selectedLocalVersion;
    private int _selectedTypeFilterIndex = 1;
    private string _searchText = string.Empty;
    private string _statusMessage = "准备就绪";
    private string _installStageText = string.Empty;
    private string _installSizeText = string.Empty;
    private string _installFileText = string.Empty;
    private double _installPercent;
    private bool _isInstalling;
    private bool _isBusy;
    private bool _isGameRunning;
    private bool _useMicrosoftAccount;
    private string _microsoftAccountId = string.Empty;
    private AccountProfile? _selectedMicrosoftAccount;
    private AccountProfile? _selectedAccount;
    private bool _isMicrosoftLoggingIn;
    private string _microsoftLoginStatus = "尚未登录微软账号";

    private string _curseForgeApiKey = string.Empty;
    private int _themeIndex;

    private VersionItemViewModel? _installTargetVersion;
    private ModLoaderKind _selectedLoaderKind = ModLoaderKind.Fabric;
    private bool _isLoaderLoading;
    private bool _loaderSupported;
    private string _loaderStatusText = string.Empty;
    private ModLoaderVersionInfo? _selectedLoaderVersion;
    private bool _includeFabricApi = true;
    private bool _isFabricApiLoading;
    private string _fabricApiStatusText = string.Empty;
    private FabricApiRelease? _recommendedFabricApi;

    /// <summary>加载器查询代号：每次切换加载器 +1，用于丢弃过期的查询结果。</summary>
    private int _loaderQueryGeneration;

    /// <summary>文件路径比较：Windows 忽略大小写，macOS/Linux 区分大小写。</summary>
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// 创建主窗口 ViewModel 并加载本地设置。
    /// </summary>
    /// <param name="settingsService">设置读写服务。</param>
    /// <param name="minecraftService">Minecraft 核心服务。</param>
    /// <param name="microsoftAuthService">微软正版登录服务。</param>
    /// <param name="fabricApiService">Fabric API 模组下载服务。</param>
    public MainWindowViewModel(
        ISettingsService settingsService,
        IMinecraftService minecraftService,
        IMicrosoftAuthService microsoftAuthService,
        IFabricApiService fabricApiService)
    {
        _settingsService = settingsService;
        _minecraftService = minecraftService;
        _microsoftAuthService = microsoftAuthService;
        _fabricApiService = fabricApiService;

        _settings = settingsService.Load();
        _minecraftService.UseGameDirectory(_settings.GameDirectory);
        _minecraftService.UseVersionsDirectory(_settings.VersionsDirectory);

        _gameDirectory = _settings.GameDirectory;
        _versionsDirectory = _settings.VersionsDirectory;
        _playerName = _settings.PlayerName;
        _javaPath = _settings.JavaPath;
        _maxMemoryMb = _settings.MaxMemoryMb;
        _checkFilesBeforeLaunch = _settings.CheckFilesBeforeLaunch;
        _useVersionIsolation = _settings.UseVersionIsolation;
        _useMicrosoftAccount = _settings.UseMicrosoftAccount;
        _microsoftAccountId = _settings.MicrosoftAccountId ?? string.Empty;
        _curseForgeApiKey = _settings.CurseForgeApiKey ?? string.Empty;
        _themeIndex = ThemeManager.IndexOf(_settings.Theme);
        ThemeManager.Apply(_settings.Theme);

        foreach (var name in _settings.OfflineNameHistory ?? new List<string>())
            OfflineNameHistory.Add(name);

        _installProgress = new UiProgress<InstallProgressInfo>(OnInstallProgressChanged);

        // NeoForge 官方源在国内经常连不上，下载会自动切换镜像；这里把每次尝试写进日志面板方便排查。
        MirrorHttpHandler.LogWritten += AppendMirrorLog;

        // ---------- 模组加载器安装页：加载器选项与命令 ----------
        LoaderOptions.Add(new LoaderOptionViewModel(
            ModLoaderKind.Vanilla, "原版（不装模组）", string.Empty,
            "只安装 Mojang 官方原版，不安装任何模组加载器，最稳定。",
            new AsyncRelayCommand(_ => SelectLoaderAsync(ModLoaderKind.Vanilla), _ => !IsInstalling)));
        LoaderOptions.Add(new LoaderOptionViewModel(
            ModLoaderKind.Fabric, "Fabric", "推荐",
            "轻量、启动快，绝大多数现代模组都用它；可以自动一起装 Fabric API。",
            new AsyncRelayCommand(_ => SelectLoaderAsync(ModLoaderKind.Fabric), _ => !IsInstalling)));
        LoaderOptions.Add(new LoaderOptionViewModel(
            ModLoaderKind.Quilt, "Quilt", string.Empty,
            "Fabric 的分支，兼容部分 Fabric 模组；生态相对少一些。",
            new AsyncRelayCommand(_ => SelectLoaderAsync(ModLoaderKind.Quilt), _ => !IsInstalling)));
        LoaderOptions.Add(new LoaderOptionViewModel(
            ModLoaderKind.Forge, "Forge", string.Empty,
            "历史最久的加载器，模组生态庞大；安装时会运行 Forge 官方安装器（需要 Java，耗时几分钟）。",
            new AsyncRelayCommand(_ => SelectLoaderAsync(ModLoaderKind.Forge), _ => !IsInstalling)));
        LoaderOptions.Add(new LoaderOptionViewModel(
            ModLoaderKind.NeoForge, "NeoForge", string.Empty,
            "Forge 的新分支（1.20.1 之后的模组常见），安装同样会运行官方安装器。",
            new AsyncRelayCommand(_ => SelectLoaderAsync(ModLoaderKind.NeoForge), _ => !IsInstalling)));

        StartInstallCommand = new AsyncRelayCommand(_ => StartInstallAsync())
        {
            ErrorHandler = ex =>
            {
                StatusIsError = true;
                StatusMessage = $"安装失败：{ex.Message}";
            },
        };
        BackToDownloadListCommand = new RelayCommand(_ => CurrentPageIndex = 1);
        RefreshLoaderOptionsCommand = CreateCommand(async _ => await SelectLoaderAsync(SelectedLoaderKind));

        ShowLocalPageCommand = new RelayCommand(_ => CurrentPageIndex = 0);
        ShowDownloadPageCommand = new RelayCommand(_ => CurrentPageIndex = 1);
        ShowSettingsPageCommand = new RelayCommand(_ => CurrentPageIndex = 2);
        ShowResourcePackPageCommand = new AsyncRelayCommand(_ => ShowResourcePackPageAsync())
        {
            ErrorHandler = ex =>
            {
                StatusIsError = true;
                StatusMessage = $"资源包页加载失败：{ex.Message}";
            },
        };
        ShowModsPageCommand = new AsyncRelayCommand(_ => ShowModsPageAsync())
        {
            ErrorHandler = ex =>
            {
                StatusIsError = true;
                StatusMessage = $"模组页加载失败：{ex.Message}";
            },
        };

        // 模组下载页：目标实例取自“本地启动”页当前选中的版本。
        ModBrowser = new ModBrowserViewModel(
            () => ModTargetResolver.Resolve(_minecraftService, SelectedLocalVersion?.Name, GameDirectory),
            () => CurseForgeApiKey,
            line => AppendAppLog(line));

        // 资源包下载页：独立 ViewModel / 服务链路，与模组下载完全分开。
        ResourcePackBrowser = new ResourcePackBrowserViewModel(
            () => ResourcePackTargetResolver.Resolve(_minecraftService, SelectedLocalVersion?.Name, GameDirectory),
            line => AppendAppLog(line));

        RefreshLocalVersionsCommand = CreateCommand(_ => RefreshLocalVersionsAsync());
        RefreshOnlineVersionsCommand = CreateCommand(_ => RefreshOnlineVersionsAsync());
        CancelInstallCommand = new RelayCommand(_ => CancelActiveOperation(), _ => IsInstalling);
        SaveSettingsCommand = new RelayCommand(_ => SaveSettings(withFeedback: true));

        BrowseGameDirectoryCommand = CreateCommand(async _ => await BrowseGameDirectoryAsync());
        BrowseJavaCommand = CreateCommand(async _ => await BrowseJavaAsync());
        AutoDetectJavaCommand = CreateCommand(async _ => await RefreshJavaCandidatesAsync());
        OpenGameDirectoryCommand = new RelayCommand(_ => OpenGameDirectory());
        UseDefaultGameDirectoryCommand = CreateCommand(async _ =>
            await ChangeGameDirectoryAsync(MinecraftPathDefault));

        BrowseVersionsDirectoryCommand = CreateCommand(async _ => await BrowseVersionsDirectoryAsync());
        UseDefaultVersionsDirectoryCommand = CreateCommand(async _ =>
            await ChangeVersionsDirectoryAsync(string.Empty));
        OpenVersionsDirectoryCommand = new RelayCommand(_ => OpenVersionsDirectory());
        OpenSelectedVersionFolderCommand = new RelayCommand(_ => OpenVersionFolder(SelectedLocalVersion?.Name),
            _ => SelectedLocalVersion is not null);

        ClearLogCommand = new RelayCommand(_ => GameLogs.Clear());
        ClearAppLogCommand = new RelayCommand(_ => AppLogs.Clear());

        // ---------- 账号相关命令 ----------
        LoginMicrosoftCommand = new AsyncRelayCommand(_ => LoginMicrosoftAsync())
        {
            ErrorHandler = ex =>
            {
                StatusIsError = true;
                MicrosoftLoginStatus = $"登录失败：{ex.Message}";
                StatusMessage = $"微软账号登录失败：{ex.Message}";
            },
        };
        SignOutMicrosoftCommand = CreateCommand(async _ => await SignOutMicrosoftAsync());
        RefreshMicrosoftAccountsCommand = CreateCommand(async _ => await LoadMicrosoftAccountsAsync());
        ClearOfflineHistoryCommand = new RelayCommand(_ => ClearOfflineHistory());
        OpenAccountsFileFolderCommand = new RelayCommand(_ => OpenAccountsFolder());

        MinimizeWindowCommand = new RelayCommand(_ => MinimizeRequested?.Invoke());
        MaximizeWindowCommand = new RelayCommand(_ => MaximizeRequested?.Invoke());
        CloseWindowCommand = new RelayCommand(_ => CloseRequested?.Invoke());

        // 启动命令不能受“读取列表 / 获取在线清单”这类忙碌状态影响，
        // 否则用户在列表加载期间点“启动”就会出现“按钮点了没反应”的现象。
        // 真正的冲突（已在启动 / 正在安装）由 LaunchVersionAsync 内部判断并给出提示。
        LaunchSelectedCommand = new AsyncRelayCommand(_ => LaunchVersionAsync(SelectedLocalVersion))
        {
            ErrorHandler = ex =>
            {
                StatusIsError = true;
                StatusMessage = $"启动失败：{ex.Message}";
            },
        };
    }

    #region 页面状态

    /// <summary>当前页面索引：0 = 本地启动，1 = 下载游戏，2 = 设置。</summary>
    public int CurrentPageIndex
    {
        get => _currentPageIndex;
        set
        {
            if (SetProperty(ref _currentPageIndex, value))
            {
                OnPropertyChanged(nameof(IsLocalPage));
                OnPropertyChanged(nameof(IsDownloadPage));
                OnPropertyChanged(nameof(IsSettingsPage));
                OnPropertyChanged(nameof(IsInstallPage));
                OnPropertyChanged(nameof(IsModsPage));
                OnPropertyChanged(nameof(IsResourcePackPage));

                // 模组页 / 资源包页的视图用 IsActive 控制显示，避免 DataContext 绑定与可见性互相影响。
                if (ModBrowser is not null)
                    ModBrowser.IsActive = value == 4;

                if (ResourcePackBrowser is not null)
                    ResourcePackBrowser.IsActive = value == 5;
            }
        }
    }

    /// <summary>是否处于“本地启动”页面。</summary>
    public bool IsLocalPage => CurrentPageIndex == 0;

    /// <summary>是否处于“下载游戏”页面。</summary>
    public bool IsDownloadPage => CurrentPageIndex == 1;

    /// <summary>是否处于“设置”页面。</summary>
    public bool IsSettingsPage => CurrentPageIndex == 2;

    /// <summary>是否处于“选择模组加载器并安装”页面。</summary>
    public bool IsInstallPage => CurrentPageIndex == 3;

    /// <summary>是否处于“模组下载”页面。</summary>
    public bool IsModsPage => CurrentPageIndex == 4;

    /// <summary>是否处于“资源包下载”页面。</summary>
    public bool IsResourcePackPage => CurrentPageIndex == 5;

    #endregion

    #region 模组加载器安装

    /// <summary>
    /// 可选的模组加载器卡片（原版 / Fabric / Quilt）。
    /// </summary>
    public ObservableCollection<LoaderOptionViewModel> LoaderOptions { get; } = new();

    /// <summary>
    /// 当前加载器可选的版本列表。
    /// </summary>
    public ObservableCollection<ModLoaderVersionInfo> LoaderVersions { get; } = new();

    /// <summary>当前正在安装的 Minecraft 版本条目。</summary>
    public VersionItemViewModel? InstallTargetVersion
    {
        get => _installTargetVersion;
        private set
        {
            if (!SetProperty(ref _installTargetVersion, value))
                return;

            OnPropertyChanged(nameof(InstallTargetVersionName));
            OnPropertyChanged(nameof(InstallPageTitle));
            OnPropertyChanged(nameof(InstallPageSubtitle));
            OnPropertyChanged(nameof(InstallSummaryText));
        }
    }

    /// <summary>目标 Minecraft 版本号。</summary>
    public string InstallTargetVersionName => _installTargetVersion?.Name ?? "—";

    /// <summary>安装页标题。</summary>
    public string InstallPageTitle => $"安装 {InstallTargetVersionName}";

    /// <summary>安装页副标题。</summary>
    public string InstallPageSubtitle =>
        "选择要和原版一起安装的模组加载器，确认后会下载并出现在本地版本列表里";

    /// <summary>当前选中的加载器类型。</summary>
    public ModLoaderKind SelectedLoaderKind
    {
        get => _selectedLoaderKind;
        private set
        {
            if (!SetProperty(ref _selectedLoaderKind, value))
                return;

            OnPropertyChanged(nameof(IsVanillaLoaderSelected));
            OnPropertyChanged(nameof(IsLoaderVersionVisible));
            OnPropertyChanged(nameof(IsFabricApiCardVisible));
            OnPropertyChanged(nameof(IsForgeLikeLoaderSelected));
            OnPropertyChanged(nameof(SelectedLoaderName));
            OnPropertyChanged(nameof(InstallSummaryText));
            OnPropertyChanged(nameof(CanStartInstall));

            foreach (var option in LoaderOptions)
                option.IsSelected = option.Kind == value;
        }
    }

    /// <summary>是否选择了原版（不安装加载器）。</summary>
    public bool IsVanillaLoaderSelected => SelectedLoaderKind == ModLoaderKind.Vanilla;

    /// <summary>是否需要选择加载器版本（原版模式下不需要）。</summary>
    public bool IsLoaderVersionVisible => SelectedLoaderKind != ModLoaderKind.Vanilla;

    /// <summary>是否显示 Fabric API 卡片（只在 Fabric 模式下显示）。</summary>
    public bool IsFabricApiCardVisible => SelectedLoaderKind == ModLoaderKind.Fabric;

    /// <summary>是否选择了需要运行官方安装器的加载器（Forge / NeoForge）。</summary>
    public bool IsForgeLikeLoaderSelected =>
        SelectedLoaderKind is ModLoaderKind.Forge or ModLoaderKind.NeoForge;

    /// <summary>当前加载器的显示名称。</summary>
    public string SelectedLoaderName => SelectedLoaderKind switch
    {
        ModLoaderKind.Fabric => "Fabric",
        ModLoaderKind.Quilt => "Quilt",
        ModLoaderKind.Forge => "Forge",
        ModLoaderKind.NeoForge => "NeoForge",
        _ => "原版",
    };

    /// <summary>是否正在加载加载器版本列表。</summary>
    public bool IsLoaderLoading
    {
        get => _isLoaderLoading;
        private set
        {
            if (SetProperty(ref _isLoaderLoading, value))
                OnPropertyChanged(nameof(InstallSummaryText));
        }
    }

    /// <summary>当前选择是否受支持（该 Minecraft 版本有对应的加载器）。</summary>
    public bool LoaderSupported
    {
        get => _loaderSupported;
        private set
        {
            if (!SetProperty(ref _loaderSupported, value))
                return;

            OnPropertyChanged(nameof(InstallSummaryText));
            OnPropertyChanged(nameof(CanStartInstall));
        }
    }

    /// <summary>加载器版本列表的提示信息。</summary>
    public string LoaderStatusText
    {
        get => _loaderStatusText;
        private set => SetProperty(ref _loaderStatusText, value);
    }

    /// <summary>当前选中的加载器版本。</summary>
    public ModLoaderVersionInfo? SelectedLoaderVersion
    {
        get => _selectedLoaderVersion;
        set
        {
            if (!SetProperty(ref _selectedLoaderVersion, value))
                return;

            OnPropertyChanged(nameof(InstallSummaryText));
            OnPropertyChanged(nameof(CanStartInstall));
        }
    }

    /// <summary>是否已获取到可用的加载器版本。</summary>
    public bool HasLoaderVersions => LoaderVersions.Count > 0;

    /// <summary>是否自动一起安装 Fabric API。</summary>
    public bool IncludeFabricApi
    {
        get => _includeFabricApi;
        set
        {
            if (SetProperty(ref _includeFabricApi, value))
                OnPropertyChanged(nameof(InstallSummaryText));
        }
    }

    /// <summary>是否推荐到了可用的 Fabric API 版本（决定卡片能否勾选）。</summary>
    public bool CanIncludeFabricApi => _recommendedFabricApi is not null;

    /// <summary>Fabric API 的推荐版本文字。</summary>
    public string FabricApiVersionText => _recommendedFabricApi?.DisplayText ?? "暂无匹配版本";

    /// <summary>Fabric API 卡片的状态提示。</summary>
    public string FabricApiStatusText
    {
        get => _fabricApiStatusText;
        private set => SetProperty(ref _fabricApiStatusText, value);
    }

    /// <summary>是否正在查询 Fabric API 版本。</summary>
    public bool IsFabricApiLoading
    {
        get => _isFabricApiLoading;
        private set => SetProperty(ref _isFabricApiLoading, value);
    }

    /// <summary>底部“将要安装什么”的汇总文字。</summary>
    public string InstallSummaryText
    {
        get
        {
            if (_installTargetVersion is null)
                return string.Empty;

            if (SelectedLoaderKind == ModLoaderKind.Vanilla)
                return $"将安装：{InstallTargetVersionName}（原版）";

            // 查询还没结束时先不提示“没有可用版本”，避免闪一下误导性的文字。
            if (!IsLoaderLoading && (!LoaderSupported || SelectedLoaderVersion is null))
                return $"该 Minecraft 版本没有可用的 {SelectedLoaderName} 版本";

            var loaderName = SelectedLoaderName;
            var loaderVersion = SelectedLoaderVersion?.Version ?? "最新版";
            var fabricApiPart = SelectedLoaderKind == ModLoaderKind.Fabric && IncludeFabricApi && CanIncludeFabricApi
                ? $" + Fabric API {_recommendedFabricApi?.VersionNumber}"
                : string.Empty;

            return $"将安装：{InstallTargetVersionName} + {loaderName} {loaderVersion}{fabricApiPart}";
        }
    }

    /// <summary>安装按钮文字。</summary>
    public string InstallActionText => IsInstalling ? "正在安装…" : "开始安装";

    /// <summary>是否可以开始安装（原版随时可以；加载器需要已查询到可用版本）。</summary>
    public bool CanStartInstall => !IsInstalling &&
                                   (IsVanillaLoaderSelected || (LoaderSupported && SelectedLoaderVersion is not null));

    #endregion

    #region 版本列表

    /// <summary>本地已安装的版本列表。</summary>
    public ObservableCollection<VersionItemViewModel> LocalVersions { get; } = new();

    /// <summary>从 Mojang 获取的在线版本完整列表。</summary>
    public ObservableCollection<VersionItemViewModel> OnlineVersions { get; } = new();

    /// <summary>经过搜索与筛选后的在线版本列表，供界面直接绑定。</summary>
    public ObservableCollection<VersionItemViewModel> FilteredOnlineVersions { get; } = new();

    /// <summary>当前选中的本地版本。</summary>
    public VersionItemViewModel? SelectedLocalVersion
    {
        get => _selectedLocalVersion;
        set
        {
            if (SetProperty(ref _selectedLocalVersion, value))
            {
                OnPropertyChanged(nameof(SelectedVersionText));
                OnPropertyChanged(nameof(HasSelectedVersion));
            }
        }
    }

    /// <summary>是否已选中本地版本。</summary>
    public bool HasSelectedVersion => SelectedLocalVersion is not null;

    /// <summary>底部栏显示的已选版本名称。</summary>
    public string SelectedVersionText => SelectedLocalVersion?.Name ?? "未选择版本";

    /// <summary>本地列表是否为空。</summary>
    public bool HasLocalVersions => LocalVersions.Count > 0;

    /// <summary>本地列表是否为空（用于显示空状态提示）。</summary>
    public bool IsLocalListEmpty => LocalVersions.Count == 0;

    /// <summary>在线列表是否为空。</summary>
    public bool IsOnlineListEmpty => FilteredOnlineVersions.Count == 0;

    /// <summary>在线版本数量提示文本。</summary>
    public string OnlineCountText => $"共 {FilteredOnlineVersions.Count} 个版本";

    /// <summary>本地版本数量提示文本。</summary>
    public string LocalCountText => $"共 {LocalVersions.Count} 个版本";

    /// <summary>可用的版本类型筛选项。</summary>
    public IReadOnlyList<string> TypeFilters { get; } = ["全部版本", "正式版", "快照", "远古版本"];

    /// <summary>当前选中的版本类型筛选索引。</summary>
    public int SelectedTypeFilterIndex
    {
        get => _selectedTypeFilterIndex;
        set
        {
            if (SetProperty(ref _selectedTypeFilterIndex, value))
                ApplyOnlineFilter();
        }
    }

    /// <summary>在线版本的搜索关键字。</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                ApplyOnlineFilter();
        }
    }

    #endregion

    #region 设置项

    /// <summary>主题下拉项（深色 / 白色 / 浅蓝毛玻璃 / 浅粉毛玻璃）。</summary>
    public ObservableCollection<string> ThemeOptions { get; } =
        new(ThemeManager.Options.Select(option => option.DisplayName));

    /// <summary>当前主题索引；切换后立即生效并写入设置。</summary>
    public int SelectedThemeIndex
    {
        get => _themeIndex;
        set
        {
            if (value < 0 || value >= ThemeOptions.Count)
                return;

            if (!SetProperty(ref _themeIndex, value))
                return;

            ThemeManager.Apply(ThemeManager.KeyAt(value));
            _settings.Theme = ThemeManager.CurrentKey;
            SaveSettings();

            OnPropertyChanged(nameof(IsGlassTheme));
            OnPropertyChanged(nameof(IsBlueGlassTheme));
            StatusMessage = $"已切换主题：{ThemeOptions[value]}";
        }
    }

    /// <summary>当前是否是毛玻璃主题（控制壁纸光晕层是否显示）。</summary>
    public bool IsGlassTheme => ThemeManager.IsGlass;

    /// <summary>当前是否是浅蓝毛玻璃主题（显示壁纸）。</summary>
    public bool IsBlueGlassTheme =>
        string.Equals(ThemeManager.CurrentKey, ThemeManager.BlueGlassKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>CurseForge API Key（保存在设置文件里，不硬编码在源码中）。</summary>
    public string CurseForgeApiKey
    {
        get => _curseForgeApiKey;
        set => SetProperty(ref _curseForgeApiKey, value);
    }

    /// <summary>游戏目录（.minecraft）路径。</summary>
    public string GameDirectory
    {
        get => _gameDirectory;
        set
        {
            if (SetProperty(ref _gameDirectory, value))
                OnPropertyChanged(nameof(VersionsDirectoryText));
        }
    }

    /// <summary>
    /// 自定义版本目录（versions）路径；为空表示使用游戏目录下的 versions 目录。
    /// </summary>
    public string VersionsDirectory
    {
        get => _versionsDirectory;
        set
        {
            if (SetProperty(ref _versionsDirectory, value))
            {
                OnPropertyChanged(nameof(VersionsDirectoryText));
                OnPropertyChanged(nameof(IsCustomVersionsDirectory));
            }
        }
    }

    /// <summary>实际生效的版本目录路径（未自定义时为 &lt;游戏目录&gt;/versions）。</summary>
    public string VersionsDirectoryText => string.IsNullOrWhiteSpace(VersionsDirectory)
        ? Path.Combine(string.IsNullOrWhiteSpace(GameDirectory) ? "." : GameDirectory, "versions")
        : VersionsDirectory;

    /// <summary>是否使用了自定义版本目录。</summary>
    public bool IsCustomVersionsDirectory => !string.IsNullOrWhiteSpace(VersionsDirectory);

    /// <summary>离线会话使用的玩家名称。</summary>
    public string PlayerName
    {
        get => _playerName;
        set
        {
            if (SetProperty(ref _playerName, value))
            {
                OnPropertyChanged(nameof(CurrentAccountText));
            }
        }
    }

    /// <summary>自定义 Java 路径；为空表示自动检测。</summary>
    public string JavaPath
    {
        get => _javaPath;
        set
        {
            if (SetProperty(ref _javaPath, value))
                OnPropertyChanged(nameof(JavaPathText));
        }
    }

    /// <summary>Java 路径的展示文本。</summary>
    public string JavaPathText => string.IsNullOrWhiteSpace(JavaPath) ? "自动检测" : JavaPath;

    /// <summary>最大内存（MB）。</summary>
    public int MaxMemoryMb
    {
        get => _maxMemoryMb;
        set
        {
            if (!SetProperty(ref _maxMemoryMb, value))
                return;

            OnPropertyChanged(nameof(MaxMemoryMbSlider));
            OnPropertyChanged(nameof(MemoryText));
        }
    }

    /// <summary>供滑块绑定的最大内存值（double 类型）。</summary>
    public double MaxMemoryMbSlider
    {
        get => MaxMemoryMb;
        set => MaxMemoryMb = (int)Math.Round(value / 512d) * 512;
    }

    /// <summary>内存数值的展示文本。</summary>
    public string MemoryText => $"{MaxMemoryMb} MB";

    /// <summary>启动前是否校验并补全游戏文件。</summary>
    public bool CheckFilesBeforeLaunch
    {
        get => _checkFilesBeforeLaunch;
        set => SetProperty(ref _checkFilesBeforeLaunch, value);
    }

    /// <summary>
    /// 版本隔离：勾选后，如果版本文件夹内含有 mods / saves / options.txt 等文件
    /// （PCL2、HMCL 的隔离实例），就把该文件夹作为游戏目录，从而加载该版本自己的模组与存档。
    /// </summary>
    public bool UseVersionIsolation
    {
        get => _useVersionIsolation;
        set => SetProperty(ref _useVersionIsolation, value);
    }

    /// <summary>本机检测到的 Java 运行时列表（已校验可用性，不可用的会在名称后标注）。</summary>
    public ObservableCollection<JavaCandidate> JavaCandidates { get; } = new();

    /// <summary>当前选中的 Java 运行时。</summary>
    public JavaCandidate? SelectedJava
    {
        get => _selectedJava;
        set
        {
            if (SetProperty(ref _selectedJava, value) && value is not null)
                JavaPath = value.Path;
        }
    }

    /// <summary>Java 检测结果提示。</summary>
    public string JavaDetectedText =>
        JavaCandidates.Count == 0
            ? "未检测到本机安装的 Java，启动时将由 CmlLib 自动下载官方运行时"
            : $"已检测到 {JavaCandidates.Count} 个 Java，可用 {JavaCandidates.Count(c => c.IsUsable)} 个";

    /// <summary>启动时使用的 Java 路径（为空表示由服务自动解析）。</summary>
    private string? JavaPathForLaunch => string.IsNullOrWhiteSpace(JavaPath) ? null : JavaPath;

    #endregion

    #region 账号

    /// <summary>账号模式选项：索引 0 = 离线登录，1 = 微软正版登录。</summary>
    public IReadOnlyList<string> AccountModes { get; } = ["离线登录", "微软正版登录"];

    /// <summary>当前账号模式索引（0 = 离线，1 = 微软正版）。</summary>
    public int AccountModeIndex
    {
        get => UseMicrosoftAccount ? 1 : 0;
        set
        {
            var useMicrosoft = value == 1;
            if (useMicrosoft == UseMicrosoftAccount)
                return;

            UseMicrosoftAccount = useMicrosoft;
            OnPropertyChanged();
            RefreshAccountSelection();

            // 记住用户的选择，下次启动仍是同一个账号模式。
            SaveSettings();
        }
    }

    /// <summary>是否使用微软正版账号（false 表示离线账号）。</summary>
    public bool UseMicrosoftAccount
    {
        get => _useMicrosoftAccount;
        set
        {
            if (!SetProperty(ref _useMicrosoftAccount, value))
                return;

            OnPropertyChanged(nameof(IsOfflineAccountMode));
            OnPropertyChanged(nameof(IsMicrosoftAccountMode));
            OnPropertyChanged(nameof(AccountModeIndex));
            OnPropertyChanged(nameof(CurrentAccountText));
            OnPropertyChanged(nameof(CurrentAccountDetail));
        }
    }

    /// <summary>是否处于离线账号模式（用于控制离线输入区域的显示）。</summary>
    public bool IsOfflineAccountMode => !UseMicrosoftAccount;

    /// <summary>是否处于微软正版账号模式（用于控制正版区域显示）。</summary>
    public bool IsMicrosoftAccountMode => UseMicrosoftAccount;

    /// <summary>已保存的离线账号名称历史（最近使用在前）。</summary>
    public ObservableCollection<string> OfflineNameHistory { get; } = new();

    /// <summary>下拉框选中的历史名称；选中后自动填入离线名称输入框。</summary>
    public string? SelectedHistoryName
    {
        // 该属性只用于“点一下就填入”，因此始终返回 null（下拉框保持未选中状态，便于重复点击）。
        get => null;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            // 用户从历史记录中选择时，直接把名称填进离线账号输入框。
            PlayerName = value;
            UseMicrosoftAccount = false;

            OnPropertyChanged(nameof(PlayerName));
            OnPropertyChanged(nameof(CurrentAccountText));
            OnPropertyChanged(nameof(CurrentAccountDetail));
            OnPropertyChanged(nameof(AccountModeIndex));

            // 通知下拉框回到未选中状态，这样同一个名称也可以反复点选。
            OnPropertyChanged(nameof(SelectedHistoryName));
            RefreshAccountSelection();
            StatusMessage = $"已选择离线账号名称：{value}";
        }
    }

    /// <summary>已保存的微软正版账号列表（只读取本地账号文件）。</summary>
    public ObservableCollection<AccountProfile> MicrosoftAccounts { get; } = new();

    /// <summary>当前选中的微软正版账号。</summary>
    public AccountProfile? SelectedMicrosoftAccount
    {
        get => _selectedMicrosoftAccount;
        set
        {
            if (!SetProperty(ref _selectedMicrosoftAccount, value))
                return;

            if (value is not null)
            {
                _microsoftAccountId = value.Identifier ?? string.Empty;
                UseMicrosoftAccount = true;
            }

            OnPropertyChanged(nameof(CurrentAccountText));
            OnPropertyChanged(nameof(CurrentAccountDetail));
            OnPropertyChanged(nameof(AccountModeIndex));
            RefreshAccountSelection();

            // 记住选择，下次启动仍然使用同一个正版账号。
            SaveSettings();
        }
    }

    /// <summary>
    /// 快速切换用的账号列表（离线历史名称 + 已登录微软账号），
    /// 直接在下拉框里选择即可切换启动使用的账号。
    /// </summary>
    public ObservableCollection<AccountProfile> Accounts { get; } = new();

    /// <summary>快速切换下拉框选中的账号。</summary>
    public AccountProfile? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            // 由 RefreshAccountSelection 主动设置时不再回写，避免循环触发。
            if (value is null || ReferenceEquals(value, _selectedAccount))
                return;

            SetProperty(ref _selectedAccount, value);

            if (value.Kind == AccountKind.Microsoft)
            {
                UseMicrosoftAccount = true;
                var matched = MicrosoftAccounts.FirstOrDefault(a =>
                    string.Equals(a.Identifier, value.Identifier, StringComparison.OrdinalIgnoreCase));
                if (matched is not null)
                    _selectedMicrosoftAccount = matched;
            }
            else
            {
                UseMicrosoftAccount = false;
                PlayerName = value.Name;
                OnPropertyChanged(nameof(PlayerName));
            }

            OnPropertyChanged(nameof(AccountModeIndex));
            OnPropertyChanged(nameof(SelectedMicrosoftAccount));
            OnPropertyChanged(nameof(CurrentAccountText));
            OnPropertyChanged(nameof(CurrentAccountDetail));
        }
    }

    /// <summary>当前将用于启动的账号名称文本。</summary>
    public string CurrentAccountText => UseMicrosoftAccount
        ? SelectedMicrosoftAccount?.Name ?? "尚未选择正版账号"
        : NormalizePlayerName(PlayerName);

    /// <summary>当前账号的补充说明。</summary>
    public string CurrentAccountDetail => UseMicrosoftAccount
        ? "微软正版账号（游戏内可进正版服务器）"
        : "离线账号（仅单机与离线服务器）";

    /// <summary>是否正在登录微软账号。</summary>
    public bool IsMicrosoftLoggingIn
    {
        get => _isMicrosoftLoggingIn;
        set => SetProperty(ref _isMicrosoftLoggingIn, value);
    }

    /// <summary>微软登录状态提示。</summary>
    public string MicrosoftLoginStatus
    {
        get => _microsoftLoginStatus;
        set => SetProperty(ref _microsoftLoginStatus, value);
    }

    /// <summary>是否有可用的已登录微软账号。</summary>
    public bool HasMicrosoftAccounts => MicrosoftAccounts.Count > 0;

    #endregion

    #region 运行状态

    /// <summary>界面状态栏文本。</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>状态栏当前文本表示的是一次失败（用于把文字显示为红色）。</summary>
    public bool StatusIsError
    {
        get => _statusIsError;
        set => SetProperty(ref _statusIsError, value);
    }

    /// <summary>是否有耗时任务正在执行（读取列表、安装等）。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
                OnPropertyChanged(nameof(IsIdle));
        }
    }

    /// <summary>当前是否空闲。</summary>
    public bool IsIdle => !IsBusy && !IsInstalling;

    /// <summary>是否正在下载 / 安装。</summary>
    public bool IsInstalling
    {
        get => _isInstalling;
        set
        {
            if (!SetProperty(ref _isInstalling, value))
                return;

            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(InstallActionText));
            OnPropertyChanged(nameof(CanStartInstall));
            (CancelInstallCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>下载进度百分比（0 ~ 100）。</summary>
    public double InstallPercent
    {
        get => _installPercent;
        set => SetProperty(ref _installPercent, value);
    }

    /// <summary>下载阶段文字。</summary>
    public string InstallStageText
    {
        get => _installStageText;
        set => SetProperty(ref _installStageText, value);
    }

    /// <summary>下载数量文字，例如“12.3 MB / 240 MB”。</summary>
    public string InstallSizeText
    {
        get => _installSizeText;
        set => SetProperty(ref _installSizeText, value);
    }

    /// <summary>当前正在处理的文件名。</summary>
    public string InstallFileText
    {
        get => _installFileText;
        set => SetProperty(ref _installFileText, value);
    }

    /// <summary>游戏日志（最多保留 300 行）。</summary>
    public ObservableCollection<string> GameLogs { get; } = new();

    /// <summary>
    /// 启动器自身日志（网络/下载/安装/模组搜索等）。
    /// 与 <see cref="GameLogs"/> 分开，避免混进游戏进程输出。
    /// </summary>
    public ObservableCollection<string> AppLogs { get; } = new();

    /// <summary>游戏进程是否正在运行。</summary>
    public bool IsGameRunning
    {
        get => _isGameRunning;
        set => SetProperty(ref _isGameRunning, value);
    }

    /// <summary>需要外部提供的最小化窗口动作。</summary>
    public Action? MinimizeRequested { get; set; }

    /// <summary>需要外部提供的最大化 / 还原窗口动作。</summary>
    public Action? MaximizeRequested { get; set; }

    /// <summary>需要外部提供的关闭窗口动作。</summary>
    public Action? CloseRequested { get; set; }

    /// <summary>文件选择服务，由主窗口在初始化时注入。</summary>
    public IFilePickerService? FilePicker { get; set; }

    #endregion

    #region 命令

    /// <summary>切换到“本地启动”页面。</summary>
    public ICommand ShowLocalPageCommand { get; }

    /// <summary>切换到“下载游戏”页面。</summary>
    public ICommand ShowDownloadPageCommand { get; }

    /// <summary>切换到“设置”页面。</summary>
    public ICommand ShowSettingsPageCommand { get; }

    /// <summary>刷新本地版本列表。</summary>
    public ICommand RefreshLocalVersionsCommand { get; }

    /// <summary>刷新在线版本列表。</summary>
    public ICommand RefreshOnlineVersionsCommand { get; }

    /// <summary>取消当前安装任务。</summary>
    public ICommand CancelInstallCommand { get; }

    /// <summary>保存设置。</summary>
    public ICommand SaveSettingsCommand { get; }

    /// <summary>选择游戏目录。</summary>
    public ICommand BrowseGameDirectoryCommand { get; }

    /// <summary>手动选择 Java 可执行文件。</summary>
    public ICommand BrowseJavaCommand { get; }

    /// <summary>重新扫描本机 Java。</summary>
    public ICommand AutoDetectJavaCommand { get; }

    /// <summary>在文件管理器中打开游戏目录。</summary>
    public ICommand OpenGameDirectoryCommand { get; }

    /// <summary>恢复默认游戏目录。</summary>
    public ICommand UseDefaultGameDirectoryCommand { get; }

    /// <summary>选择自定义版本目录（versions）。</summary>
    public ICommand BrowseVersionsDirectoryCommand { get; }

    /// <summary>恢复为游戏目录下的默认版本目录。</summary>
    public ICommand UseDefaultVersionsDirectoryCommand { get; }

    /// <summary>在文件管理器中打开版本目录。</summary>
    public ICommand OpenVersionsDirectoryCommand { get; }

    /// <summary>在文件管理器中打开当前选中版本的文件夹。</summary>
    public ICommand OpenSelectedVersionFolderCommand { get; }

    /// <summary>清空游戏日志。</summary>
    public ICommand ClearLogCommand { get; }

    /// <summary>清空启动器日志。</summary>
    public ICommand ClearAppLogCommand { get; }

    /// <summary>最小化窗口。</summary>
    public ICommand MinimizeWindowCommand { get; }

    /// <summary>最大化 / 还原窗口。</summary>
    public ICommand MaximizeWindowCommand { get; }

    /// <summary>关闭窗口。</summary>
    public ICommand CloseWindowCommand { get; }

    /// <summary>启动当前选中的本地版本。</summary>
    public ICommand LaunchSelectedCommand { get; }

    /// <summary>登录新的微软正版账号（弹窗完成微软登录）。</summary>
    public ICommand LoginMicrosoftCommand { get; }

    /// <summary>退出当前选中的微软账号。</summary>
    public ICommand SignOutMicrosoftCommand { get; }

    /// <summary>重新读取已保存的微软账号列表。</summary>
    public ICommand RefreshMicrosoftAccountsCommand { get; }

    /// <summary>清空离线账号名称历史。</summary>
    public ICommand ClearOfflineHistoryCommand { get; }

    /// <summary>打开保存微软账号文件的目录。</summary>
    public ICommand OpenAccountsFileFolderCommand { get; }

    /// <summary>按当前选择开始安装（原版 / 加载器 / 可选 Fabric API）。</summary>
    public ICommand StartInstallCommand { get; }

    /// <summary>从安装页返回下载列表。</summary>
    public ICommand BackToDownloadListCommand { get; }

    /// <summary>切换到“模组下载”页面。</summary>
    public ICommand ShowModsPageCommand { get; }

    /// <summary>打开资源包下载页。</summary>
    public ICommand ShowResourcePackPageCommand { get; }

    /// <summary>模组下载页的 ViewModel。</summary>
    public ModBrowserViewModel ModBrowser { get; }

    /// <summary>资源包下载页（独立于模组下载）。</summary>
    public ResourcePackBrowserViewModel ResourcePackBrowser { get; }

    /// <summary>重新查询当前加载器类型的可用版本。</summary>
    public ICommand RefreshLoaderOptionsCommand { get; }

    #endregion

    /// <summary>
    /// 初始化启动器：扫描 Java、加载本地版本、读取已保存的微软账号，并在后台拉取在线版本列表。
    /// </summary>
    public async Task InitializeAsync()
    {
        await RefreshJavaCandidatesAsync().ConfigureAwait(true);
        await LoadMicrosoftAccountsAsync().ConfigureAwait(true);
        await RefreshLocalVersionsAsync().ConfigureAwait(true);
        await RefreshOnlineVersionsAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 把界面上的设置写回磁盘。
    /// </summary>
    public void SaveSettings(bool withFeedback = false)
    {
        _settings.GameDirectory = GameDirectory;
        _settings.VersionsDirectory = VersionsDirectory;
        _settings.PlayerName = NormalizePlayerName(PlayerName);
        _settings.JavaPath = JavaPath;
        _settings.MaxMemoryMb = MaxMemoryMb;
        _settings.CheckFilesBeforeLaunch = CheckFilesBeforeLaunch;
        _settings.UseVersionIsolation = UseVersionIsolation;
        _settings.UseMicrosoftAccount = UseMicrosoftAccount;
        _settings.MicrosoftAccountId = SelectedMicrosoftAccount?.Identifier ?? _microsoftAccountId;
        _settings.OfflineNameHistory = OfflineNameHistory.ToList();
        _settings.CurseForgeApiKey = CurseForgeApiKey;
        _settings.Theme = ThemeManager.CurrentKey;

        _settingsService.Save(_settings);

        if (withFeedback)
            StatusMessage = "设置已保存";
    }

    /// <summary>
    /// 读取已保存的微软账号列表（仅本地文件，不访问网络），并刷新账号下拉框。
    /// </summary>
    private async Task LoadMicrosoftAccountsAsync()
    {
        try
        {
            var accounts = await _microsoftAuthService.GetSavedAccountsAsync().ConfigureAwait(true);

            MicrosoftAccounts.Clear();
            foreach (var account in accounts)
                MicrosoftAccounts.Add(account);

            // 恢复上次选择的账号；找不到时退化为最近使用的账号。
            _selectedMicrosoftAccount = MicrosoftAccounts.FirstOrDefault(a =>
                                         !string.IsNullOrWhiteSpace(_microsoftAccountId) &&
                                         string.Equals(a.Identifier, _microsoftAccountId, StringComparison.OrdinalIgnoreCase))
                                     ?? MicrosoftAccounts.FirstOrDefault();

            if (MicrosoftAccounts.Count == 0)
            {
                MicrosoftLoginStatus = "尚未登录微软账号";
                // 没有可用正版账号时自动切回离线模式，避免启动时报错。
                if (UseMicrosoftAccount)
                    UseMicrosoftAccount = false;
            }
            else
            {
                MicrosoftLoginStatus = $"已保存 {MicrosoftAccounts.Count} 个微软账号，可在启动页直接切换";
            }

            OnPropertyChanged(nameof(SelectedMicrosoftAccount));
            OnPropertyChanged(nameof(HasMicrosoftAccounts));
            OnPropertyChanged(nameof(AccountModeIndex));
            OnPropertyChanged(nameof(CurrentAccountText));
            OnPropertyChanged(nameof(CurrentAccountDetail));
            RefreshAccountSelection();
        }
        catch (Exception ex)
        {
            MicrosoftLoginStatus = $"读取账号列表失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 交互式登录微软账号，成功后保存账号并切换为正版模式。
    /// </summary>
    private async Task LoginMicrosoftAsync()
    {
        if (IsMicrosoftLoggingIn)
            return;

        IsMicrosoftLoggingIn = true;
        StatusIsError = false;
        MicrosoftLoginStatus = "正在打开微软登录窗口…";
        StatusMessage = "正在启动微软登录…";

        try
        {
            // 登录过程可能在后台线程回调状态，必须切回 UI 线程再更新界面属性。
            var account = await _microsoftAuthService
                .LoginInteractivelyAsync(message =>
                    Dispatcher.UIThread.Post(() => MicrosoftLoginStatus = message))
                .ConfigureAwait(true);

            await LoadMicrosoftAccountsAsync().ConfigureAwait(true);

            var matched = MicrosoftAccounts.FirstOrDefault(a =>
                              string.Equals(a.Identifier, account.Identifier, StringComparison.OrdinalIgnoreCase))
                          ?? MicrosoftAccounts.FirstOrDefault(a =>
                              string.Equals(a.Name, account.Name, StringComparison.OrdinalIgnoreCase));

            if (matched is not null)
            {
                _selectedMicrosoftAccount = matched;
                OnPropertyChanged(nameof(SelectedMicrosoftAccount));
            }

            UseMicrosoftAccount = true;
            MicrosoftLoginStatus = $"登录成功：{account.Name}";
            StatusMessage = $"微软账号 {account.Name} 登录成功，启动时将使用正版账号";
            SaveSettings();
        }
        catch (OperationCanceledException)
        {
            StatusIsError = false;
            MicrosoftLoginStatus = "已取消微软登录";
            StatusMessage = "已取消微软登录";
        }
        catch (Exception ex)
        {
            StatusIsError = true;
            var friendly = DescribeMicrosoftLoginError(ex);
            MicrosoftLoginStatus = $"登录失败：{friendly}";
            StatusMessage = $"微软账号登录失败：{friendly}";
        }
        finally
        {
            IsMicrosoftLoggingIn = false;
        }
    }

    /// <summary>
    /// 把微软登录过程中抛出的异常翻译成用户能看懂的提示。
    /// </summary>
    /// <param name="exception">登录异常。</param>
    private static string DescribeMicrosoftLoginError(Exception exception)
    {
        var typeName = exception.GetType().Name;
        var message = exception.Message;

        // 库有时会抛出没有消息的异常（Message 只有默认文本），这里给出更具体的原因。
        if (string.IsNullOrWhiteSpace(message) || message.StartsWith("Exception of type", StringComparison.Ordinal))
        {
            return typeName switch
            {
                "AuthCodeException" => "没有拿到有效的授权码，请重新登录并在浏览器登录后复制完整的回跳网址",
                "MicrosoftOAuthException" => "微软授权失败，请重新登录（如果使用了代理，请检查网络是否可访问 login.live.com）",
                "JEAuthException" => "Minecraft 账号验证失败：该账号可能没有购买 Minecraft: Java 版，或需要先在官方启动器完成年龄验证",
                "XboxAuthException" => "Xbox 验证失败：请确认该微软账号已开通 Xbox 相关服务，或先在官方启动器登录一次",
                _ => $"{typeName}（{message}）",
            };
        }

        return message;
    }

    /// <summary>
    /// 退出当前选中的微软账号（清除保存的令牌）。
    /// </summary>
    private async Task SignOutMicrosoftAsync()
    {
        var target = SelectedMicrosoftAccount ?? MicrosoftAccounts.FirstOrDefault();
        if (target is null)
        {
            MicrosoftLoginStatus = "当前没有可退出的微软账号";
            return;
        }

        try
        {
            await _microsoftAuthService.SignOutAsync(target.Identifier).ConfigureAwait(true);
            MicrosoftLoginStatus = $"已退出账号：{target.Name}";
            StatusMessage = $"已退出微软账号 {target.Name}";
            await LoadMicrosoftAccountsAsync().ConfigureAwait(true);
            SaveSettings();
        }
        catch (Exception ex)
        {
            StatusIsError = true;
            MicrosoftLoginStatus = $"退出登录失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 清空离线账号名称历史。
    /// </summary>
    private void ClearOfflineHistory()
    {
        OfflineNameHistory.Clear();
        RefreshAccountSelection();
        SaveSettings();
        StatusMessage = "已清空离线账号名称历史";
    }

    /// <summary>
    /// 打开保存微软账号信息的目录。
    /// </summary>
    private void OpenAccountsFolder()
    {
        var directory = Path.GetDirectoryName(_microsoftAuthService.AccountsFilePath);
        if (string.IsNullOrWhiteSpace(directory) || !SystemIntegration.OpenDirectory(directory))
            StatusMessage = "无法打开账号目录";
    }

    /// <summary>
    /// 记录一个离线账号名称到历史列表（最近使用的排在最前，去重，最多保留 10 条）。
    /// </summary>
    private void RecordOfflineName(string name)
    {
        name = NormalizePlayerName(name);

        var existing = OfflineNameHistory.FirstOrDefault(n =>
            string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            OfflineNameHistory.Remove(existing);

        OfflineNameHistory.Insert(0, name);
        while (OfflineNameHistory.Count > 10)
            OfflineNameHistory.RemoveAt(OfflineNameHistory.Count - 1);

        RefreshAccountSelection();

        // 立即持久化，保证下次启动时历史记录仍然存在。
        SaveSettings();
    }

    /// <summary>
    /// 重建“快速切换”账号列表，并把当前使用的账号设为选中项。
    /// </summary>
    private void RefreshAccountSelection()
    {
        var current = CurrentAccountText;

        Accounts.Clear();

        // 离线历史名称（排在最前，便于快速切换）
        foreach (var name in OfflineNameHistory)
        {
            Accounts.Add(new AccountProfile
            {
                Kind = AccountKind.Offline,
                Name = name,
            });
        }

        var currentOffline = NormalizePlayerName(PlayerName);
        if (!Accounts.Any(a => a.Kind == AccountKind.Offline &&
                               string.Equals(a.Name, currentOffline, StringComparison.OrdinalIgnoreCase)))
        {
            Accounts.Add(new AccountProfile { Kind = AccountKind.Offline, Name = currentOffline });
        }

        // 已登录的微软账号
        foreach (var account in MicrosoftAccounts)
            Accounts.Add(account);

        var selected = UseMicrosoftAccount
            ? Accounts.FirstOrDefault(a => a.Kind == AccountKind.Microsoft &&
                                           string.Equals(a.Name, current, StringComparison.OrdinalIgnoreCase))
              ?? Accounts.FirstOrDefault(a => a.Kind == AccountKind.Microsoft)
            : Accounts.FirstOrDefault(a => a.Kind == AccountKind.Offline &&
                                           string.Equals(a.Name, current, StringComparison.OrdinalIgnoreCase))
              ?? Accounts.FirstOrDefault(a => a.Kind == AccountKind.Offline);

        _selectedAccount = selected;
        OnPropertyChanged(nameof(SelectedAccount));
        OnPropertyChanged(nameof(CurrentAccountText));
        OnPropertyChanged(nameof(CurrentAccountDetail));
        OnPropertyChanged(nameof(HasMicrosoftAccounts));
    }

    /// <summary>
    /// 读取本地已安装版本并刷新列表。
    /// </summary>
    private async Task RefreshLocalVersionsAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            // 用户可能手动修改了目录，这里统一应用一次（目录未变化时不会有任何开销）。
            if (!string.Equals(_minecraftService.GameDirectory, GameDirectory, PathComparison))
                _minecraftService.UseGameDirectory(GameDirectory);

            _minecraftService.UseVersionsDirectory(VersionsDirectory);

            StatusMessage = "正在读取本地版本…";

            var versions = await _minecraftService.GetLocalVersionsAsync().ConfigureAwait(true);
            var previousName = SelectedLocalVersion?.Name;

            LocalVersions.Clear();
            foreach (var version in versions)
                LocalVersions.Add(CreateVersionItem(version, allowInstall: false));

            SelectedLocalVersion = LocalVersions.FirstOrDefault(v => v.Name == previousName)
                                   ?? LocalVersions.FirstOrDefault();

            OnPropertyChanged(nameof(HasLocalVersions));
            OnPropertyChanged(nameof(IsLocalListEmpty));
            OnPropertyChanged(nameof(LocalCountText));

            StatusMessage = LocalVersions.Count == 0
                ? "未发现本地版本，请前往“下载游戏”页面安装原版游戏"
                : $"已加载 {LocalVersions.Count} 个本地版本";
        }
        catch (Exception ex)
        {
            StatusMessage = $"读取本地版本失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 从 Mojang 获取在线版本列表并刷新筛选结果。
    /// </summary>
    private async Task RefreshOnlineVersionsAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            StatusMessage = "正在从 Mojang 获取版本清单…";

            IReadOnlyList<MinecraftVersionInfo>? versions = null;
            Exception? lastError = null;

            // 清单会被缓存到版本目录下的 json 文件，若同时有其他启动器占用该文件会抛 IOException，
            // 这里做一次短暂重试，避免把“文件被占用”误报成网络问题。
            for (var attempt = 1; attempt <= 3 && versions is null; attempt++)
            {
                try
                {
                    versions = await _minecraftService.GetOnlineVersionsAsync().ConfigureAwait(true);
                }
                catch (IOException ex)
                {
                    lastError = ex;
                    StatusMessage = $"版本清单文件被占用，正在重试（{attempt}/3）…";
                    await Task.Delay(1200).ConfigureAwait(true);
                }
                catch (UnauthorizedAccessException ex)
                {
                    lastError = ex;
                    StatusMessage = $"版本清单文件无法访问，正在重试（{attempt}/3）…";
                    await Task.Delay(1200).ConfigureAwait(true);
                }
            }

            if (versions is null)
                throw lastError ?? new InvalidOperationException("获取版本清单失败");

            OnlineVersions.Clear();
            foreach (var version in versions)
                OnlineVersions.Add(CreateVersionItem(version, allowInstall: true));

            ApplyOnlineFilter();

            // 不要覆盖正在进行的安装 / 启动流程，也不要覆盖错误提示。
            if (!IsInstalling && !IsGameRunning && !StatusIsError)
                StatusMessage = $"已获取 {OnlineVersions.Count} 个在线版本";
        }
        catch (IOException ex)
        {
            StatusMessage = $"获取在线版本失败：版本清单文件被其他程序占用（{ex.Message}）。请关闭其他启动器后点击“刷新清单”。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"获取在线版本失败：{ex.Message}（请检查网络连接或代理设置）";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 按关键字与类型筛选在线版本列表。
    /// </summary>
    private void ApplyOnlineFilter()
    {
        IEnumerable<VersionItemViewModel> query = OnlineVersions;

        query = SelectedTypeFilterIndex switch
        {
            1 => query.Where(v => v.Kind == GameVersionKind.Release),
            2 => query.Where(v => v.Kind == GameVersionKind.Snapshot),
            3 => query.Where(v => v.Kind == GameVersionKind.Old),
            _ => query,
        };

        var keyword = (SearchText ?? string.Empty).Trim();
        if (keyword.Length > 0)
            query = query.Where(v => v.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase));

        FilteredOnlineVersions.Clear();
        foreach (var item in query)
            FilteredOnlineVersions.Add(item);

        OnPropertyChanged(nameof(IsOnlineListEmpty));
        OnPropertyChanged(nameof(OnlineCountText));
    }

    /// <summary>
    /// 创建版本条目，并为其绑定安装 / 启动 / 打开版本文件夹 / 打开安装页面的回调。
    /// </summary>
    private VersionItemViewModel CreateVersionItem(MinecraftVersionInfo info, bool allowInstall)
        => new(
            info,
            allowInstall ? InstallVersionAsync : null,
            LaunchVersionAsync,
            item => OpenVersionFolder(item.Name),
            allowInstall ? OpenInstallPageAsync : null);

    /// <summary>
    /// 打开“选择模组加载器”安装页面，并自动加载默认加载器（Fabric）的可用版本与 Fabric API 推荐版本。
    /// </summary>
    /// <param name="item">要安装的 Minecraft 版本条目。</param>
    private async Task OpenInstallPageAsync(VersionItemViewModel? item)
    {
        if (item is null)
            return;

        if (IsInstalling)
        {
            StatusMessage = "已有安装任务正在进行，请等待完成或先取消";
            return;
        }

        InstallTargetVersion = item;
        StatusIsError = false;

        // 先切到安装页，让用户马上看到界面（加载器版本与 Fabric API 在后台查询）。
        CurrentPageIndex = 3;
        StatusMessage = $"请选择与 {item.Name} 一起安装的模组加载器";

        // 默认选中 Fabric：这是最常见的选择，同时会自动推荐 Fabric API。
        await SelectLoaderAsync(ModLoaderKind.Fabric).ConfigureAwait(true);
    }

    /// <summary>
    /// 选择加载器类型，并加载该类型在目标 Minecraft 版本下的可用版本列表。
    /// </summary>
    /// <param name="kind">加载器类型。</param>
    private async Task SelectLoaderAsync(ModLoaderKind kind)
    {
        SelectedLoaderKind = kind;

        // 即使类型没有变化（例如首次打开页面默认就是 Fabric），也要同步卡片选中状态。
        foreach (var option in LoaderOptions)
            option.IsSelected = option.Kind == kind;

        LoaderVersions.Clear();
        SelectedLoaderVersion = null;
        OnPropertyChanged(nameof(HasLoaderVersions));

        _recommendedFabricApi = null;
        OnPropertyChanged(nameof(CanIncludeFabricApi));
        OnPropertyChanged(nameof(FabricApiVersionText));
        OnPropertyChanged(nameof(InstallSummaryText));

        if (kind == ModLoaderKind.Vanilla)
        {
            LoaderSupported = true;
            LoaderStatusText = "原版不需要选择加载器版本。";
            FabricApiStatusText = string.Empty;
            return;
        }

        var minecraftVersion = InstallTargetVersionName;
        var loaderName = SelectedLoaderName;
        var generation = ++_loaderQueryGeneration;

        IsLoaderLoading = true;
        LoaderStatusText = $"正在查询 {minecraftVersion} 可用的 {loaderName} 版本…";

        try
        {
            var query = await _minecraftService
                .QueryModLoaderVersionsAsync(kind, minecraftVersion)
                .ConfigureAwait(true);

            // 用户已经切换到别的加载器：丢弃这次结果，避免旧数据覆盖新选择。
            if (generation != _loaderQueryGeneration)
                return;

            var versions = query.Versions;

            // 稳定版优先，其次按构建号从新到旧。
            var ordered = versions
                .OrderByDescending(v => v.IsStable)
                .ThenByDescending(v => v.Build)
                .ToList();

            foreach (var version in ordered)
                LoaderVersions.Add(version);

            OnPropertyChanged(nameof(HasLoaderVersions));

            LoaderSupported = ordered.Count > 0;

            if (ordered.Count == 0)
            {
                LoaderStatusText = string.IsNullOrWhiteSpace(query.ErrorText)
                    ? $"该 Minecraft 版本没有可用的 {loaderName} 版本，请改选其他加载器。"
                    : query.ErrorText!;
            }
            else
            {
                // 默认选中最新的稳定版（列表已把稳定版排在前面）。
                SelectedLoaderVersion = ordered[0];
                var badge = string.IsNullOrWhiteSpace(ordered[0].Badge) ? "最新" : ordered[0].Badge;
                LoaderStatusText = $"共 {ordered.Count} 个 {loaderName} 版本（默认选中 {ordered[0].Version}，{badge}）";
            }
        }
        catch (Exception ex)
        {
            if (generation != _loaderQueryGeneration)
                return;

            LoaderSupported = false;
            LoaderStatusText = $"查询 {loaderName} 版本失败：{ex.Message}";
        }
        finally
        {
            // 只有仍然是最新一次查询时才关掉加载状态。
            if (generation == _loaderQueryGeneration)
            {
                IsLoaderLoading = false;
                OnPropertyChanged(nameof(InstallSummaryText));
            }
        }

        // Fabric 模式下顺便查询推荐一起安装的 Fabric API。
        if (kind == ModLoaderKind.Fabric && generation == _loaderQueryGeneration)
            await LoadRecommendedFabricApiAsync(minecraftVersion, generation).ConfigureAwait(true);
    }

    /// <summary>
    /// 查询与该 Minecraft 版本匹配的 Fabric API，默认勾选一起安装。
    /// </summary>
    /// <param name="minecraftVersion">Minecraft 版本号。</param>
    /// <param name="generation">发起查询时的加载器代号。</param>
    private async Task LoadRecommendedFabricApiAsync(string minecraftVersion, int generation)
    {
        IsFabricApiLoading = true;
        FabricApiStatusText = "正在查询与当前版本匹配的 Fabric API…";

        try
        {
            var release = await _fabricApiService
                .GetRecommendedAsync(minecraftVersion, SelectedLoaderVersion?.Version)
                .ConfigureAwait(true);

            // 期间又切换了加载器：丢弃这次推荐结果。
            if (generation != _loaderQueryGeneration)
                return;

            _recommendedFabricApi = release;

            if (release is null)
            {
                IncludeFabricApi = false;
                FabricApiStatusText = "没有找到与当前版本匹配的 Fabric API；可以不装，或稍后手动放进 mods 文件夹。";
            }
            else
            {
                IncludeFabricApi = true;
                FabricApiStatusText = $"已自动推荐 Fabric API {release.VersionNumber}，会随加载器一起下载到该版本的 mods 文件夹。";
            }
        }
        catch (Exception ex)
        {
            IncludeFabricApi = false;
            FabricApiStatusText = $"查询 Fabric API 失败：{ex.Message}（可以只装加载器，稍后手动添加模组）";
        }
        finally
        {
            IsFabricApiLoading = false;
            OnPropertyChanged(nameof(CanIncludeFabricApi));
            OnPropertyChanged(nameof(FabricApiVersionText));
            OnPropertyChanged(nameof(InstallSummaryText));
        }
    }

    /// <summary>
    /// 按当前选择执行安装：原版 / 原版 + 加载器 / 原版 + 加载器 + Fabric API。
    /// </summary>
    private async Task StartInstallAsync()
    {
        var target = _installTargetVersion;
        if (target is null)
        {
            StatusIsError = true;
            StatusMessage = "请先在下载页选择一个 Minecraft 版本";
            return;
        }

        if (IsInstalling)
        {
            StatusMessage = "已有安装任务正在进行，请等待完成或先取消";
            return;
        }

        if (SelectedLoaderKind != ModLoaderKind.Vanilla && (!LoaderSupported || SelectedLoaderVersion is null))
        {
            StatusIsError = true;
            StatusMessage = "当前加载器没有可用版本，请改选其他加载器或先刷新";
            return;
        }

        SaveSettings();

        _activeCts?.Dispose();
        _activeCts = new CancellationTokenSource();
        var cancellationToken = _activeCts.Token;

        var minecraftVersion = target.Name;
        var loaderName = SelectedLoaderName;

        IsInstalling = true;
        StatusIsError = false;
        target.IsBusy = true;
        target.StatusText = "安装中…";
        InstallPercent = 0;
        InstallSizeText = string.Empty;
        InstallFileText = string.Empty;
        InstallStageText = SelectedLoaderKind == ModLoaderKind.Vanilla
            ? $"正在安装 {minecraftVersion}"
            : $"正在安装 {minecraftVersion} + {loaderName} {SelectedLoaderVersion?.Version}";
        StatusMessage = InstallStageText;

        try
        {
            string installedVersionName;

            if (SelectedLoaderKind == ModLoaderKind.Vanilla)
            {
                await _minecraftService
                    .InstallAsync(minecraftVersion, _installProgress, cancellationToken)
                    .ConfigureAwait(true);

                installedVersionName = minecraftVersion;
            }
            else
            {
                // 安装加载器（内部会先补全原版文件，再生成加载器版本并补全其库文件）。
                installedVersionName = await _minecraftService
                    .InstallModLoaderAsync(
                        SelectedLoaderKind,
                        minecraftVersion,
                        SelectedLoaderVersion?.Version,
                        _installProgress,
                        cancellationToken)
                    .ConfigureAwait(true);
            }

            // 可选：一起下载 Fabric API 到该版本的 mods 文件夹。
            var fabricApiRelease = _recommendedFabricApi;
            if (SelectedLoaderKind == ModLoaderKind.Fabric && IncludeFabricApi && fabricApiRelease is not null)
            {
                InstallStageText = $"正在下载 Fabric API {fabricApiRelease.VersionNumber}";
                InstallPercent = 0;
                StatusMessage = InstallStageText;

                var modsFolder = Path.Combine(
                    _minecraftService.GetVersionDirectory(installedVersionName), "mods");

                var downloadProgress = new UiProgress<double>(ratio =>
                {
                    InstallPercent = Math.Round(Math.Clamp(ratio, 0d, 1d) * 100d, 1);
                    InstallSizeText = $"{(int)InstallPercent}%";
                });

                var savedPath = await _fabricApiService
                    .DownloadAsync(fabricApiRelease, modsFolder, downloadProgress, cancellationToken)
                    .ConfigureAwait(true);

                InstallFileText = Path.GetFileName(savedPath);
            }

            // 刷新列表，并把新版本设为当前选中版本。
            await RefreshLocalVersionsAsync().ConfigureAwait(true);

            var installedItem = LocalVersions.FirstOrDefault(v =>
                string.Equals(v.Name, installedVersionName, StringComparison.OrdinalIgnoreCase));
            if (installedItem is not null)
                SelectedLocalVersion = installedItem;

            // 在线列表里的原版条目同步标记为已安装。
            foreach (var online in OnlineVersions.Where(v =>
                         string.Equals(v.Name, minecraftVersion, StringComparison.OrdinalIgnoreCase)))
            {
                online.IsInstalled = true;
            }

            target.IsInstalled = true;
            target.StatusText = "安装完成";

            StatusIsError = false;
            StatusMessage = SelectedLoaderKind == ModLoaderKind.Vanilla
                ? $"{installedVersionName} 安装完成，可以直接启动游戏"
                : $"{installedVersionName} 安装完成，已切换到本地版本列表，可直接启动";

            // 安装成功后回到本地启动页，方便直接点启动。
            CurrentPageIndex = 0;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "已取消安装";
            target.StatusText = "已取消";
        }
        catch (Exception ex)
        {
            StatusIsError = true;
            StatusMessage = $"安装失败：{ex.Message}";
            target.StatusText = "安装失败";
        }
        finally
        {
            IsInstalling = false;
            target.IsBusy = false;
            InstallPercent = 0;
            _activeCts?.Dispose();
            _activeCts = null;
        }
    }

    /// <summary>
    /// 切换到模组下载页，并刷新目标实例与下载源状态。
    /// </summary>
    /// <summary>
    /// 打开资源包下载页（页面切换会触发 <see cref="ResourcePackBrowserViewModel.IsActive"/> 刷新目标实例）。
    /// </summary>
    private Task ShowResourcePackPageAsync()
    {
        CurrentPageIndex = 5;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 打开模组下载页。
    /// </summary>
    private async Task ShowModsPageAsync()
    {
        CurrentPageIndex = 4;
        await ModBrowser.OnShownAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 下载并安装指定版本。
    /// </summary>
    private async Task InstallVersionAsync(VersionItemViewModel? item)
    {
        if (item is null)
            return;

        if (IsInstalling)
        {
            StatusMessage = "已有安装任务正在进行，请等待完成或先取消";
            return;
        }

        if (item.IsInstalled)
        {
            StatusMessage = $"{item.Name} 已经安装，无需重复下载";
            return;
        }

        SaveSettings();

        _activeCts?.Dispose();
        _activeCts = new CancellationTokenSource();
        var cancellationToken = _activeCts.Token;

        IsInstalling = true;
        item.IsBusy = true;
        item.StatusText = "准备下载…";
        InstallPercent = 0;
        InstallStageText = $"正在安装 {item.Name}";
        InstallSizeText = string.Empty;
        InstallFileText = string.Empty;
        StatusMessage = $"开始下载 {item.Name} …";

        try
        {
            await _minecraftService
                .InstallAsync(item.Name, _installProgress, cancellationToken)
                .ConfigureAwait(true);

            item.IsInstalled = true;
            item.StatusText = "安装完成";
            StatusMessage = $"{item.Name} 安装完成，可以启动游戏了";

            // 同步在线列表中同名条目的安装状态。
            foreach (var online in OnlineVersions.Where(v => v.Name == item.Name))
                online.IsInstalled = true;

            await RefreshLocalVersionsAsync().ConfigureAwait(true);
            SelectedLocalVersion = LocalVersions.FirstOrDefault(v => v.Name == item.Name) ?? SelectedLocalVersion;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"已取消安装 {item.Name}";
            item.StatusText = "已取消";
        }
        catch (Exception ex)
        {
            StatusMessage = $"安装 {item.Name} 失败：{ex.Message}";
            item.StatusText = "安装失败";
        }
        finally
        {
            IsInstalling = false;
            item.IsBusy = false;
            InstallPercent = 0;
            _activeCts?.Dispose();
            _activeCts = null;
        }
    }

    /// <summary>
    /// 使用离线会话启动指定版本，并等待游戏进程退出。
    /// </summary>
    private async Task LaunchVersionAsync(VersionItemViewModel? item)
    {
        if (item is null)
        {
            StatusIsError = false;
            StatusMessage = "请先选择一个版本";
            return;
        }

        if (IsGameRunning)
        {
            StatusMessage = "游戏已经在运行中";
            return;
        }

        if (IsInstalling)
        {
            StatusMessage = "安装任务进行中，请稍后再启动游戏";
            return;
        }

        SaveSettings();

        _activeCts?.Dispose();
        _activeCts = new CancellationTokenSource();
        var cancellationToken = _activeCts.Token;

        item.IsBusy = true;
        GameLogs.Clear();
        StatusIsError = false;

        try
        {
            // ---------- 第 1 步：准备账号会话（先做，账号无效时不必浪费时间校验文件）----------
            CmlLib.Core.Auth.MSession? premiumSession = null;

            if (UseMicrosoftAccount)
            {
                item.StatusText = "验证正版账号…";
                StatusMessage = $"正在验证微软账号 {SelectedMicrosoftAccount?.Name ?? string.Empty} …";

                try
                {
                    premiumSession = await _microsoftAuthService
                        .AuthenticateAsync(SelectedMicrosoftAccount?.Identifier, cancellationToken)
                        .ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    StatusIsError = true;
                    StatusMessage = $"微软账号验证失败：{ex.Message}";
                    MicrosoftLoginStatus = $"验证失败：{ex.Message}";
                    return;
                }

                MicrosoftLoginStatus = $"已登录：{premiumSession.Username}";
                StatusMessage = $"微软账号 {premiumSession.Username} 验证通过";
            }
            else
            {
                // 离线模式：记录玩家名到历史，供下次快速选择。
                RecordOfflineName(PlayerName);
            }

            // ---------- 第 2 步：校验并补全游戏文件（含 Java 运行时）----------
            // 这一阶段可能会下载缺失文件，必须把进度显示出来，否则用户会觉得“点了一下没反应”。
            if (CheckFilesBeforeLaunch)
            {
                IsInstalling = true;
                InstallPercent = 0;
                InstallSizeText = string.Empty;
                InstallFileText = string.Empty;
                InstallStageText = $"正在校验 {item.Name} 的文件与 Java 运行时";
                item.StatusText = "校验文件…";
                StatusMessage = $"正在校验 {item.Name} 的游戏文件（缺失文件会自动下载，可能需要几分钟）…";

                await _minecraftService
                    .InstallAsync(item.Name, _installProgress, cancellationToken)
                    .ConfigureAwait(true);

                IsInstalling = false;
            }

            // ---------- 第 3 步：解析并校验 Java 运行时 ----------
            item.StatusText = "解析 Java…";
            StatusMessage = $"正在解析 {item.Name} 所需的 Java 运行时…";

            var javaRuntime = await _minecraftService
                .ResolveJavaRuntimeAsync(item.Name, JavaPathForLaunch, cancellationToken)
                .ConfigureAwait(true);

            // ---------- 第 4 步：构建并启动游戏进程 ----------
            var request = new LaunchRequest
            {
                PlayerName = NormalizePlayerName(PlayerName),
                JavaPath = javaRuntime?.Path,
                MaxMemoryMb = MaxMemoryMb,
                WindowWidth = 1280,
                WindowHeight = 720,
                // 文件已在第 1 步校验，这里不再重复校验。
                CheckFilesBeforeLaunch = false,
                InstanceDirectory = ResolveInstanceDirectory(item.Name),
                // 正版账号使用认证服务返回的会话；离线模式保持 null，由服务生成离线会话。
                Session = premiumSession,
            };

            item.StatusText = "启动中…";
            IsGameRunning = true;
            StatusMessage = premiumSession is null
                ? $"正在启动 {item.Name}（离线账号 {request.PlayerName}，Java {javaRuntime?.MajorVersion}）…"
                : $"正在启动 {item.Name}（正版账号 {premiumSession.Username}，Java {javaRuntime?.MajorVersion}）…";

            var startedAt = DateTime.UtcNow;
            var exitCode = await _minecraftService
                .LaunchAsync(item.Name, request, null, AppendGameLog, cancellationToken)
                .ConfigureAwait(true);

            var runningSeconds = (DateTime.UtcNow - startedAt).TotalSeconds;

            // 进程极短时间内退出通常意味着启动失败（例如 Java 不可用、jar 损坏等）。
            if (exitCode != 0 && runningSeconds < 30)
            {
                StatusIsError = true;
                var javaHint = OperatingSystem.IsWindows()
                    ? "请查看下方游戏日志；若提示 javaw 或 jvm.dll 相关错误，说明 Java 运行时已损坏，" +
                      "可勾选“启动前校验并补全游戏文件”后重试，或在设置中指定其他 Java。"
                    : "请查看下方游戏日志；常见原因是 Java 路径无效、Java 版本过低或运行时缺少依赖，" +
                      "可以在设置中指定其他 Java 后重试。";

                StatusMessage =
                    $"启动失败：游戏进程在 {runningSeconds:0.0} 秒后退出（退出码 {exitCode}）。" + javaHint;
            }
            else
            {
                StatusMessage = $"游戏已退出（退出码 {exitCode}，运行 {FormatDuration(runningSeconds)}）";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"已取消启动 {item.Name}";
        }
        catch (Exception ex)
        {
            StatusIsError = true;
            StatusMessage = $"启动 {item.Name} 失败：{ex.Message}";
            AppendAppLog($"[DPL] 启动失败：{ex.Message}");
        }
        finally
        {
            IsInstalling = false;
            IsGameRunning = false;
            item.IsBusy = false;
            item.StatusText = string.Empty;
            InstallPercent = 0;
            _activeCts?.Dispose();
            _activeCts = null;
        }
    }

    /// <summary>
    /// 取消当前正在进行的安装 / 启动前校验任务。
    /// </summary>
    private void CancelActiveOperation()
    {
        if (_activeCts is null || _activeCts.IsCancellationRequested)
            return;

        StatusMessage = "正在取消当前任务…";
        _activeCts.Cancel();
    }

    /// <summary>
    /// 判断该版本是否应使用“版本隔离”：勾选了隔离选项，且版本文件夹看起来是独立实例
    /// （包含 mods / saves / options.txt 等，典型的 PCL2、HMCL 隔离实例）。
    /// </summary>
    /// <param name="versionName">版本名称。</param>
    /// <returns>作为游戏目录的文件夹；不需要隔离时返回 null。</returns>
    private string? ResolveInstanceDirectory(string versionName)
    {
        if (!UseVersionIsolation)
            return null;

        if (!_minecraftService.LooksLikeIsolatedInstance(versionName))
            return null;

        var directory = _minecraftService.GetVersionDirectory(versionName);
        AppendAppLog($"[DPL] 版本隔离：游戏目录 = {directory}");
        return directory;
    }

    /// <summary>
    /// 把秒数格式化为便于阅读的时长文本。
    /// </summary>
    private static string FormatDuration(double seconds)
        => seconds < 60
            ? $"{seconds:0.0} 秒"
            : $"{seconds / 60:0.0} 分钟";

    /// <summary>
    /// 选择新的游戏目录并刷新版本列表。
    /// </summary>
    private async Task BrowseGameDirectoryAsync()
    {
        if (FilePicker is null)
            return;

        var directory = await FilePicker.PickFolderAsync("选择 .minecraft 游戏目录").ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(directory))
            await ChangeGameDirectoryAsync(directory).ConfigureAwait(true);
    }

    /// <summary>
    /// 让用户手动选择 Java 可执行文件。
    /// </summary>
    private async Task BrowseJavaAsync()
    {
        if (FilePicker is null)
            return;

        var javaPath = await FilePicker.PickJavaExecutableAsync().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(javaPath))
            return;

        JavaPath = javaPath;
        SelectedJava = JavaCandidates.FirstOrDefault(c =>
            string.Equals(c.Path, javaPath, PathComparison));
        StatusMessage = $"已选择 Java：{javaPath}";
    }

    /// <summary>
    /// 切换到指定游戏目录。
    /// </summary>
    private async Task ChangeGameDirectoryAsync(string directory)
    {
        directory = directory.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(directory))
            return;

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            StatusMessage = $"无法使用该目录：{ex.Message}";
            return;
        }

        GameDirectory = directory;
        _minecraftService.UseGameDirectory(directory);
        SaveSettings();

        await RefreshLocalVersionsAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 在系统文件管理器中打开游戏目录。
    /// </summary>
    private void OpenGameDirectory()
    {
        try
        {
            Directory.CreateDirectory(GameDirectory);
        }
        catch (Exception)
        {
            // 目录无法创建时仍尝试打开，由系统返回失败提示。
        }

        if (!SystemIntegration.OpenDirectory(GameDirectory))
            StatusMessage = $"无法打开目录：{GameDirectory}";
    }

    /// <summary>
    /// 让用户选择自定义的版本目录（versions）。
    /// </summary>
    private async Task BrowseVersionsDirectoryAsync()
    {
        if (FilePicker is null)
            return;

        var directory = await FilePicker.PickFolderAsync("选择版本目录（versions）").ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(directory))
            await ChangeVersionsDirectoryAsync(directory).ConfigureAwait(true);
    }

    /// <summary>
    /// 切换版本目录；传入空字符串表示恢复为游戏目录下的默认 versions 目录。
    /// </summary>
    private async Task ChangeVersionsDirectoryAsync(string directory)
    {
        directory = directory.Trim().Trim('"');

        if (!string.IsNullOrWhiteSpace(directory))
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                StatusMessage = $"无法使用该版本目录：{ex.Message}";
                return;
            }
        }

        VersionsDirectory = directory;
        _minecraftService.UseVersionsDirectory(directory);
        SaveSettings();

        await RefreshLocalVersionsAsync().ConfigureAwait(true);

        StatusMessage = string.IsNullOrWhiteSpace(directory)
            ? $"已恢复默认版本目录：{VersionsDirectoryText}"
            : $"已切换版本目录：{directory}";
    }

    /// <summary>
    /// 在系统文件管理器中打开版本目录。
    /// </summary>
    private void OpenVersionsDirectory()
    {
        var directory = VersionsDirectoryText;

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception)
        {
            // 目录无法创建时仍尝试打开，由系统返回失败提示。
        }

        if (!SystemIntegration.OpenDirectory(directory))
            StatusMessage = $"无法打开版本目录：{directory}";
    }

    /// <summary>
    /// 在系统文件管理器中打开指定版本的文件夹。
    /// </summary>
    /// <param name="versionName">版本名称；为空时不做任何操作。</param>
    private void OpenVersionFolder(string? versionName)
    {
        if (string.IsNullOrWhiteSpace(versionName))
        {
            StatusMessage = "请先选择一个版本";
            return;
        }

        var directory = _minecraftService.GetVersionDirectory(versionName);

        if (!Directory.Exists(directory))
        {
            StatusMessage = $"版本文件夹不存在：{directory}";
            return;
        }

        if (SystemIntegration.OpenDirectory(directory))
            StatusMessage = $"已打开版本文件夹：{directory}";
        else
            StatusMessage = $"无法打开版本文件夹：{directory}";
    }

    /// <summary>
    /// 重新扫描本机 Java 运行时，并逐个实际执行 <c>java -version</c> 校验可用性与版本号。
    /// 磁盘上存在 java.exe 并不等于可用（运行时文件残缺时 JVM 会直接拒绝启动），必须校验。
    /// </summary>
    private async Task RefreshJavaCandidatesAsync()
    {
        var rawCandidates = _minecraftService.FindJavaCandidates();

        JavaCandidates.Clear();

        foreach (var raw in rawCandidates)
        {
            var info = await JavaRuntimeProbe.ProbeAsync(raw.Path).ConfigureAwait(true);

            var displayName = info is null
                ? $"{raw.DisplayName}（不可用）"
                : $"{raw.DisplayName}（Java {info.MajorVersion}）";

            JavaCandidates.Add(new JavaCandidate(raw.Path, displayName)
            {
                IsUsable = info is not null,
                MajorVersion = info?.MajorVersion ?? 0,
            });
        }

        if (!string.IsNullOrWhiteSpace(JavaPath))
        {
            _selectedJava = JavaCandidates.FirstOrDefault(c =>
                string.Equals(c.Path, JavaPath, PathComparison));
            OnPropertyChanged(nameof(SelectedJava));
        }

        OnPropertyChanged(nameof(JavaDetectedText));
    }

    /// <summary>
    /// 接收安装进度并更新界面状态。
    /// </summary>
    private void OnInstallProgressChanged(InstallProgressInfo info)
    {
        InstallPercent = Math.Round(Math.Clamp(info.Ratio, 0d, 1d) * 100d, 1);

        if (!string.IsNullOrWhiteSpace(info.Stage))
            InstallStageText = info.Stage;

        InstallSizeText = info.SizeText;

        if (string.IsNullOrWhiteSpace(info.CurrentFile))
        {
            InstallFileText = info.TotalTasks > 0
                ? $"{info.ProgressedTasks} / {info.TotalTasks} 个文件"
                : string.Empty;
        }
        else
        {
            var fileName = Path.GetFileName(info.CurrentFile);
            InstallFileText = info.TotalTasks > 0
                ? $"{info.ProgressedTasks} / {info.TotalTasks} 个文件 · {fileName}"
                : fileName;
        }
    }

    /// <summary>
    /// 追加一行镜像日志（下载源切换 / 失败原因），显示在日志面板里。
    /// </summary>
    /// <param name="message">镜像处理器给出的一行说明。</param>
    private void AppendMirrorLog(string message)
        => AppendAppLog(string.IsNullOrWhiteSpace(message) ? "[镜像] " : $"[镜像] {message}");

    /// <summary>
    /// 追加一行启动器日志（网络 / 下载 / 模组搜索等），写进独立的 <see cref="AppLogs"/>。
    /// </summary>
    private void AppendAppLog(string line)
    {
        if (string.IsNullOrEmpty(line))
            return;

        Dispatcher.UIThread.Post(() =>
        {
            AppLogs.Add(line);
            while (AppLogs.Count > 300)
                AppLogs.RemoveAt(0);
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// 追加一行游戏日志（自动切换回 UI 线程，并限制日志长度）。
    /// </summary>
    private void AppendGameLog(string line)
    {
        if (string.IsNullOrEmpty(line))
            return;

        Dispatcher.UIThread.Post(() =>
        {
            GameLogs.Add(line);
            while (GameLogs.Count > 300)
                GameLogs.RemoveAt(0);
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// 规范化离线会话名称：仅保留字母、数字和下划线，长度限制为 3 ~ 16。
    /// </summary>
    private static string NormalizePlayerName(string? name)
    {
        var cleaned = new string((name ?? string.Empty)
            .Where(c => char.IsLetterOrDigit(c) || c == '_')
            .ToArray());

        if (cleaned.Length == 0)
            cleaned = "Steve";
        if (cleaned.Length > 16)
            cleaned = cleaned[..16];

        return cleaned;
    }

    /// <summary>
    /// 创建带异常兜底的异步命令。
    /// </summary>
    private AsyncRelayCommand CreateCommand(Func<object?, Task> execute)
        => new(execute, _ => !IsBusy && !IsInstalling)
        {
            ErrorHandler = ex => StatusMessage = $"操作失败：{ex.Message}",
        };

    /// <summary>
    /// 各平台默认的游戏目录路径。
    /// </summary>
    private static string MinecraftPathDefault => CmlLib.Core.MinecraftPath.GetOSDefaultPath();
}
