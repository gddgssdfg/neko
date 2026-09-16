using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DesktopPet.Core;
using DesktopPet.Runtime;

namespace DesktopPet.Tests.PluginSmoke;

/// <summary>
/// 内置插件冒烟工具（审计用）。
///
/// <para><b>做什么</b>：用<b>真实的</b> <see cref="PluginLoader"/>（DesktopPet.Runtime）+ 真实的可回收 ALC，
/// 从 App 输出目录 <c>…/win-x64/plugins/</c> 逐个加载全部内置插件，对每个插件执行
/// <c>OnLoad → 空闲运行 ≥3s（让定时器 tick） → OnUnload</c>，并报告：
/// 加载成败（含确切错误）、OnLoad/OnUnload 异常（逐个列出）、加载前后
/// <c>Process.PrivateMemorySize64</c> 与 GC 堆增量、卸载后 ALC 能否回收、以及<b>闲置期 CPU 增量</b>。</para>
///
/// <para><b>安全</b>：<b>绝不调用任何槽位回调</b>（不触发截图写盘 / 媒体键 / 网络请求）。
/// 系统能力走纯内存替身（不碰真实剪贴板、不弹通知、不开浏览器）。
/// 若某插件在 <c>OnLoad</c> 阶段本身就有副作用（如 Screenshot 建数据目录、Bazi 跑本地自检），
/// 照常执行并在报告末尾「OnLoad 副作用标注」一节显式标注。</para>
///
/// <para><b>诊断开关</b>（用于二分定位「插件 ALC 收不回来」）：
/// <c>--only &lt;子串&gt;</c> 只跑匹配插件；<c>--skip-onload</c> 不调 OnLoad；
/// <c>--selftest &lt;清单&gt;</c> 不调 OnLoad、改为反射调用插件自检静态方法（<c>none|all|lunar,fortune,api,mbti,prompt,client</c>）；
/// <c>--probe &lt;none|default|owned&gt;</c> 对插件自定义类型做一次 JSON 序列化，验证 System.Text.Json 全局反射缓存的钉根机理。</para>
///
/// <para><b>退出策略</b>：结尾 <c>Console.Out.Flush()</c> 后显式 <c>Environment.Exit(code)</c>，
/// 避免遗留在途线程导致进程不退出（本仓库已知环境坑）。</para>
/// </summary>
public static class Program
{
    /// <summary>主线程 ID（用于判定定时器回调是否跑在线程池上）。</summary>
    internal static int MainThreadId = Environment.CurrentManagedThreadId;

    /// <summary>每个插件的空闲观测窗口（毫秒）。要求 ≥3000。</summary>
    private const int IdleWindowMs = 3200;

    /// <summary>已知的 OnLoad 阶段副作用标注（审计结论，仅用于报告，不改变运行行为）。</summary>
    private static readonly Dictionary<string, string> OnLoadSideEffects = new(StringComparer.OrdinalIgnoreCase)
    {
        ["com.lantern.bazi"] = "OnLoad 运行 6 项本地自检（lunar 排盘 + 运势 + MBTI 计分 + DPAPI 往返），无网络、无写盘",
        ["com.lantern.screenshot"] = "OnLoad 创建插件数据目录（写盘，路径在临时目录内）",
        ["com.lantern.clipboard"] = "OnLoad 读一次剪贴板（本工具用纯内存替身，不碰真实剪贴板）",
    };

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        MainThreadId = Environment.CurrentManagedThreadId;

        // 可选诊断开关：
        //   --only <子串>        只跑 id/目录名匹配的插件（便于快速复现单点问题）
        //   --skip-onload        只加载 + 卸载，不调 OnLoad（区分「加载即泄漏」与「OnLoad 引起泄漏」）
        //   --selftest <清单>    不调 OnLoad，改为按名字反射调用插件的自检静态方法（二分定位泄漏源）。
        //                        清单 = all | none | 逗号分隔 <lunar,fortune,api,mbti,prompt,client>
        //   --probe <模式>       不调 OnLoad，改为对「插件自定义类型」做一次 JSON 序列化，验证
        //                        System.Text.Json 全局反射缓存的钉根机理。
        //                        模式 = none | default（用全局默认 options） | owned（用新建 options）
        var only = ExtractOption(args, "--only");
        var selfTestSpec = ExtractOption(args, "--selftest");
        var probeSpec = ExtractOption(args, "--probe");
        var skipOnLoad = args.Any(a => string.Equals(a, "--skip-onload", StringComparison.OrdinalIgnoreCase));
        var repoArg = args.FirstOrDefault(a => !a.StartsWith("--")
                                               && !string.Equals(a, only, StringComparison.Ordinal)
                                               && !string.Equals(a, selfTestSpec, StringComparison.Ordinal)
                                               && !string.Equals(a, probeSpec, StringComparison.Ordinal));

