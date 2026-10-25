using System.Windows.Input;
using Avalonia.Media;
using DPL.Models;

namespace DPL.ViewModels;

/// <summary>
/// 版本列表中的单个条目，封装显示信息与“安装 / 启动”操作。
/// </summary>
public sealed class VersionItemViewModel : ViewModelBase
{
    private bool _isInstalled;
    private bool _isBusy;
    private string _statusText = string.Empty;

    /// <summary>
    /// 创建版本条目。
    /// </summary>
    /// <param name="info">版本数据模型。</param>
    /// <param name="installHandler">安装操作；为 null 表示该列表不允许安装。</param>
    /// <param name="launchHandler">启动操作；为 null 表示该列表不允许启动。</param>
    /// <param name="openFolderHandler">打开该版本文件夹的操作；为 null 表示不支持。</param>
    /// <param name="openInstallHandler">打开“安装/模组加载器选择”页面的操作；为 null 表示不支持。</param>
    public VersionItemViewModel(
        MinecraftVersionInfo info,
        Func<VersionItemViewModel, Task>? installHandler,
        Func<VersionItemViewModel, Task>? launchHandler,
        Action<VersionItemViewModel>? openFolderHandler = null,
        Func<VersionItemViewModel, Task>? openInstallHandler = null)
    {
        Name = info.Name;
        Kind = info.Kind;
        ReleaseTime = info.ReleaseTime;
        _isInstalled = info.IsInstalled;

        TypeText = Kind switch
        {
            GameVersionKind.Release => "正式版",
            GameVersionKind.Snapshot => "快照",
            GameVersionKind.Old => "远古版本",
            _ => "自定义",
        };

        CanInstall = installHandler is not null;
        CanLaunch = launchHandler is not null;
        CanOpenFolder = openFolderHandler is not null;
        CanOpenInstall = openInstallHandler is not null;

        InstallCommand = new AsyncRelayCommand(
            _ => installHandler is null ? Task.CompletedTask : installHandler(this));
        LaunchCommand = new AsyncRelayCommand(
            _ => launchHandler is null ? Task.CompletedTask : launchHandler(this));
        OpenFolderCommand = new RelayCommand(
            _ => openFolderHandler?.Invoke(this));
        OpenInstallCommand = new AsyncRelayCommand(
            _ => openInstallHandler is null ? Task.CompletedTask : openInstallHandler(this));
    }

    /// <summary>版本名称。</summary>
    public string Name { get; }

    /// <summary>版本分类。</summary>
    public GameVersionKind Kind { get; }

    /// <summary>版本发布时间。</summary>
    public DateTimeOffset ReleaseTime { get; }

    /// <summary>分类的中文名称。</summary>
    public string TypeText { get; }

    /// <summary>是否是正式版（供标签样式的类选择器使用）。</summary>
    public bool IsRelease => Kind == GameVersionKind.Release;

    /// <summary>是否是快照（供标签样式的类选择器使用）。</summary>
    public bool IsSnapshot => Kind == GameVersionKind.Snapshot;

    /// <summary>是否是远古版本（供标签样式的类选择器使用）。</summary>
    public bool IsOld => Kind == GameVersionKind.Old;

    /// <summary>发布时间的显示文本，本地无法确定的版本显示为短横线。</summary>
    public string ReleaseTimeText => ReleaseTime.Year <= 1
        ? "—"
        : ReleaseTime.LocalDateTime.ToString("yyyy-MM-dd");

    /// <summary>是否允许执行安装。</summary>
    public bool CanInstall { get; }

    /// <summary>是否允许执行启动。</summary>
    public bool CanLaunch { get; }

    /// <summary>是否支持打开该版本的文件夹。</summary>
    public bool CanOpenFolder { get; }

    /// <summary>是否支持打开“安装/模组加载器选择”页面。</summary>
    public bool CanOpenInstall { get; }

    /// <summary>该版本是否已安装到本地游戏目录。</summary>
    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (SetProperty(ref _isInstalled, value))
            {
                OnPropertyChanged(nameof(InstallButtonVisible));
                OnPropertyChanged(nameof(LaunchButtonVisible));
                OnPropertyChanged(nameof(OpenFolderVisible));
            }
        }
    }

    /// <summary>安装按钮是否可见（未安装且允许安装）。</summary>
    public bool InstallButtonVisible => CanInstall && !IsInstalled;

    /// <summary>启动按钮是否可见（已安装且允许启动）。</summary>
    public bool LaunchButtonVisible => CanLaunch && IsInstalled;

    /// <summary>“打开版本文件夹”按钮是否可见（已安装且支持打开）。</summary>
    public bool OpenFolderVisible => CanOpenFolder && IsInstalled;

    /// <summary>该条目是否正在执行安装或启动操作。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
                OnPropertyChanged(nameof(IsIdle));
        }
    }

    /// <summary>是否处于空闲状态（用于控制操作按钮是否可点击）。</summary>
    public bool IsIdle => !IsBusy;

    /// <summary>条目上的状态提示文字，例如“下载中…”。</summary>
    public string StatusText
    {
        get => _statusText;
        set
        {
            if (SetProperty(ref _statusText, value))
                OnPropertyChanged(nameof(HasStatusText));
        }
    }

    /// <summary>是否存在状态提示文字。</summary>
    public bool HasStatusText => !string.IsNullOrEmpty(StatusText);

    /// <summary>安装命令。</summary>
    public ICommand InstallCommand { get; }

    /// <summary>启动命令。</summary>
    public ICommand LaunchCommand { get; }

    /// <summary>打开该版本所在文件夹的命令。</summary>
    public ICommand OpenFolderCommand { get; }

    /// <summary>打开“安装/模组加载器选择”页面的命令。</summary>
    public ICommand OpenInstallCommand { get; }
}
