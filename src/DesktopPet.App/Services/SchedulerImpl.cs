using DesktopPet.App.Core;
using DesktopPet.Core;

namespace DesktopPet.App.Services;

/// <summary>
/// 轻量调度器（v0.1.61：回调异常隔离 + 熔断）。
/// 关键约束：System.Threading.Timer 的回调跑在**线程池**上，未处理异常会直接终止整个进程，
/// 而 App.xaml.cs 的 DispatcherUnhandledException 只覆盖 UI 线程。因此这里的每个回调
/// 都必须 try/catch 包裹 —— 否则任一插件定时任务抛异常就会杀死 Neko 宿主
/// （违反架构铁律「单插件崩溃不拖垮宿主」）。
/// </summary>
public sealed class SchedulerImpl : IScheduler
{
    /// <summary>连续失败阈值：达到后自动停表，避免刷屏 + 反复异常。</summary>
    private const int MaxConsecutiveFailures = 5;

    private readonly List<TimerHandle> _handles = new();

    public IScheduleHandle Every(int intervalMs, Action callback)
        => Add(intervalMs, intervalMs, callback);

    public IScheduleHandle At(DateTime time, Action callback)
    {
        var delay = Math.Max(0, (int)(time - DateTime.Now).TotalMilliseconds);
        return Add(delay, Timeout.Infinite, callback);
    }

    private IScheduleHandle Add(int dueMs, int periodMs, Action callback)
    {
        TimerHandle? handle = null;
        Timer? timer = null;
        // 回调闭包引用 handle，用于失败计数/熔断；timer 在回调内也可能触发，故用局部捕获
        timer = new Timer(_ => SafeInvoke(handle!, callback), null, dueMs, periodMs);
        handle = new TimerHandle(timer);
        lock (_handles) _handles.Add(handle);
        return handle;
    }

    /// <summary>隔离插件回调异常：记录日志 + 计数；连续失败达阈值即停表并告警。</summary>
    private static void SafeInvoke(TimerHandle handle, Action callback)
    {
        try
        {
            if (!handle.IsActive) return;
            callback();
            handle.ResetFailures();
        }
        catch (Exception ex)
        {
            var failures = handle.IncrementFailures();
            Log.Error($"[调度器] 定时任务回调抛异常（第 {failures} 次）: {ex.Message}", ex);
            if (failures >= MaxConsecutiveFailures)
            {
                handle.Dispose();
                Log.Warn($"[调度器] 定时任务连续失败 {failures} 次，已自动停用该定时器");
            }
        }
    }

    private sealed class TimerHandle : IScheduleHandle
    {
        private readonly Timer _timer;
        private int _consecutiveFailures;

        public TimerHandle(Timer timer) => _timer = timer;

        public bool IsActive { get; private set; } = true;

        public int IncrementFailures() => Interlocked.Increment(ref _consecutiveFailures);

        public void ResetFailures() => Interlocked.Exchange(ref _consecutiveFailures, 0);

        public void Dispose()
        {
            IsActive = false;
            try { _timer.Dispose(); } catch { }
        }
    }
}