        // 强制把 SDK 载入默认 ALC：插件 ALC 依赖宿主侧已加载的 DesktopPet.Sdk 做类型统一。
        _ = typeof(DesktopPet.Sdk.PluginBase).Assembly.FullName;

        var repoRoot = repoArg ?? FindRepoRoot();
        if (string.IsNullOrEmpty(repoRoot) || !Directory.Exists(repoRoot))
        {
            Console.WriteLine("EXCEPTION: 无法定位仓库根目录（可从命令行传入：dotnet PluginSmoke.dll <repoRoot>）");
            return 2;
        }

        var appOut = FindAppOutputDir(repoRoot);
        if (appOut is null)
        {
            Console.WriteLine("EXCEPTION: 未找到 App 输出目录（请先构建 src/DesktopPet.App）");
            return 2;
        }

        var pluginsRoot = Path.Combine(appOut, "plugins");
        if (!Directory.Exists(pluginsRoot))
        {
            Console.WriteLine($"EXCEPTION: 插件目录不存在: {pluginsRoot}");
            return 2;
        }

        var runRoot = Path.Combine(Path.GetTempPath(), "neko-plugin-smoke-" + Guid.NewGuid().ToString("N"));
        var dataRoot = Path.Combine(runRoot, "data");
        Directory.CreateDirectory(dataRoot);

