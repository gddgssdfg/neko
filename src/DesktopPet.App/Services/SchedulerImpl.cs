using DesktopPet.Core;

namespace DesktopPet.App.Services;

/// <summary>轻量调度器</summary>
public sealed class SchedulerImpl : IScheduler
{
    private readonly List<TimerHandle> _handles = new();

    public IScheduleHandle Every(int intervalMs, Action callback)
    {
        var h = new TimerHandle(new Timer(_ => callback(), null, intervalMs, intervalMs));
        lock (_handles) _handles.Add(h);
        return h;
    }

    public IScheduleHandle At(DateTime time, Action callback)
    {
        var delay = Math.Max(0, (int)(time - DateTime.Now).TotalMilliseconds);
        var h = new TimerHandle(new Timer(_ => callback(), null, delay, Timeout.Infinite));
        lock (_handles) _handles.Add(h);
        return h;
    }

    public IScheduleHandle Cron(string expression, Action callback)
    {
        // v1 简化：忽略 cron 表达式，退化为每分钟一次
        var h = new TimerHandle(new Timer(_ => callback(), null, 1000, 60000));
        lock (_handles) _handles.Add(h);
        return h;
    }

    private sealed class TimerHandle : IScheduleHandle
    {
        private readonly Timer _timer;
        public TimerHandle(Timer timer) => _timer = timer;
        public bool IsActive { get; private set; } = true;
        public void Dispose() { IsActive = false; _timer.Dispose(); }
    }
}
