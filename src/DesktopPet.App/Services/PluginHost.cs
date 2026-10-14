using DesktopPet.App.Core;
using DesktopPet.Core;
using DesktopPet.Runtime;

namespace DesktopPet.App.Services;

/// <summary>
/// 插件宿主：负责为每个插件构造上下文、调用 OnLoad、维护生命周期。
/// 加载失败不拖垮宿主，收集错误日志。
/// v0.1.34：拆分 LoadCore（立即）/ LoadDeferred（主窗口显示后异步），压冷启动。
/// </summary>
public sealed class PluginHost : IDisposable
{
    private readonly PluginLoader _loader;
    private readonly HashSet<string> _disabled = new();   // v0.1.46：改 HashSet（之前 IReadOnlySet）——运行时增删支持启用/禁用
    private readonly IReadOnlySet<string> _deferredIds;   // v0.1.34：延迟加载清单（联网/重资源）
    private readonly Version _hostVersion;
    private readonly string _dataRoot;
    private readonly IPetApi _petApi;
    private readonly UIApiImpl _uiApi;
    private readonly ICapabilityApi _capability;

    private readonly List<LoadedPlugin> _plugins = new();
    private readonly List<(string dir, string error)> _errors = new();

    // v0.1.95（审计 P2-1 核心段）：每插件一份调度器，宿主持有其引用，随插件禁用 / 宿主退出释放。
    // 改前：BuildContext 里 `new SchedulerImpl()` 无人持有、永不 Dispose → 其 System.Threading.Timer
    // 一直被运行时定时器队列 root（并 root 住回调委托 → 指向插件 ALC），造成"禁用后回调仍在跑 +
    // 插件 ALC 回收不了"。此处收口为宿主所有权。
    private readonly Dictionary<string, SchedulerImpl> _schedulers = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;
    public IReadOnlyList<(string dir, string error)> Errors => _errors;

    /// <summary>v0.1.46：运行时启用/禁用某插件（SettingsWindow 切换 + 持久化到 Config.DisabledPlugins）</summary>
    public event Action<string, bool>? EnabledChanged;

    /// <summary>v0.1.46：查询某插件当前启用状态</summary>
    public bool IsEnabled(string id) => !_disabled.Contains(id);

    public PluginHost(
        string pluginsRoot,
        IReadOnlySet<string> disabled,
        Version hostVersion,
        string dataRoot,
        IPetApi petApi,
        UIApiImpl uiApi,                // 收紧类型：需要 PushScope
        ICapabilityApi capability,
        IReadOnlySet<string>? deferredIds = null)  // v0.1.34：可选；null = 全部立即加载（向后兼容）
    {
        // v0.1.82：接线内置插件信任清单（见 LoadBuiltinTrust 注释）。
        _loader = new PluginLoader(pluginsRoot, LoadBuiltinTrust());
        _deferredIds = deferredIds ?? (IReadOnlySet<string>)new HashSet<string>(0);
        _hostVersion = hostVersion;
        _dataRoot = dataRoot;
        _petApi = petApi;
        _uiApi = uiApi;
        _capability = capability;
        // v0.1.46：从启动时 disabled 集合初始化运行时集合（启动时不加载这些）
        if (disabled is not null) foreach (var d in disabled) _disabled.Add(d);
    }

    /// <summary>向后兼容：一次加载全部（LoaderTest 用）。生产代码改用 LoadCore + LoadDeferred</summary>
    public void LoadAll()
    {
        var loaded = _loader.LoadAll(_disabled, out var errors);
        _errors.AddRange(errors);
        ReportErrors("LoadAll", errors);
        OnLoadAll(loaded);
    }

    /// <summary>v0.1.34：加载核心插件（不在 deferredIds 里的）并立即 OnLoad。冷启动时调用。</summary>
    public void LoadCore()
    {
        var loaded = _loader.LoadCore(_disabled, _deferredIds, out var errors);
        _errors.AddRange(errors);
        ReportErrors("LoadCore", errors);
        OnLoadAll(loaded);
    }

    /// <summary>v0.1.34：加载延迟插件（ID 在 deferredIds 里的）并 OnLoad。主窗口显示后 Background 异步调用。</summary>
    public void LoadDeferred()
    {
        var loaded = _loader.LoadDeferred(_disabled, _deferredIds, out var errors);
        _errors.AddRange(errors);
        ReportErrors("LoadDeferred", errors);
        OnLoadAll(loaded);
    }