        var dirs = Directory.EnumerateDirectories(pluginsRoot)
            .Where(d => !Path.GetFileName(d).StartsWith('_'))   // 跳过 _template 等临时目录（与 PluginLoader 一致）
            .Where(d => only is null || Path.GetFileName(d).Contains(only, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var trust = BuildTrust(dirs.Select(Path.GetFileName)! , runRoot);

        Console.WriteLine("============================================================");
        Console.WriteLine(" Neko 内置插件冒烟报告");
        Console.WriteLine($"   App 输出       : {appOut}");
        Console.WriteLine($"   插件根目录     : {pluginsRoot}");
        Console.WriteLine($"   发现插件       : {dirs.Count} 个");
        Console.WriteLine($"   空闲观测窗口   : {IdleWindowMs} ms / 插件");
        Console.WriteLine($"   宿主版本(伪造) : 1.0.0    进程位数: {(Environment.Is64BitProcess ? "x64" : "x86")}");
        if (only is not null) Console.WriteLine($"   仅跑匹配       : {only}");
        if (skipOnLoad) Console.WriteLine("   诊断模式       : --skip-onload（只加载 + 卸载，不调 OnLoad）");
        if (selfTestSpec is not null) Console.WriteLine($"   诊断模式       : --selftest {selfTestSpec}（不调 OnLoad，改按清单反射调用自检静态方法）");
        if (probeSpec is not null) Console.WriteLine($"   诊断模式       : --probe {probeSpec}（不调 OnLoad，改为对插件自定义类型做一次 JSON 序列化）");
        Console.WriteLine("   安全约束       : 不调用槽位回调；截图/媒体键/网络均不触发；不碰真实剪贴板");
        Console.WriteLine("============================================================");
        Console.WriteLine();

        var results = new List<PluginRunResult>(dirs.Count);
        var index = 0;
        foreach (var dir in dirs)
        {
            index++;
            var id = Path.GetFileName(dir);
            Console.Write($"[{index,2}/{dirs.Count}] {id,-28} ... ");
            Console.Out.Flush();

            // 基线：先强制回收，尽量让上一个插件的东西先掉下去
            SmokeMemory.ForceCollect();
            var basePriv = SmokeMemory.PrivateMb();
            var baseHeap = SmokeMemory.HeapMb();

            var r = RunOne(dir, dataRoot, trust, IdleWindowMs, skipOnLoad, selfTestSpec, probeSpec);
            r.BasePrivMb = basePriv;
            r.BaseHeapMb = baseHeap;

            // 卸载后：强制回收 ALC，再测残留
            SmokeMemory.ForceCollect();
            r.RetainedPrivMb = SmokeMemory.PrivateMb() - basePriv;
            r.RetainedHeapMb = SmokeMemory.HeapMb() - baseHeap;
            r.AlcCollected = r.AlcWeak is null ? null : !r.AlcWeak.IsAlive;

            results.Add(r);
            Console.WriteLine(r.Loaded
                ? $"loaded{(r.OnLoadOk ? "" : " [OnLoad EX]")}  cpu={r.CpuMsIdle:F0}ms  slots={r.Slots}  timers={r.TimersCreated}"
                : $"FAILED: {r.LoadError}");
        }

        Console.WriteLine();
        PrintPrimaryTable(results);
        PrintDetailTable(results);
        PrintProblemSections(results);
        PrintSelfTestSection(results);
        PrintProbeSection(results);
        PrintSideEffectNotes(results);

        var loadFailures = results.Count(r => !r.Loaded);
        var onLoadErrors = results.Count(r => r.Loaded && !r.OnLoadOk);
        var onUnloadErrors = results.Count(r => r.Loaded && !r.OnUnloadOk);

        Console.WriteLine("============================================================");
        Console.WriteLine($" 汇总：加载 {results.Count - loadFailures}/{results.Count} 成功；" +
                          $"OnLoad 异常 {onLoadErrors}；OnUnload 异常 {onUnloadErrors}");
        Console.WriteLine(loadFailures == 0 && onLoadErrors == 0 ? " 结论：PASS" : " 结论：FAIL");
        Console.WriteLine("============================================================");

        try { Directory.Delete(runRoot, true); } catch { /* 临时目录清理失败不影响结论 */ }

        Console.Out.Flush();
        // 已知环境坑：测试进程可能因遗留线程不退出 → 显式 Exit 强制结束。
        Environment.Exit(loadFailures == 0 && onLoadErrors == 0 ? 0 : 1);
        return 0; // 不会到达
    }

    // ---------------------------------------------------------------------
    //  单个插件：加载 → OnLoad → 空闲 → OnUnload → 卸载
    // ---------------------------------------------------------------------

    /// <summary>
    /// 单独加载并运行一个插件。标记 <see cref="MethodImplOptions.NoInlining"/>：
    /// 方法返回后所有持有 ALC / 插件实例的局部引用都失效，外层才能真正判定 ALC 是否被回收。
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PluginRunResult RunOne(string pluginDir, string dataRoot, PluginTrustSet trust, int idleMs, bool skipOnLoad, string? selfTestSpec, string? probeSpec)
    {
        var r = new PluginRunResult { Id = Path.GetFileName(pluginDir) };
        var loader = new PluginLoader(Path.GetDirectoryName(pluginDir)!, trust);

        var storage = new SmokeStorage(Path.Combine(dataRoot, "plugins", r.Id));
        var sched = new SmokeScheduler();
        var sys = new SmokeSystem();
        var ev = new SmokeEvents();
        var pet = new SmokePet();
        var ui = new SmokeUi();
        var cap = new SmokeCapability(storage, sched, sys, ev);

        LoadedPlugin? lp = null;
        try
        {
            lp = loader.LoadOne(pluginDir, out var loadError);
            r.LoadError = loadError;
        }
        catch (Exception ex)
        {
            r.LoadError = Short(ex);
        }

        if (lp is null)
        {
            r.Loaded = false;
            return r;
        }

        r.Loaded = true;
        r.GrantedPermissions = lp.GrantedPermissions.ToString();
        r.ManifestId = lp.Manifest.Id;
        r.HandlersBeforeLoad = ev.TotalHandlers;

        var ctx = new SmokeContext
        {
            HostVersion = new Version(1, 0, 0),
            DataRoot = dataRoot,
            PluginDataDir = storage.Dir,
            Pet = pet,
            UI = ui,
            Capability = cap,
        };

        try
        {
            if (selfTestSpec is not null)
            {
                // 诊断：不调 OnLoad，改为按清单反射调用「自检静态方法」，二分泄漏源。
                r.OnLoadOk = true;
                r.OnLoadSkipped = true;
                r.SelfTestSpec = selfTestSpec;
                var (summary, ran, failed, details) = RunSelfChecks(lp.Instance.GetType().Assembly, selfTestSpec);
                r.SelfTestSummary = summary;
                r.SelfTestRan = ran;
                r.SelfTestFailed = failed;
                r.SelfTestDetails = details;
            }
            else if (probeSpec is not null)
            {
                // 诊断：不调 OnLoad，仅对插件自定义类型做一次 JSON 序列化，验证 STJ 全局反射缓存的钉根机理。
                r.OnLoadOk = true;
                r.OnLoadSkipped = true;
                r.ProbeSpec = probeSpec;
                r.ProbeSummary = RunStjProbe(lp.Instance.GetType().Assembly, probeSpec);
            }
            else if (skipOnLoad) { r.OnLoadOk = true; r.OnLoadSkipped = true; }
            else { lp.Instance.OnLoad(ctx); r.OnLoadOk = true; }
        }
        catch (Exception ex)
        {
            r.OnLoadOk = false;
            r.OnLoadError = Short(ex);
        }

        SmokeMemory.ForceCollect();
        r.PrivAfterLoadMb = SmokeMemory.PrivateMb();
        r.HeapAfterLoadMb = SmokeMemory.HeapMb();

        // 空闲窗口：主线程睡觉，插件的定时器在线程池自行 tick
        var cpu0 = SmokeMemory.CpuMs();
        Thread.Sleep(idleMs);
        r.CpuMsIdle = SmokeMemory.CpuMs() - cpu0;

        // 采集注册与行为指标
        r.Slots = ui.RadialSlotCount;
        r.Pages = ui.SettingsPageCount;
        r.TrayItems = ui.TrayItemCount;
        r.SlotIds = ui.SlotIds;
        r.PageTitles = ui.PageTitles;
        r.TimersCreated = sched.CreatedCount;
        r.TimerCallbacksOnNonMain = sched.CallbacksOnNonMainThread;
        r.TimerCallbackErrors = sched.CallbackErrors;
        r.TimerCallbackErrorSample = sched.LastCallbackError;
        r.ClipboardReads = sys.ReadClipboardCount;
        r.Notifies = sys.NotifyCount;
        r.Says = pet.SayCount;
        r.SaysFromNonMain = pet.SayFromNonMainThread;
        r.SetMoodCalls = pet.SetMoodCount;
        r.HandlersAfterLoad = ev.TotalHandlers;

        // 卸载
        try
        {
            lp.Instance.OnUnload();
            r.OnUnloadOk = true;
        }
        catch (Exception ex)
        {
            r.OnUnloadOk = false;
            r.OnUnloadError = Short(ex);
        }

        r.TimersLeaked = sched.ActiveCount;
        r.HandlersAfterUnload = ev.TotalHandlers;

        try { lp.Instance.Dispose(); }
        catch (Exception ex) { r.DisposeError = Short(ex); }

        var alc = lp.Context;
        try { alc.Unload(); }
        catch (Exception ex) { r.UnloadError = Short(ex); }
        r.AlcWasCollectible = alc.IsCollectible;
        r.AlcWeak = new WeakReference(alc);

        // 断掉强引用（真正的回收判定交给外层 GC）
        lp = null;
        alc = null;
        _ = loader;
        return r;
    }

    // ---------------------------------------------------------------------
    //  报告输出
    // ---------------------------------------------------------------------

    private static void PrintPrimaryTable(List<PluginRunResult> rs)
    {
        Console.WriteLine("表 A｜加载 / 内存 / CPU / 卸载（一行一个插件）");
        Console.WriteLine(new string('-', 118));
        Console.WriteLine(
            Pad("id", 26) + Pad("load", 6) + Pad("onLoad", 8) + Pad("onUnload", 10) +
            Pad("dPrivMB", 10) + Pad("dHeapMB", 10) + Pad("rPrivMB", 10) + Pad("rHeapMB", 10) +
            Pad("cpuMs", 9) + Pad("alc", 6));
        Console.WriteLine(new string('-', 118));
        foreach (var r in rs)
        {
            var dPriv = r.Loaded ? r.PrivAfterLoadMb - r.BasePrivMb : double.NaN;
            var dHeap = r.Loaded ? r.HeapAfterLoadMb - r.BaseHeapMb : double.NaN;
            Console.WriteLine(
                Pad(r.Id, 26) +
                Pad(r.Loaded ? "OK" : "FAIL", 6) +
                Pad(r.Loaded ? (r.OnLoadOk ? "OK" : "EX") : "-", 8) +
                Pad(r.Loaded ? (r.OnUnloadOk ? "OK" : "EX") : "-", 10) +
                Pad(r.Loaded ? dPriv.ToString("F2") : "-", 10) +
                Pad(r.Loaded ? dHeap.ToString("F2") : "-", 10) +
                Pad(r.Loaded ? r.RetainedPrivMb.ToString("F2") : "-", 10) +
                Pad(r.Loaded ? r.RetainedHeapMb.ToString("F2") : "-", 10) +
                Pad(r.Loaded ? r.CpuMsIdle.ToString("F0") : "-", 9) +
                Pad(r.Loaded ? (r.AlcCollected == true ? "Y" : "N") : "-", 6));
        }
        Console.WriteLine(new string('-', 118));
        Console.WriteLine("  dPrivMB/dHeapMB = OnLoad 后 相对 加载前 的进程私有内存 / GC 堆增量");
        Console.WriteLine("  rPrivMB/rHeapMB = OnUnload+GC 后 的残留（≈负值代表压缩回收；明显正值 = 卸载没退干净）");
        Console.WriteLine("  cpuMs           = 空闲窗口内的进程累计 CPU 增量（越大越说明「闲着也烧 CPU」）");
        Console.WriteLine("  alc             = 卸载并强制 GC 后 ALC 是否真的被回收（Y=可回收，N=有根引用残留）");
        Console.WriteLine();
    }

    private static void PrintDetailTable(List<PluginRunResult> rs)
    {
        Console.WriteLine("表 B｜注册与闲置行为明细");
        Console.WriteLine(new string('-', 138));
        Console.WriteLine(
            Pad("id", 26) + Pad("slot", 6) + Pad("page", 6) + Pad("tray", 6) +
            Pad("tmrC", 6) + Pad("tmrL", 6) + Pad("clip", 6) + Pad("say", 6) +
            Pad("say!", 6) + Pad("cbThr", 7) + Pad("cbErr", 7) + Pad("notify", 7) + Pad("activeTimers", 13));
        Console.WriteLine(new string('-', 138));
        foreach (var r in rs)
        {
            if (!r.Loaded) { Console.WriteLine(Pad(r.Id, 26) + "(未加载)"); continue; }
            Console.WriteLine(
                Pad(r.Id, 26) +
                Pad(r.Slots.ToString(), 6) +
                Pad(r.Pages.ToString(), 6) +
                Pad(r.TrayItems.ToString(), 6) +
                Pad(r.TimersCreated.ToString(), 6) +
                Pad(r.TimersLeaked.ToString(), 6) +
                Pad(r.ClipboardReads.ToString(), 6) +
                Pad(r.Says.ToString(), 6) +
                Pad(r.SaysFromNonMain.ToString(), 6) +
                Pad(r.TimerCallbacksOnNonMain.ToString(), 7) +
                Pad(r.TimerCallbackErrors.ToString(), 7) +
                Pad(r.Notifies.ToString(), 7) +
                Pad(r.TimersLeaked.ToString(), 13));
        }
        Console.WriteLine(new string('-', 138));
        Console.WriteLine("  slot/page/tray = OnLoad 注册的径向槽位 / 设置页 / 托盘项数量");
        Console.WriteLine("  tmrC = 空闲期创建的定时器数；tmrL = OnUnload 后仍未 Dispose 的定时器数（>0 = 泄漏）");
        Console.WriteLine("  clip = 空闲期 ReadClipboard 调用次数；say/notify = 空闲期 Pet.Say / System.Notify 次数");
        Console.WriteLine("  say!/cbThr = Say 调用 / 定时回调 发生在「非主线程」上的次数（>0 = 宿主 Pet.Say 跨线程隐患）");
        Console.WriteLine();
    }

    private static void PrintProblemSections(List<PluginRunResult> rs)
    {
        Console.WriteLine("问题清单");
        Console.WriteLine(new string('-', 78));

        var loadFails = rs.Where(r => !r.Loaded).ToList();
        Console.WriteLine($"[加载失败] {loadFails.Count} 个");
        if (loadFails.Count == 0) Console.WriteLine("  （无）");
        foreach (var r in loadFails) Console.WriteLine($"  · {r.Id}: {r.LoadError}");

        Console.WriteLine($"[OnLoad 抛异常] {rs.Count(r => r.Loaded && !r.OnLoadOk)} 个");
        var onLoad = rs.Where(r => r.Loaded && !r.OnLoadOk).ToList();
        if (onLoad.Count == 0) Console.WriteLine("  （无）");
        foreach (var r in onLoad) Console.WriteLine($"  · {r.Id}: {r.OnLoadError}");

        Console.WriteLine($"[OnUnload 抛异常] {rs.Count(r => r.Loaded && !r.OnUnloadOk)} 个");
        var onUnload = rs.Where(r => r.Loaded && !r.OnUnloadOk).ToList();
        if (onUnload.Count == 0) Console.WriteLine("  （无）");
        foreach (var r in onUnload) Console.WriteLine($"  · {r.Id}: {r.OnUnloadError}");

        Console.WriteLine($"[Dispose/ALC.Unload 抛异常] {rs.Count(r => r.DisposeError is not null || r.UnloadError is not null)} 个");
        var disposeErrors = rs.Where(r => r.DisposeError is not null || r.UnloadError is not null).ToList();
        if (disposeErrors.Count == 0) Console.WriteLine("  （无）");
        foreach (var r in disposeErrors)
            Console.WriteLine($"  · {r.Id}: dispose={r.DisposeError ?? "-"} unload={r.UnloadError ?? "-"}");

        Console.WriteLine($"[定时器泄漏（OnUnload 后仍有活动句柄）] {rs.Count(r => r.Loaded && r.TimersLeaked > 0)} 个");
        foreach (var r in rs.Where(r => r.Loaded && r.TimersLeaked > 0))
            Console.WriteLine($"  · {r.Id}: leaked={r.TimersLeaked}");

        Console.WriteLine($"[事件未解绑（OnUnload 后处理器数 > 加载前）] {rs.Count(r => r.Loaded && r.HandlersAfterUnload > r.HandlersBeforeLoad)} 个");
        foreach (var r in rs.Where(r => r.Loaded && r.HandlersAfterUnload > r.HandlersBeforeLoad))
            Console.WriteLine($"  · {r.Id}: before={r.HandlersBeforeLoad} afterLoad={r.HandlersAfterLoad} afterUnload={r.HandlersAfterUnload}");

        Console.WriteLine($"[ALC 未能回收（强制 GC 后弱引用仍存活）] {rs.Count(r => r.Loaded && r.AlcCollected == false)} 个");
        foreach (var r in rs.Where(r => r.Loaded && r.AlcCollected == false))
            Console.WriteLine($"  · {r.Id}: IsCollectible={r.AlcWasCollectible}，强引用可能残留（静态字段 / 未解绑委托 / 在途 Timer）");

        Console.WriteLine("[常驻内存增量 Top（OnLoad 后 进程私有内存增量）]");
        foreach (var r in rs.Where(r => r.Loaded).OrderByDescending(r => r.PrivAfterLoadMb - r.BasePrivMb).Take(5))
            Console.WriteLine($"  · {r.Id,-26} dPriv={(r.PrivAfterLoadMb - r.BasePrivMb):F2} MB  dHeap={(r.HeapAfterLoadMb - r.BaseHeapMb):F2} MB");

        Console.WriteLine("[闲置 CPU 增量 Top]");
        foreach (var r in rs.Where(r => r.Loaded).OrderByDescending(r => r.CpuMsIdle).Take(5))
            Console.WriteLine($"  · {r.Id,-26} cpu={r.CpuMsIdle:F0} ms / {IdleWindowMs} ms  timers={r.TimersCreated}  clip={r.ClipboardReads}");

        Console.WriteLine("[定时器回调抛异常]");
        var cbErr = rs.Where(r => r.Loaded && r.TimerCallbackErrors > 0).ToList();
        if (cbErr.Count == 0) Console.WriteLine("  （无）");
        foreach (var r in cbErr)
            Console.WriteLine($"  · {r.Id}: {r.TimerCallbackErrors} 次，例：{r.TimerCallbackErrorSample}");
        Console.WriteLine();
    }

    private static void PrintSideEffectNotes(List<PluginRunResult> rs)
    {
        Console.WriteLine("OnLoad 副作用标注（本工具已运行这些 OnLoad）");
        Console.WriteLine(new string('-', 78));
        foreach (var r in rs.Where(r => r.Loaded))
        {
            var note = OnLoadSideEffects.TryGetValue(r.Id, out var n) ? n : "仅注册 UI / 读 Storage，无外部副作用";
            Console.WriteLine($"  · {r.Id,-26} {note}");
        }
        Console.WriteLine("  说明：本工具的系统能力全部为纯内存替身，所谓「写盘」仅落在临时目录；");
        Console.WriteLine("        槽位回调（截图 / 媒体键 / 联网 / 打开浏览器）一律未调用。");
        Console.WriteLine();
    }

    // ---------------------------------------------------------------------
    //  自检二分（--selftest）
    // ---------------------------------------------------------------------

    /// <summary>
    /// Bazi 插件 OnLoad 里跑的 6 项自检静态方法（与 <c>BaziPlugin.cs:35-40</c> 一一对应）。
    /// key 供 <c>--selftest</c> 清单引用：lunar / fortune / api / mbti / prompt / client。
    /// </summary>
    private static readonly (string Key, string Type, string Method)[] SelfChecks =
    {
        ("lunar",   "DesktopPet.Builtin.Bazi.BaziEngine",         "VerifyKnownCases"), // BaziPlugin.cs:35（唯一触碰第三方 lunar.dll）
        ("fortune", "DesktopPet.Builtin.Bazi.DailyFortuneBuilder", "SelfCheck"),        // BaziPlugin.cs:36
        ("api",     "DesktopPet.Builtin.Bazi.ApiKeyProtector",     "SelfCheck"),        // BaziPlugin.cs:37（DPAPI）
        ("mbti",    "DesktopPet.Builtin.Bazi.MbtiQuiz",            "SelfCheck"),        // BaziPlugin.cs:38
        ("prompt",  "DesktopPet.Builtin.Bazi.FortunePrompt",       "SelfCheck"),        // BaziPlugin.cs:39
        ("client",  "DesktopPet.Builtin.Bazi.DeepSeekClient",      "SelfCheck"),        // BaziPlugin.cs:40
    };

    /// <summary>
    /// 在**插件 ALC 已加载的程序集**上，按清单反射调用自检静态方法（不调 OnLoad）。
    /// 用于二分「到底是哪一项 OnLoad 自检建立了进程级强根，钉住整个插件 ALC」。
    /// </summary>
    private static (string Summary, int Ran, int Failed, List<string> Details) RunSelfChecks(Assembly asm, string spec)
    {
        var details = new List<string>();
        var selected = ParseSelfTestSpec(spec);
        var ran = 0;
        var failed = 0;

        foreach (var entry in SelfChecks)
        {
            if (!selected.Contains(entry.Key)) continue;
            try
            {
                var type = asm.GetType(entry.Type, throwOnError: false);
                var mi = type?.GetMethod(entry.Method, BindingFlags.Public | BindingFlags.Static);
                if (mi is null)
                {
                    failed++;
                    details.Add($"{entry.Key,-7} = <未找到 {entry.Type}.{entry.Method}>");
                    continue;
                }

                var result = mi.Invoke(null, null) as string ?? "(null)";
                ran++;
                if (!string.Equals(result, "OK", StringComparison.Ordinal)) failed++;
                details.Add($"{entry.Key,-7} = {result}");
            }
            catch (TargetInvocationException tie)
            {
                failed++;
                details.Add($"{entry.Key,-7} = EX {Short(tie.InnerException ?? tie)}");
            }
            catch (Exception ex)
            {
                failed++;
                details.Add($"{entry.Key,-7} = EX {Short(ex)}");
            }
        }

        var summary = $"{ran}/{selected.Count} 执行, {failed} 失败";
        return (summary, ran, failed, details);
    }

    /// <summary>
    /// STJ 钉根机理探针：对**插件 ALC 里的自定义类型**做一次 JSON 序列化，对比两种 options 来源。
    /// <list type="bullet">
    ///   <item><c>none</c> —— 不序列化（基线）。</item>
    ///   <item><c>default</c> —— 用 <c>JsonSerializerOptions.Default</c>（进程全局静态，位于默认 ALC）。</item>
    ///   <item><c>owned</c> —— 用插件自己 new 出来的 <c>JsonSerializerOptions</c>（随插件一起被回收）。</item>
    /// </list>
    /// 这是把「<c>--selftest client=N</c>」的相关性升级为因果性的对照实验：
    /// 若 default=N 而 owned=Y、none=Y，则钉根就是 STJ 的<b>全局反射缓存</b>。
    /// </summary>
    private static string RunStjProbe(Assembly asm, string mode)
    {
        if (mode.Equals("none", StringComparison.OrdinalIgnoreCase))
            return "未序列化（基线）";

        var cmType = asm.GetType("DesktopPet.Builtin.Bazi.ChatMessage", throwOnError: false);
        if (cmType is null)
            return $"未找到 ChatMessage 类型（{asm.GetName().Name} 无此类型，跳过）";

        var instance = Activator.CreateInstance(cmType, "user", "你好");
        var owned = mode.Equals("owned", StringComparison.OrdinalIgnoreCase);
        var options = owned ? new JsonSerializerOptions() : null;
        var json = JsonSerializer.Serialize(instance, cmType, options);
        var which = owned ? "新建 JsonSerializerOptions（插件自持）" : "JsonSerializerOptions.Default（进程全局）";
        return $"序列化 ChatMessage via {which} → {json}";
    }

    /// <summary>解析 <c>--selftest</c> 清单：none（空集）/ all（全部 6 项）/ 逗号分隔的 key 子集。</summary>
    private static HashSet<string> ParseSelfTestSpec(string spec)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var s = (spec ?? "").Trim();
        if (s.Length == 0 || s.Equals("none", StringComparison.OrdinalIgnoreCase)) return set;
        if (s.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var e in SelfChecks) set.Add(e.Key);
            return set;
        }
        foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            set.Add(part);
        return set;
    }

    private static void PrintSelfTestSection(List<PluginRunResult> rs)
    {
        if (!rs.Any(r => r.SelfTestSpec is not null)) return;
        Console.WriteLine("自检二分结果（--selftest：不调 OnLoad，仅反射调用指定自检静态方法）");
        Console.WriteLine(new string('-', 78));
        foreach (var r in rs.Where(r => r.SelfTestSpec is not null))
        {
            Console.WriteLine($"  · {r.Id}  spec={r.SelfTestSpec}  执行 {r.SelfTestSummary}  ALC回收={(r.AlcCollected == true ? "Y" : "N")}");
            foreach (var d in r.SelfTestDetails) Console.WriteLine($"      - {d}");
        }
        Console.WriteLine("  说明：ALC回收=Y =「仅跑这些自检」不钉住插件 ALC；=N = 这些自检本身建立了进程级强根（二分收敛点）。");
        Console.WriteLine();
    }

    private static void PrintProbeSection(List<PluginRunResult> rs)
    {
        if (!rs.Any(r => r.ProbeSpec is not null)) return;
        Console.WriteLine("STJ 钉根机理探针（--probe：不调 OnLoad，仅对插件自定义类型做一次 JSON 序列化）");
        Console.WriteLine(new string('-', 78));
        foreach (var r in rs.Where(r => r.ProbeSpec is not null))
        {
            Console.WriteLine($"  · {r.Id}  probe={r.ProbeSpec}  ALC回收={(r.AlcCollected == true ? "Y" : "N")}");
            Console.WriteLine($"      {r.ProbeSummary}");
        }
        Console.WriteLine("  说明：default=N 而 owned=Y、none=Y ⇒ 钉根确为 System.Text.Json 的进程全局反射缓存。");
        Console.WriteLine();
    }

    // ---------------------------------------------------------------------
    //  基础设施
    // ---------------------------------------------------------------------

    /// <summary>从输出目录向上定位仓库根（含 src/DesktopPet.App）。</summary>
    private static string? FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var i = 0; i < 14 && !string.IsNullOrEmpty(dir); i++)
        {
            if (Directory.Exists(Path.Combine(dir, "src", "DesktopPet.App"))) return dir;
            var parent = Path.GetDirectoryName(dir);
            if (parent is null || parent == dir) break;
            dir = parent;
        }
        return null;
    }

    /// <summary>定位 DesktopPet.App 的输出目录（含 plugins/）：兼容 Debug/Release × win-x64/无 RID。</summary>
    private static string? FindAppOutputDir(string repoRoot)
    {
        var bin = Path.Combine(repoRoot, "src", "DesktopPet.App", "bin");
        foreach (var cfg in new[] { "Debug", "Release" })
        foreach (var rid in new[] { "win-x64", "" })
        {
            var cand = Path.Combine(bin, cfg, "net8.0-windows", rid);
            if (Directory.Exists(Path.Combine(cand, "plugins"))) return cand;
        }
        return null;
    }

    /// <summary>为全部内置插件合成一张信任清单（等价宿主随包携带的 builtins.lock.json，仅按 id 信任）。</summary>
    private static PluginTrustSet BuildTrust(IEnumerable<string> ids, string tempDir)
    {
        var items = string.Join(", ", ids.Select(id => "{\"id\":" + JsonSerializer.Serialize(id) + "}"));
        var json = "{\"schema\":1,\"builtins\":[" + items + "]}";
        var path = Path.Combine(tempDir, PluginTrustSet.DefaultFileName);
        File.WriteAllText(path, json);
        return PluginTrustSet.Load(path, out _);
    }

    private static string Short(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    /// <summary>取 <c>--name value</c> 形式的选项值。</summary>
    private static string? ExtractOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    private static string Pad(string s, int width) => (s ?? "").Length >= width ? s! : s!.PadRight(width);
}

