using DPL.Models.Mods;

namespace DPL.Services.Mods;

/// <summary>
/// 依赖解析：从主模组的某个版本出发，递归收集必须（required）依赖，
/// 带最大深度与去重，避免循环依赖导致死循环。
/// </summary>
public sealed class ModDependencyResolver
{
    private readonly ModSourceRouter _router;
    private readonly ModConfig _config;

    /// <summary>
    /// 创建依赖解析器。
    /// </summary>
    /// <param name="router">平台路由器（用于跨平台取版本）。</param>
    /// <param name="config">模组下载配置。</param>
    public ModDependencyResolver(ModSourceRouter router, ModConfig config)
    {
        _router = router;
        _config = config;
    }

    /// <summary>日志回调。</summary>
    public event Action<string>? LogWritten;

    /// <summary>
    /// 解析主模组及其全部必须依赖。
    /// </summary>
    /// <param name="project">主模组信息。</param>
    /// <param name="rootVersion">用户选择的版本。</param>
    /// <param name="gameVersion">游戏版本（依赖过滤条件与主模组一致）。</param>
    /// <param name="loader">加载器（同上）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<ModResolutionResult> ResolveAsync(
        ModProjectSummary project,
        ModVersionInfo rootVersion,
        string gameVersion,
        string? loader,
        CancellationToken cancellationToken = default)
    {
        var files = new List<ModResolvedFile>();
        var resolvedProjects = new List<string>();

        var visitedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ProjectKey(rootVersion.Source, rootVersion.ProjectId),
        };
        var visitedVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var queue = new Queue<PendingDependency>();
        queue.Enqueue(new PendingDependency(project.Name, rootVersion, 0, false));

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = queue.Dequeue();

            if (!visitedVersions.Add(VersionKey(current.Version)))
                continue;

            files.Add(new ModResolvedFile(
                current.Version.Source,
                current.ProjectName,
                current.Version,
                current.Version.DownloadUrl,
                current.IsDependency));

            resolvedProjects.Add($"{current.ProjectName} {current.Version.VersionNumber}");

            if (current.Depth >= _config.MaxDependencyDepth)
            {
                if (current.Version.Dependencies.Any(d => d.Required))
                    Log($"达到最大依赖深度 {_config.MaxDependencyDepth}，停止继续展开 {current.ProjectName}");

                continue;
            }

            foreach (var dependency in current.Version.Dependencies.Where(d => d.Required))
            {
                if (string.IsNullOrWhiteSpace(dependency.ProjectId) && string.IsNullOrWhiteSpace(dependency.VersionId))
                    continue;

                var key = ProjectKey(dependency.Source, dependency.ProjectId);
                if (!visitedProjects.Add(key))
                {
                    Log($"依赖 {dependency.ProjectId} 已处理过，跳过（避免重复/循环）");
                    continue;
                }

                var resolved = await ResolveDependencyAsync(dependency, gameVersion, loader, cancellationToken)
                    .ConfigureAwait(false);

                if (resolved is null)
                {
                    Log($"依赖 {dependency.ProjectId}（{dependency.Name ?? "未知"}）没有找到兼容版本，已忽略");
                    continue;
                }

                queue.Enqueue(new PendingDependency(
                    dependency.Name ?? resolved.ProjectId,
                    resolved,
                    current.Depth + 1,
                    true));
            }
        }

        return new ModResolutionResult(files, resolvedProjects);
    }

    /// <summary>
    /// 解析单个依赖：优先用它指定的版本 ID，否则取最新的兼容版本。
    /// </summary>
    private async Task<ModVersionInfo?> ResolveDependencyAsync(
        ModDependency dependency,
        string gameVersion,
        string? loader,
        CancellationToken cancellationToken)
    {
        var source = _router.GetSource(dependency.Source);

        if (!source.IsConfigured)
        {
            Log($"依赖平台 {source.DisplayName} 未配置，跳过 {dependency.ProjectId}");
            return null;
        }

        if (string.IsNullOrWhiteSpace(dependency.ProjectId))
            return null;

        var versions = await source
            .GetVersionsAsync(dependency.ProjectId, gameVersion, loader, cancellationToken)
            .ConfigureAwait(false);

        if (versions.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(dependency.VersionId))
        {
            var exact = versions.FirstOrDefault(v => string.Equals(v.VersionId, dependency.VersionId, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
                return exact;

            Log($"依赖 {dependency.ProjectId} 指定的版本 {dependency.VersionId} 与当前实例不兼容，改用最新兼容版本");
        }

        return versions[0];
    }

    /// <summary>
    /// 写日志。
    /// </summary>
    private void Log(string message)
    {
        try
        {
            LogWritten?.Invoke(message);
        }
        catch (Exception)
        {
            // 忽略日志订阅者异常。
        }
    }

    /// <summary>项目唯一键。</summary>
    private static string ProjectKey(ModSourceKind source, string projectId) => $"{source}|{projectId}";

    /// <summary>版本唯一键。</summary>
    private static string VersionKey(ModVersionInfo version) => $"{version.Source}|{version.VersionId}";

    /// <summary>待处理的依赖队列项。</summary>
    private sealed record PendingDependency(
        string ProjectName,
        ModVersionInfo Version,
        int Depth,
        bool IsDependency);
}
