namespace DesktopPet.Core;

/// <summary>插件宿主上下文：插件可访问的全部能力的入口</summary>
public interface IPluginContext
{
    /// <summary>宿主程序版本</summary>
    Version HostVersion { get; }
    /// <summary>宿主数据根目录</summary>
    /// <remarks>
    /// v0.1.98 起这是**独立于程序目录**的可写位置：默认 <c>%AppData%\Neko</c>；
    /// 程序根存在 <c>portable.flag</c> 时为程序根的 <c>data\</c>（便携模式）；
    /// 环境变量 <c>NEKO_DATA_DIR</c> 可覆盖（测试隔离用）。
    /// 插件**不要**自己拼路径——始终用 <see cref="PluginDataDir"/>。
    /// </remarks>
    string DataRoot { get; }
    /// <summary>插件数据目录（数据根下的 <c>plugins/&lt;id&gt;/</c>；见 <see cref="DataRoot"/> 的说明）</summary>
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
