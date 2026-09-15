using DesktopPet.App.Core;
using DesktopPet.Core;

namespace DesktopPet.App.Services;

/// <summary>
/// 轻量调度器（v0.1.61：回调异常隔离 + 熔断；v0.1.77：修复「首帧回调竞态杀宿主」）。
///
/// <para>关键约束：System.Threading.Timer 的回调跑在<b>线程池</b>上，未处理异常会直接终止整个进程，
/// 而 App.xaml.cs 的 DispatcherUnhandledException 只覆盖 UI 线程。因此这里的每个回调
/// 都必须 try/catch 包裹 —— 否则任一插件定时任务抛异常就会杀死 Neko 宿主
/// （违反架构铁律「单插件崩溃不拖垮宿主」）。</para>
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
        // v0.1.77 修复致命竞态（原实现）：
        //   timer = new Timer(_ => SafeInvoke(handle!, ...), null, dueMs, periodMs);  // handle 此刻仍为 null
        //   handle = new TimerHandle(timer);
        // 当 dueMs=0（At(DateTime.Now) 常见）时，回调可能抢在 handle 赋值前就在线程池触发 →
        // SafeInvoke(null!) 内 NRE → catch 里再访问 null handle 二次 NRE → 逃出回调 →
        // 线程池未处理异常 → CLR 直接终止进程（三层兜底拦不住）。
        //
        // 修法：Timer 先以 Timeout.Infinite 创建（装配完成前绝不触发）；等 handle 完全就位后再
        // Change(dueMs, periodMs) 手动启动。回调闭包捕获的 handle 在启动前已是非 null 的完整对象。
        var handle = new TimerHandle();
        var timer = new Timer(_ => SafeInvoke(handle, callback), null, Timeout.Infinite, Timeout.Infinite);
        handle.Attach(timer);
        lock (_handles) _handles.Add(handle);
        timer.Change(dueMs, periodMs);   // 此刻 handle 已就位：回调无论何时触发都拿得到非 null handle
        return handle;
    }

    /// <summary>
    /// 隔离插件回调异常：记录日志 + 计数；连续失败达阈值即停表并告警。
    /// <para>本方法运行在线程池上，<b>任何路径都不得向外抛异常</b>（否则未处理异常会终止进程）——
    /// 因此 handle 为 null 时直接返回，且 catch 体内再包一层 try 兜底。</para>
    /// </summary>
    private static void SafeInvoke(TimerHandle? handle, Action callback)
    {
        if (handle is null) return;   // 防御：理论上不会发生（见 Add 注释），但绝不从这里抛出

        try
        {
            if (!handle.IsActive) return;
            callback();
            handle.ResetFailures();
        }
        catch (Exception ex)
        {
            // 二次防护：catch 体自身任何异常都不得外抛（原 bug 正是在此二次 NRE 杀死进程）。
            try
            {
                var failures = handle.IncrementFailures();
                Log.Error($"[调度器] 定时任务回调抛异常（第 {failures} 次）: {ex.Message}", ex);
                if (failures >= MaxConsecutiveFailures)
                {
                    handle.Dispose();
                    Log.Warn($"[调度器] 定时任务连续失败 {failures} 次，已自动停用该定时器");
                }
            }
            catch
            {
                // 计数/日志本身失败：静默吞掉，绝不复抛
            }
        }
    }

    private sealed class TimerHandle : IScheduleHandle
    {
        private Timer? _timer;
        private int _consecutiveFailures;
        private volatile bool _active = true;

        /// <summary>
        /// 装配底层 Timer（构造后再注入）。
        /// 与 <see cref="Add"/> 的「先建 handle → 再 Change 启动」配合，消除首次回调竞态。
        /// </summary>
        public void Attach(Timer timer) => _timer = timer;

        public bool IsActive => _active;

        public int IncrementFailures() => Interlocked.Increment(ref _consecutiveFailures);

        public void ResetFailures() => Interlocked.Exchange(ref _consecutiveFailures, 0);

        public void Dispose()
        {
            _active = false;
            try { _timer?.Dispose(); } catch { }
        }
    }
}
