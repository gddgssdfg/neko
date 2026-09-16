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
        _loader = new PluginLoader(pluginsRoot);
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
    /// v0.1.82：把加载阶段的错误写进日志。
    /// <para>踩坑：此前 <c>_errors</c> 只收集不落地，插件「一个都没加载」时日志里干干净净，
    /// 只能靠猜——插件加载失败必须能在 data/logs 里看到原因。</para>
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
                try { _uiApi.UnregisterAll(plugin.Manifest.Id); } catch { }
                plugin.Dispose();
            }
        }
    }

    private IPluginContext BuildContext(LoadedPlugin plugin)
    {
        var scopeId = plugin.Manifest.Id;
        var ui = new ScopedUiApi(_uiApi, scopeId);
        var cap = new CapabilityApiImpl
        {
            Storage = new JsonPluginStorage(Paths.PluginDataDir(plugin.Manifest.Id)),
            Scheduler = new SchedulerImpl(),
            System = new SystemImpl(),
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
            try { _uiApi.UnregisterAll(id); } catch { }
            try { lp.Dispose(); } catch { }
            return false;
        }
    }

    /// <summary>v0.1.46：运行时禁用插件（OnUnload + 撤销 UI + 卸载 ALC + 标记 disabled）。返回是否成功（原本没加载也返回 true）。</summary>
    public bool DisablePlugin(string id)
    {
        var p = _plugins.FirstOrDefault(x => string.Equals(x.Manifest.Id, id, StringComparison.OrdinalIgnoreCase));
        if (p is null) { _disabled.Add(id); return true; }   // 没加载也确认禁用（一致性）
        try { p.Instance.OnUnload(); } catch { }
        try { _uiApi.UnregisterAll(id); } catch { }
        try { p.Dispose(); } catch { }
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
            try { _uiApi.UnregisterAll(p.Manifest.Id); } catch { }
            try { p.Dispose(); } catch { }
        }
        _plugins.Clear();
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
