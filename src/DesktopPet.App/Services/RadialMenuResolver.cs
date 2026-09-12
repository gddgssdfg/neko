using DesktopPet.App.Core;
using DesktopPet.Core;

namespace DesktopPet.App.Services;

/// <summary>
/// 径向菜单槽位的「用户配置 → 最终展示列表」解析器（纯静态函数，无状态、无 IO，便于单测）。
/// 覆盖层只存在于宿主（App），不改 Core API；插件声明的 RadialSlot.Order 是默认基准。
/// v0.1.71 新增。
/// </summary>
public static class RadialMenuResolver
{
    /// <summary>某槽位的有效顺序：用户覆盖优先，否则回退插件声明的 Order。</summary>
    public static int EffectiveOrder(RadialSlot slot, RadialMenuConfig cfg)
        => cfg.Order.TryGetValue(slot.Id, out var o) ? o : slot.Order;

    /// <summary>某槽位是否可见（默认可见）。</summary>
    public static bool IsVisible(RadialSlot slot, RadialMenuConfig cfg)
        => cfg.Visible.GetValueOrDefault(slot.Id, true);

    /// <summary>
    /// 主入口：过滤隐藏 → 按有效顺序排序（平局按 Id 稳定）→ 截断到 MaxVisible（0 = 不限）。
    /// 返回最终要在径向菜单里渲染的槽位列表。
    /// </summary>
    public static List<RadialSlot> Resolve(IEnumerable<RadialSlot> all, RadialMenuConfig cfg)
    {
        var visible = all.Where(s => IsVisible(s, cfg));
        var ordered = visible
            .OrderBy(s => EffectiveOrder(s, cfg))
            .ThenBy(s => s.Id, StringComparer.Ordinal);
        var list = ordered.ToList();
        if (cfg.MaxVisible > 0 && list.Count > cfg.MaxVisible)
            list.RemoveRange(cfg.MaxVisible, list.Count - cfg.MaxVisible);
        return list;
    }

    /// <summary>
    /// 用户上移/下移后调用：把「当前可见有序列表」物化回 cfg.Order（index 0..n-1），
    /// 冻结用户顺序，跨重启稳定。只写传入的可见项，隐藏项保留原 Order 以支持取消隐藏后恢复原位。
    /// </summary>
    public static void FreezeOrder(IReadOnlyList<RadialSlot> orderedVisible, RadialMenuConfig cfg)
    {
        for (int i = 0; i < orderedVisible.Count; i++)
            cfg.Order[orderedVisible[i].Id] = i;
    }

    /// <summary>
    /// 清理残留：删除 cfg.Order / cfg.Visible 中已不存在的 slotId（插件卸载/改名后）。
    /// 保存前调用，避免配置无限膨胀。
    /// </summary>
    public static void Prune(IEnumerable<RadialSlot> all, RadialMenuConfig cfg)
    {
        var alive = new HashSet<string>(all.Select(s => s.Id), StringComparer.Ordinal);
        foreach (var key in cfg.Order.Keys.Where(k => !alive.Contains(k)).ToList())
            cfg.Order.Remove(key);
        foreach (var key in cfg.Visible.Keys.Where(k => !alive.Contains(k)).ToList())
            cfg.Visible.Remove(key);
    }
}
