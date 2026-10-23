using System.Reflection;
using System.Runtime.Loader;
using DesktopPet.Core;

namespace DesktopPet.Runtime;

/// <summary>安装目录中的清单；发现清单不会执行插件代码。</summary>
public sealed record InstalledPlugin(PluginManifest Manifest, string PluginDir);

/// <summary>单个插件的装载上下文。负责创建 ALC、加载 dll、实例化插件、卸载。</summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    // 宿主共享的核心程序集：插件必须解析到宿主侧，否则 IPetPlugin 类型会分裂成两份，
    // 导致 IsAssignableFrom 判断失败。这是 .NET 插件系统的经典坑。
    private static readonly HashSet<string> HostSharedAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "DesktopPet.Core",
        "DesktopPet.Sdk",
    };

    public PluginLoadContext(string pluginDir, string entryDll)
        : base($"plugin-{Path.GetFileName(pluginDir)}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(Path.Combine(pluginDir, entryDll));
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // 共享程序集：复用宿主已加载的实例，保证类型同一性
        if (HostSharedAssemblies.Contains(assemblyName.Name ?? ""))
        {
            var existing = Default.Assemblies.FirstOrDefault(a =>
                string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return existing;
        }

        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (path is null) return null;
        return LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? 0 : LoadUnmanagedDllFromPath(path);
    }
}

/// <summary>已加载的插件实例（持有上下文句柄，用于卸载）</summary>
public sealed class LoadedPlugin : IDisposable
{
    public required PluginManifest Manifest { get; init; }
    public required string PluginDir { get; init; }
    public required PluginLoadContext Context { get; init; }
    public required IPetPlugin Instance { get; init; }
    public PluginPermissions GrantedPermissions { get; init; }
    public bool IsCollectible => Context.IsCollectible;

    private bool _unloaded;
    private bool _disposed;

    /// <summary>
    /// v0.1.95（审计 P2-3）：**幂等卸载**——保证 <see cref="IPetPlugin.OnUnload"/> 恰好被调用一次。
    /// <para>背景：<c>PluginHost.DisablePlugin</c> 曾显式调一次 <c>Instance.OnUnload()</c>，随后 <see cref="Dispose"/>
    /// 内又调一次（而 <c>PluginHost.Dispose</c> 路径只调一次）——同一「禁用」路径出现两种调用次数。
    /// 契约要求「恰好一次」；非幂等的 <c>OnUnload</c>（重复写盘 / 计数 / FlushNow）会产生重复副作用。此闸门统一收口。</para>
    /// <para>「只增」：<c>LoadedPlugin</c> 新增本方法，不改既有成员语义；<see cref="Dispose"/> 行为保持等价（仍会触发一次 OnUnload）。</para>
    /// </summary>
    public void Unload()
    {
        if (_unloaded) return;
        _unloaded = true;
        try { Instance.OnUnload(); } catch { /* 卸载回调失败不影响卸载 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unload();   // v0.1.95：收口为幂等入口（改前此处直接 try { Instance.OnUnload(); }）
        try { Instance.Dispose(); } catch { /* 插件 Dispose 失败不影响 ALC 卸载（最后防线，Runtime 无宿主 Log 可用）*/ }
        Context.Unload();
    }
}

/// <summary>插件加载器：扫描 plugins/ 目录 → 解析 manifest → 加载实例</summary>
public sealed class PluginLoader
{
    private readonly string _pluginsRoot;
    private readonly PluginTrustSet _trust;

    /// <summary>
    /// v0.1.80 / P0-4 T02：新增可选参 <paramref name="trust"/>——内置插件信任集。
    /// 不传（现有调用点不动）→ <see cref="PluginTrustSet.Empty"/>，行为与旧版一致（高风险一律拒载）。
    /// </summary>
    public PluginLoader(string pluginsRoot, PluginTrustSet? trust = null)
    {
        _pluginsRoot = Path.GetFullPath(pluginsRoot);
        _trust = trust ?? PluginTrustSet.Empty;
    }

