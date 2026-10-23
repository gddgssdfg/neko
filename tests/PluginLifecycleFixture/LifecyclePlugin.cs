using DesktopPet.Core;

namespace PluginLifecycleFixture;

// Deliberately leaves host-owned resources registered, including when OnLoad fails.
public sealed class LifecyclePlugin : IPetPlugin
{
    private IPluginStorage? _storage;
    public string Id => "com.neko.lifecycle-test";
    public string Name => "Lifecycle fixture";
    public Version ApiVersion => new(1, 0);
    public PluginPermissions RequiredPermissions => PluginPermissions.None;
    public void OnLoad(IPluginContext ctx)
    {
        _storage = ctx.Capability.Storage;
        _storage.Set("data-dir", ctx.PluginDataDir);
        _storage.Set("loads", _storage.Get<int>("loads") + 1);
        ctx.Capability.Events.On("probe:lifecycle", _ => _storage.Set("events", _storage.Get<int>("events") + 1));
        ctx.Capability.Scheduler.Every(60000, () => { });
        if (_storage.Get<bool>("fail")) throw new InvalidOperationException("intentional OnLoad failure");
    }
    public void OnTick(TimeSpan delta) { }
    public void OnUnload() => _storage?.Set("unloads", _storage.Get<int>("unloads") + 1);
    public void Dispose() => _storage?.Set("disposes", _storage.Get<int>("disposes") + 1);
}
