using System.Diagnostics;
using System.IO;
using System.Reflection;
using DesktopPet.Builtin.Affection;
using DesktopPet.Core;

namespace AffectionProbe;

/// <summary>
/// QA 独立证伪夹具：好感度插件「内存即时更新 + 去抖落盘」（v0.1.82）。
/// <para>
/// 直接构造 <see cref="AffectionPlugin"/> 并用自造 <see cref="IPluginContext"/> 驱动，验证：
///   1) 内存即时（11 次抚摸后内存=11）+ 落盘延迟（磁盘 &lt; 11）；
///   2) 升级提示即时（由内存等级驱动）；
///   3) trailing timer：不再抚摸，静置 &gt;5s 后磁盘会自行追平（不需 FlushNow）；
///   4) OnUnload 必落盘；
///   5) 并发压力：UI 线程连续抚摸 与 另一线程高频 FlushNow 并发 → 不崩、不写坏、最终不丢。
/// </para>
/// </summary>
internal static class Program
{
    private static int _fail, _pass;
    private static void Ok(string m) { _pass++; Console.WriteLine("PASS: " + m); }
    private static void Bad(string m) { _fail++; Console.WriteLine("FAIL: " + m); }
    private static void Check(bool ok, string okMsg, string failMsg) { if (ok) Ok(okMsg); else Bad(failMsg); }

    private const string ScoreKey = "score";

    private static int Main()
    {
        Console.WriteLine("=== 好感度「去抖落盘」独立证伪（AffectionProbe）===");
        try
        {
            TestDebounceAndTrailing();
            TestOnUnloadFlush();
            TestConcurrency();
        }
        catch (Exception ex)
        {
            Bad($"夹具异常 {ex}");
        }
        Console.WriteLine();
        Console.WriteLine(_fail == 0 ? $"=== 全部通过（{_pass} 项）===" : $"=== {_fail} 项失败（通过 {_pass}）===");
        return _fail == 0 ? 0 : 1;
    }

    // ---- 反射读插件私有 _score（内存真值）----
    private static int Memory(AffectionPlugin p)
        => (int)typeof(AffectionPlugin).GetField("_score", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(p)!;

    // ---- 1+2+3：去抖 / 即时升级 / trailing timer ----
    private static void TestDebounceAndTrailing()
    {
        Console.WriteLine();
        Console.WriteLine("--- 1. 去抖 + 内存即时 + trailing timer ---");

        var storage = new SlowStorage();
        var bus = new EventBus();
        var pet = new PetApi();
        var ctx = BuildContext(storage, bus, pet);
        var plugin = new AffectionPlugin();
        plugin.OnLoad(ctx);

        for (int i = 0; i < 11; i++) bus.Emit(PetEvents.PetPetted, null);

        var mem = Memory(plugin);
        var disk = storage.Get<int>(ScoreKey);
        Check(mem == 11, $"内存即时：11 次抚摸后 _score = {mem}", $"内存未即时：_score = {mem}（期望 11）");
        Check(disk < 11, $"落盘延迟：磁盘 score = {disk}（< 11，未每次写盘）", $"落盘未延迟：磁盘 = {disk}（期望 < 11）");
        Check(disk > 0, $"首次抚摸即落盘：磁盘 score = {disk}（> 0）", $"首次抚摸未落盘：磁盘 = {disk}（期望 > 0）");

        var levelUp = pet.Says.Count(s => s.Contains("好感度提升"));
        Check(levelUp >= 1, $"升级提示即时触发（{levelUp} 条）", "未触发升级提示（等级判定可能依赖磁盘而非内存）");

        // trailing timer：静置 6s（>FlushIntervalMs=5000），不调用 FlushNow，磁盘应自行追平
        Console.WriteLine("   静置 6s 观察 trailing timer ...");
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 6000 && storage.Get<int>(ScoreKey) < 11)
            Thread.Sleep(200);
        var diskAfter = storage.Get<int>(ScoreKey);
        Check(diskAfter == 11,
            $"trailing timer 生效：静置 {sw.ElapsedMilliseconds}ms 后磁盘自行追平 = {diskAfter}（无 FlushNow）",
            $"trailing timer 未生效：静置 {sw.ElapsedMilliseconds}ms 后磁盘仍 = {diskAfter}（期望 11）");

        plugin.OnUnload();
    }