    /// <summary>v0.1.46：插件目录根（用于运行时按 id 定位 dir）</summary>
    public string PluginsRoot => _pluginsRoot;

    public IReadOnlyList<InstalledPlugin> Discover(out IReadOnlyList<(string dir, string error)> errors)
    {
        var found = new List<InstalledPlugin>();
        var failures = new List<(string, string)>();
        errors = failures;
        try
        {
            EnsurePluginsRoot();
            foreach (var dir in Directory.GetDirectories(_pluginsRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (Path.GetFileName(dir).StartsWith('_')) continue;
                try
                {
                    var manifest = ManifestParser.Parse(Path.Combine(dir, "plugin.toml"));
                    if (!string.Equals(manifest.Id, Path.GetFileName(dir), StringComparison.OrdinalIgnoreCase))
                        throw new FormatException($"manifest id '{manifest.Id}' 与目录名不一致");
                    found.Add(new InstalledPlugin(manifest, dir));
                }
                catch (Exception ex) { failures.Add((dir, ex.Message)); }
            }
        }
        catch (Exception ex) { failures.Add((_pluginsRoot, $"扫描插件目录失败: {ex.Message}")); }
        return found;
    }

    /// <summary>v0.1.46：运行时加载单个插件（启用已禁用插件用；不读 disabled 集合——PluginHost 显式控制）</summary>
    public LoadedPlugin? LoadOne(string pluginDir, out string? error)
    {
        if (TryLoadOne(Path.GetFullPath(pluginDir), new HashSet<string>(), out var lp, out error))
            return lp;
        return null;
    }

    /// <summary>扫描并加载全部未禁用插件。</summary>
    public IReadOnlyList<LoadedPlugin> LoadAll(
        IReadOnlySet<string> disabledPlugins,
        out IReadOnlyList<(string dir, string error)> errors)
        => LoadMatching(disabledPlugins, _ => true, out errors);

    /// <summary>启动时加载非延迟插件。</summary>
    public IReadOnlyList<LoadedPlugin> LoadCore(
        IReadOnlySet<string> disabledPlugins,
        IReadOnlySet<string> deferredIds,
        out IReadOnlyList<(string dir, string error)> errors)
        => LoadMatching(disabledPlugins, id => !deferredIds.Contains(id), out errors);

    /// <summary>主窗口显示后加载延迟插件。</summary>
    public IReadOnlyList<LoadedPlugin> LoadDeferred(
        IReadOnlySet<string> disabledPlugins,
        IReadOnlySet<string> deferredIds,
        out IReadOnlyList<(string dir, string error)> errors)
        => LoadMatching(disabledPlugins, deferredIds.Contains, out errors);

    private IReadOnlyList<LoadedPlugin> LoadMatching(
        IReadOnlySet<string> disabledPlugins, Func<string, bool> include,
        out IReadOnlyList<(string dir, string error)> errors)
    {
        var loaded = new List<LoadedPlugin>();
        var errs = new List<(string, string)>();
        errors = errs;
        try
        {
            EnsurePluginsRoot();
            // Materialize before loading any assemblies so a scan failure cannot leak instances.
            var dirs = Directory.GetDirectories(_pluginsRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase);
            foreach (var dir in dirs)
            {
                if (!include(Path.GetFileName(dir))) continue;
                if (TryLoadOne(dir, disabledPlugins, out var plugin, out var error)) loaded.Add(plugin!);
                else if (error is not null) errs.Add((dir, error));
            }
        }
        catch (Exception ex) { errs.Add((_pluginsRoot, $"扫描插件目录失败: {ex.Message}")); }
        return loaded;
    }

    private void EnsurePluginsRoot()
    {
        if (!Directory.Exists(_pluginsRoot)) Directory.CreateDirectory(_pluginsRoot);
    }

    /// <summary>v0.1.34：抽取的"加载单个插件"逻辑，返回 LoadedPlugin 或 null（out 错误信息）
    /// <para>v0.1.80 T02：由 static 改实例方法——判定高风险豁免需要访问实例字段 <see cref="_trust"/>。</para></summary>
    private bool TryLoadOne(
        string dir,
        IReadOnlySet<string> disabledPlugins,
        out LoadedPlugin? plugin,
        out string? error)
    {
        plugin = null;
        error = null;
        PluginLoadContext? alc = null;
        var name = Path.GetFileName(dir);
        if (name.StartsWith('_')) return false;                    // 临时目录跳过（不计错误）
        if (disabledPlugins.Contains(name)) return false;          // 禁用跳过（不计错误）

        try
        {
            var manifestFile = Path.Combine(dir, "plugin.toml");
            if (!File.Exists(manifestFile)) { error = "缺少 plugin.toml"; return false; }

            var manifest = ManifestParser.Parse(manifestFile);
            if (!string.Equals(manifest.Id, name, StringComparison.OrdinalIgnoreCase))
            { error = $"manifest id '{manifest.Id}' 与目录名 '{name}' 不一致"; return false; }
            if (manifest.Api != 1)
            { error = $"不兼容的插件 API {manifest.Api}，当前宿主支持 API 1"; return false; }

            // 权限：manifest 声明 → 计算授予掩码
            var declared = PermissionChecker.FromManifest(manifest);

            // v0.1.80 T02：入口 dll 判定前置（信任通道要拿它做内容摘要绑定）
            var packageRoot = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
            var entryDll = Path.GetFullPath(Path.Combine(dir, manifest.Entry));
            if (Path.IsPathRooted(manifest.Entry) ||
                !entryDll.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetExtension(entryDll), ".dll", StringComparison.OrdinalIgnoreCase))
            { error = "入口 entry 必须是插件目录内的相对 DLL 路径"; return false; }
            if (!File.Exists(entryDll)) { error = $"入口 dll 不存在: {manifest.Entry}"; return false; }

            // 高风险权限：默认拒载；唯一豁免路径 = 命中内置信任清单（官方内置插件，可含内容摘要绑定）。
            // 第三方插件不在清单内 → 仍被拒载（安全底线不动）。
            if (PermissionChecker.IsHighRisk(declared) &&
                !_trust.IsTrusted(manifest.Id, manifestFile, entryDll, out var why))
            {
                error = $"插件声明了高风险权限且未受信任，拒绝自动加载: {declared}（{why}）";
                return false;
            }

            // ALC 隔离加载
            alc = new PluginLoadContext(dir, manifest.Entry);
            var assembly = alc.LoadFromAssemblyPath(entryDll);
            var instance = Instantiate(assembly, dir, out var instErr);
            if (instance is null) { error = instErr ?? "实例化失败"; return false; }
            try
            {
                if (!string.Equals(instance.Id, manifest.Id, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"入口类 Id '{instance.Id}' 与清单 id '{manifest.Id}' 不一致");
                if (instance.ApiVersion.Major != 1)
                    throw new InvalidOperationException($"入口类需要 API {instance.ApiVersion}，当前宿主支持 API 1");
            }
            catch
            {
                try { instance.Dispose(); } catch { }
                throw;
            }

            plugin = new LoadedPlugin
            {
                Manifest = manifest,
                PluginDir = dir,
                Context = alc,
                Instance = instance,
                GrantedPermissions = declared,
            };
            alc = null; // Ownership transferred to LoadedPlugin.
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            alc?.Unload(); // Also release contexts when loading or reflection throws.
        }
    }

    private static IPetPlugin? Instantiate(Assembly asm, string dir, out string? error)
    {
        error = null;
        try
        {
            foreach (var type in asm.GetTypes())
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (typeof(IPetPlugin).IsAssignableFrom(type))
                {
                    return (IPetPlugin)Activator.CreateInstance(type)!;
                }
            }
            error = $"在 {asm.GetName().Name} 中未找到实现 IPetPlugin 的类型";
        }
        catch (ReflectionTypeLoadException ex)
        {
            error = $"类型加载失败: {string.Join("; ", ex.LoaderExceptions.Select(e => e?.Message))}";
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        return null;
    }
}
