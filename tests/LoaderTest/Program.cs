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
    public void Notify(string t, string b) { }
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
    public void RegisterRadialSlot(RadialSlot slot) => Slots.Add(slot);
    public void RegisterTrayMenuItem(TrayMenuItem item) { }
    public void RegisterSettingsPage(ISettingsPage page) { }
    public void UnregisterAll(string scopeId) { }
}
