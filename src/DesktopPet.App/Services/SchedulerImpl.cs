using DesktopPet.App.Core;
using DesktopPet.Core;

namespace DesktopPet.App.Services;

/// <summary>
/// 轻量调度器（v0.1.61：回调异常隔离 + 熔断；v0.1.77：修复「首帧回调竞态杀宿主」；
/// v0.1.82：失败路径补可观测性日志「插件标识 + 异常消息」+ 停表显式告警）。
///
/// <para>关键约束：System.Threading.Timer 的回调跑在<b>线程池</b>上，未处理异常会直接终止整个进程，
/// 而 App.xaml.cs 的 DispatcherUnhandledException 只覆盖 UI 线程。因此这里的每个回调
/// 都必须 try/catch 包裹 —— 否则任一插件定时任务抛异常就会杀死 Neko 宿主
/// （违反架构铁律「单插件崩溃不拖垮宿主」）。</para>
/// </summary>
public sealed class SchedulerImpl : IScheduler, IDisposable
{
    /// <summary>连续失败阈值：达到后自动停表，避免刷屏 + 反复异常。</summary>
    private const int MaxConsecutiveFailures = 5;

    private readonly HashSet<TimerHandle> _handles = new();
    private bool _disposed;

    public IScheduleHandle Every(int intervalMs, Action callback)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intervalMs);
        return Add(intervalMs, intervalMs, callback);
    }

    public IScheduleHandle At(DateTime time, Action callback)
    {
        var localTime = time.Kind == DateTimeKind.Utc ? time.ToLocalTime() : time;
        return Add(DelayUntil(localTime), Timeout.Infinite, callback, localTime);
    }

    // Long dates are re-armed in bounded chunks. Never truncate a double to int before clamping.
    private static int DelayUntil(DateTime time)
        => (int)Math.Clamp(Math.Ceiling((time - DateTime.Now).TotalMilliseconds), 0, int.MaxValue);

    private IScheduleHandle Add(int dueMs, int periodMs, Action callback, DateTime? deadline = null)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_handles)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var handle = new TimerHandle(DescribeCallback(callback), RemoveHandle);
            // Build everything with the timer disabled, then register and start under the owner lock.
            // Dispose cannot miss a timer created concurrently with the shutdown snapshot.
            var timer = new Timer(_ =>
            {
                if (deadline is { } target && DateTime.Now < target)
                {
                    handle.Reschedule(DelayUntil(target));
                    return;
                }
                try { SafeInvoke(handle, callback); }
                finally { if (periodMs == Timeout.Infinite) handle.Dispose(); }
            }, null, Timeout.Infinite, Timeout.Infinite);
            handle.Attach(timer);
            _handles.Add(handle);
            try { timer.Change(dueMs, periodMs); }
            catch { handle.Dispose(); throw; }
            return handle;
        }
    }

    private void RemoveHandle(TimerHandle handle)
    {
        lock (_handles) _handles.Remove(handle);
    }

    /// <summary>Stop all owned timers and permanently reject new registrations. Does not wait for in-flight callbacks.</summary>
    public void Dispose()
    {
        TimerHandle[] snapshot;
        lock (_handles)
        {
            if (_disposed) return;
            _disposed = true;
            snapshot = _handles.ToArray();
            _handles.Clear();
        }
        foreach (var handle in snapshot) handle.Dispose();
    }

    /// <summary>
    /// 生成回调来源描述（形如 <c>DesktopPet.Builtin.HourlyChime.HourlyChimePlugin.CheckHour</c>），
    /// 供失败日志定位到具体插件。仅在装配定时器时调用一次（<b>非 tick 路径</b>），成功失败都不抛异常。
    /// </summary>
    private static string DescribeCallback(Action callback)
    {
        try
        {
            var method = callback.Method;
            var type = method.DeclaringType;
            var typeName = type?.FullName ?? type?.Name;
            return string.IsNullOrEmpty(typeName) ? method.Name : $"{typeName}.{method.Name}";
        }
        catch
        {
            return "(未知回调)";
        }
    }

    /// <summary>
    /// 隔离插件回调异常：以 <see cref="Log.Error"/> 记录日志 + 计数；连续失败达阈值即停表并再记一条错误。
    /// <para>用 Error（而非 Warn）是刻意的：回调抛异常属真实故障，必须「任何日志级别下都可见」；
    /// 且每个定时器最多只记 MaxConsecutiveFailures 条，不会无界污染 errors.log。</para>
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
                // v0.1.82：每次失败都记日志，并带上可定位的回调来源标识（插件类型 + 方法名），
                // 不再只记 ex.Message —— 否则配合插件里的 catch {} 故障会永久隐身。
                // ⚠️ 必须是 Log.Error，绝不能降级为 Warn/Debug（曾误改为 Warn，属回归）：
                //   1) 级别语义：插件定时回调抛异常是【真实故障】，属于「即使把日志级别调到 error 也必须可见」
                //      的那一类。旧实现即 Log.Error；改成 Warn 后 Warn < Error，会被 Log.Write 的
                //      `if (level < _minLevel) return;` 静默过滤 → error 级别下故障彻底隐身。
                //   2) 不会无界污染 errors.log：熔断器把输出限制在每个定时器最多 MaxConsecutiveFailures(=5) 条，
                //      所以「怕污染 errors.log」的理由不成立。
                Log.Error($"[调度器] 定时任务回调异常：{handle.Description}（连续第 {failures}/{MaxConsecutiveFailures} 次）：{ex.GetType().Name}: {ex.Message}");
                if (failures >= MaxConsecutiveFailures)
                {
                    handle.Dispose();
                    // 同上是【故障】而非可恢复告警：定时器被熔断停用（与上一条成对出现），必须 Error 级别可见。
                    Log.Error($"[调度器] 定时器「{handle.Description}」已因连续失败 {failures} 次被停用（此后不再触发该回调）");
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
        private readonly object _gate = new();
        private int _active = 1;
        private Action<TimerHandle>? _onDisposed;

        /// <summary>
        /// 回调来源描述（插件类型 + 方法名）。在装配时一次性算出并缓存，供异常日志定位来源；
        /// 避免在「每 tick 可能走到」的失败路径上再做反射（分配热点）。
        /// </summary>
        public string Description { get; }

        public TimerHandle(string description, Action<TimerHandle> onDisposed)
        {
            Description = description;
            _onDisposed = onDisposed;
        }

        /// <summary>
        /// 装配底层 Timer（构造后再注入）。
        /// 与 <see cref="Add"/> 的「先建 handle → 再 Change 启动」配合，消除首次回调竞态。
        /// </summary>
        public void Attach(Timer timer) => _timer = timer;

        public bool IsActive => Volatile.Read(ref _active) != 0;

        public void Reschedule(int dueMs)
        {
            lock (_gate)
            {
                if (IsActive) _timer?.Change(dueMs, Timeout.Infinite);
            }
        }

        public int IncrementFailures() => Interlocked.Increment(ref _consecutiveFailures);

        public void ResetFailures() => Interlocked.Exchange(ref _consecutiveFailures, 0);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _active, 0) == 0) return;
            lock (_gate)
            {
                _timer?.Dispose();
                _timer = null;
            }
            Interlocked.Exchange(ref _onDisposed, null)?.Invoke(this);
        }
    }
}
