using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Threading;
using DPL.Models.Mods;
using DPL.Services;
using DPL.Services.Mods;

namespace DPL.ViewModels;

/// <summary>
/// 模组下载页的 ViewModel：负责搜索、择优展示、版本选择、依赖解析与下载。
/// 平台细节全部封装在 <see cref="IModSource"/> 实现里，这里只做交互编排。
/// </summary>
public sealed class ModBrowserViewModel : ViewModelBase
{
    private readonly Func<ModTarget?> _targetProvider;
    private readonly Func<string?> _apiKeyProvider;
    private readonly Action<string> _logSink;

    private ModSourceRouter? _router;
    private ModDependencyResolver? _resolver;
    private ModInstallService? _installer;
    private string? _routerApiKey;

    private string _searchText = string.Empty;
    private int _sourceIndex;
    private string _sourceStatusText = "尚未探测下载源";
    private string _syncAtText = string.Empty;
    private ModTarget? _target;
    private ModResultViewModel? _selectedResult;
    private ModVersionViewModel? _selectedVersion;
    private bool _isSearching;
    private bool _isDownloading;
    private bool _isLoadingVersions;
    private bool _isLoadingPopular;
    private bool _isPopularMode;
    private bool _hasPopularItems;
    private int _popularTotal;
    private bool _popularIncludeGameVersion = true;
    private string _popularError = string.Empty;
    private string _popularNotice = string.Empty;
    private double _progressPercent;
    private string _statusText = "输入关键字搜索模组（例如 fabric api、jei）";
    private string _errorText = string.Empty;
    private bool _isActive;

    /// <summary>
    /// 创建模组下载页 ViewModel。
    /// </summary>
    /// <param name="targetProvider">获取当前目标实例（版本 / 加载器 / mods 目录）。</param>
    /// <param name="apiKeyProvider">获取 CurseForge API Key。</param>
    /// <param name="logSink">日志输出到主界面的日志面板。</param>
    public ModBrowserViewModel(
        Func<ModTarget?> targetProvider,
        Func<string?> apiKeyProvider,
        Action<string> logSink)
    {
        _targetProvider = targetProvider;
        _apiKeyProvider = apiKeyProvider;
        _logSink = logSink;

        SourceOptions = new ObservableCollection<string> { "自动（择优）", "Modrinth", "CurseForge" };

        SearchCommand = new AsyncRelayCommand(_ => SearchAsync()) { ErrorHandler = ShowError };
        RefreshSourceCommand = new AsyncRelayCommand(_ => RefreshSourceStatusAsync()) { ErrorHandler = ShowError };
        DownloadCommand = new AsyncRelayCommand(_ => DownloadAsync()) { ErrorHandler = ShowError };
        LoadMorePopularCommand = new AsyncRelayCommand(_ => LoadMorePopularAsync()) { ErrorHandler = ShowError };
        RetryPopularCommand = new AsyncRelayCommand(_ => EnsurePopularAsync(force: true)) { ErrorHandler = ShowError };
    }

    /// <summary>页面当前是否可见（由主窗口在切换页面时设置）。</summary>
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    /// <summary>搜索结果集合。</summary>
    public ObservableCollection<ModResultViewModel> Results { get; } = new();

    /// <summary>版本集合。</summary>
    public ObservableCollection<ModVersionViewModel> Versions { get; } = new();

    /// <summary>下载源选项（自动 / Modrinth / CurseForge）。</summary>
    public ObservableCollection<string> SourceOptions { get; }

    /// <summary>搜索关键字。</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            var wasEmpty = string.IsNullOrWhiteSpace(_searchText);
            if (!SetProperty(ref _searchText, value))
                return;

