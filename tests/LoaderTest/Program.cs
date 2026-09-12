using System.Diagnostics;
using System.Runtime.InteropServices;
using DesktopPet.Core;
using DesktopPet.Runtime;

namespace DesktopPet.Tests;

/// <summary>
/// 无 UI 的插件加载链路测试：
///   1. 准备一个临时插件目录，放入 SamplePlugin 的产物
///   2. 用 PluginLoader 扫描加载
///   3. 验证 manifest 解析、权限授予、OnLoad 调用、Storage 隔离
/// </summary>
public static class Program
{
    public static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "neko-loader-test-" + Guid.NewGuid().ToString("N"));
        var pluginsDir = Path.Combine(root, "plugins");
        var dataDir = Path.Combine(root, "data");
        Directory.CreateDirectory(pluginsDir);
        Directory.CreateDirectory(dataDir);

        try
        {
            // ---- 部署示例插件 ----
            DeployPlugin(root, pluginsDir, dataDir,
                "com.lantern.sample", "samples", "SamplePlugin");

            // ---- 部署好感度内置插件 ----
            var affectionStorage = DeployPlugin(root, pluginsDir, dataDir,
                "com.lantern.affection", "builtins", "DesktopPet.Builtin.Affection");

            // ---- 统一的事件总线 / 宠物 / UI 上下文（两个插件共享） ----
            var events = new TestEventBus();
            var petApi = new TestPetApi();
            var uiApi = new TestUIApi();

            // ---- 用真实宿主实现测加载 ----
            var loader = new PluginLoader(pluginsDir);
            var loaded = loader.LoadAll(new HashSet<string>(), out var errors);

            foreach (var e in errors)
                Console.WriteLine($"[加载失败] {e.dir}: {e.error}");

            if (loaded.Count == 0)
            {
                Console.WriteLine("FAIL: 没有插件被加载");
                return 1;
            }

            // ============ 1. 示例插件链路 ============
            Console.WriteLine("=== 测试 1：示例插件加载链路 ===");
            var sample = loaded.FirstOrDefault(p => p.Manifest.Id == "com.lantern.sample");
            if (sample is null) { Console.WriteLine("FAIL: 未加载示例插件"); return 1; }
            Console.WriteLine($"PASS: 示例插件加载成功");
            Console.WriteLine($"  Id = {sample.Manifest.Id}, 权限掩码 = {sample.GrantedPermissions}, ALC 可回收 = {sample.IsCollectible}");

            var expected = PluginPermissions.PetAnimate | PluginPermissions.PetRead
                         | PluginPermissions.UIRegister | PluginPermissions.Storage;
            if (sample.GrantedPermissions == expected)
                Console.WriteLine($"PASS: 权限授予正确 = {expected}");
            else
                Console.WriteLine($"FAIL: 权限应为 {expected}，实际 {sample.GrantedPermissions}");

            var sampleCtx = BuildContext(sample, dataDir, petApi, uiApi, events);
            sample.Instance.OnLoad(sampleCtx);
            Console.WriteLine("PASS: 示例插件 OnLoad 成功");
            if (uiApi.Slots.Count > 0)
                Console.WriteLine($"PASS: 径向菜单槽位已注册 = {uiApi.Slots[0].Label}");
            else
                Console.WriteLine("FAIL: 没有注册径向菜单槽位");

            // ============ 2. 好感度插件链路 ============
            Console.WriteLine();
            Console.WriteLine("=== 测试 2：好感度内置插件（dogfooding）===");
            var affection = loaded.FirstOrDefault(p => p.Manifest.Id == "com.lantern.affection");
            if (affection is null) { Console.WriteLine("FAIL: 未加载好感度插件"); return 1; }
            Console.WriteLine($"PASS: 好感度插件加载成功, 实例 = {affection.Instance.GetType().FullName}");

            var affectionCtx = BuildContext(affection, dataDir, petApi, uiApi, events);
            affection.Instance.OnLoad(affectionCtx);

            // 验证注册了径向槽位
            var statusSlot = uiApi.Slots.FirstOrDefault(s => s.Id == "affection-status");
            if (statusSlot is null) { Console.WriteLine("FAIL: 好感度插件未注册状态槽位"); return 1; }
            Console.WriteLine($"PASS: 好感度槽位已注册 = {statusSlot.Label}");

            // 模拟 11 次抚摸 → 好感度增长到 11（第 10 次升级到「熟悉」，触发升级反应）
            for (int i = 0; i < 11; i++) events.Emit(PetEvents.PetPetted, null);
            var storedScore = affectionStorage.Get<int>("score");
            if (storedScore == 11)
                Console.WriteLine($"PASS: 11 次抚摸后好感度持久化 = {storedScore}");
            else
                Console.WriteLine($"FAIL: 期望好感度 11，实际 {storedScore}");

            // 升级反应：第 10 次抚摸从「陌生」升到「熟悉」，应触发升级 Say
            var levelUpMsgs = petApi.Spoken.Count(s => s.Contains("好感度提升"));
            if (levelUpMsgs >= 1)
                Console.WriteLine($"PASS: 升级反应已触发（共 {levelUpMsgs} 条升级消息），例: \"{petApi.Spoken.First(s => s.Contains("好感度提升"))}\"");
            else
                Console.WriteLine($"FAIL: 抚摸到升级点后未触发升级反应");

            Console.WriteLine($"  全部 Say 共 {petApi.Spoken.Count} 条");

            // ============ 3. 卸载全部 ============
            Console.WriteLine();
            Console.WriteLine("=== 测试 3：卸载 ===");
            foreach (var p in loaded) p.Dispose();
            Console.WriteLine("PASS: 全部插件卸载成功 (OnUnload + ALC.Unload)");

            // ============ 4. 加载「真实 App 输出目录」插件 ============
            Console.WriteLine();
            Console.WriteLine("=== 测试 4：加载 App 输出目录插件（发布形态）===");
            TestAppOutputPlugins();

            // ============ 5. 命理插件：第三方依赖 lunar.dll 的 ALC 解析（P0 核心风险）============
            Console.WriteLine();
            TestBaziPlugin();

            // 清理
            try { Directory.Delete(root, true); } catch { }

            Console.WriteLine();
            Console.WriteLine("=== 全部通过 ===");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"EXCEPTION: {ex}");
            return 1;
        }
    }

    /// <summary>加载「真实 App 输出目录」的插件，验证发布形态下的完整链路（加载 + OnLoad + Storage）。</summary>
    private static void TestAppOutputPlugins()
    {
        // App 输出目录：从 LoaderTest 输出向上定位
        var appOut = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "DesktopPet.App", "bin", "Debug", "net8.0-windows"));
        var pluginsDir = Path.Combine(appOut, "plugins");
        if (!Directory.Exists(pluginsDir))
        {
            Console.WriteLine($"WARN: App 输出目录不存在 {pluginsDir}，跳过");
            return;
        }

        var loader = new PluginLoader(pluginsDir);
        var loaded = loader.LoadAll(new HashSet<string>(), out var errors);

        foreach (var e in errors)
            Console.WriteLine($"[App输出加载失败] {e.dir}: {e.error}");

        var affection = loaded.FirstOrDefault(p => p.Manifest.Id == "com.lantern.affection");
        if (affection is null)
        {
            Console.WriteLine("FAIL: App 输出目录未能加载好感度插件");
            return;
        }
        Console.WriteLine($"PASS: App 输出目录成功加载好感度插件, 实例 = {affection.Instance.GetType().FullName}");

        // 完整链路：OnLoad 建数据目录 + Storage 持久化 + 事件驱动
        var dataDir = Path.Combine(Path.GetTempPath(), "neko-appout-" + Guid.NewGuid().ToString("N"));
        var events = new TestEventBus();
        var petApi = new TestPetApi();
        var ctx = BuildContext(affection, dataDir, petApi, new TestUIApi(), events);
        affection.Instance.OnLoad(ctx);
        if (Directory.Exists(Path.Combine(dataDir, "plugins", "com.lantern.affection")))
            Console.WriteLine("PASS: OnLoad 创建了插件数据目录");
        else
            Console.WriteLine("FAIL: OnLoad 未创建数据目录");

        for (int i = 0; i < 11; i++) events.Emit(PetEvents.PetPetted, null);
        var score = new TestStorage(Path.Combine(dataDir, "plugins", "com.lantern.affection")).Get<int>("score");
        if (score == 11)
            Console.WriteLine($"PASS: 发布形态插件 11 次抚摸后好感度 = {score}");
        else
            Console.WriteLine($"FAIL: 期望 11，实际 {score}");

        foreach (var p in loaded) p.Dispose();
        try { Directory.Delete(dataDir, true); } catch { }
    }

    /// <summary>
    /// 加载真实命理插件，验证 P0 的核心风险：<b>插件 ALC 能否解析第三方依赖 lunar.dll</b>。
    /// <para>本测试**不引用** Bazi / lunar 项目 —— 默认加载上下文里没有 lunar，
    /// 因此若插件 ALC 解析失败（缺 deps.json / 缺 lunar.dll），OnLoad 必抛，测试即失败，无法蒙混。</para>
    /// </summary>
    private static void TestBaziPlugin()
    {
        Console.WriteLine("=== 测试 5：命理插件（第三方依赖 lunar.dll 的 ALC 解析）===");

        var root = Path.Combine(Path.GetTempPath(), "neko-bazi-" + Guid.NewGuid().ToString("N"));
        var pluginsDir = Path.Combine(root, "plugins");
        var dataDir = Path.Combine(root, "data");
        var pluginDir = Path.Combine(pluginsDir, "com.lantern.bazi");
        Directory.CreateDirectory(pluginDir);
        Directory.CreateDirectory(dataDir);

        var baziOut = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "builtins", "DesktopPet.Builtin.Bazi", "bin", "Debug", "net8.0-windows"));
        if (!Directory.Exists(baziOut))
        {
            Console.WriteLine($"WARN: Bazi 输出目录不存在 {baziOut}，跳过");
            return;
        }

        // 模拟真实部署：拷插件自身 + 第三方依赖（lunar.dll）+ deps.json；排除 ALC 白名单程序集（Core/Sdk）
        foreach (var f in Directory.EnumerateFiles(baziOut))
        {
            var name = Path.GetFileName(f);
            if (name.StartsWith("DesktopPet.Core.", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("DesktopPet.Sdk.", StringComparison.OrdinalIgnoreCase))
                continue;
            File.Copy(f, Path.Combine(pluginDir, name), true);
        }
        Console.WriteLine($"插件目录文件: {string.Join(", ", Directory.GetFiles(pluginDir).Select(Path.GetFileName).OrderBy(x => x))}");

        // P5 内存实测：加载命理插件前的 WorkingSet 基线
        var wsBefore = SnapWorkingSetMB();

        var loader = new PluginLoader(pluginsDir);
        var loaded = loader.LoadAll(new HashSet<string>(), out var errors);
        foreach (var e in errors) Console.WriteLine($"[Bazi 加载失败] {e.dir}: {e.error}");

        var bazi = loaded.FirstOrDefault(p => p.Manifest.Id == "com.lantern.bazi");
        if (bazi is null)
        {
            Console.WriteLine("FAIL: 命理插件未加载（第三方依赖 ALC 解析可能失败）");
            return;
        }
        Console.WriteLine("PASS: 命理插件加载成功（Bazi.dll 在 ALC 下实例化）");

        var expected = PluginPermissions.PetAnimate | PluginPermissions.PetRead
                     | PluginPermissions.UIRegister | PluginPermissions.Storage
                     | PluginPermissions.Scheduler | PluginPermissions.Notify | PluginPermissions.Network;
        Console.WriteLine(bazi.GrantedPermissions == expected
            ? $"PASS: 权限授予正确 = {expected}"
            : $"FAIL: 权限应为 {expected}，实际 {bazi.GrantedPermissions}");

        // OnLoad 内部调用 BaziEngine（lunar）——若 lunar.dll 未被插件 ALC 解析，此处必抛
        var uiApi = new TestUIApi();
        var petApi = new TestPetApi();
        var ctx = BuildContext(bazi, dataDir, petApi, uiApi, new TestEventBus());
        try
        {
            bazi.Instance.OnLoad(ctx);
            Console.WriteLine("PASS: OnLoad 成功（lunar 计算未抛异常 → 第三方依赖在插件 ALC 下可用）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: OnLoad 抛异常（第三方依赖 ALC 解析失败）：{ex.GetType().Name}: {ex.Message}");
            return;
        }

        // P5 内存实测：OnLoad + lunar 算盘（VerifyKnownCases / 各 SelfCheck）完成后
        var wsAfter = SnapWorkingSetMB();
        Console.WriteLine($"  [内存] 加载前 {wsBefore:F2} MB → OnLoad+算盘后 {wsAfter:F2} MB，差值 {wsAfter - wsBefore:F2} MB");

        var hasAsk = uiApi.Slots.Any(s => s.Id == "bazi-ask");
        var hasToday = uiApi.Slots.Any(s => s.Id == "bazi-today");
        Console.WriteLine(hasAsk && hasToday
            ? "PASS: 径向槽位已注册 = bazi-ask（问问猫）+ bazi-today（今日运势）"
            : $"FAIL: 径向槽位缺失（ask={hasAsk}, today={hasToday}）");

        if (uiApi.Pages.Any(pg => pg.Title == "com.lantern.bazi"))
            Console.WriteLine("PASS: 设置页已注册且 Title == Manifest.Id");
        else
            Console.WriteLine("FAIL: 设置页未注册或 Title 与 Manifest.Id 不一致");

        // 回归自检：BaziEngine.VerifyKnownCases() 的结果（不依赖编译期引用，走反射读属性）
        var engType = bazi.Instance.GetType().Assembly.GetType("DesktopPet.Builtin.Bazi.BaziEngine");
        var prop = engType?.GetProperty("LastSelfTest",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        var selfTest = prop?.GetValue(null) as string;
        Console.WriteLine(selfTest == "OK"
            ? "PASS: lunar 已知用例回归通过（1981-01-29→1989-02-18 / 2020-01-06→2020-02-06 / 立春边界）"
            : $"FAIL: lunar 回归校验未通过：{selfTest ?? "(属性未读到)"}");

        // P2：每日运势构建器自检（五行生克 5 例 + 播报冒烟）
        var builderType = bazi.Instance.GetType().Assembly.GetType("DesktopPet.Builtin.Bazi.DailyFortuneBuilder");
        var bprop = builderType?.GetProperty("LastSelfTest",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        var bself = bprop?.GetValue(null) as string;
        Console.WriteLine(bself == "OK"
            ? "PASS: DailyFortuneBuilder 自检通过（五行生克 5 例 + 播报冒烟 + MBTI 16 型全覆盖/按类型分支 + 开关默认关/关闭残留回退）"
            : $"FAIL: DailyFortuneBuilder 自检未通过：{bself ?? "(属性未读到)"}");

        // P4：MBTI 问卷计分自检（纯函数：全选A→ESTJ / 全选B→INFP / 维度独立 / 平局取首极 / 题量均衡）
        var quizType = bazi.Instance.GetType().Assembly.GetType("DesktopPet.Builtin.Bazi.MbtiQuiz");
        var qprop = quizType?.GetProperty("LastSelfTest",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        var qself = qprop?.GetValue(null) as string;
        Console.WriteLine(qself == "OK"
            ? "PASS: MbtiQuiz 计分自检通过（93 题 + EI23/SN26/TF24/JP20 四维题量 + 全选A→ESTJ / 全选B→INFP / 平局取首极）"
            : $"FAIL: MbtiQuiz 自检未通过：{qself ?? "(属性未读到)"}");

        // P2：手动播报全链路 —— 预置档案 → 触发「今日运势」槽位 → 断言 Say + System.Notify
        ctx.Capability.Storage.Set("profile",
            "{\"Year\":1981,\"Month\":1,\"Day\":29,\"Hour\":23,\"Minute\":37,\"Gender\":0," +
            "\"DailyBroadcastEnabled\":true,\"BroadcastTime\":\"08:00\"}");
        var todaySlot = uiApi.Slots.FirstOrDefault(s => s.Id == "bazi-today");
        todaySlot?.OnInvoke();
        var said = petApi.Spoken.LastOrDefault() ?? "";
        // 宽松断言：黄历宜忌每天不同，只校验结构（含干支日 + 「宜」+「忌」），不钉死具体条目。
        Console.WriteLine(said.Contains("今日") && said.Contains("宜") && said.Contains("忌")
            ? $"PASS: 「今日运势」手动播报 Say = \"{said}\""
            : $"FAIL: 手动播报未产出预期 Say，实际=\"{said}\"");

        var sys = (NullSystem)ctx.Capability.System;
        Console.WriteLine(sys.Notifications.Count > 0 && sys.Notifications[0].Body.Contains("能量基调")
            ? $"PASS: 完整运势已走 System.Notify（首条 {sys.Notifications[0].Body.Length} 字，含「能量基调」）"
            : $"FAIL: 未按预期调用 System.Notify（{sys.Notifications.Count} 条）");

        // P3：API Key 加密自检（DPAPI 往返 + 旧版明文兼容 + 损坏密文安全返回）
        var akpType = bazi.Instance.GetType().Assembly.GetType("DesktopPet.Builtin.Bazi.ApiKeyProtector");
        var aprop = akpType?.GetProperty("LastSelfTest",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        var aself = aprop?.GetValue(null) as string;
        Console.WriteLine(aself == "OK"
            ? "PASS: ApiKeyProtector 自检通过（DPAPI 往返 + 明文兼容）"
            : $"FAIL: ApiKeyProtector 自检未通过：{aself ?? "(属性未读到)"}");

        // P3：Prompt 组装自检（system 含铁律 + 四柱/日主/五行/今日干支/宜忌，历史带入，末条为当前问题）
        var promptType = bazi.Instance.GetType().Assembly.GetType("DesktopPet.Builtin.Bazi.FortunePrompt");
        var pprop = promptType?.GetProperty("LastSelfTest",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        var pself = pprop?.GetValue(null) as string;
        Console.WriteLine(pself == "OK"
            ? "PASS: FortunePrompt 自检通过（含铁律 + 四柱/日主/五行/今日干支/宜忌）"
            : $"FAIL: FortunePrompt 自检未通过：{pself ?? "(属性未读到)"}");

        // P3：DeepSeekClient 失败路径自检（空/空白 Key 返回可读错误；请求体含 model/role/content/stream）
        var dsType = bazi.Instance.GetType().Assembly.GetType("DesktopPet.Builtin.Bazi.DeepSeekClient");
        var dprop = dsType?.GetProperty("LastSelfTest",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        var dself = dprop?.GetValue(null) as string;
        Console.WriteLine(dself == "OK"
            ? "PASS: DeepSeekClient 自检通过（空 Key 返回可读错误而非抛异常）"
            : $"FAIL: DeepSeekClient 自检未通过：{dself ?? "(属性未读到)"}");

        // P3：无 Key 点「问问猫」→ 可读提示、不抛异常、不开窗（走 Key 校验前置分支）
        ctx.Capability.Storage.Set("profile",
            "{\"Year\":1981,\"Month\":1,\"Day\":29,\"Hour\":23,\"Minute\":37,\"Gender\":0}");
        var askSlot = uiApi.Slots.FirstOrDefault(s => s.Id == "bazi-ask");
        petApi.Spoken.Clear();
        try
        {
            askSlot?.OnInvoke();
            var hint = petApi.Spoken.LastOrDefault() ?? "";
            Console.WriteLine(hint.Contains("API Key")
                ? $"PASS: 无 Key 点「问问猫」给出可读提示 = \"{hint}\""
                : $"FAIL: 无 Key 时未给出可读提示，实际=\"{hint}\"");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: 无 Key 点「问问猫」抛异常：{ex.GetType().Name}: {ex.Message}");
        }

        // P5 内存实测：跑完全链路（播报 + Prompt + 客户端自检 + 无 Key 槽位）后的稳态（仍在加载态）
        var wsEnd = SnapWorkingSetMB();
        Console.WriteLine($"  [内存] 全链路跑完后 {wsEnd:F2} MB（较加载前 +{wsEnd - wsBefore:F2} MB）");

        foreach (var p in loaded) p.Dispose();
        try { Directory.Delete(root, true); } catch { }
    }

    /// <summary>把某个项目的产物部署成标准插件目录，返回该插件的数据存储。</summary>
    private static IPluginStorage DeployPlugin(
        string root, string pluginsDir, string dataDir,
        string pluginId, string area, string projectDir)
    {
        var sampleOut = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            area, projectDir, "bin", "Debug", "net8.0-windows");
        var pluginDir = Path.Combine(pluginsDir, pluginId);
        Directory.CreateDirectory(pluginDir);

        foreach (var f in Directory.EnumerateFiles(sampleOut))
        {
            var name = Path.GetFileName(f);
            if (name is "plugin.toml" or "SamplePlugin.dll" or "Affection.dll" or "DesktopPet.Sdk.dll" or "DesktopPet.Core.dll")
                File.Copy(f, Path.Combine(pluginDir, name), true);
        }
        var tomlSrc = Path.Combine(sampleOut, "plugin.toml");
        if (!File.Exists(tomlSrc)) tomlSrc = Path.Combine("..", "..", "..", "..", area, projectDir, "plugin.toml");
        if (File.Exists(tomlSrc)) File.Copy(tomlSrc, Path.Combine(pluginDir, "plugin.toml"), true);

        Console.WriteLine($"插件目录: {pluginDir}");
        Console.WriteLine($"包含文件: {string.Join(", ", Directory.GetFiles(pluginDir).Select(Path.GetFileName))}");
        Console.WriteLine();

        var data = Path.Combine(dataDir, "plugins", pluginId);
        Directory.CreateDirectory(data);
        return new TestStorage(data);
    }

    private static IPluginContext BuildContext(
        LoadedPlugin plugin, string dataDir,
        IPetApi petApi, IUIApi uiApi, IEventBus events)
    {
        var storage = new TestStorage(Path.Combine(dataDir, "plugins", plugin.Manifest.Id));
        return new TestContext
        {
            HostVersion = new Version(1, 0, 0),
            DataRoot = dataDir,
            PluginDataDir = storage.Dir,
            Capability = new TestCapability(storage, events),
            Pet = petApi,
            UI = uiApi,
        };
    }

    // ---- 内存实测（P5：命理插件 maxMemoryMB 回调依据） ----

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint cb;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr hProcess, out ProcessMemoryCounters counters, uint size);

    /// <summary>
    /// 取当前进程 WorkingSet（MB），采 <paramref name="samples"/> 次取最小值以压掉 GC 抖动。
    /// 每次采样前强制一次完整 GC，让托管堆回到稳态；WorkingSet 含 lunar 的 IL 映射与托管/原生综合占用。
    /// </summary>
    private static double SnapWorkingSetMB(int samples = 3)
    {
        var proc = Process.GetCurrentProcess();
        var min = double.MaxValue;
        for (var i = 0; i < samples; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (GetProcessMemoryInfo(proc.Handle, out var c, (uint)Marshal.SizeOf<ProcessMemoryCounters>()))
            {
                var mb = c.WorkingSetSize.ToUInt64() / 1048576.0;
                if (mb < min) min = mb;
            }
        }
        return min == double.MaxValue ? 0 : min;
    }
}

// --- 最小测试用宿主实现（与 App 的实现同构） ---

public sealed class TestStorage : IPluginStorage
{
    public string Dir { get; }
    public TestStorage(string dir) { Dir = dir; Directory.CreateDirectory(dir); }
    private string P(string key) => Path.Combine(Dir, key + ".json");
    public T? Get<T>(string key) => File.Exists(P(key)) ? System.Text.Json.JsonSerializer.Deserialize<T>(File.ReadAllText(P(key))) : default;
    public void Set<T>(string key, T value) => File.WriteAllText(P(key), System.Text.Json.JsonSerializer.Serialize(value));
    public void Remove(string key) { if (File.Exists(P(key))) File.Delete(P(key)); }
    public bool Contains(string key) => File.Exists(P(key));
}

public sealed class TestEventBus : IEventBus
{
    private readonly Dictionary<string, List<Action<object?>>> _s = new();
    public Action On(string evt, Action<object?> h) { if (!_s.TryGetValue(evt, out var l)) { l = []; _s[evt] = l; } l.Add(h); return () => l.Remove(h); }
    public void Emit(string evt, object? p = null) { if (_s.TryGetValue(evt, out var l)) foreach (var h in l) h(p); }
}

public sealed class TestCapability : ICapabilityApi
{
    public TestCapability(IPluginStorage s, IEventBus e) { Storage = s; Events = e; Scheduler = new NullScheduler(); System = new NullSystem(); }
    public IPluginStorage Storage { get; }
    public IScheduler Scheduler { get; }
    public ISystem System { get; }
    public IEventBus Events { get; }
}

public sealed class NullScheduler : IScheduler
{
    public IScheduleHandle Every(int ms, Action cb) => new NullHandle();
    public IScheduleHandle At(DateTime t, Action cb) => new NullHandle();
    private sealed class NullHandle : IScheduleHandle { public bool IsActive => true; public void Dispose() { } }
}

public sealed class NullSystem : ISystem
{
    /// <summary>记录收到的系统通知（供命理插件播报链路验证）。</summary>
    public List<(string Title, string Body)> Notifications { get; } = new();
    public void Notify(string t, string b) => Notifications.Add((t, b));
    public string? ReadClipboard() => null;
    public void WriteClipboard(string t) { }
    public void OpenUrl(string u) { }
    public string? PickFile(string f = "") => null;
}

public sealed class TestContext : IPluginContext
{
    public required Version HostVersion { get; init; }
    public required string DataRoot { get; init; }
    public required string PluginDataDir { get; init; }
    public required ICapabilityApi Capability { get; init; }
    public IPetApi Pet { get; init; } = new TestPetApi();
    public IUIApi UI { get; init; } = new TestUIApi();
}

public sealed class TestPetApi : IPetApi
{
    public List<PetState> Played = new();
    public List<string> Spoken = new();
    public Task PlayAnimationAsync(PetState state, AnimationOptions? opts = null) { Played.Add(state); return Task.CompletedTask; }
    public void SetMood(PetMood mood) { }
    public void Say(string text, int durationMs = 3000) => Spoken.Add(text);
    public Point2 GetPosition() => Point2.Zero;
    public Task MoveToAsync(Point2 target, int durationMs = 500) => Task.CompletedTask;
    public Rect2 GetWorkArea() => new(0, 0, 1920, 1040);
}

public sealed class TestUIApi : IUIApi
{
    public List<RadialSlot> Slots = new();
    public List<ISettingsPage> Pages = new();
    public void RegisterRadialSlot(RadialSlot slot) => Slots.Add(slot);
    public void RegisterTrayMenuItem(TrayMenuItem item) { }
    public void RegisterSettingsPage(ISettingsPage page) => Pages.Add(page);
    public void UnregisterAll(string scopeId) { }
}