/// <summary>单个插件的运行结果。</summary>
internal sealed class PluginRunResult
{
    public string Id { get; init; } = "";

    // 加载
    public bool Loaded { get; set; }
    public string? LoadError { get; set; }
    public string ManifestId { get; set; } = "";
    public string GrantedPermissions { get; set; } = "";

    // 生命周期
    public bool OnLoadOk { get; set; }
    public bool OnLoadSkipped { get; set; }
    public string? OnLoadError { get; set; }
    public bool OnUnloadOk { get; set; }
    public string? OnUnloadError { get; set; }
    public string? DisposeError { get; set; }
    public string? UnloadError { get; set; }

    // 自检二分（--selftest）
    public string? SelfTestSpec { get; set; }
    public string SelfTestSummary { get; set; } = "";
    public int SelfTestRan { get; set; }
    public int SelfTestFailed { get; set; }
    public List<string> SelfTestDetails { get; set; } = new();

    // STJ 钉根机理探针（--probe）
    public string? ProbeSpec { get; set; }
    public string ProbeSummary { get; set; } = "";

    // 内存（MB）
    public double BasePrivMb { get; set; }
    public double BaseHeapMb { get; set; }
    public double PrivAfterLoadMb { get; set; }
    public double HeapAfterLoadMb { get; set; }
    public double RetainedPrivMb { get; set; }
    public double RetainedHeapMb { get; set; }

    // CPU
    public double CpuMsIdle { get; set; }

    // 注册与行为
    public int Slots { get; set; }
    public int Pages { get; set; }
    public int TrayItems { get; set; }
    public List<string> SlotIds { get; set; } = new();
    public List<string> PageTitles { get; set; } = new();
    public int TimersCreated { get; set; }
    public int TimersLeaked { get; set; }
    public int TimerCallbacksOnNonMain { get; set; }
    public int TimerCallbackErrors { get; set; }
    public string? TimerCallbackErrorSample { get; set; }
    public int ClipboardReads { get; set; }
    public int Notifies { get; set; }
    public int Says { get; set; }
    public int SaysFromNonMain { get; set; }
    public int SetMoodCalls { get; set; }
    public int HandlersBeforeLoad { get; set; }
    public int HandlersAfterLoad { get; set; }
    public int HandlersAfterUnload { get; set; }

    // ALC 卸载
    public bool AlcWasCollectible { get; set; }
    public bool? AlcCollected { get; set; }
    public WeakReference? AlcWeak { get; set; }
}