    /// <summary>
    /// v0.1.82：载入 exe 同级的 <c>builtins.lock.json</c>（内置插件信任清单），交给
    /// <see cref="PluginLoader"/> 以放行「随包发布的官方内置插件」的高风险权限声明。
    /// <para><b>为什么补这一步</b>：此前 <c>PluginHost</c> 直接 <c>new PluginLoader(pluginsRoot)</c>，
    /// 未传 trust → 落回 <see cref="PluginTrustSet.Empty"/>，于是所有声明高危权限的内置插件
    /// （如 com.lantern.music）在<em>任何</em>环境下都被一律拒载——内置信任通道「设计好了从未接线」。
    /// 清单由构建期 <c>tools/gen_builtins_lock.py</c> 生成并置于 App 输出目录（exe 同级）。</para>
    /// <para><b>安全不变式</b>：清单缺失 / 损坏 / schema 不符时 <see cref="PluginTrustSet.Load"/> 返回
    /// <see cref="PluginTrustSet.Empty"/> 并给出 warning，行为与旧版<b>逐字节一致</b>（一律不信任），
    /// 不引入任何新风险；此处只是把 warning 落到日志，便于排查「内置高危插件没加载」。</para>
    /// </summary>
    private static PluginTrustSet LoadBuiltinTrust()
    {
        var lockPath = System.IO.Path.Combine(AppContext.BaseDirectory, PluginTrustSet.DefaultFileName);
        var trust = PluginTrustSet.Load(lockPath, out var warning);
        if (!string.IsNullOrEmpty(warning))
            Log.Warn($"[插件] {warning}");
        else
            Log.Info($"[插件] 已载入内置插件信任清单：{lockPath}");
        return trust;
    }

    /// <summary>
    /// v0.1.82：把加载阶段的错误写进日志。
    /// <para>踩坑：此前 <c>_errors</c> 只收集不落地，插件「一个都没加载」时日志里干干净净，
    /// 只能靠猜——插件加载失败必须能在 数据根/logs 里看到原因。</para>
    /// </summary>
    private static void ReportErrors(string stage, IReadOnlyList<(string dir, string error)> errors)
    {
        if (errors.Count == 0) return;
        Log.Warn($"[插件] {stage}：{errors.Count} 个插件加载失败");
        foreach (var (dir, error) in errors)
            Log.Warn($"[插件] {System.IO.Path.GetFileName(dir)} 加载失败：{error}");
    }

    private void OnLoadAll(IEnumerable<LoadedPlugin> loaded)
    {
        foreach (var plugin in loaded)
        {
            try
            {
                var ctx = BuildContext(plugin);
                plugin.Instance.OnLoad(ctx);
                _plugins.Add(plugin);
                Log.Info($"[插件] 已加载: {plugin.Manifest.Name}");
            }
            catch (Exception ex)
            {
                _errors.Add((plugin.PluginDir, $"OnLoad 失败: {ex.Message}"));
                // 撤销该插件可能已经注册的 UI（OnLoad 中途抛异常时残留的槽位/托盘项）
                try { _uiApi.UnregisterAll(plugin.Manifest.Id); } catch { /* OnLoad 失败后的尽力回滚：撤销失败不阻断 Dispose */ }
                plugin.Dispose();
            }
        }
    }

    private IPluginContext BuildContext(LoadedPlugin plugin)
    {
        var scopeId = plugin.Manifest.Id;
        var ui = new ScopedUiApi(_uiApi, scopeId);
        // v0.1.95（审计 P2-1 核心段）：把每插件的 SchedulerImpl 存进 _schedulers，宿主据此在禁用/退出时释放。
        var scheduler = new SchedulerImpl();
        _schedulers[scopeId] = scheduler;
        var cap = new CapabilityApiImpl
        {
            Storage = new JsonPluginStorage(Paths.PluginDataDir(plugin.Manifest.Id)),
            Scheduler = scheduler,
            // v0.1.83：传入插件 id → 插件经 Capability.System.Log 写的日志会带 [插件:<id>] 前缀
            System = new SystemImpl(plugin.Manifest.Id),
            Events = _capability.Events,
        };

        return new PluginContextImpl
        {
            HostVersion = _hostVersion,
            DataRoot = _dataRoot,
            PluginDataDir = Paths.PluginDataDir(plugin.Manifest.Id),
            Pet = _petApi,
            UI = ui,
            Capability = cap,
        };
    }

