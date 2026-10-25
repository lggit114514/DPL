using System.Collections.ObjectModel;
using System.Windows.Input;
using DPL.Models.ResourcePacks;
using DPL.Services;
using DPL.Services.ResourcePacks;

namespace DPL.ViewModels;

/// <summary>
/// 资源包（材质包）下载页的 ViewModel：独立于模组下载，不复用模组的搜索/依赖/安装语义。
/// 当前只接入 Modrinth（CurseForge 用 classId=12 的源待补）。
/// </summary>
public sealed class ResourcePackBrowserViewModel : ViewModelBase
{
    /// <summary>热门资源包每页条数。</summary>
    private const int PopularPageSize = 30;

    /// <summary>热门列表缓存时长：按游戏版本缓存，避免反复清空搜索框时重复请求。</summary>
    private static readonly TimeSpan PopularCacheDuration = TimeSpan.FromMinutes(10);

    /// <summary>带游戏版本过滤却为空时的轻提示。</summary>
    private const string PopularFallbackNotice = "该版本暂无热门资源包，已为你展示全版本资源包";

    private readonly Func<ResourcePackTarget?> _targetProvider;
    private readonly Action<string> _logSink;

    /// <summary>热门列表缓存（key = 游戏版本），独立于模组页的热门缓存。</summary>
    private readonly Dictionary<string, PopularCacheEntry> _popularCache =
        new(StringComparer.OrdinalIgnoreCase);

    private ResourcePackSource? _source;
    private ResourcePackInstallService? _installer;

    private ResourcePackTarget? _target;
    private string _searchText = string.Empty;
    private ResourcePackItemViewModel? _selectedResult;
    private ResourcePackVersionViewModel? _selectedVersion;
    private bool _isSearching;
    private bool _isLoadingVersions;
    private bool _isDownloading;
    private bool _isActive;
    private bool _isPopularMode;
    private bool _isLoadingPopular;
    private bool _hasPopularItems;
    private bool _canLoadMorePopular;
    private bool _popularIncludeGameVersion = true;
    private int _popularTotal;
    private int _popularServerOffset;
    private double _progressPercent;
    private string _statusText = "输入关键字搜索资源包（只按游戏版本过滤，与加载器无关）";
    private string _errorText = string.Empty;
    private string _targetText = "尚未选择版本";
    private string _popularError = string.Empty;
    private string _popularNotice = string.Empty;

    /// <summary>
    /// 创建资源包下载页 ViewModel。
    /// </summary>
    /// <param name="targetProvider">获取当前目标实例（游戏版本 + resourcepacks 目录）。</param>
    /// <param name="logSink">日志输出到主界面（启动器日志面板）。</param>
    public ResourcePackBrowserViewModel(
        Func<ResourcePackTarget?> targetProvider,
        Action<string> logSink)
    {
        _targetProvider = targetProvider;
        _logSink = logSink;

        SearchCommand = new AsyncRelayCommand(_ => SearchAsync()) { ErrorHandler = ShowError };
        DownloadCommand = new AsyncRelayCommand(_ => DownloadAsync()) { ErrorHandler = ShowError };
        RetryPopularCommand = new AsyncRelayCommand(_ => EnsurePopularAsync(force: true));
        LoadMorePopularCommand = new AsyncRelayCommand(_ => LoadMorePopularAsync());
    }

    /// <summary>搜索结果。</summary>
    public ObservableCollection<ResourcePackItemViewModel> Results { get; } = new();

    /// <summary>选中资源包的兼容版本。</summary>
    public ObservableCollection<ResourcePackVersionViewModel> Versions { get; } = new();

    /// <summary>搜索命令。</summary>
    public ICommand SearchCommand { get; }

    /// <summary>下载命令。</summary>
    public ICommand DownloadCommand { get; }

    /// <summary>热门列表加载失败后重试。</summary>
    public ICommand RetryPopularCommand { get; }

    /// <summary>热门列表“加载更多”。</summary>
    public ICommand LoadMorePopularCommand { get; }

    /// <summary>页面是否处于激活状态（由主窗口切换页面时设置）。</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (!SetProperty(ref _isActive, value))
                return;

