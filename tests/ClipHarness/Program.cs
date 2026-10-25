// =============================================================================
// tests/ClipHarness —— 剪贴板历史插件【主路径卸载】验证工具（独立工程，不进 src/DesktopPet.sln）
//
// ■ 这个工具体验什么
//   验证 ClipboardHistoryPlugin 在**事件驱动主路径**（AddClipboardFormatListener + WM_CLIPBOARDUPDATE）
//   下的生命周期是否干净：
//     1) 主路径判定 —— 替身 IScheduler.EveryCount==1（仅过期清理）（主路径不调 Every()；走“降级轮询兜底”才会 >0）
//     2) 真写剪贴板 → 真实收到 WM_CLIPBOARDUPDATE → 历史被记录（证明监听真在收消息）
//     3) OnUnload 无异常、无“注销剪贴板监听失败/清理剪贴板监听异常”
//     4) 监听确实注销 —— 卸载后再改剪贴板，历史不再增长
//     5) HwndSource 已释放 —— 卸载后 Win32 IsWindow(hwnd)==false
//     6) ALC 可回收（最关键）—— 两个口径：WPF 线程存活时 / 线程退出后（隔离“栈帧残留”误报）
//     7) 重新 Enable 能重新挂上、旧窗口仍死、单监听（写一次只记一条）
//
// ■ 怎么跑
//     bash /c/Users/Lantern/neko-dotnet-env.sh build tests/ClipHarness/ClipHarness.csproj -c Debug
//     tests/ClipHarness/bin/Debug/net8.0-windows/ClipHarness.exe
//   （结果同时打印到控制台并写入 tests/ClipHarness/out.txt；会临时改写系统剪贴板，结束前还原）
//
// ■ 复用到别的“窗口型插件”
//   可传参：ClipHarness.exe <pluginDir> <outFile>
//   默认 pluginDir = <repo>/src/DesktopPet.App/bin/Debug/net8.0-windows/win-x64/plugins/com.lantern.clipboard
//   （repo 根 = 从 exe 目录向上找到含 src/DesktopPet.sln 的那层；`_messageWindow` 字段名是本工具对该插件的私有约定。）
//
// ■ 设计要点
//   · 真实 DesktopPet.Runtime.PluginLoader.LoadOne()（真实可回收 ALC，与宿主一致）。
//   · 专用 STA 线程 + 真实 Dispatcher.Run()；剪贴板用独立进程 clip.exe 写入（避开 WPF/OLE 自占用）。
//   · ALC 检查放在【另一个 Dispatcher 操作】里（此时载入/卸载方法的栈帧已返回），排除误报。
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using DesktopPet.Core;
using DesktopPet.Runtime;

namespace DesktopPet.Tests.ClipHarness;

