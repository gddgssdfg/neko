using System.Diagnostics;
using DesktopPet.Core;

namespace DesktopPet.Tests.PluginSmoke;

/// <summary>
/// 冒烟工具用的「宿主能力」替身集合。
/// <para>目标是在<b>无 UI、无副作用</b>的前提下，尽可能忠实地复刻宿主侧语义：
/// <list type="bullet">
///   <item><see cref="SmokeScheduler"/> 用 <see cref="System.Threading.Timer"/>（与宿主 SchedulerImpl 一致）
///         —— 让定时器真的按周期在线程池触发，才能测到"闲置是否空转"和"回调在哪个线程"。</item>
///   <item><see cref="SmokeStorage"/> 用文件落地（与宿主 JsonPluginStorage 一致）—— 能测到插件的数据目录读写。</item>
///   <item><see cref="SmokeSystem"/> 是<b>纯内存</b>：不碰真实剪贴板 / 不弹通知 / 不打开浏览器 / 不截图。</item>
///   <item><see cref="SmokePet"/> 只做计数，不碰任何 WPF 对象。</item>
/// </list>
/// 副作用一律不落到真实系统：截图/媒体键/网络都因为「不调用槽位回调」而不会被触发。</para>
/// </summary>
internal sealed class SmokeScheduler : IScheduler
{
    private readonly List<SmokeHandle> _handles = new();

    /// <summary>本插件累计创建的定时器句柄数。</summary>
    public int CreatedCount { get; private set; }

    /// <summary>回调跑在非主线程（线程池）上的次数 —— 宿主 Pet.Say 未做 Dispatcher 回调的隐患证据。</summary>
    public int CallbacksOnNonMainThread { get; private set; }

    /// <summary>回调抛异常次数（宿主 SafeInvoke 同款隔离，绝不让线程池异常拖垮进程）。</summary>
    public int CallbackErrors { get; private set; }

    /// <summary>最近一次回调异常的简报。</summary>
    public string? LastCallbackError { get; private set; }

    /// <summary>当前仍处于活动（未 Dispose）状态的句柄数 —— OnUnload 后应归零，否则就是定时器泄漏。</summary>
    public int ActiveCount
    {
        get { lock (_handles) return _handles.Count(h => !h.Disposed); }
    }

    public IScheduleHandle Every(int intervalMs, Action callback) => Add(intervalMs, intervalMs, callback);

    public IScheduleHandle At(DateTime time, Action callback)
    {
        var delay = Math.Max(0, (int)(time - DateTime.Now).TotalMilliseconds);
        return Add(delay, Timeout.Infinite, callback);
    }

    private IScheduleHandle Add(int dueMs, int periodMs, Action callback)
    {
        // 与宿主 SchedulerImpl 同款：先以 Infinite 装配，句柄就位后再 Change 启动，消除首帧竞态。
        var handle = new SmokeHandle();
        var timer = new Timer(_ => Invoke(handle, callback), null, Timeout.Infinite, Timeout.Infinite);
        handle.Attach(timer);
        lock (_handles) _handles.Add(handle);
        CreatedCount++;
        timer.Change(dueMs, periodMs);
        return handle;
    }

    private void Invoke(SmokeHandle handle, Action callback)
    {
        if (!handle.IsActive) return;
        try
        {
            if (Environment.CurrentManagedThreadId != Program.MainThreadId)
                CallbacksOnNonMainThread++;
            callback();
        }
        catch (Exception ex)
        {
            CallbackErrors++;
            LastCallbackError = ex.GetType().Name + ": " + ex.Message;
        }
    }

    private sealed class SmokeHandle : IScheduleHandle
    {
        private Timer? _timer;
        private volatile bool _active = true;

        public void Attach(Timer timer) => _timer = timer;
        public bool IsActive => _active;
        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
            _active = false;
            try { _timer?.Dispose(); } catch { /* 与宿主一致：Dispose 不抛 */ }
        }
    }
}

/// <summary>文件落地的 KV 存储（与宿主 JsonPluginStorage 同构），每个插件一个隔离目录。</summary>
internal sealed class SmokeStorage : IPluginStorage
{
    private readonly string _dir;
    private readonly System.Text.Json.JsonSerializerOptions _opts = new() { WriteIndented = true };

    public SmokeStorage(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(_dir);
    }

    public string Dir => _dir;

    public T? Get<T>(string key)
    {
        var path = Path.Combine(_dir, key + ".json");
        if (!File.Exists(path)) return default;
        try { return System.Text.Json.JsonSerializer.Deserialize<T>(File.ReadAllText(path), _opts); }
        catch { return default; }
    }

    public void Set<T>(string key, T value)
    {
        var path = Path.Combine(_dir, key + ".json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(value, _opts));
    }

    public void Remove(string key)
    {
        var path = Path.Combine(_dir, key + ".json");
        if (File.Exists(path)) File.Delete(path);
    }

    public bool Contains(string key) => File.Exists(Path.Combine(_dir, key + ".json"));
}

/// <summary>
/// 系统能力替身：<b>全部只做计数，不产生任何真实副作用</b>
/// （不读/写真实剪贴板、不弹系统通知、不打开浏览器、不弹文件选择框）。
/// </summary>
internal sealed class SmokeSystem : ISystem
{
    public int ReadClipboardCount { get; private set; }
    public int WriteClipboardCount { get; private set; }
    public int NotifyCount { get; private set; }
    public int OpenUrlCount { get; private set; }
    public List<string> NotifiedBodies { get; } = new();