            // 清空搜索框：回到热门列表（用缓存，不重新请求）。
            if (!wasEmpty && string.IsNullOrWhiteSpace(value) && !IsSearching)
                _ = EnsurePopularAsync();
        }
    }

    /// <summary>下载源选择索引（0=自动）。</summary>
    public int SourceIndex
    {
        get => _sourceIndex;
        set
        {
            if (SetProperty(ref _sourceIndex, value))
                OnPropertyChanged(nameof(SourcePreference));
        }
    }

    /// <summary>当前下载源偏好。</summary>
    public ModSourcePreference SourcePreference => SourceIndex switch
    {
        1 => ModSourcePreference.Modrinth,
        2 => ModSourcePreference.CurseForge,
        _ => ModSourcePreference.Auto,
    };

    /// <summary>下载源状态文案（探测结果）。</summary>
    public string SourceStatusText
    {
        get => _sourceStatusText;
        private set => SetProperty(ref _sourceStatusText, value);
    }

    /// <summary>
    /// 数据缓存同步时间（来自 MCIM 响应的 <c>sync_at</c>），让用户知道数据的新旧程度。
    /// </summary>
    public string SyncAtText
    {
        get => _syncAtText;
        private set
        {
            if (SetProperty(ref _syncAtText, value))
                OnPropertyChanged(nameof(HasSyncAt));
        }
    }

    /// <summary>是否有缓存同步时间可显示。</summary>
    public bool HasSyncAt => !string.IsNullOrWhiteSpace(SyncAtText);

    /// <summary>当前目标实例描述。</summary>
    public string TargetText
    {
        get
        {
            _target = _targetProvider();
            return _target is null
                ? "未选择实例：请先在「本地启动」页选择一个已安装的版本"
                : $"{_target.DisplayText} · mods：{_target.ModsDirectory}";
        }
    }

    /// <summary>当前选中的搜索结果。</summary>
    public ModResultViewModel? SelectedResult
    {
        get => _selectedResult;
        set
        {
            if (SetProperty(ref _selectedResult, value))
                _ = LoadVersionsAsync(value);
        }
    }

    /// <summary>当前选中的版本。</summary>
    public ModVersionViewModel? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (SetProperty(ref _selectedVersion, value))
                OnPropertyChanged(nameof(DownloadText));
        }
    }

    /// <summary>搜索中。</summary>
    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (SetProperty(ref _isSearching, value))
                OnPropertyChanged(nameof(IsIdle));
        }
    }

    /// <summary>下载中。</summary>
    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            if (SetProperty(ref _isDownloading, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(DownloadText));
            }
        }
    }

    /// <summary>是否空闲。</summary>
    public bool IsIdle => !IsSearching && !IsDownloading && !IsLoadingPopular;

    /// <summary>当前是否展示“热门模组”列表（搜索框为空时）。</summary>
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

    /// <summary>结果区标题：热门模组 / 搜索结果。</summary>
    public string ResultsTitle => IsPopularMode ? "热门模组（按下载量）" : "搜索结果";

    /// <summary>是否正在加载热门列表。</summary>
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

    /// <summary>是否存在热门模组结果。</summary>
    public bool HasPopularItems
    {
        get => _hasPopularItems;
        private set
        {
            if (SetProperty(ref _hasPopularItems, value))
                OnPropertyChanged(nameof(ShowPopularEmpty));
        }
    }

    /// <summary>热门列表加载失败信息（为空表示没有错误）。</summary>
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

    /// <summary>是否有热门列表加载错误（界面显示“重试”按钮）。</summary>
    public bool HasPopularError => !string.IsNullOrWhiteSpace(PopularError);

    /// <summary>轻量提示（例如 429 限流）。</summary>
    public string PopularNotice
    {
        get => _popularNotice;
        private set
        {
            if (SetProperty(ref _popularNotice, value))
                OnPropertyChanged(nameof(HasPopularNotice));
        }
    }

    /// <summary>是否有轻量提示。</summary>
    public bool HasPopularNotice => !string.IsNullOrWhiteSpace(PopularNotice);

    /// <summary>热门列表为空（过滤后仍为空）。</summary>
    public bool ShowPopularEmpty => IsPopularMode && !HasPopularItems && !IsLoadingPopular && !HasPopularError;

    /// <summary>热门列表空状态文案。</summary>
    public string PopularEmptyText => "暂无热门模组，试试搜索";

    /// <summary>是否还能“加载更多”。</summary>
    public bool CanLoadMorePopular
    {
        get => _canLoadMorePopular;
        private set => SetProperty(ref _canLoadMorePopular, value);
    }

    private bool _canLoadMorePopular;

    /// <summary>是否正在加载版本列表。</summary>
    public bool IsLoadingVersions
    {
        get => _isLoadingVersions;
        private set => SetProperty(ref _isLoadingVersions, value);
    }

    /// <summary>下载进度（0~100）。</summary>
    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    /// <summary>状态文案。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>错误文案（为空表示没有错误）。</summary>
    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    /// <summary>是否有错误。</summary>
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    /// <summary>下载按钮文字。</summary>
    public string DownloadText => IsDownloading ? "正在下载…" : "下载到 mods";

    /// <summary>搜索命令。</summary>
    public ICommand SearchCommand { get; }

    /// <summary>重新探测下载源。</summary>
    public ICommand RefreshSourceCommand { get; }

    /// <summary>下载选中版本（含依赖）。</summary>
    public ICommand DownloadCommand { get; }

    /// <summary>热门列表“加载更多”。</summary>
    public ICommand LoadMorePopularCommand { get; }

    /// <summary>热门列表加载失败后重试。</summary>
    public ICommand RetryPopularCommand { get; }

    /// <summary>
    /// 页面显示时刷新目标实例与下载源状态。
    /// </summary>
    public async Task OnShownAsync()
    {
        OnPropertyChanged(nameof(TargetText));
        _target = _targetProvider();

        if (_target is null)
        {
            StatusText = "请先在「本地启动」页选择一个已安装的版本";
            return;
        }

        if (_target.Loader is null)
        {
            StatusText = "当前实例是原版，未安装模组加载器；仍可浏览模组，但下载的模组不会生效";
        }

        await RefreshSourceStatusAsync().ConfigureAwait(true);

        // 搜索框为空时展示热门列表（命中缓存则不发请求）。
        if (string.IsNullOrWhiteSpace(SearchText) && !IsPopularMode)
            await EnsurePopularAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 重新探测两个平台的可达性。
    /// </summary>
    public async Task RefreshSourceStatusAsync()
    {
        try
        {
            EnsureServices();
            SourceStatusText = "正在探测下载源…";
            var status = await _router!.ProbeAsync().ConfigureAwait(true);
            SourceStatusText = status.Text;
        }
        catch (Exception ex)
        {
            SourceStatusText = $"探测失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 执行搜索。
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

        // 搜索框为空 → 回到热门列表（不发起中文搜索请求）。
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            await EnsurePopularAsync().ConfigureAwait(true);
            return;
        }

        EnsureServices();

        IsSearching = true;
        IsPopularMode = false;
        CanLoadMorePopular = false;
        ErrorText = string.Empty;
        StatusText = "正在搜索…";
        Results.Clear();
        Versions.Clear();
        SelectedResult = null;
        SelectedVersion = null;

        try
        {
            var outcome = await _router!.SearchAsync(
                SearchText.Trim(),
                target.GameVersion,
                target.Loader,
                SourcePreference).ConfigureAwait(true);

            foreach (var item in outcome.Items)
                Results.Add(new ModResultViewModel(item));

            StatusText = Results.Count == 0
                ? $"没有找到与 {target.GameVersion} / {target.LoaderText} 兼容的模组"
                : $"找到 {Results.Count} 个结果（{target.GameVersion} · {target.LoaderText}）";

            if (!string.IsNullOrWhiteSpace(outcome.Notice))
                _logSink("[模组] " + outcome.Notice);

            SourceStatusText = await GetStatusTextAsync().ConfigureAwait(true);
            UpdateSyncAtText();

            if (Results.Count > 0)
                SelectedResult = Results[0];
        }
        catch (MineKuaiRateLimitException ex)
        {
            // 429：不重试、不降级，提示用户稍后再试（限流窗口内不会再有请求）。
            StatusText = "麦块搜索限流中，已暂停请求";
            ErrorText = ex.Message;
            SourceStatusText = "麦块 API 限流（429），请稍后再试";
            _logSink("[模组] " + ex.Message);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsSearching = false;
        }
    }

    /// <summary>
    /// 加载选中模组的兼容版本。
    /// </summary>
    public async Task LoadVersionsAsync(ModResultViewModel? result)
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
            var fetch = await _router!.GetVersionsAsync(result.Item, target.GameVersion, target.Loader)
                .ConfigureAwait(true);

            foreach (var version in fetch.Versions)
                Versions.Add(new ModVersionViewModel(version, fetch.Source));

            if (!string.IsNullOrWhiteSpace(fetch.Notice))
                _logSink("[模组] " + fetch.Notice);

            UpdateSyncAtText();

            StatusText = Versions.Count == 0
                ? $"{result.Item.Project.Name} 没有与 {target.GameVersion} / {target.LoaderText} 兼容的版本"
                : $"{result.Item.Project.Name}：{Versions.Count} 个兼容版本（来源 {fetch.Source}）";

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
    /// 加载“热门模组”列表（搜索框为空时展示；命中缓存不重复请求）。
    /// </summary>
    /// <param name="force">是否强制刷新（忽略缓存与当前状态）。</param>
    private async Task EnsurePopularAsync(bool force = false)
    {
        if (IsLoadingPopular || IsDownloading)
            return;

        var target = _targetProvider();
        _target = target;

        if (target is null)
        {
            PopularError = string.Empty;
            return;
        }

        if (!force && IsPopularMode && Results.Count > 0)
            return;

        EnsureServices();
        IsLoadingPopular = true;
        PopularError = string.Empty;
        PopularNotice = string.Empty;

        try
        {
            var result = await _router!.MineKuai
                .GetPopularModsAsync(target.GameVersion, target.Loader, limit: 30, offset: 0,
                    includeGameVersion: true)
                .ConfigureAwait(true);

            Results.Clear();
            foreach (var item in result.Items)
                Results.Add(new ModResultViewModel(item));

            _popularIncludeGameVersion = result.UsedGameVersionFilter;
            _popularTotal = result.Total;
            HasPopularItems = Results.Count > 0;
            IsPopularMode = true;
            CanLoadMorePopular = HasPopularItems && Results.Count < _popularTotal;
            SelectedResult = null;

            StatusText = HasPopularItems
                ? $"热门模组：{Results.Count} 个（{target.GameVersion} · {target.LoaderText}，按下载量排序）"
                : PopularEmptyText;

            if (!result.UsedGameVersionFilter && HasPopularItems)
                PopularNotice = "该游戏版本暂无热门模组，已放宽版本过滤";
        }
        catch (MineKuaiRateLimitException ex)
        {
            // 429：停止请求，保留已有结果，只给轻量提示。
            PopularNotice = "请求过于频繁，稍后再试";
            StatusText = "热门列表限流中，已展示已有结果";
            _logSink("[模组] " + ex.Message);
        }
        catch (Exception ex)
        {
            PopularError = $"热门模组加载失败：{ex.Message}";
            StatusText = PopularError;
            _logSink("[模组] " + PopularError);
        }
        finally
        {
            IsLoadingPopular = false;
        }
    }

    /// <summary>
    /// 热门列表“加载更多”：带 offset 追加下一页。
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
            var page = await _router!.MineKuai
                .GetPopularModsAsync(target.GameVersion, target.Loader, limit: 30, offset: Results.Count,
                    includeGameVersion: _popularIncludeGameVersion)
                .ConfigureAwait(true);

            foreach (var item in page.Items)
                Results.Add(new ModResultViewModel(item));

            _popularTotal = page.Total;
            HasPopularItems = Results.Count > 0;
            CanLoadMorePopular = Results.Count < _popularTotal && page.Items.Count > 0;
            StatusText = $"热门模组：已加载 {Results.Count} / {_popularTotal} 个";
        }
        catch (MineKuaiRateLimitException ex)
        {
            PopularNotice = "请求过于频繁，稍后再试";
            _logSink("[模组] " + ex.Message);
        }
        catch (Exception ex)
        {
            PopularError = $"加载更多失败：{ex.Message}";
            _logSink("[模组] " + PopularError);
        }
        finally
        {
            IsLoadingPopular = false;
        }
    }

    /// <summary>
    /// 下载选中版本（自动解析并下载 required 依赖）。
    /// </summary>
    public async Task DownloadAsync()
    {
        if (IsDownloading || IsSearching)
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
            ErrorText = "请先搜索并选择一个模组版本";
            return;
        }

        EnsureServices();
        IsDownloading = true;
        ErrorText = string.Empty;
        ProgressPercent = 0;

        try
        {
            StatusText = "正在解析依赖…";
            var resolution = await _resolver!.ResolveAsync(
                SelectedResult.Item.Project,
                SelectedVersion.Version,
                target.GameVersion,
                target.Loader).ConfigureAwait(true);

            _logSink($"[模组] 解析到 {resolution.Files.Count} 个文件：" +
                     string.Join("、", resolution.ResolvedProjects));

            var progress = new UiProgress<ModDownloadProgress>(info =>
            {
                ProgressPercent = info.Percent;
                StatusText = $"{info.Stage}（{info.FileIndex}/{info.FileCount}）";
            });

            var report = await _installer!.InstallAsync(
                resolution.Files,
                target.ModsDirectory,
                progress).ConfigureAwait(true);

            ProgressPercent = 100;
            StatusText = $"完成：新增 {report.SavedFiles.Count} 个文件" +
                         (report.SkippedFiles.Count > 0 ? $"，{report.SkippedFiles.Count} 个已存在" : string.Empty) +
                         $" → {target.ModsDirectory}";
            _logSink($"[模组] 安装完成：{string.Join("、", report.SavedFiles.Select(Path.GetFileName))}");
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
    /// 确保服务实例使用当前的 API Key（设置改动后会重建）。
    /// </summary>
    private void EnsureServices()
    {
        var apiKey = _apiKeyProvider();
        if (_router is not null && string.Equals(apiKey, _routerApiKey, StringComparison.Ordinal))
            return;

        _router?.Dispose();

        var config = new ModConfig { CurseForgeApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim() };
        var http = new ModHttp(config);
        http.LogWritten += message => _logSink("[模组] " + message);

        _router = new ModSourceRouter(config, http);
        _resolver = new ModDependencyResolver(_router, config);
        _resolver.LogWritten += message => _logSink("[模组] " + message);
        _installer = new ModInstallService(_router, http, config);
        _routerApiKey = apiKey;
    }

    /// <summary>
    /// 刷新“数据同步时间”文案（MCIM 返回 sync_at 时才有意义）。
    /// </summary>
    private void UpdateSyncAtText()
    {
        var syncAt = _router?.LastSyncAt;
        SyncAtText = syncAt is null
            ? string.Empty
            : $"数据同步时间：{syncAt.Value.ToLocalTime():yyyy-MM-dd HH:mm}（MCIM 缓存）";
    }

    /// <summary>
    /// 取探测状态文案。
    /// </summary>
    private async Task<string> GetStatusTextAsync()
    {
        try
        {
            var status = await _router!.ProbeAsync().ConfigureAwait(true);
            return status.Text;
        }
        catch (Exception)
        {
            return _sourceStatusText;
        }
    }

    /// <summary>
    /// 展示错误信息。
    /// </summary>
    private void ShowError(Exception ex)
    {
        ErrorText = ex is ModDownloadException download
            ? download.Message
            : $"{ex.Message}";

        StatusText = "操作失败，具体原因见上方提示";
        _logSink("[模组] 失败：" + ErrorText);
    }
}

