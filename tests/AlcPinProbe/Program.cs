using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using DesktopPet.Core;
using DesktopPet.Runtime;

namespace AlcPinProbe;

/// <summary>
/// QA 独立证伪夹具：用**真实** <see cref="PluginLoader"/> + 可回收 ALC，
/// 验证「调用插件内某个方法后，插件 ALC 还能否被 GC 回收」。
///
/// <para>
/// 用法：<c>AlcPinProbe.exe &lt;repoRoot&gt; &lt;case&gt;</c>
/// 每次<strong>单独进程</strong>运行，避免 STJ 全局缓存在用例间互相污染。
/// </para>
///
/// <para>
/// v0.1.82 Round2 修订（QA / software-qa-engineer-2）：
/// 旧的 <c>todolistsave</c> 是「机制探针」——直接 <c>JsonSerializer.Serialize(list, pluginListType)</c>
/// 模拟宿主反射，属**永远 N**、不能当回归判据（工程师-3 指正，采纳）。
/// 现改为走**插件真实路径**：OnLoad 真 <c>TodoListPlugin</c> → 反射调用其私有
/// <c>Save</c>/<c>Load</c>，底层 <see cref="HostLikeStorage"/> 逐行复刻宿主
/// <c>JsonPluginStorage</c>（<c>Set&lt;T&gt;</c> 里 <c>JsonSerializer.Serialize(value, WriteIndented)</c>）。
/// 这样：若插件退回旧写法（把 <c>List&lt;TodoItem&gt;</c> 交给宿主泛型存储），
/// <c>Set&lt;List&lt;TodoItem&gt;&gt;</c> 就会真的反射插件类型 → N（判据敏感）；用源生成则为 Y。
/// </para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var repo = args.Length > 0 ? args[0] : "G:/lantetn-li/neko";
        var kase = (args.Length > 1 ? args[1] : "control").Trim().ToLowerInvariant();
        var pluginsRoot = Path.Combine(repo, "plugins");

        // 期望值：Y = 可回收（无跨 ALC 钉根），N = 被钉住。默认 Y。
        var expectedCollected = kase != "todohostsave-mechanism";

        var pluginId = kase switch
        {
            "todoroundtrip" or "todosave" or "todoload" or "todohostsave-mechanism" => "com.lantern.todo",
            _ => "com.lantern.bazi",
        };
        var pluginDir = Path.Combine(pluginsRoot, pluginId);

        Console.WriteLine($"[AlcPinProbe] case = {kase}  (plugin = {pluginId})");
        if (!Directory.Exists(pluginDir))
        {
            Console.WriteLine($"SKIP: 插件目录不存在 {pluginDir}");
            return 2;
        }

        Action<Assembly> action = kase switch
        {
            "control" => _ => { },
            "profileload" => CallProfileLoad,
            "profilesave" => CallProfileSave,
            "chatappend" => CallChatAppend,
            "buildbody" => CallBuildBody,
            "sdkload" => CallSdkLoadProfile,
            "clipstring" => CallListStringSerialize,
            "todohostsave-mechanism" => CallTodoHostSaveMechanism,
            "todosave" => CallTodoRealSaveOnly,
            "todoload" => CallTodoRealLoadLegacy,
            "todoroundtrip" => CallTodoRealRoundTrip,
            _ => throw new ArgumentException("未知 case: " + kase),
        };

        bool collected = RunOnce(pluginsRoot, pluginDir, action, out var detail);
        var verdict = collected ? "COLLECTED(Y)" : "PINNED(N)";
        Console.WriteLine($"[AlcPinProbe] {kase}: action={detail}");
        Console.WriteLine($"[AlcPinProbe] {kase}: ALC {verdict}  (expected {(expectedCollected ? "Y" : "N")})");

        bool verdictOk = collected == expectedCollected;
        Console.WriteLine($"[AlcPinProbe] {kase}: {(verdictOk ? "VERDICT-OK" : "VERDICT-MISMATCH")}");

        // 0 = 结果符合期望；1 = ALC 结论与期望不符；3 = 功能性断言失败（见 detail）
        if (!verdictOk) return 1;
        if (detail.StartsWith("FUNC-FAIL", StringComparison.Ordinal)) return 3;
        return 0;
    }

    /// <summary>执行 action → 卸载 → 等装载帧返回后，再强制 GC×3 → 看 ALC 弱引用是否还活着。</summary>
    private static bool RunOnce(string pluginsRoot, string pluginDir, Action<Assembly> action, out string detail)
    {
        // 关键：把「加载/执行/卸载」放进独立方法，等它返回后其局部（lp / ALC）才真正脱离作用域；
        // 否则 Debug 构建下 JIT 会为整个方法保活局部变量 → 永远收不回（假 N）。
        var (alcRef, det) = LoadRunUnload(pluginsRoot, pluginDir, action);
        detail = det;
        if (alcRef is null) return false;

        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();

        return !alcRef.IsAlive;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference? alc, string detail) LoadRunUnload(
        string pluginsRoot, string pluginDir, Action<Assembly> action)
    {
        var loader = new PluginLoader(pluginsRoot);
        var lp = loader.LoadOne(pluginDir, out var err);
        if (lp is null) return (null, "load failed: " + err);

        string det;
        try
        {
            action(lp.Instance.GetType().Assembly);
            det = "ok";
        }
        catch (Exception ex)
        {
            det = $"{ex.GetType().Name}: {ex.Message}";
        }

        var alcRef = new WeakReference(lp.Context);
        lp.Dispose();
        return (alcRef, det);
    }

    // ---- Bazi：直接调插件内静态方法（走源生成或手写路径）----

    /// <summary>BaziProfile.Load（源码现走源生成 <c>BaziJsonContext.Default.BaziProfile</c>）。</summary>
    private static void CallProfileLoad(Assembly asm)
    {
        var t = asm.GetType("DesktopPet.Builtin.Bazi.BaziProfile")!;
        var mi = t.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)!;
        var st = new MemStorage();
        st.Set("profile", "{\"Year\":1981,\"Month\":1,\"Day\":29,\"Hour\":23,\"Minute\":37,\"Gender\":0}");
        mi.Invoke(null, new object[] { st });
    }

    /// <summary>BaziProfile.Save（源码现走源生成）。</summary>
    private static void CallProfileSave(Assembly asm)
    {
        var t = asm.GetType("DesktopPet.Builtin.Bazi.BaziProfile")!;
        var obj = Activator.CreateInstance(t)!;
        var mi = t.GetMethod("Save", BindingFlags.Public | BindingFlags.Instance)!;
        mi.Invoke(obj, new object[] { new MemStorage() });
    }

    /// <summary>v0.1.82 已修：ChatHistory.Append（手写 Utf8JsonWriter）。期望可回收。</summary>
    private static void CallChatAppend(Assembly asm)
    {
        var t = asm.GetType("DesktopPet.Builtin.Bazi.ChatHistory")!;
        var mi = t.GetMethod("Append", BindingFlags.Public | BindingFlags.Static)!;
        mi.Invoke(null, new object[] { new MemStorage(), "q", "a" });
    }

    /// <summary>v0.1.82 已修：DeepSeekClient.BuildRequestBody（手写 Utf8JsonWriter）。期望可回收。</summary>
    private static void CallBuildBody(Assembly asm)
    {
        var ds = asm.GetType("DesktopPet.Builtin.Bazi.DeepSeekClient")!;
        var cm = asm.GetType("DesktopPet.Builtin.Bazi.ChatMessage")!;
        var listT = typeof(List<>).MakeGenericType(cm);
        var list = (System.Collections.IList)Activator.CreateInstance(listT)!;
        list.Add(Activator.CreateInstance(cm, "user", "hi")!);
        var mi = ds.GetMethod("BuildRequestBody", BindingFlags.NonPublic | BindingFlags.Static)!;
        mi.Invoke(null, new object[] { list });
    }

    /// <summary>v0.1.82 已修：ChatHistory.Load（手写 JsonDocument，不经 STJ 反射）。期望可回收。</summary>
    private static void CallSdkLoadProfile(Assembly asm)
    {
        var t = asm.GetType("DesktopPet.Builtin.Bazi.ChatHistory")!;
        var mi = t.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)!;
        var st = new MemStorage();
        st.Set("chatHistory", "[{\"Role\":\"user\",\"Content\":\"hi\"}]");
        mi.Invoke(null, new object[] { st });
    }

    /// <summary>对照：序列化 <c>List&lt;string&gt;</c>（BCL 泛参）。期望：可回收。</summary>
    private static void CallListStringSerialize(Assembly asm)
    {
        _ = asm;
        System.Text.Json.JsonSerializer.Serialize(new List<string> { "a", "b" });
    }

    // ---- TodoList：真实插件路径 ----

    /// <summary>
    /// 敏感度对照（**机制探针**，非插件路径）：模拟「旧版插件把 List&lt;TodoItem&gt; 交给宿主泛型存储」，
    /// 直接经 <see cref="HostLikeStorage.Set{T}"/> 反射序列化插件类型。期望：N（被钉住）。
    /// 用途：证明本夹具的 HostLikeStorage 确实会反射插件类型——即判据是「敏感」的，
    /// 一旦插件退回旧写法，<c>todosave</c> 就会翻成 N。
    /// </summary>
    private static void CallTodoHostSaveMechanism(Assembly asm)
    {
        var itemType = asm.GetType("DesktopPet.Builtin.TodoList.TodoItem")!;
        var listType = typeof(List<>).MakeGenericType(itemType);
        var list = Activator.CreateInstance(listType)!;

        var itemA = Activator.CreateInstance(itemType)!;
        itemType.GetProperty("Text")!.SetValue(itemA, "旧写法");
        listType.GetMethod("Add")!.Invoke(list, new[] { itemA });

        var st = new HostLikeStorage();
        // 反射调用宿主同款泛型 Set<List<TodoItem>>（宿主 JsonPluginStorage.Set 正是这么反射序列化的）
        var setMi = typeof(HostLikeStorage).GetMethod("Set")!.MakeGenericMethod(listType);
        setMi.Invoke(st, new object[] { "todos", list });
    }

    /// <summary>真实插件 Save：OnLoad → 反射调私有 <c>Save(List&lt;TodoItem&gt;)</c>。期望：Y。</summary>
    private static void CallTodoRealSaveOnly(Assembly asm)
    {
        var (plugin, storage) = LoadTodoPlugin(asm);
        InvokeTodoSave(asm, plugin, "买牛奶", "写周报");
        // 功能断言：落盘必须是字符串格式（新格式），且不含明文反射痕迹
        var raw = storage.PeekRaw("todos");
        if (raw is null || !raw.TrimStart().StartsWith("\"", StringComparison.Ordinal))
            throw new InvalidOperationException("FUNC-FAIL: 新格式落盘不是 JSON 字符串: " + (raw ?? "<null>"));
    }

    /// <summary>真实插件 Load（旧格式兼容）：预置旧版「裸数组」存档 → 反射调私有 <c>Load()</c>。期望：Y + 读回 2 条。</summary>
    private static void CallTodoRealLoadLegacy(Assembly asm)
    {
        var (plugin, storage) = LoadTodoPlugin(asm);
        // 旧版存档 = 宿主对 List<TodoItem> 反射序列化的产物（裸数组；缩进对解析无影响）
        storage.SeedRaw("todos", "[{\"Text\":\"旧A\",\"Done\":false},{\"Text\":\"旧B\",\"Done\":true}]");
        var list = InvokeTodoLoad(asm, plugin);
        if (list.Count != 2)
            throw new InvalidOperationException($"FUNC-FAIL: 旧格式读回 {list.Count} 条（期望 2）");
    }

    /// <summary>真实插件 Save→Load 往返：期望 Y + 往返一致（新格式）。</summary>
    private static void CallTodoRealRoundTrip(Assembly asm)
    {
        var (plugin, storage) = LoadTodoPlugin(asm);
        InvokeTodoSave(asm, plugin, "甲", "乙", "丙");
        var list = InvokeTodoLoad(asm, plugin);
        if (list.Count != 3)
            throw new InvalidOperationException($"FUNC-FAIL: 新格式往返读回 {list.Count} 条（期望 3）");
        if ((string?)list[0]!.GetType().GetProperty("Text")!.GetValue(list[0]) != "甲")
            throw new InvalidOperationException("FUNC-FAIL: 新格式往返内容不符");
    }

    /// <summary>用测试替身装载真实 TodoListPlugin（OnLoad 后 Capability 才可用）。</summary>
    private static (object plugin, HostLikeStorage storage) LoadTodoPlugin(Assembly asm)
    {
        var pluginType = asm.GetType("DesktopPet.Builtin.TodoList.TodoListPlugin")!;
        var plugin = Activator.CreateInstance(pluginType)!;
        var storage = new HostLikeStorage();
        var ctx = new Ctx
        {
            HostVersion = new Version(0, 1, 82),
            DataRoot = Path.GetTempPath(),
            PluginDataDir = Path.GetTempPath(),
            Pet = new PetApi(),
            UI = new NoopUi(),
            Capability = new Cap
            {
                Storage = storage,
                Scheduler = new NoopSched(),
                System = new NoopSys(),
                Events = new NoopBus(),
            },
        };
        pluginType.GetMethod("OnLoad")!.Invoke(plugin, new object[] { ctx });
        return (plugin, storage);
    }

    private static void InvokeTodoSave(Assembly asm, object plugin, params string[] texts)
    {
        var itemType = asm.GetType("DesktopPet.Builtin.TodoList.TodoItem")!;
        var listType = typeof(List<>).MakeGenericType(itemType);
        var list = Activator.CreateInstance(listType)!;
        foreach (var t in texts)
        {
            var item = Activator.CreateInstance(itemType)!;
            itemType.GetProperty("Text")!.SetValue(item, t);
            listType.GetMethod("Add")!.Invoke(list, new[] { item });
        }
        plugin.GetType().GetMethod("Save", BindingFlags.NonPublic | BindingFlags.Instance)!
              .Invoke(plugin, new[] { list });
    }

    private static System.Collections.IList InvokeTodoLoad(Assembly asm, object plugin)
    {
        var ret = plugin.GetType().GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .Invoke(plugin, Array.Empty<object>());
        return (System.Collections.IList)ret!;
    }

    // ---------------- 测试替身 ----------------

    /// <summary>
    /// 宿主 <c>JsonPluginStorage</c> 的**内存镜像**（逐行复刻其泛型序列化语义）：
    /// <c>Set&lt;T&gt;</c> 把值 <c>JsonSerializer.Serialize(value, WriteIndented)</c> 成 JSON 文本存下；
    /// <c>Get&lt;T&gt;</c> 再 <c>Deserialize&lt;T&gt;</c> 回来。因此当 T 是插件类型时会真的反射插件类型
    /// （与宿主同款钉根路径），T 是 <c>string</c>/<c>JsonElement</c> 等 BCL 类型则不反射插件类型。
    /// </summary>
    private sealed class HostLikeStorage : IPluginStorage
    {
        private readonly Dictionary<string, string?> _files = new();
        private static readonly System.Text.Json.JsonSerializerOptions Opts = new() { WriteIndented = true };

        public T? Get<T>(string key)
        {
            if (!_files.TryGetValue(key, out var text) || text is null) return default;
            try { return System.Text.Json.JsonSerializer.Deserialize<T>(text, Opts); }
            catch { return default; }
        }

        public void Set<T>(string key, T value)
            => _files[key] = System.Text.Json.JsonSerializer.Serialize(value, Opts);

        public void Remove(string key) => _files.Remove(key);
        public bool Contains(string key) => _files.ContainsKey(key);

        /// <summary>直接读原始落盘文本（绕过反序列化），供功能断言。</summary>
        public string? PeekRaw(string key) => _files.TryGetValue(key, out var t) ? t : null;

        /// <summary>直接写入原始落盘文本，模拟「磁盘上既有的旧存档」。</summary>
        public void SeedRaw(string key, string text) => _files[key] = text;
    }

    /// <summary>最小内存版 IPluginStorage（Bazi 用例用；只存对象，不引入额外反射）。</summary>
    private sealed class MemStorage : IPluginStorage
    {
        private readonly Dictionary<string, object?> _d = new();
        public T? Get<T>(string key) => _d.TryGetValue(key, out var v) ? (T?)v : default;
        public void Set<T>(string key, T value) => _d[key] = value;
        public void Remove(string key) => _d.Remove(key);
        public bool Contains(string key) => _d.ContainsKey(key);
    }

    private sealed class NoopUi : IUIApi
    {
        public void RegisterRadialSlot(RadialSlot slot) { }
        public void RegisterTrayMenuItem(TrayMenuItem item) { }
        public void RegisterSettingsPage(ISettingsPage page) { }
        public void UnregisterAll(string scopeId) { }
    }

    private sealed class NoopSched : IScheduler
    {
        public IScheduleHandle Every(int intervalMs, Action callback) => new Handle();
        public IScheduleHandle At(DateTime time, Action callback) => new Handle();
        private sealed class Handle : IScheduleHandle
        {
            public bool IsActive => false;
            public void Dispose() { }
        }
    }

    private sealed class NoopSys : ISystem
    {
        public void Notify(string title, string body) { }
        public string? ReadClipboard() => null;
        public void WriteClipboard(string text) { }
        public void OpenUrl(string url) { }
        public string? PickFile(string filter = "") => null;
        public void Log(PluginLogLevel level, string message, Exception? exception = null) { }
    }

    private sealed class NoopBus : IEventBus
    {
        public Action On(string evt, Action<object?> handler) => () => { };
        public void Emit(string evt, object? payload = null) { }
    }

    private sealed class PetApi : IPetApi
    {
        public Task PlayAnimationAsync(PetState state, AnimationOptions? opts = null) => Task.CompletedTask;
        public void SetMood(PetMood mood) { }
        public void Say(string text, int durationMs = 3000) { }
        public Point2 GetPosition() => default;
        public Task MoveToAsync(Point2 target, int durationMs = 500) => Task.CompletedTask;
        public Rect2 GetWorkArea() => default;
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