            if (value)
                _ = OnShownAsync();
        }
    }

    /// <summary>当前目标实例的展示文本。</summary>
    public string TargetText
    {
        get => _targetText;
        private set => SetProperty(ref _targetText, value);
    }

    /// <summary>当前是否展示热门资源包列表（搜索框为空时）。</summary>
    public bool IsPopularMode
    {
        get => _isPopularMode;
        private set
        {
            if (!SetProperty(ref _isPopularMode, value))
                return;

            OnPropertyChanged(nameof(ResultsTitle));
            OnPropertyChanged(nameof(ShowPopularEmpty));
        }
    }

    /// <summary>结果区标题：热门资源包 / 搜索结果。</summary>
    public string ResultsTitle => IsPopularMode ? "热门资源包（按下载量）" : "搜索结果";

    /// <summary>是否正在加载热门资源包。</summary>
    public bool IsLoadingPopular
    {
        get => _isLoadingPopular;
        private set
        {
            if (!SetProperty(ref _isLoadingPopular, value))
                return;

            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(ShowPopularEmpty));
        }
    }

    /// <summary>热门列表当前是否存在结果。</summary>
    public bool HasPopularItems
    {
        get => _hasPopularItems;
        private set
        {
            if (SetProperty(ref _hasPopularItems, value))
                OnPropertyChanged(nameof(ShowPopularEmpty));
        }
    }

    /// <summary>热门列表的轻提示（例如版本过滤为空后已放宽到全版本）。</summary>
    public string PopularNotice
    {
        get => _popularNotice;
        private set
        {
            if (SetProperty(ref _popularNotice, value))
                OnPropertyChanged(nameof(HasPopularNotice));
        }
    }

    /// <summary>是否存在热门列表提示。</summary>
    public bool HasPopularNotice => !string.IsNullOrWhiteSpace(PopularNotice);

    /// <summary>热门列表的错误信息。</summary>
    public string PopularError
    {
        get => _popularError;
        private set
        {
            if (!SetProperty(ref _popularError, value))
                return;

            OnPropertyChanged(nameof(HasPopularError));
            OnPropertyChanged(nameof(ShowPopularEmpty));
        }
    }

    /// <summary>热门列表是否加载失败。</summary>
    public bool HasPopularError => !string.IsNullOrWhiteSpace(PopularError);

    /// <summary>热门列表为空（且不在加载 / 报错）。</summary>
    public bool ShowPopularEmpty => IsPopularMode && !HasPopularItems && !IsLoadingPopular && !HasPopularError;

    /// <summary>热门列表空状态文案。</summary>
    public string PopularEmptyText => "该版本暂无热门资源包，可以试试搜索";

    /// <summary>是否还能加载更多热门资源包。</summary>
    public bool CanLoadMorePopular
    {
        get => _canLoadMorePopular;
        private set => SetProperty(ref _canLoadMorePopular, value);
    }

    /// <summary>搜索关键字。</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            var wasEmpty = string.IsNullOrWhiteSpace(_searchText);
            if (!SetProperty(ref _searchText, value))
                return;

            // 从“有内容”变成空：自动回到热门列表（命中缓存则不会重复请求）。
            if (!wasEmpty && string.IsNullOrWhiteSpace(value) && !IsSearching && !IsDownloading)
                _ = EnsurePopularAsync();
        }
    }

    /// <summary>选中的资源包；变化时自动加载兼容版本。</summary>
    public ResourcePackItemViewModel? SelectedResult
    {
        get => _selectedResult;
        set
        {
            if (!SetProperty(ref _selectedResult, value))
                return;

            _ = LoadVersionsAsync(value);
        }
    }

    /// <summary>选中的版本。</summary>
    public ResourcePackVersionViewModel? SelectedVersion
    {
        get => _selectedVersion;
        set => SetProperty(ref _selectedVersion, value);
    }

    /// <summary>是否正在搜索。</summary>
    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (SetProperty(ref _isSearching, value))
                OnPropertyChanged(nameof(IsIdle));
        }
    }

    /// <summary>是否正在加载版本列表。</summary>
    public bool IsLoadingVersions
    {
        get => _isLoadingVersions;
        private set => SetProperty(ref _isLoadingVersions, value);
    }

    /// <summary>是否正在下载。</summary>
    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            if (SetProperty(ref _isDownloading, value))
                OnPropertyChanged(nameof(IsIdle));
        }
    }

    /// <summary>当前是否可以发起搜索 / 下载。</summary>
    public bool IsIdle => !IsSearching && !IsDownloading && !IsLoadingPopular;

    /// <summary>下载进度（0~100）。</summary>
    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    /// <summary>状态文字。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>错误提示。</summary>
    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    /// <summary>是否有错误提示。</summary>
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    /// <summary>
    /// 页面显示时刷新目标实例信息；搜索框为空时自动展示热门资源包。
    /// </summary>
    public async Task OnShownAsync()
    {
        _target = _targetProvider();
        TargetText = _target?.DisplayText ?? "尚未选择版本";

        if (_target is null)
        {
            StatusText = "请先在「本地启动」页选择一个已安装的版本";
            return;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            await EnsurePopularAsync().ConfigureAwait(true);
            return;
        }

        if (Results.Count == 0)
            StatusText = "输入关键字搜索资源包（只按游戏版本过滤，与加载器无关）";
    }

    /// <summary>
    /// 搜索资源包。
    /// </summary>
    public async Task SearchAsync()
    {
        if (IsSearching || IsDownloading || IsLoadingPopular)
            return;

        var target = _targetProvider();
        _target = target;

        if (target is null)
        {
            ShowError(new InvalidOperationException("请先在「本地启动」页选择一个已安装的版本"));
            return;
        }

        // 搜索框为空：不报错，直接回到热门列表（命中缓存不会重复请求）。
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            await EnsurePopularAsync().ConfigureAwait(true);
            return;
        }

        EnsureServices();

        IsSearching = true;
        IsPopularMode = false;
        CanLoadMorePopular = false;
        PopularNotice = string.Empty;
        PopularError = string.Empty;
        ErrorText = string.Empty;
        StatusText = "正在搜索资源包…";
        Results.Clear();
        Versions.Clear();
        SelectedResult = null;
        SelectedVersion = null;

        try
        {
            var items = await _source!
                .SearchAsync(SearchText.Trim(), target.GameVersion)
                .ConfigureAwait(true);

            foreach (var item in items)
                Results.Add(new ResourcePackItemViewModel(item));

            StatusText = Results.Count == 0
                ? $"没有找到与 {target.GameVersion} 兼容的资源包"
                : $"找到 {Results.Count} 个资源包（{target.GameVersion}）";

            if (Results.Count > 0)
                SelectedResult = Results[0];
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsSearching = false;

            // 搜索期间用户可能把关键字清空了：搜索结束后回到热门列表。
            if (string.IsNullOrWhiteSpace(SearchText))
                _ = EnsurePopularAsync();
        }
    }

    /// <summary>
    /// 加载选中资源包的兼容版本（只按游戏版本过滤）。
    /// </summary>
    public async Task LoadVersionsAsync(ResourcePackItemViewModel? result)
    {
        Versions.Clear();
        SelectedVersion = null;

        if (result is null)
            return;

        var target = _targetProvider();
        _target = target;
        if (target is null)
            return;

        EnsureServices();
        IsLoadingVersions = true;

        try
        {
            var versions = await _source!
                .GetVersionsAsync(result.Project.ProjectId, target.GameVersion)
                .ConfigureAwait(true);

            foreach (var version in versions)
                Versions.Add(new ResourcePackVersionViewModel(version));

            StatusText = Versions.Count == 0
                ? $"{result.Name} 没有与 {target.GameVersion} 兼容的版本"
                : $"{result.Name}：{Versions.Count} 个兼容版本";

            SelectedVersion = Versions.FirstOrDefault();
        }
        catch (Exception ex)
        {
            ErrorText = $"获取版本失败：{ex.Message}";
        }
        finally
        {
            IsLoadingVersions = false;
        }
    }

    /// <summary>
    /// 下载选中版本到 resourcepacks 目录（不解析依赖、不解压）。
    /// </summary>
    public async Task DownloadAsync()
    {
        if (IsDownloading || IsSearching || IsLoadingPopular)
            return;

        var target = _targetProvider();
        _target = target;

        if (target is null)
        {
            ShowError(new InvalidOperationException("请先选择一个已安装的版本"));
            return;
        }

        if (SelectedResult is null || SelectedVersion is null)
        {
            ErrorText = "请先搜索并选择一个资源包版本";
            return;
        }

        EnsureServices();
        IsDownloading = true;
        ErrorText = string.Empty;
        ProgressPercent = 0;

        try
        {
            var progress = new UiProgress<ResourcePackProgress>(info =>
            {
                ProgressPercent = info.Percent;
                StatusText = $"{info.Stage}（{info.Percent:0.#}%）";
            });

            var report = await _installer!
                .InstallAsync(SelectedVersion.Version, target.ResourcePacksDirectory, progress)
                .ConfigureAwait(true);

            ProgressPercent = 100;
            StatusText = report.SkippedFiles.Count > 0
                ? $"已存在，跳过：{Path.GetFileName(report.SkippedFiles[0])} → {target.ResourcePacksDirectory}"
                : $"完成：新增 {report.SavedFiles.Count} 个文件 → {target.ResourcePacksDirectory}";

            _logSink($"[资源包] " + StatusText);
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消下载";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsDownloading = false;
        }
    }

    /// <summary>
    /// 加载热门资源包（搜索框为空时调用）。
    /// 命中 10 分钟缓存则直接使用；带游戏版本过滤为空时自动放宽到全版本。
    /// </summary>
    /// <param name="force">是否强制忽略缓存（重试按钮使用）。</param>
    private async Task EnsurePopularAsync(bool force = false)
    {
        if (IsLoadingPopular || IsSearching || IsDownloading)
            return;

        var target = _targetProvider();
        _target = target;

        if (target is null)
        {
            PopularError = string.Empty;
            StatusText = "请先在「本地启动」页选择一个已安装的版本";
            return;
        }

        var cacheKey = string.IsNullOrWhiteSpace(target.GameVersion) ? "-" : target.GameVersion;

        if (!force && TryGetCachedPopular(cacheKey, out var cached))
        {
            ApplyPopular(cached.Items, cached.TotalHits, cached.UsedGameVersionFilter, cached.RawCount);
            PopularNotice = cached.UsedGameVersionFilter ? string.Empty : PopularFallbackNotice;
            StatusText = HasPopularItems ? BuildPopularStatus() : PopularEmptyText;
            _logSink($"[资源包] 热门资源包命中缓存（{cacheKey}，{cached.Items.Count} 条）");
            return;
        }

        EnsureServices();
        IsLoadingPopular = true;
        IsPopularMode = true;
        PopularError = string.Empty;
        PopularNotice = string.Empty;
        ErrorText = string.Empty;

        try
        {
            var page = await _source!
                .SearchPopularAsync(target.GameVersion, PopularPageSize, 0, includeGameVersion: true)
                .ConfigureAwait(true);

            // 带游戏版本过滤后为空（例如很新的版本）：去掉版本限制再搜一次。
            if (page.Items.Count == 0 && page.UsedGameVersionFilter)
            {
                page = await _source!
                    .SearchPopularAsync(target.GameVersion, PopularPageSize, 0, includeGameVersion: false)
                    .ConfigureAwait(true);

                // 只有全版本确实有结果时才提示“已放宽版本”，避免空列表也给出误导提示。
                if (page.Items.Count > 0)
                    PopularNotice = PopularFallbackNotice;
            }

            ApplyPopular(page.Items, page.TotalHits, page.UsedGameVersionFilter, page.RawCount);
            CachePopular(cacheKey, page.Items, page.TotalHits, page.UsedGameVersionFilter, page.RawCount);
            StatusText = HasPopularItems ? BuildPopularStatus() : PopularEmptyText;

            _logSink($"[资源包] 热门资源包：{page.Items.Count} 个" +
                     $"（{target.GameVersion}，{(page.UsedGameVersionFilter ? "按版本过滤" : "全版本")}）");
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消加载热门资源包";
        }
        catch (Exception ex)
        {
            // 失败时不保留旧缓存，避免清空搜索框后又拿到过期结果。
            _popularCache.Remove(cacheKey);
            PopularError = $"热门资源包加载失败：{ex.Message}";
            StatusText = PopularError;
            _logSink("[资源包] " + PopularError);
        }
        finally
        {
            IsLoadingPopular = false;
        }
    }

    /// <summary>
    /// 热门列表“加载更多”：带 offset 追加下一页（沿用首页是否放宽版本的结论）。
    /// </summary>
    private async Task LoadMorePopularAsync()
    {
        if (IsLoadingPopular || IsSearching || IsDownloading || !CanLoadMorePopular)
            return;

        var target = _targetProvider();
        _target = target;
        if (target is null)
            return;

        EnsureServices();
        IsLoadingPopular = true;
        PopularError = string.Empty;

        try
        {
            var page = await _source!
                .SearchPopularAsync(
                    target.GameVersion,
                    PopularPageSize,
                    _popularServerOffset,
                    includeGameVersion: _popularIncludeGameVersion)
                .ConfigureAwait(true);

            foreach (var item in page.Items)
                Results.Add(new ResourcePackItemViewModel(item));

            _popularServerOffset += page.RawCount;
            _popularTotal = Math.Max(page.TotalHits, _popularServerOffset);
            HasPopularItems = Results.Count > 0;
            CanLoadMorePopular = page.RawCount > 0 && _popularServerOffset < _popularTotal;
            StatusText = $"热门资源包：已加载 {Results.Count} 个（共 {_popularTotal} 条命中）";
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消加载更多";
        }
        catch (Exception ex)
        {
            PopularError = $"加载更多失败：{ex.Message}";
            StatusText = PopularError;
            _logSink("[资源包] " + PopularError);
        }
        finally
        {
            IsLoadingPopular = false;
        }
    }

    /// <summary>
    /// 把一页热门结果写入界面状态。
    /// </summary>
    private void ApplyPopular(
        IReadOnlyList<ResourcePackProject> items,
        int totalHits,
        bool usedGameVersionFilter,
        int rawCount)
    {
        Results.Clear();
        foreach (var item in items)
            Results.Add(new ResourcePackItemViewModel(item));

        Versions.Clear();
        SelectedResult = null;
        SelectedVersion = null;

        PopularError = string.Empty;
        ErrorText = string.Empty;
        _popularServerOffset = rawCount;
        _popularTotal = Math.Max(totalHits, rawCount);
        _popularIncludeGameVersion = usedGameVersionFilter;
        IsPopularMode = true;
        HasPopularItems = Results.Count > 0;
        CanLoadMorePopular = rawCount > 0 && _popularServerOffset < _popularTotal;
    }

    /// <summary>热门列表的状态栏文案。</summary>
    private string BuildPopularStatus()
    {
        var scope = _popularIncludeGameVersion
            ? _target?.GameVersion ?? "当前版本"
            : "全版本";

        return $"热门资源包：{Results.Count} 个（{scope} · 按下载量）";
    }

    /// <summary>读取未过期的热门缓存。</summary>
    private bool TryGetCachedPopular(string cacheKey, out PopularCacheEntry entry)
    {
        if (_popularCache.TryGetValue(cacheKey, out var cached) &&
            DateTimeOffset.UtcNow - cached.FetchedAt < PopularCacheDuration)
        {
            entry = cached;
            return true;
        }

        _popularCache.Remove(cacheKey);
        entry = default!;
        return false;
    }

    /// <summary>写入热门缓存（只缓存第一页）。</summary>
    private void CachePopular(
        string cacheKey,
        IReadOnlyList<ResourcePackProject> items,
        int totalHits,
        bool usedGameVersionFilter,
        int rawCount)
        => _popularCache[cacheKey] = new PopularCacheEntry(
            items, totalHits, usedGameVersionFilter, rawCount, DateTimeOffset.UtcNow);

    /// <summary>
    /// 按需创建服务，并把底层日志转给主界面。
    /// </summary>
    private void EnsureServices()
    {
        if (_source is not null && _installer is not null)
            return;

        var config = new ResourcePackConfig();

        _source = new ResourcePackSource(config);
        _source.LogWritten += message => _logSink("[资源包] " + message);

        _installer = new ResourcePackInstallService(config);
        _installer.LogWritten += message => _logSink("[资源包] " + message);
    }

    /// <summary>
    /// 展示错误信息。
    /// </summary>
    private void ShowError(Exception ex)
    {
        ErrorText = ex.Message;
        StatusText = "操作失败，具体原因见上方提示";
        _logSink("[资源包] 失败：" + ErrorText);
    }

    /// <summary>热门列表缓存条目（按游戏版本，完全独立于模组页）。</summary>
    private sealed record PopularCacheEntry(
        IReadOnlyList<ResourcePackProject> Items,
        int TotalHits,
        bool UsedGameVersionFilter,
        int RawCount,
        DateTimeOffset FetchedAt);
}

