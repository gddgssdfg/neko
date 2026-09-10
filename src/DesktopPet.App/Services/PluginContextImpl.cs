using DesktopPet.Core;

namespace DesktopPet.App.Services;

/// <summary>插件宿主上下文：把各 API 实现聚合，提供给插件。</summary>
public sealed class PluginContextImpl : IPluginContext
{
    public required Version HostVersion { get; init; }
    public required string DataRoot { get; init; }
    public required string PluginDataDir { get; init; }

    public required IPetApi Pet { get; init; }
    public required IUIApi UI { get; init; }
    public required ICapabilityApi Capability { get; init; }
}

/// <summary>能力聚合实现</summary>
public sealed class CapabilityApiImpl : ICapabilityApi
{
    public required IPluginStorage Storage { get; init; }
    public required IScheduler Scheduler { get; init; }
    public required ISystem System { get; init; }
    public required IEventBus Events { get; init; }
}

/// <summary>事件总线：宿主 → 插件广播</summary>
public sealed class EventBusImpl : IEventBus
{
    private readonly Dictionary<string, List<Action<object?>>> _subs = new();
    private readonly object _lock = new();

    public Action On(string evt, Action<object?> handler)
    {
        lock (_lock)
        {
            if (!_subs.TryGetValue(evt, out var list))
            {
                list = [];
                _subs[evt] = list;
            }
            list.Add(handler);
        }
        return () =>
        {
            lock (_lock)
            {
                if (_subs.TryGetValue(evt, out var l)) l.Remove(handler);
            }
        };
    }

    public void Emit(string evt, object? payload = null)
    {
        List<Action<object?>> snapshot;
        lock (_lock)
        {
            if (!_subs.TryGetValue(evt, out var l)) return;
            snapshot = new List<Action<object?>>(l);
        }
        foreach (var h in snapshot)
        {
            try { h(payload); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"事件处理异常 {evt}: {ex.Message}"); }
        }
    }
}
