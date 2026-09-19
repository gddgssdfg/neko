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
        lock (_gate) AddOrReplace(_radialSlots, _currentScope!, slot, s => s.Id);
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
        lock (_gate)
        {
            _radialSlots.Remove(scopeId);
            _trayItems.Remove(scopeId);
            _pages.Remove(scopeId);
        }
    }

    /// <summary>
    /// 把 <paramref name="item"/> 加入 <paramref name="scopeId"/> 对应的列表：若同 scope 下已存在
    /// 相同 <paramref name="idOf"/> 的条目，则<b>原地替换</b>（保留其位置），否则追加。
    /// <para>为什么用「替换」而非「追加」：径向槽位的 <c>Label</c> 会随插件状态变化（开关 / 时间 / 等级），
    /// 插件需在状态变化点重注册同 Id 槽位来刷新文案；追加会让每次刷新都多占一个位子（审计 P2-2）。
    /// 该幂等语义对托盘项 / 设置页同样成立（三者都有唯一 <c>Id</c>），故统一复用，
    /// 避免「只有径向槽位特殊」这一处隐蔽的不一致。</para>
    /// <para>调用方须持有 <see cref="_gate"/>。</para>
    /// </summary>
    private static void AddOrReplace<T>(
        Dictionary<string, List<T>> map, string scopeId, T item, Func<T, string> idOf)
    {
        if (!map.TryGetValue(scopeId, out var list))
        {
            list = [];
            map[scopeId] = list;
        }

        var id = idOf(item);
        var index = list.FindIndex(existing => string.Equals(idOf(existing), id, StringComparison.Ordinal));
        if (index >= 0) list[index] = item;   // 同 Id = 更新那一条（保留位置，不改变并列顺序）
        else list.Add(item);                  // 新 Id = 追加
    }
}
