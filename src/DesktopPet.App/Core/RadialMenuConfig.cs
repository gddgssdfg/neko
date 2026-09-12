namespace DesktopPet.App.Core;

/// <summary>
/// 径向菜单的用户级覆盖配置（宿主级，跨插件全局）。
/// v0.1.71：让用户可自由调整右键径向菜单里插件的显示顺序、显隐与总数量上限。
/// 覆盖层只存在于宿主（不改 Core API）；插件声明的 RadialSlot.Order 是默认基准。
/// </summary>
public sealed class RadialMenuConfig
{
    /// <summary>全局可见槽位总数上限（排序 + 显隐之后取前 N 个）。0 = 不限制。</summary>
    public int MaxVisible { get; set; }

    /// <summary>用户覆盖顺序：slotId → 显式顺序（越小越靠前）。缺省时回退插件声明的 Order。</summary>
    public Dictionary<string, int> Order { get; set; } = new();

    /// <summary>显隐覆盖：slotId → 是否显示（false = 隐藏）。缺省时 = 显示。</summary>
    public Dictionary<string, bool> Visible { get; set; } = new();
}
