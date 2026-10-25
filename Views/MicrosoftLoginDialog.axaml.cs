using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DPL.Views;

/// <summary>
/// 微软登录辅助窗口：负责引导用户在浏览器中登录，并回收浏览器跳转后的网址。
/// 之所以需要这一步：CmlLib 的 .NET 版本不提供内嵌登录界面（默认 WebUI 实现会直接抛异常）。
/// </summary>
public partial class MicrosoftLoginDialog : Window
{
    private readonly Func<string, string?> _validate;
    private readonly Action _reopenBrowser;

    /// <summary>
    /// 供 XAML 设计器使用的无参构造函数。
    /// </summary>
    public MicrosoftLoginDialog()
        : this(_ => null, () => { })
    {
    }

    /// <summary>
    /// 创建微软登录窗口。
    /// </summary>
    /// <param name="validate">校验粘贴的网址：返回 null 表示有效，返回字符串表示错误提示。</param>
    /// <param name="reopenBrowser">重新打开 Microsoft 登录页的动作。</param>
    public MicrosoftLoginDialog(Func<string, string?> validate, Action reopenBrowser)
    {
        InitializeComponent();
        _validate = validate;
        _reopenBrowser = reopenBrowser;
    }

    /// <summary>
    /// 打开窗口时把焦点放到输入框，方便用户直接 Ctrl+V。
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        UrlBox.Focus();
    }

    /// <summary>
    /// 点击「完成」：校验网址，通过则关闭窗口并返回 true。
    /// </summary>
    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        var text = UrlBox.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            ShowError("请先把浏览器地址栏里的网址粘贴到输入框");
            return;
        }

        var error = _validate(text);
        if (error is not null)
        {
            ShowError(error);
            return;
        }

        Close(true);
    }

    /// <summary>
    /// 从剪贴板读取网址并填入输入框。
    /// </summary>
    private async void OnPasteFromClipboard(object? sender, RoutedEventArgs e)
    {
        try
        {
            var clipboard = Clipboard;
            if (clipboard is null)
            {
                ShowError("无法访问剪贴板，请手动粘贴");
                return;
            }

#pragma warning disable CS0618 // 旧版剪贴板 API 更通用，这里沿用
            var text = await clipboard.GetTextAsync();
#pragma warning restore CS0618
            if (string.IsNullOrWhiteSpace(text))
            {
                ShowError("剪贴板里没有文本，请先在浏览器地址栏复制网址");
                return;
            }

            UrlBox.Text = text.Trim();
            ErrorText.IsVisible = false;
        }
        catch (Exception ex)
        {
            ShowError($"读取剪贴板失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 重新打开 Microsoft 登录页。
    /// </summary>
    private void OnReopenBrowser(object? sender, RoutedEventArgs e)
    {
        ErrorText.IsVisible = false;
        _reopenBrowser();
    }

    /// <summary>
    /// 取消登录。
    /// </summary>
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    /// <summary>
    /// 显示错误提示。
    /// </summary>
    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
