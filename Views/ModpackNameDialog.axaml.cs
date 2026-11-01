using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DPL.Views;

/// <summary>
/// 整合包实例命名窗口：确认名称前由 ViewModel 校验非法字符与重名。
/// </summary>
public partial class ModpackNameDialog : Window
{
    private readonly Func<string, string?> _validate;

    /// <summary>供 XAML 设计器使用的无参构造函数。</summary>
    public ModpackNameDialog()
        : this(string.Empty, _ => null)
    {
    }

    /// <summary>
    /// 创建命名窗口。
    /// </summary>
    /// <param name="defaultName">默认实例名（通常是整合包文件名）。</param>
    /// <param name="validate">校验名称：返回 null 表示有效，否则返回错误提示。</param>
    public ModpackNameDialog(string defaultName, Func<string, string?> validate)
    {
        InitializeComponent();

        _validate = validate;
        NameBox.Text = defaultName ?? string.Empty;
    }

    /// <inheritdoc />
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        NameBox.Focus();
        NameBox.CaretIndex = NameBox.Text?.Length ?? 0;
    }

    /// <summary>确认名称：校验通过后关闭窗口并返回名称。</summary>
    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? string.Empty;

        string? error;
        try
        {
            error = _validate(name);
        }
        catch (Exception ex)
        {
            error = $"校验实例名称失败：{ex.Message}";
        }

        if (error is not null)
        {
            ShowError(error);
            return;
        }

        Close(name);
    }

    /// <summary>取消安装。</summary>
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    /// <summary>显示错误并重新聚焦输入框。</summary>
    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
        NameBox.Focus();
    }
}