    // ---- 4：OnUnload 必落盘 ----
    private static void TestOnUnloadFlush()
    {
        Console.WriteLine();
        Console.WriteLine("--- 2. OnUnload 强制落盘 ---");

        var storage = new SlowStorage();
        var bus = new EventBus();
        var ctx = BuildContext(storage, bus, new PetApi());
        var plugin = new AffectionPlugin();
        plugin.OnLoad(ctx);

        for (int i = 0; i < 37; i++) bus.Emit(PetEvents.PetPetted, null);
        var beforeUnload = storage.Get<int>(ScoreKey);

        plugin.OnUnload();
        var afterUnload = storage.Get<int>(ScoreKey);

        Check(Memory(plugin) == 37, $"内存 = 37", $"内存 = {Memory(plugin)}（期望 37）");
        Check(afterUnload == 37,
            $"OnUnload 强制落盘：卸载后磁盘 = {afterUnload}（= 内存 37）",
            $"OnUnload 未落盘：卸载前磁盘 = {beforeUnload}，卸载后 = {afterUnload}（期望 37）");
    }

    // ---- 5：并发压力 ----
    private static void TestConcurrency()
    {
        Console.WriteLine();
        Console.WriteLine("--- 3. 并发压力（UI 线程快抚摸 + 另一线程高频 FlushNow）---");

        var storage = new SlowStorage { SetDelayMs = 2 };   // 加宽落盘窗口
        var bus = new EventBus();
        var ctx = BuildContext(storage, bus, new PetApi());
        var plugin = new AffectionPlugin();
        plugin.OnLoad(ctx);

        const int pets = 5000;
        var exceptions = new List<Exception>();
        var corrupted = 0;
        var stop = false;

        // 线程 A：顺序抚摸（模拟 UI 线程）
        var tA = new Thread(() =>
        {
            try { for (int i = 0; i < pets; i++) bus.Emit(PetEvents.PetPetted, null); }
            catch (Exception ex) { lock (exceptions) exceptions.Add(ex); }
        });

        // 线程 B：高频 FlushNow（模拟后台 flush 与显式落盘点并发）
        var tB = new Thread(() =>
        {
            try
            {
                var sw = Stopwatch.StartNew();
                while (!Volatile.Read(ref stop) && sw.ElapsedMilliseconds < 8000)
                {
                    plugin.FlushNow();
                    Thread.Sleep(1);
                }
            }
            catch (Exception ex) { lock (exceptions) exceptions.Add(ex); }
        });

        // 线程 C：持续读磁盘值，校验始终是可解析的整数（写坏 → 异常/异常值）
        var tC = new Thread(() =>
        {
            try
            {
                while (!Volatile.Read(ref stop))
                {
                    var v = storage.Get<int>(ScoreKey);
                    if (v < 0) Interlocked.Increment(ref corrupted);
                    Thread.Sleep(5);
                }
            }
            catch (Exception ex) { lock (exceptions) exceptions.Add(ex); }
        });

        tA.Start(); tB.Start(); tC.Start();
        tA.Join();
        Volatile.Write(ref stop, true);
        tB.Join(3000);
        tC.Join(3000);

        var mem = Memory(plugin);
        Check(mem == pets, $"并发抚摸后内存 = {mem}（期望 {pets}，无丢计数）", $"并发抚摸后内存 = {mem}（期望 {pets}，疑似丢计数）");
        Check(exceptions.Count == 0, $"并发期无异常（0 个）", $"并发期抛异常 {exceptions.Count} 个：{exceptions.FirstOrDefault()?.Message}");
        Check(corrupted == 0, "并发期磁盘值始终合法（未写坏）", $"并发期读到 {corrupted} 次非法磁盘值");

        // 并发结束后卸载 → 磁盘必须 = 内存
        plugin.OnUnload();
        var finalDisk = storage.Get<int>(ScoreKey);
        Check(finalDisk == pets,
            $"并发 + OnUnload 后磁盘 = {finalDisk}（= 内存 {pets}，无丢失）",
            $"并发 + OnUnload 后磁盘 = {finalDisk}（期望 {pets}）");
    }