    /// <summary>v0.1.46：运行时启用插件（从 plugins dir 重新加载 + OnLoad + 撤销 disabled 标记）。返回是否成功。</summary>
    public bool EnablePlugin(string id)
    {
        if (_plugins.Any(p => string.Equals(p.Manifest.Id, id, StringComparison.OrdinalIgnoreCase)))
            return true;   // 已加载
        var dir = System.IO.Path.Combine(_loader.PluginsRoot, id);
        if (!System.IO.Directory.Exists(dir))
        {
            _errors.Add((dir, "插件目录不存在")); return false;
        }
        var lp = _loader.LoadOne(dir, out var err);
        if (lp is null) { _errors.Add((dir, err ?? "加载失败")); return false; }
        try
        {
            var ctx = BuildContext(lp);
            lp.Instance.OnLoad(ctx);
            _plugins.Add(lp);
            _disabled.Remove(id);   // 从禁用集合移出（若启动时记着）
            EnabledChanged?.Invoke(id, true);
            return true;
        }
        catch (Exception ex)
        {
            _errors.Add((dir, $"OnLoad 失败: {ex.Message}"));
            try { _uiApi.UnregisterAll(id); } catch { /* 回滚已注册 UI：单步失败不阻断后续清理（尽力而为）*/ }
            try { lp.Dispose(); } catch { /* 回滚卸载 ALC：失败不影响返回 false 的结果（尽力而为）*/ }
            // v0.1.95（审计 P2-1 核心段）：回滚 BuildContext 已登记的调度器，避免"加载失败"时残留定时器 + ALC root。
            if (_schedulers.Remove(lp.Manifest.Id, out var sch)) { try { sch.Dispose(); } catch { /* 尽力而为 */ } }
            return false;
        }
    }

    /// <summary>v0.1.46：运行时禁用插件（OnUnload + 撤销 UI + 卸载 ALC + 标记 disabled）。返回是否成功（原本没加载也返回 true）。</summary>
    public bool DisablePlugin(string id)
    {
        var p = _plugins.FirstOrDefault(x => string.Equals(x.Manifest.Id, id, StringComparison.OrdinalIgnoreCase));
        if (p is null) { _disabled.Add(id); return true; }   // 没加载也确认禁用（一致性）
        // v0.1.95（审计 P2-3）：改走幂等 Unload（Dispose 内也会调，但已有幂等闸）→ OnUnload 恰好一次。
        try { p.Unload(); } catch { /* 卸载回调异常不得阻断后续撤销 UI 与 ALC 卸载（尽力而为）*/ }
        try { _uiApi.UnregisterAll(id); } catch { /* 撤销 UI 失败不阻断 ALC 卸载 */ }
        try { p.Dispose(); } catch { /* 释放失败不阻断从已加载列表移除 */ }
        // v0.1.95（审计 P2-1 核心段）：释放该插件的调度器 → 停掉全部 System.Threading.Timer，
        // 解开定时器队列对插件 ALC 的 root（改前定时器仍在跑、且 ALC 回收不了）。
        if (_schedulers.Remove(id, out var scheduler))
        {
            try { scheduler.Dispose(); } catch { /* 停表失败不阻断禁用 */ }
        }
        _plugins.Remove(p);
        _disabled.Add(id);   // 标记禁用
        EnabledChanged?.Invoke(id, false);
        return true;
    }

    public void Dispose()
    {
        foreach (var p in _plugins)
        {
            // 先撤销该插件注册的全部 UI（径向菜单槽位 / 托盘项 / 设置页）
            try { _uiApi.UnregisterAll(p.Manifest.Id); } catch { /* 宿主退出清理：单个插件失败不阻断其余插件 */ }
            try { p.Dispose(); } catch { /* 同上，尽力而为 */ }
            // v0.1.95（审计 P2-1 核心段）：宿主退出同样释放每插件调度器（停表 + 解开对插件 ALC 的 root）。
            if (_schedulers.TryGetValue(p.Manifest.Id, out var scheduler))
            {
                try { scheduler.Dispose(); } catch { /* 同上，尽力而为 */ }
            }
        }
        _plugins.Clear();
        _schedulers.Clear();
    }

    /// <summary>带作用域的 UI API：Register* 自动归到本插件 scope，UnregisterAll 撤销本插件</summary>
    private sealed class ScopedUiApi : IUIApi
    {
        private readonly UIApiImpl _inner;
        private readonly string _scope;
        public ScopedUiApi(UIApiImpl inner, string scope) { _inner = inner; _scope = scope; }
        public void RegisterRadialSlot(RadialSlot slot)   { using (_inner.PushScope(_scope)) _inner.RegisterRadialSlot(slot); }
        public void RegisterTrayMenuItem(TrayMenuItem item) { using (_inner.PushScope(_scope)) _inner.RegisterTrayMenuItem(item); }
        public void RegisterSettingsPage(ISettingsPage page) { using (_inner.PushScope(_scope)) _inner.RegisterSettingsPage(page); }
        public void UnregisterAll(string scopeId) => _inner.UnregisterAll(scopeId);
        public void UnregisterAll() => _inner.UnregisterAll(_scope);
    }
}