/// <summary>
/// 资源包搜索结果条目。
/// </summary>
public sealed class ResourcePackItemViewModel
{
    /// <summary>
    /// 创建结果条目。
    /// </summary>
    /// <param name="project">资源包项目。</param>
    public ResourcePackItemViewModel(ResourcePackProject project) => Project = project;

    /// <summary>资源包项目。</summary>
    public ResourcePackProject Project { get; }

    /// <summary>名称。</summary>
    public string Name => Project.Name;

    /// <summary>简短描述。</summary>
    public string Description => Project.Description;

    /// <summary>来源平台文字。</summary>
    public string SourceText => Project.SourceText;

    /// <summary>更新时间文字。</summary>
    public string UpdatedAtText => Project.UpdatedAtText;

    /// <summary>下载量文字。</summary>
    public string DownloadsText => Project.DownloadsText;
}

/// <summary>
/// 资源包版本条目。
/// </summary>
public sealed class ResourcePackVersionViewModel
{
    /// <summary>
    /// 创建版本条目。
    /// </summary>
    /// <param name="version">版本信息。</param>
    public ResourcePackVersionViewModel(ResourcePackVersion version) => Version = version;

    /// <summary>版本信息。</summary>
    public ResourcePackVersion Version { get; }

    /// <summary>版本显示文本。</summary>
    public string DisplayText => Version.DisplayText;

    /// <summary>文件大小文字。</summary>
    public string FileSizeText => Version.FileSizeText;
}
