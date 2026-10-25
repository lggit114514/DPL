using System.Net;
using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.Auth.Microsoft.Sessions;
using DPL.Models;
using XboxAuthNet.Game.Accounts;

namespace DPL.Services;

/// <summary>
/// <see cref="IMicrosoftAuthService"/> 的默认实现。
/// 登录、令牌刷新、账号持久化全部委托给 CmlLib.Core.Auth.Microsoft（JELoginHandler + 账号管理器），
/// 账号数据保存在 <c>%AppData%/DPL/microsoft-accounts.json</c>，因此下次启动仍可选择已登录的账号。
/// </summary>
public sealed class MicrosoftAuthService : IMicrosoftAuthService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _accountsFilePath;
    private readonly IMicrosoftLoginUi _loginUi;

    private JELoginHandler? _loginHandler;

    /// <summary>
    /// 创建微软登录服务并确定账号文件路径。
    /// </summary>
    /// <param name="loginUi">
    /// 登录界面实现。CmlLib 的 .NET 版没有内置登录 WebUI，必须由启动器自己提供，
    /// 否则登录时会抛出 “Current platform does not support to provide default WebUI.”。
    /// </param>
    public MicrosoftAuthService(IMicrosoftLoginUi loginUi)
    {
        _loginUi = loginUi;

        _httpClient = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromMinutes(5),
        };

        AppPaths.EnsureDirectory(AppPaths.RoamingDirectory);
        _accountsFilePath = AppPaths.AccountsFile;
    }

    /// <inheritdoc />
    public string AccountsFilePath => _accountsFilePath;

    /// <inheritdoc />
    public Task<IReadOnlyList<AccountProfile>> GetSavedAccountsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var accounts = new List<AccountProfile>();

        try
        {
            accounts.AddRange(EnumerateSavedAccounts(GetHandler()).Select(pair => pair.Profile));
        }
        catch (Exception)
        {
            // 账号文件损坏或格式不兼容时，视为没有已保存的账号，不影响离线模式使用。
            return Task.FromResult<IReadOnlyList<AccountProfile>>(Array.Empty<AccountProfile>());
        }

        return Task.FromResult<IReadOnlyList<AccountProfile>>(
            accounts.OrderByDescending(a => a.LastAccess ?? DateTime.MinValue).ToList());
    }

    /// <summary>
    /// 枚举账号管理器中"真正登录过"的账号（即带有 Minecraft 用户名的账号）。
    /// </summary>
    /// <param name="handler">登录处理器。</param>
    private static IEnumerable<(JEGameAccount Account, AccountProfile Profile)> EnumerateSavedAccounts(
        JELoginHandler handler)
    {
        foreach (var account in handler.AccountManager.GetAccounts())
        {
            if (account is not JEGameAccount gameAccount)
                continue;

            var username = gameAccount.Profile?.Username;
            if (string.IsNullOrWhiteSpace(username))
            {
                // 从未成功登录（或已退出登录并清空令牌）的账号不显示。
                continue;
            }

            yield return (gameAccount, new AccountProfile
            {
                Kind = AccountKind.Microsoft,
                Name = username,
                Identifier = gameAccount.Identifier,
                Uuid = gameAccount.Profile?.UUID,
                Gamertag = gameAccount.Gamertag,
                LastAccess = gameAccount.LastAccess,
            });
        }
    }

    /// <inheritdoc />
    public async Task<AccountProfile> LoginInteractivelyAsync(
        Action<string>? statusProgress = null,
        CancellationToken cancellationToken = default)
    {
        var handler = GetHandler();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        MSession session;
        try
        {
            statusProgress?.Invoke("请在弹出的登录窗口中完成 Microsoft 账号登录…");

            // 注意：这里不能用 handler.AuthenticateInteractively()——
            // 它走的是 CmlLib 的默认 WebUI，而 .NET 版库并没有提供该实现（会抛
            // “Current platform does not support to provide default WebUI.”）。
            // 因此改用 Authenticator 组合 API，并显式注入启动器自己的 WebUI。
            var authenticator = handler.CreateAuthenticatorWithNewAccount(cancellationToken);
            authenticator.AddMicrosoftOAuthForJE(oauth => oauth.Interactive(codeFlow =>
                codeFlow.WithWebUI(new InjectWebUi(_loginUi))));
            authenticator.AddXboxAuthForJE(xbox => xbox.Basic());
            authenticator.AddJEAuthenticator();

            session = await authenticator.ExecuteForLauncherAsync().ConfigureAwait(false);
            handler.AccountManager.SaveAccounts();
        }
        finally
        {
            _gate.Release();
        }

        var accounts = await GetSavedAccountsAsync(cancellationToken).ConfigureAwait(false);
        var matched = accounts.FirstOrDefault(a =>
            string.Equals(a.Name, session.Username, StringComparison.OrdinalIgnoreCase));

        return matched ?? new AccountProfile
        {
            Kind = AccountKind.Microsoft,
            Name = session.Username ?? "未知账号",
            Uuid = session.UUID,
            LastAccess = DateTime.Now,
        };
    }

    /// <inheritdoc />
    public async Task<MSession> AuthenticateAsync(string? accountIdentifier, CancellationToken cancellationToken = default)
    {
        var handler = GetHandler();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 只在“真正登录过”的账号里查找：空账号（从未登录成功）没有意义，应提示用户先登录。
            var savedAccounts = EnumerateSavedAccounts(handler).ToList();
            if (savedAccounts.Count == 0)
            {
                throw new InvalidOperationException(
                    "尚未登录任何微软账号。请在“设置 → 账号”中点击“登录微软账号”。");
            }

            var selected = savedAccounts.FirstOrDefault(pair =>
                               !string.IsNullOrWhiteSpace(accountIdentifier) &&
                               string.Equals(pair.Profile.Identifier, accountIdentifier, StringComparison.OrdinalIgnoreCase));
            if (selected.Account is null)
                selected = savedAccounts[0];

            var account = selected.Account;
            var accountName = selected.Profile.Name;

            try
            {
                // 用保存的令牌刷新会话：登录状态仍有效时无需任何交互。
                var session = await handler.AuthenticateSilently(account, cancellationToken).ConfigureAwait(false);
                handler.AccountManager.SaveAccounts();
                return session;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"微软账号（{accountName}）的登录状态已失效，请重新登录。原始错误：{ex.Message}", ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SignOutAsync(string? accountIdentifier, CancellationToken cancellationToken = default)
    {
        var handler = GetHandler();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var account = FindAccount(handler, accountIdentifier);
            if (account is null)
                return;

            await handler.Signout(account, cancellationToken).ConfigureAwait(false);
            handler.AccountManager.SaveAccounts();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 释放内部使用的 HttpClient。
    /// </summary>
    public void Dispose()
    {
        _httpClient.Dispose();
        _gate.Dispose();
    }

    /// <summary>
    /// 获取（必要时创建）登录处理器，账号列表由 JSON 文件持久化。
    /// </summary>
    private JELoginHandler GetHandler()
    {
        if (_loginHandler is not null)
            return _loginHandler;

        try
        {
            _loginHandler = new JELoginHandlerBuilder()
                .WithHttpClient(_httpClient)
                .WithAccountManager(_accountsFilePath)
                .Build();
        }
        catch (Exception)
        {
            // 账号文件损坏时回退为默认（内存）账号管理器，保证离线模式不受影响。
            _loginHandler = new JELoginHandlerBuilder()
                .WithHttpClient(_httpClient)
                .WithAccountManager(new InMemoryXboxGameAccountManager(JEGameAccount.FromSessionStorage))
                .Build();
        }

        return _loginHandler;
    }

    /// <summary>
    /// 按唯一标识查找账号；未指定时取最近使用的账号。
    /// </summary>
    /// <param name="handler">登录处理器。</param>
    /// <param name="accountIdentifier">账号唯一标识，可为 null。</param>
    private static IXboxGameAccount? FindAccount(JELoginHandler handler, string? accountIdentifier)
    {
        var accounts = handler.AccountManager.GetAccounts();

        if (!string.IsNullOrWhiteSpace(accountIdentifier) &&
            accounts.TryGetAccount(accountIdentifier, out var account) && account is not null)
        {
            return account;
        }

        return handler.AccountManager.GetDefaultAccount();
    }
}
