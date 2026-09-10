namespace DesktopPet.Core;

/// <summary>径向菜单槽位</summary>
public sealed record RadialSlot
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    /// <summary>图标 key（宿主解析为内置图标）</summary>
    public string Icon { get; init; } = "default";
    /// <summary>越小越靠前（置顶）</summary>
    public int Order { get; init; } = 100;
    /// <summary>点击回调</summary>
    public required Action OnInvoke { get; init; }
}

/// <summary>托盘菜单项</summary>
public sealed record TrayMenuItem
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public bool IsSeparator { get; init; }
    public Action? OnClick { get; init; }
}

/// <summary>设置页（宿主提供承载控件）</summary>
public interface ISettingsPage
{
    /// <summary>页面 id</summary>
    string Id { get; }
    /// <summary>设置窗口左侧标题</summary>
    string Title { get; }
    /// <summary>返回页面内容控件</summary>
    object BuildContent();
}

/// <summary>界面注册 API（径向菜单 / 托盘 / 设置页）</summary>
public interface IUIApi
{
    /// <summary>注册径向菜单槽位</summary>
    void RegisterRadialSlot(RadialSlot slot);

    /// <summary>注册托盘菜单项</summary>
    void RegisterTrayMenuItem(TrayMenuItem item);

    /// <summary>注册设置页</summary>
    void RegisterSettingsPage(ISettingsPage page);

    /// <summary>撤销某插件注册的全部 UI（卸载时调用）</summary>
    void UnregisterAll(string scopeId);
}
