using System.Windows;
using DesktopPet.Core;

namespace DesktopPet.App.Services;

/// <summary>
/// UI 注册 API：管理插件注册的径向菜单槽位 / 托盘项 / 设置页。
/// 插件卸载时按 scopeId 批量撤销。
/// <para>v0.1.84：注册改为「按 Id 幂等（upsert）」—— 同一 scope 下同 Id 再次注册即<b>原地更新</b>那一条，
/// 而不是追加。这样插件可在状态变化点（如开关切换 / 设置保存 / 等级提升）重新注册同一 Id 来刷新文案，
/// 既不会产生重复槽位，也无需先 <see cref="UnregisterAll"/>（后者会连同该 scope 的其它注册一起清掉）。</para>
/// <para>线程安全：三张注册表的所有读写都在 <see cref="_gate"/> 上串行化。插件多在 UI 线程调用注册，
/// 而 <see cref="AllRadialSlots"/> 等读取发生在渲染时；二者可能来自不同线程（如未来插件在定时器线程刷新），
/// 加锁可避免 <c>Dictionary</c>/<c>List</c> 的并发修改异常或读到半更新状态。</para>
/// </summary>
public sealed class UIApiImpl : IUIApi
{
    private readonly Dictionary<string, List<RadialSlot>> _radialSlots = new();
    private readonly Dictionary<string, List<TrayMenuItem>> _trayItems = new();
    private readonly Dictionary<string, List<ISettingsPage>> _pages = new();
    private readonly Func<RadialSlot, object?> _slotViewFactory; // 预留

    /// <summary>保护三张注册表的所有读写（注册 / 读取快照 / 撤销）。</summary>
    private readonly object _gate = new();

    public IReadOnlyList<RadialSlot> AllRadialSlots
    {
        get { lock (_gate) return _radialSlots.Values.SelectMany(v => v).OrderBy(s => s.Order).ToList(); }
    }

    public IReadOnlyList<TrayMenuItem> AllTrayItems
    {
        get { lock (_gate) return _trayItems.Values.SelectMany(v => v).ToList(); }
    }

    public IReadOnlyList<ISettingsPage> AllSettingsPages
    {
        get { lock (_gate) return _pages.Values.SelectMany(v => v).ToList(); }
    }

    public UIApiImpl(Func<RadialSlot, object?> slotViewFactory)
    {
        _slotViewFactory = slotViewFactory;
    }

    public void RegisterRadialSlot(RadialSlot slot)
    {
        RequireScope();
        bool changed;
        lock (_gate) changed = AddOrReplace(_radialSlots, _currentScope!, slot, s => s.Id, RadialVisualChanged);
        if (changed) RaiseRadialRegistryChanged();
    }

    public void RegisterTrayMenuItem(TrayMenuItem item)
    {
        RequireScope();
        lock (_gate) AddOrReplace(_trayItems, _currentScope!, item, i => i.Id);
    }

    public void RegisterSettingsPage(ISettingsPage page)
    {
        RequireScope();
        lock (_gate) AddOrReplace(_pages, _currentScope!, page, p => p.Id);
    }

    /// <summary>线程局部当前 scope（ScopedUiApi.PushScope 进入时设置）</summary>
    [ThreadStatic] private static string? _currentScope;

    /// <summary>进入插件 scope，期间所有 Register* 都归到该 scope。using 结束时自动退出。</summary>
    public IDisposable PushScope(string scopeId)
    {
        var prev = _currentScope;
        _currentScope = scopeId;
        return new ScopeToken(prev);
    }
    private sealed class ScopeToken : IDisposable
    {
        private readonly string? _prev;
        public ScopeToken(string? _prev) { this._prev = _prev; }
        public void Dispose() => _currentScope = _prev;
    }

    private static void RequireScope()
    {
        if (_currentScope is null)
            throw new InvalidOperationException("UI 注册必须在插件 OnLoad 内通过 ScopedUiApi 调用");
    }

    public void UnregisterAll(string scopeId)
    {
        bool radialRemoved;
        lock (_gate)
        {
            radialRemoved = _radialSlots.Remove(scopeId);   // 只在确实有径向槽位被移除时才通知
            _trayItems.Remove(scopeId);
            _pages.Remove(scopeId);
        }
        if (radialRemoved) RaiseRadialRegistryChanged();
    }

    /// <summary>
    /// v0.1.89：径向注册表【真变更】事件（宿主内部事件，<b>不进 DesktopPet.Core 公共契约</b>——Core 只增不改）。
    /// <para>触发时机：新增槽位 / 移除某 scope 的径向槽位 / 同 Id 重注册且<b>可见字段</b>（Label·Icon·Order）确有变化。</para>
    /// <para><b>值相同不发</b>：upsert 只换了回调委托或原样重注册（可见字段不变）时不触发——这是「设置页径向页
    /// 不过度重建」的前提。</para>
    /// <para>⚠️ 可能在插件线程触发（插件在定时器/回调里刷新槽位文案）；订阅方须自行 marshal 到 UI 线程。</para>
    /// </summary>
    internal event Action? RadialRegistryChanged;

    private void RaiseRadialRegistryChanged() => RadialRegistryChanged?.Invoke();

    /// <summary>径向槽位「可见字段」是否变化。Id 为匹配键必相同；OnInvoke 委托<b>不参与</b>比较——
    /// 纯换回调无任何视觉变化，不应导致设置页重建。</summary>
    private static bool RadialVisualChanged(RadialSlot a, RadialSlot b)
        => !string.Equals(a.Label, b.Label, StringComparison.Ordinal)
           || !string.Equals(a.Icon, b.Icon, StringComparison.Ordinal)
           || a.Order != b.Order;

    /// <summary>
    /// 把 <paramref name="item"/> 加入 <paramref name="scopeId"/> 对应的列表：若同 scope 下已存在
    /// 相同 <paramref name="idOf"/> 的条目，则<b>原地替换</b>（保留其位置），否则追加。
    /// <para>为什么用「替换」而非「追加」：径向槽位的 <c>Label</c> 会随插件状态变化（开关 / 时间 / 等级），
    /// 插件需在状态变化点重注册同 Id 槽位来刷新文案；追加会让每次刷新都多占一个位子（审计 P2-2）。
    /// 该幂等语义对托盘项 / 设置页同样成立（三者都有唯一 <c>Id</c>），故统一复用，
    /// 避免「只有径向槽位特殊」这一处隐蔽的不一致。</para>
    /// <para>返回是否发生<b>实质变更</b>：新增=true；同 Id 替换时由 <paramref name="changedOf"/> 判定
    /// （径向传入 <see cref="RadialVisualChanged"/>；未传则退化为默认值相等比较）。调用方须持有 <see cref="_gate"/>。</para>
    /// </summary>
    private static bool AddOrReplace<T>(
        Dictionary<string, List<T>> map, string scopeId, T item, Func<T, string> idOf,
        Func<T, T, bool>? changedOf = null)
    {
        if (!map.TryGetValue(scopeId, out var list))
        {
            list = [];
            map[scopeId] = list;
        }

        var id = idOf(item);
        var index = list.FindIndex(existing => string.Equals(idOf(existing), id, StringComparison.Ordinal));
        if (index >= 0)
        {
            var old = list[index];
            list[index] = item;   // 同 Id = 更新那一条（保留位置，不改变并列顺序）
            return changedOf is null ? !EqualityComparer<T>.Default.Equals(old, item) : changedOf(old, item);
        }
        list.Add(item);           // 新 Id = 追加
        return true;
    }
}