    public void Notify(string title, string body)
    {
        NotifyCount++;
        NotifiedBodies.Add(body);
    }

    public string? ReadClipboard()
    {
        ReadClipboardCount++;
        return null;   // 安全：绝不碰真实剪贴板
    }

    public void WriteClipboard(string text) => WriteClipboardCount++;

    public void OpenUrl(string url) => OpenUrlCount++;

    public string? PickFile(string filter = "") => null;

    /// <summary>v0.1.83：新增 ISystem.Log 的替身实现（只计数 + 记录，无副作用）。</summary>
    public int LogCount { get; private set; }
    public List<(PluginLogLevel Level, string Message)> Logs { get; } = new();
    public void Log(PluginLogLevel level, string message, Exception? exception = null)
    {
        LogCount++;
        Logs.Add((level, message));
    }
}

/// <summary>事件总线替身（与宿主 EventBusImpl 同语义）。记录订阅/退订，便于验证 OnUnload 是否解绑。</summary>
internal sealed class SmokeEvents : IEventBus
{
    private readonly Dictionary<string, List<Action<object?>>> _subs = new();
    private readonly object _lock = new();

    public int EmitCount { get; private set; }

    public Action On(string evt, Action<object?> handler)
    {
        lock (_lock)
        {
            if (!_subs.TryGetValue(evt, out var list)) { list = new(); _subs[evt] = list; }
            list.Add(handler);
        }
        return () =>
        {
            lock (_lock) { if (_subs.TryGetValue(evt, out var l)) l.Remove(handler); }
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
        EmitCount++;
        foreach (var h in snapshot)
        {
            try { h(payload); } catch { /* 事件处理异常不拖垮宿主 */ }
        }
    }

    /// <summary>当前全部事件处理器总数（卸载后应回到加载前的数量）。</summary>
    public int TotalHandlers
    {
        get { lock (_lock) return _subs.Values.Sum(v => v.Count); }
    }
}

/// <summary>宠物 API 替身：只计数，不碰 WPF。记录 Say 是否来自非主线程。</summary>
internal sealed class SmokePet : IPetApi
{
    public int SayCount { get; private set; }
    public int SayFromNonMainThread { get; private set; }
    public List<string> Spoken { get; } = new();
    public int SetMoodCount { get; private set; }
    public int PlayCount { get; private set; }

    public Task PlayAnimationAsync(PetState state, AnimationOptions? opts = null)
    {
        PlayCount++;
        return Task.CompletedTask;
    }

    public void SetMood(PetMood mood) => SetMoodCount++;

    public void Say(string text, int durationMs = 3000)
    {
        SayCount++;
        if (Environment.CurrentManagedThreadId != Program.MainThreadId) SayFromNonMainThread++;
        Spoken.Add(text);
    }

    public Point2 GetPosition() => Point2.Zero;

    public Task MoveToAsync(Point2 target, int durationMs = 500) => Task.CompletedTask;

    public Rect2 GetWorkArea() => new(0, 0, 1920, 1040);
}

/// <summary>UI 注册 API 替身：只计数（径向槽位 / 托盘项 / 设置页），不建任何控件。</summary>
internal sealed class SmokeUi : IUIApi
{
    public int RadialSlotCount { get; private set; }
    public int TrayItemCount { get; private set; }
    public int SettingsPageCount { get; private set; }
    public int UnregisterCount { get; private set; }
    public List<string> SlotIds { get; } = new();
    public List<string> PageTitles { get; } = new();

    public void RegisterRadialSlot(RadialSlot slot) { RadialSlotCount++; SlotIds.Add(slot.Id); }
    public void RegisterTrayMenuItem(TrayMenuItem item) => TrayItemCount++;
    public void RegisterSettingsPage(ISettingsPage page) { SettingsPageCount++; PageTitles.Add(page.Title); }
    public void UnregisterAll(string scopeId) => UnregisterCount++;
}

/// <summary>能力聚合替身。</summary>
internal sealed class SmokeCapability : ICapabilityApi
{
    public SmokeCapability(IPluginStorage s, SmokeScheduler sc, ISystem sys, IEventBus ev)
    {
        Storage = s; Scheduler = sc; System = sys; Events = ev;
    }

    public IPluginStorage Storage { get; }
    public IScheduler Scheduler { get; }
    public ISystem System { get; }
    public IEventBus Events { get; }
}

/// <summary>插件上下文替身（与宿主 PluginContextImpl 同结构）。</summary>
internal sealed class SmokeContext : IPluginContext
{
    public required Version HostVersion { get; init; }
    public required string DataRoot { get; init; }
    public required string PluginDataDir { get; init; }
    public required IPetApi Pet { get; init; }
    public required IUIApi UI { get; init; }
    public required ICapabilityApi Capability { get; init; }
}

/// <summary>进程内存采样（PrivateMemorySize64 + GC 托管堆）。</summary>
internal static class SmokeMemory
{
    /// <summary>进程私有内存（MB）。</summary>
    public static double PrivateMb()
    {
        var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.PrivateMemorySize64 / 1048576.0;
    }

    /// <summary>GC 托管堆（MB），强制一次完整回收后取。</summary>
    public static double HeapMb() => GC.GetTotalMemory(forceFullCollection: true) / 1048576.0;

    /// <summary>进程累计 CPU 时间（毫秒）。</summary>
    public static double CpuMs()
    {
        var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.TotalProcessorTime.TotalMilliseconds;
    }

    /// <summary>强制回收并等待终结器（多次以压掉抖动）。</summary>
    public static void ForceCollect()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