internal static class Program
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindow(IntPtr hWnd);

    private static readonly List<string> Report = new();
    private static string _pluginDir = "";
    private static string _outFile = "";
    internal static bool SyntheticInput;
    internal static string SyntheticText = "";
    private static IntPtr _liveHandle;
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    private static WeakReference? _weakAlc;
    private static WeakReference? _weakInst;

    private static void W(string s) { Report.Add(s); Console.WriteLine(s); }

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        SyntheticInput = args.Contains("--synthetic-input");
        args = args.Where(a => a != "--synthetic-input").ToArray();
        var repoRoot = FindRepoRoot();
        _pluginDir = args.Length > 0
            ? args[0]
            : Path.Combine(repoRoot, "src", "DesktopPet.App", "bin", "Debug", "net8.0-windows", "win-x64",
                           "plugins", "com.lantern.clipboard");
        _outFile = args.Length > 1
            ? args[1]
            : Path.Combine(repoRoot, "tests", "ClipHarness", "out.txt");

        _ = typeof(IPetPlugin);
        _ = typeof(DesktopPet.Sdk.PluginBase);

        W("==== ClipboardHistory 主路径卸载验证 ====");
        W("HOST_APARTMENT=" + Thread.CurrentThread.GetApartmentState() + " (harness Main)");

        var t = new Thread(RunSta);
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();   // 等 WPF/STA 线程（含 Dispatcher）彻底退出

        GcAndReport("AFTER_THREAD_EXIT");   // 口径 B：WPF 线程已退出

        // ---- 硬判定哨兵（v0.1.87 QA 补丁）：从 Report 核对关键断言 → 机器可读结论 + 退出码 ----
        // 背景：此前本工具无论断言真假都 return 0，也不打印统一结论，自动化闸门只能靠人眼读 out.txt
        //      （run_probes.py 找 "CLIP_OK=OK" 永远落空 → 判为未知）。现补：CLIP_OK=OK/FAIL 哨兵
        //      + 失败清单 + 失败时非零退出码，CI 可直接据此 PASS/FAIL，杜绝“静默红”。
        var required = new (string Key, string Want)[]
        {
            ("LOAD1", "OK"),
            ("MAIN_PATH_ENGAGED", "True"),
            ("LOG_EVENT_DRIVEN", "True"),
            ("LIVE_LISTENER_WORKS", "True"),
            ("UNLOAD_EXCEPTION", "none"),
            ("LOG_UNREGISTER_FAIL", "False"),
            ("LOG_CLEANUP_FAIL", "False"),
            ("HWND_ISWINDOW_AFTER_UNLOAD", "False"),
            ("LISTENER_UNREGISTERED", "True"),
            ("RELOAD", "OK"),
            ("RELOAD_MAIN_PATH", "True"),
            ("HWND_RELOAD_DISTINCT", "True"),
            ("OLD_HWND_STILL_DEAD", "True"),
            ("RELOAD_WORKS", "True"),
            ("RELOAD_HWND_ISWINDOW_AFTER_UNLOAD", "False"),
            ("WPF_THREAD_ALIVE_ALC_COLLECTED", "True"),
            ("WPF_THREAD_ALIVE_AFTER_IDLE_ALC_COLLECTED", "True"),
            ("AFTER_THREAD_EXIT_ALC_COLLECTED", "True"),
            ("AFTER_THREAD_EXIT_INSTANCE_COLLECTED", "True"),
        };
        var fails = new List<string>();
        foreach (var (key, want) in required)
        {
            var hit = Report.FirstOrDefault(l => l.StartsWith(key + "=", StringComparison.Ordinal));
            var got = hit is null ? "(missing)" : hit[(key.Length + 1)..];
            if (!string.Equals(got, want, StringComparison.Ordinal))
                fails.Add($"{key}={got} (want {want})");
        }
        if (fails.Count == 0)
        {
            W("CLIP_OK=OK");
        }
        else
        {
            W("CLIP_OK=FAIL");
            foreach (var f in fails) W("  CLIP_FAIL: " + f);
        }

        try { File.WriteAllText(_outFile, string.Join("\r\n", Report), System.Text.Encoding.UTF8); }
        catch (Exception ex) { Console.WriteLine("WRITE_OUT_FAIL=" + ex.Message); }
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>从 exe 目录向上找到含 src/DesktopPet.sln 的仓库根（避免写死本机绝对路径）。</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "DesktopPet.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }

    private static List<string> ReadEntries(IPluginStorage storage)
    {
        var json = storage.Get<string>("entries") ?? "[]";
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(e => e.GetProperty("text").GetString() ?? "").ToList();
    }

    private static void GcAndReport(string tag)
    {
        for (int i = 0; i < 4; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        W($"{tag}_ALC_ALIVE=" + (_weakAlc?.IsAlive ?? false));
        W($"{tag}_INSTANCE_ALIVE=" + (_weakInst?.IsAlive ?? false));
        W($"{tag}_ALC_COLLECTED=" + !(_weakAlc?.IsAlive ?? true));
        W($"{tag}_INSTANCE_COLLECTED=" + !(_weakInst?.IsAlive ?? true));
    }

    private static void RunSta()
    {
        try
        {
            var d = Dispatcher.CurrentDispatcher;
            // op1：载入/卸载/回收场景；op2：在 op1 栈帧消失后再测 ALC（隔离误报）；op3：关泵
            d.BeginInvoke(new Action(() =>
            {
                try { RunScenario(); }
                catch (Exception ex) { W("FATAL=" + ex); }
                Dispatcher.CurrentDispatcher.BeginInvoke(new Action(CheckAlcWhileAlive), DispatcherPriority.ApplicationIdle);
            }), DispatcherPriority.Normal);
            Dispatcher.Run();
        }
        catch (Exception ex) { W("STA_FATAL=" + ex); }
    }

    /// <summary>跑消息泵 ms 毫秒（真实把 WM_CLIPBOARDUPDATE 派发给 WndProc）。</summary>
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(ms), DispatcherPriority.Background,
            (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    /// <summary>用独立进程 clip.exe 写剪贴板（避开本进程 WPF/OLE 自占用），并回读确认。</summary>
    private static bool SetClip(string text)
    {
        if (SyntheticInput)
        {
            SyntheticText = text;
            PostMessage(_liveHandle, 0x031D, IntPtr.Zero, IntPtr.Zero);
            return true;
        }
        for (int i = 0; i < 6; i++)
        {
            try
            {
                var psi = new ProcessStartInfo("C:\\Windows\\System32\\clip.exe")
                {
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi)!;
                p.StandardInput.Write(text);
                p.StandardInput.Close();
                p.WaitForExit(3000);
            }
            catch { }
            Pump(120);
            // 回读确认（读到含目标子串即算成功——clip.exe 可能带尾换行）
            try
            {
                if (System.Windows.Clipboard.ContainsText())
                {
                    var got = System.Windows.Clipboard.GetText();
                    if (got.Contains(text)) return true;
                    W($"SETCLIP_DIAG want={text} got={got.Replace("\r", "\\r").Replace("\n", "\\n")}");
                }
            }
            catch { }
        }
        W("SETCLIP_FAILED=" + text);
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunScenario()
    {
        W("STA_APARTMENT=" + Thread.CurrentThread.GetApartmentState());
        W("PLUGIN_DIR_EXISTS=" + Directory.Exists(_pluginDir));
        if (!Directory.Exists(_pluginDir)) { W("ABORT=plugin dir missing"); return; }

        // Preserve non-text clipboard formats by declining this optional live-input probe.
        var original = SyntheticInput ? null : System.Windows.Clipboard.GetDataObject();
        var formats = original?.GetFormats(false) ?? Array.Empty<string>();
        if (formats.Any(f => f is not ("Text" or "UnicodeText" or "OEMText" or "Locale")))
        { W("ABORT=clipboard contains non-text formats; left unchanged"); return; }
        string? origClip = null;
        try { if (!SyntheticInput) origClip = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null; } catch { }

        var stamp = DateTime.Now.ToString("HHmmss");
        var loader = new PluginLoader(Path.GetDirectoryName(_pluginDir)!);

        // ---------------- LOAD #1 ----------------
        var lp1 = loader.LoadOne(_pluginDir, out var err1);
        W("LOAD1=" + (lp1 is null ? "FAIL:" + err1 : "OK"));
        if (lp1 is null) { W("DONE"); return; }

        var ctx1 = new FakeContext();
        var inst1 = lp1.Instance;
        _weakAlc = new WeakReference(lp1.Context);
        _weakInst = new WeakReference(inst1);

        ctx1.Cap.Stor.Set("recordingEnabled", true);
        inst1.OnLoad(ctx1);

        W("SCHED_TIMERS_CREATED=" + ctx1.Cap.Sched.EveryCount);
        W("MAIN_PATH_ENGAGED=" + (ctx1.Cap.Sched.EveryCount == 1));
        W("LOG_EVENT_DRIVEN=" + ctx1.Cap.Sys.Logs.Any(l => l.Contains("已启用事件驱动监听")));
        W("LOG_FALLBACK_WARN=" + ctx1.Cap.Sys.Logs.Any(l => l.Contains("降级")));
        W("UI_SLOTS_REGISTERED=" + ctx1.Ui.Slots);

        var hwnd1 = GetMessageWindowHandle(inst1);
        _liveHandle = hwnd1;
        W("HWND_BEFORE_UNLOAD=" + hwnd1);

        var h1 = "NEKO-H1-" + stamp;
        var set1 = SetClip(h1);
        W("SET1_OK=" + set1);
        Pump(700);
        var hist1 = ReadEntries(ctx1.Cap.Stor);
        W("AFTER_H1_COUNT=" + hist1.Count);
        W("AFTER_H1_TOP=" + (hist1.Count > 0 ? hist1[0].Replace("\r", "").Replace("\n", "") : "(none)"));
        W("LIVE_LISTENER_WORKS=" + (hist1.Count > 0 && hist1[0].Contains(h1)));

        // ---------------- ONUNLOAD ----------------
        string uerr = "";
        try { inst1.OnUnload(); } catch (Exception ex) { uerr = ex.GetType().Name + ": " + ex.Message; }
        W("UNLOAD_EXCEPTION=" + (uerr == "" ? "none" : uerr));
        W("LOG_UNREGISTER_FAIL=" + ctx1.Cap.Sys.Logs.Any(l => l.Contains("注销剪贴板监听失败")));
        W("LOG_CLEANUP_FAIL=" + ctx1.Cap.Sys.Logs.Any(l => l.Contains("清理剪贴板监听异常")));
        Pump(250);
        W("HWND_ISWINDOW_AFTER_UNLOAD=" + IsWindow(hwnd1));

        var h2 = "NEKO-H2-" + stamp;
        var set2 = SetClip(h2);
        W("SET2_OK=" + set2);
        Pump(700);
        var hist2 = ReadEntries(ctx1.Cap.Stor);
        W("AFTER_H2_COUNT=" + hist2.Count);
        W("LISTENER_UNREGISTERED=" + (set2 && hist2.Count == hist1.Count));

        // ---------------- Dispose + Context.Unload ----------------
        lp1.Dispose();               // OnUnload(幂等) + Dispose + Context.Unload()
        lp1 = null!; inst1 = null!;
        Pump(300);
        // ALC 检查不在此处做（本方法栈帧还在）—— 交给 CheckAlcWhileAlive（另一 Dispatcher op）

        // ---------------- 重新 Enable ----------------
        var lp2 = loader.LoadOne(_pluginDir, out var err2);
        W("RELOAD=" + (lp2 is null ? "FAIL:" + err2 : "OK"));
        if (lp2 is not null)
        {
            var ctx2 = new FakeContext();
            var inst2 = lp2.Instance;
            ctx2.Cap.Stor.Set("recordingEnabled", true);
            inst2.OnLoad(ctx2);
            W("RELOAD_MAIN_PATH=" + (ctx2.Cap.Sched.EveryCount == 1));
            var hwnd2 = GetMessageWindowHandle(inst2);
            _liveHandle = hwnd2;
            W("HWND_RELOAD=" + hwnd2);
            W("HWND_RELOAD_DISTINCT=" + (hwnd2 != hwnd1 && hwnd2 != IntPtr.Zero));
            W("OLD_HWND_STILL_DEAD=" + !IsWindow(hwnd1));

            var h3 = "NEKO-H3-" + stamp;
            var set3 = SetClip(h3);
            W("SET3_OK=" + set3);
            Pump(700);
            var hist3 = ReadEntries(ctx2.Cap.Stor);
            W("AFTER_RELOAD_COUNT=" + hist3.Count);
            W("RELOAD_WORKS=" + (set3 && hist3.Count == 1 && hist3[0].Contains(h3)));

            inst2.OnUnload();
            lp2.Dispose();
            Pump(250);
            W("RELOAD_HWND_ISWINDOW_AFTER_UNLOAD=" + IsWindow(hwnd2));
        }

        try { if (!SyntheticInput) { if (origClip is not null) System.Windows.Clipboard.SetText(origClip); else System.Windows.Clipboard.Clear(); } } catch { }
        W("SCENARIO_DONE");
    }

    /// <summary>在 RunScenario 栈帧已返回后（同一 WPF 线程仍存活）测 ALC —— 排除“栈帧残留”误报。</summary>
    private static void CheckAlcWhileAlive()
    {
        Pump(300);
        GcAndReport("WPF_THREAD_ALIVE");
        Pump(400);                                  // 再多泵一会儿，看是否随 Dispatcher 空闲而释放
        GcAndReport("WPF_THREAD_ALIVE_AFTER_IDLE");
        Dispatcher.CurrentDispatcher.InvokeShutdown();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IntPtr GetMessageWindowHandle(object plugin)
    {
        var f = plugin.GetType().GetField("_messageWindow", BindingFlags.NonPublic | BindingFlags.Instance);
        var src = f?.GetValue(plugin);
        if (src is null) return IntPtr.Zero;
        var p = src.GetType().GetProperty("Handle", BindingFlags.Public | BindingFlags.Instance);
        var v = p?.GetValue(src);
        return v is IntPtr ip ? ip : IntPtr.Zero;
    }
}

// ------------------------- 最小宿主替身 -------------------------

internal sealed class FakeHandle : IScheduleHandle
{
    private bool _active = true;
    public bool IsActive => _active;
    public void Dispose() => _active = false;
}

internal sealed class FakeScheduler : IScheduler
{
    public int EveryCount { get; private set; }
    public IScheduleHandle Every(int intervalMs, Action callback) { EveryCount++; return new FakeHandle(); }
    public IScheduleHandle At(DateTime time, Action callback) => new FakeHandle();
}

internal sealed class FakeStorage : IPluginStorage
{
    private readonly Dictionary<string, object?> _map = new();
    public T? Get<T>(string key) => _map.TryGetValue(key, out var v) && v is T t ? t : default;
    public void Set<T>(string key, T value) => _map[key] = value;
    public void Remove(string key) => _map.Remove(key);
    public bool Contains(string key) => _map.ContainsKey(key);
}

internal sealed class FakeSystem : ISystem
{
    public List<string> Logs { get; } = new();
    public void Notify(string title, string body) { }
    public string? ReadClipboard()
    {
        if (Program.SyntheticInput) return Program.SyntheticText;
        try { return System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null; }
        catch { return null; }
    }
    public void WriteClipboard(string text) { try { System.Windows.Clipboard.SetText(text); } catch { } }
    public void OpenUrl(string url) { }
    public string? PickFile(string filter = "") => null;
    public void Log(PluginLogLevel level, string message, Exception? exception = null) => Logs.Add($"[{level}] {message}");
}

internal sealed class FakeEventBus : IEventBus
{
    public Action On(string evt, Action<object?> handler) => () => { };
    public void Emit(string evt, object? payload = null) { }
}

internal sealed class FakeUi : IUIApi
{
    public int Slots { get; private set; }
    public void RegisterRadialSlot(RadialSlot slot) => Slots++;
    public void RegisterTrayMenuItem(TrayMenuItem item) { }
    public void RegisterSettingsPage(ISettingsPage page) { }
    public void UnregisterAll(string scopeId) { }
}

internal sealed class FakeCapability : ICapabilityApi
{
    public FakeStorage Stor { get; } = new();
    public FakeScheduler Sched { get; } = new();
    public FakeSystem Sys { get; } = new();
    public FakeEventBus Ev { get; } = new();
    public IPluginStorage Storage => Stor;
    public IScheduler Scheduler => Sched;
    public ISystem System => Sys;
    public IEventBus Events => Ev;
}

internal sealed class FakeContext : IPluginContext
{
    public Version HostVersion => new(1, 0, 0);
    public string DataRoot => Path.GetTempPath();
    public string PluginDataDir => Path.Combine(Path.GetTempPath(), "clip-harness");
    public IPetApi Pet => null!;
    public FakeUi Ui { get; } = new();
    public FakeCapability Cap { get; } = new();
    public IUIApi UI => Ui;
    public ICapabilityApi Capability => Cap;
}
