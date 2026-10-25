using Avalonia.Controls;

namespace DPL.Services;

/// <summary>
/// 根据当前目标框架选择微软登录界面的实现：
/// Windows 版使用内嵌 WebView2 窗口（体验与官方启动器一致），
/// 其他平台退化为“浏览器 + 粘贴回跳网址”的方式。
/// </summary>
public static class MicrosoftLoginUiFactory
{
    /// <summary>
    /// 创建登录界面实现。
    /// </summary>
    /// <param name="owner">主窗口，作为降级方案对话框的父窗口。</param>
    public static IMicrosoftLoginUi Create(Window owner)
    {
#if WINDOWS
        return new WebView2MicrosoftLoginUi(owner);
#else
        return new AvaloniaMicrosoftLoginUi(owner);
#endif
    }
}
