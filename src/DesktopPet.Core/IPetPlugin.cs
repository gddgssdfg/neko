namespace DesktopPet.Core;

/// <summary>插件宿主上下文：插件可访问的全部能力的入口</summary>
public interface IPluginContext
{
    /// <summary>宿主程序版本</summary>
    Version HostVersion { get; }
    /// <summary>宿主数据根目录</summary>
    string DataRoot { get; }
    /// <summary>插件数据目录（data/plugins/&lt;id&gt;/）</summary>
    string PluginDataDir { get; }

    IPetApi Pet { get; }
    IUIApi UI { get; }
    ICapabilityApi Capability { get; }
}

/// <summary>插件主接口：所有插件必须实现</summary>
public interface IPetPlugin : IDisposable
{
    /// <summary>唯一标识（反向域名，与插件目录同名）</summary>
    string Id { get; }
    /// <summary>显示名</summary>
    string Name { get; }
    /// <summary>依赖的 Core API 版本</summary>
    Version ApiVersion { get; }
    /// <summary>所需权限（manifest 声明后宿主据此授予）</summary>
    PluginPermissions RequiredPermissions { get; }

    /// <summary>装载时调用</summary>
    void OnLoad(IPluginContext ctx);
    /// <summary>
    /// 主循环 tick。
    /// <para><b>⚠️ 宿主当前不会调用此方法</b>（保留仅为 API 兼容，请勿依赖它做任何逻辑）。
    /// 需要周期逻辑请用宿主上下文里的 <c>IPluginContext.Capability.Scheduler</c>：
    /// <c>Every(intervalMs, callback)</c> / <c>At(time, callback)</c>。</para>
    /// </summary>
    /// <param name="delta">距上次 tick 的时长（当前无调用方，无实际取值）。</param>
    void OnTick(TimeSpan delta);
    /// <summary>卸载时调用</summary>
    void OnUnload();
}
