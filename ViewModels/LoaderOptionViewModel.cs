using System.Windows.Input;
using DPL.Models;

namespace DPL.ViewModels;

/// <summary>
/// 安装页面里的一张“模组加载器”选项卡片。
/// </summary>
public sealed class LoaderOptionViewModel : ViewModelBase
{
    private bool _isSelected;

    /// <summary>
    /// 创建加载器选项。
    /// </summary>
    /// <param name="kind">加载器类型。</param>
    /// <param name="title">卡片标题。</param>
    /// <param name="badge">右上角徽标文字；为空表示不显示。</param>
    /// <param name="description">卡片说明。</param>
    /// <param name="selectCommand">点击卡片时的选择命令。</param>
    public LoaderOptionViewModel(
        ModLoaderKind kind,
        string title,
        string badge,
        string description,
        ICommand selectCommand)
    {
        Kind = kind;
        Title = title;
        Badge = badge;
        Description = description;
        SelectCommand = selectCommand;
    }

    /// <summary>加载器类型。</summary>
    public ModLoaderKind Kind { get; }

    /// <summary>卡片标题。</summary>
    public string Title { get; }

    /// <summary>右上角徽标文字（例如“推荐”）。</summary>
    public string Badge { get; }

    /// <summary>是否存在徽标。</summary>
    public bool HasBadge => !string.IsNullOrWhiteSpace(Badge);

    /// <summary>卡片说明。</summary>
    public string Description { get; }

    /// <summary>点击卡片的选择命令。</summary>
    public ICommand SelectCommand { get; }

    /// <summary>该卡片当前是否被选中。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetProperty(ref _isSelected, value))
                return;

            OnPropertyChanged(nameof(SelectionText));
        }
    }

    /// <summary>选中状态的文字提示。</summary>
    public string SelectionText => _isSelected ? "已选择" : string.Empty;
}