/// <summary>
/// 搜索结果条目。
/// </summary>
public sealed class ModResultViewModel
{
    /// <summary>
    /// 创建结果条目。
    /// </summary>
    /// <param name="item">择优后的结果。</param>
    public ModResultViewModel(ModSearchItem item) => Item = item;

    /// <summary>择优后的结果。</summary>
    public ModSearchItem Item { get; }

    /// <summary>模组名称。</summary>
    public string Name => Item.Project.Name;

    /// <summary>来源平台文字。</summary>
    public string SourceText => Item.Project.SourceText;

    /// <summary>更新时间文字。</summary>
    public string UpdatedText => $"更新于 {Item.Project.UpdatedAtText}";

    /// <summary>简短描述。</summary>
    public string Description => Item.Project.Description;

    /// <summary>选择原因（自动择优的结论）。</summary>
    public string ChosenBecause => Item.ChosenBecause;

    /// <summary>下载量。</summary>
    public string DownloadsText => $"{Item.Project.DownloadsText} 次下载";
}

/// <summary>
/// 版本条目。
/// </summary>
public sealed class ModVersionViewModel
{
    /// <summary>
    /// 创建版本条目。
    /// </summary>
    /// <param name="version">版本信息。</param>
    /// <param name="source">实际来源平台。</param>
    public ModVersionViewModel(ModVersionInfo version, ModSourceKind source)
    {
        Version = version;
        Source = source;
    }

    /// <summary>版本信息。</summary>
    public ModVersionInfo Version { get; }

    /// <summary>实际来源平台。</summary>
    public ModSourceKind Source { get; }

    /// <summary>展示文本。</summary>
    public string DisplayText => Version.DisplayText;

    /// <summary>支持的游戏版本。</summary>
    public string GameVersionsText => string.Join(", ", Version.GameVersions.Take(6));

    /// <summary>支持的加载器。</summary>
    public string LoadersText => string.Join(", ", Version.Loaders);

    /// <summary>文件大小。</summary>
    public string FileSizeText => Version.FileSizeText;

    /// <summary>依赖数量文字。</summary>
    public string DependencyText => Version.Dependencies.Count == 0
        ? "无前置依赖"
        : $"{Version.Dependencies.Count} 个前置依赖";
}
