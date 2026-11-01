using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DPL.Views;

/// <summary>
/// 轻量确认弹窗（代码构建，避免为一次性提示新增 XAML）。
/// </summary>
public static class ConfirmDialog
{
    /// <summary>
    /// 显示确认弹窗并等待用户选择。
    /// </summary>
    /// <param name="owner">父窗口。</param>
    /// <param name="title">标题。</param>
    /// <param name="message">正文。</param>
    /// <param name="confirmText">确认按钮文字。</param>
    /// <param name="cancelText">取消按钮文字。</param>
    public static async Task<bool> ShowAsync(
        Window owner,
        string title,
        string message,
        string confirmText = "确定",
        string cancelText = "取消")
    {
        var result = false;

        var dialog = new Window
        {
            Title = title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };

        var confirmButton = new Button
        {
            Content = confirmText,
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };

        var cancelButton = new Button
        {
            Content = cancelText,
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };

        confirmButton.Click += (_, _) =>
        {
            result = true;
            dialog.Close();
        };

        cancelButton.Click += (_, _) => dialog.Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(confirmButton);

        var panel = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 18,
        };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(buttons);

        dialog.Content = panel;
        await dialog.ShowDialog(owner);
        return result;
    }
}
