using DesktopPet.Core;

namespace DesktopPet.Sdk;

/// <summary>
/// 插件基类。第三方插件继承它即可，宿主通过反射识别。
/// 内置插件与第三方插件使用完全相同的基类（dogfooding）。
/// </summary>
public abstract class PluginBase : IPetPlugin
{
    /// <summary>插件上下文（OnLoad 后可用）</summary>
    protected IPluginContext Context { get; private set; } = null!;
    /// <summary>宠物控制快捷方式</summary>
    protected IPetApi Pet => Context.Pet;
    /// <summary>界面注册快捷方式</summary>
    protected IUIApi UI => Context.UI;
    /// <summary>能力快捷方式</summary>
    protected ICapabilityApi Capability => Context.Capability;

    public abstract string Id { get; }
    public abstract string Name { get; }
    public virtual Version ApiVersion => new(1, 0);
    public virtual PluginPermissions RequiredPermissions => PluginPermissions.None;

    public virtual void OnLoad(IPluginContext ctx) => Context = ctx;
    public virtual void OnTick(TimeSpan delta) { }
    public virtual void OnUnload() { }
    public virtual void Dispose() { }
}