    private static IPluginContext BuildContext(IPluginStorage storage, EventBus bus, PetApi pet)
        => new Ctx
        {
            HostVersion = new Version(0, 1, 82),
            DataRoot = Path.GetTempPath(),
            PluginDataDir = Path.GetTempPath(),
            Pet = pet,
            UI = new UiApi(),
            Capability = new Cap { Storage = storage, Scheduler = new Sched(), System = new Sys(), Events = bus },
        };

    // ---------------- 测试替身 ----------------

    /// <summary>内存存储；可注入 Set 延迟以加宽落盘并发窗口。</summary>
    private sealed class SlowStorage : IPluginStorage
    {
        private readonly Dictionary<string, object?> _d = new();
        public int SetDelayMs { get; set; }
        public T? Get<T>(string key) => _d.TryGetValue(key, out var v) ? (T?)v : default;
        public void Set<T>(string key, T value)
        {
            if (SetDelayMs > 0) Thread.Sleep(SetDelayMs);
            _d[key] = value;
        }
        public void Remove(string key) => _d.Remove(key);
        public bool Contains(string key) => _d.ContainsKey(key);
    }

    private sealed class EventBus : IEventBus
    {
        private readonly Dictionary<string, List<Action<object?>>> _h = new();
        private readonly object _lock = new();
        public Action On(string evt, Action<object?> handler)
        {
            lock (_lock) { if (!_h.TryGetValue(evt, out var l)) _h[evt] = l = new(); l.Add(handler); }
            return () =>
            {
                lock (_lock)
                {
                    if (_h.TryGetValue(evt, out var l)) l.Remove(handler);
                }
            };
        }
        public void Emit(string evt, object? payload = null)
        {
            Action<object?>[] copy;
            lock (_lock) copy = _h.TryGetValue(evt, out var l) ? l.ToArray() : Array.Empty<Action<object?>>();
            foreach (var h in copy) h.Invoke(payload);
        }
    }

    private sealed class PetApi : IPetApi
    {
        public readonly List<string> Says = new();
        public readonly List<PetMood> Moods = new();
        public Task PlayAnimationAsync(PetState state, AnimationOptions? opts = null) => Task.CompletedTask;
        public void SetMood(PetMood mood) { lock (Says) Moods.Add(mood); }
        public void Say(string text, int durationMs = 3000) { lock (Says) Says.Add(text); }
        public Point2 GetPosition() => default;
        public Task MoveToAsync(Point2 target, int durationMs = 500) => Task.CompletedTask;
        public Rect2 GetWorkArea() => default;
    }

    private sealed class UiApi : IUIApi
    {
        public void RegisterRadialSlot(RadialSlot slot) { }
        public void RegisterTrayMenuItem(TrayMenuItem item) { }
        public void RegisterSettingsPage(ISettingsPage page) { }
        public void UnregisterAll(string scopeId) { }
    }

    private sealed class Sched : IScheduler
    {
        public IScheduleHandle Every(int intervalMs, Action callback) => new Handle();
        public IScheduleHandle At(DateTime time, Action callback) => new Handle();
        private sealed class Handle : IScheduleHandle
        {
            public bool IsActive => false;
            public void Dispose() { }
        }
    }

    private sealed class Sys : ISystem
    {
        public void Notify(string title, string body) { }
        public string? ReadClipboard() => null;
        public void WriteClipboard(string text) { }
        public void OpenUrl(string url) { }
        public string? PickFile(string filter = "") => null;
        public void Log(PluginLogLevel level, string message, Exception? exception = null) { }
    }

    private sealed class Cap : ICapabilityApi
    {
        public required IPluginStorage Storage { get; init; }
        public required IScheduler Scheduler { get; init; }
        public required ISystem System { get; init; }
        public required IEventBus Events { get; init; }
    }

    private sealed class Ctx : IPluginContext
    {
        public required Version HostVersion { get; init; }
        public required string DataRoot { get; init; }
        public required string PluginDataDir { get; init; }
        public required IPetApi Pet { get; init; }
        public required IUIApi UI { get; init; }
        public required ICapabilityApi Capability { get; init; }
    }
}
