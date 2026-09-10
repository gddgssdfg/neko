using System.Windows;
using DesktopPet.Core;

namespace DesktopPet.App.Services;

/// <summary>
/// UI 注册 API：管理插件注册的径向菜单槽位 / 托盘项 / 设置页。
/// 插件卸载时按 scopeId 批量撤销。
/// </summary>
public sealed class UIApiImpl : IUIApi
{
    private readonly Dictionary<string, List<RadialSlot>> _radialSlots = new();
    private readonly Dictionary<string, List<TrayMenuItem>> _trayItems = new();
    private readonly Dictionary<string, List<ISettingsPage>> _pages = new();
    private readonly Func<RadialSlot, object?> _slotViewFactory; // 预留

    public IReadOnlyList<RadialSlot> AllRadialSlots
        => _radialSlots.Values.SelectMany(v => v).OrderBy(s => s.Order).ToList();

    public IReadOnlyList<TrayMenuItem> AllTrayItems
        => _trayItems.Values.SelectMany(v => v).ToList();

    public IReadOnlyList<ISettingsPage> AllSettingsPages
        => _pages.Values.SelectMany(v => v).ToList();

    public UIApiImpl(Func<RadialSlot, object?> slotViewFactory)
    {
        _slotViewFactory = slotViewFactory;
    }

    public void RegisterRadialSlot(RadialSlot slot)
    {
        RequireScope();
        AddToList(_radialSlots, _currentScope!, slot);
    }

    public void RegisterTrayMenuItem(TrayMenuItem item)
    {
        RequireScope();
        AddToList(_trayItems, _currentScope!, item);
    }

    public void RegisterSettingsPage(ISettingsPage page)
    {
        RequireScope();
        AddToList(_pages, _currentScope!, page);
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
        _radialSlots.Remove(scopeId);
        _trayItems.Remove(scopeId);
        _pages.Remove(scopeId);
    }

    private static void AddToList<T>(Dictionary<string, List<T>> map, string scopeId, T item)
    {
        if (!map.TryGetValue(scopeId, out var list))
        {
            list = [];
            map[scopeId] = list;
        }
        list.Add(item);
    }
}
