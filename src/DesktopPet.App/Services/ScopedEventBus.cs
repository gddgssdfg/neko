using DesktopPet.Core;

namespace DesktopPet.App.Services;

/// <summary>宿主持有订阅所有权，插件漏退订或 OnLoad 抛出时也可回收。</summary>
internal sealed class ScopedEventBus(IEventBus inner) : IEventBus, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<long, Action> _subscriptions = new();
    private long _nextId;
    private bool _disposed;

    public Action On(string evt, Action<object?> handler)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var id = ++_nextId;
            _subscriptions.Add(id, inner.On(evt, handler));
            return () => Cancel(id);
        }
    }

    private void Cancel(long id)
    {
        Action? unsubscribe;
        lock (_gate) _subscriptions.Remove(id, out unsubscribe);
        unsubscribe?.Invoke();
    }

    public void Emit(string evt, object? payload = null)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        inner.Emit(evt, payload);
    }

    public void Dispose()
    {
        Action[] pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            pending = _subscriptions.Values.ToArray();
            _subscriptions.Clear();
        }
        foreach (var unsubscribe in pending)
            try { unsubscribe(); } catch { /* Continue releasing the remaining subscriptions. */ }
    }
}
