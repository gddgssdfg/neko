using DesktopPet.Core;
using DesktopPet.Sdk;

namespace SamplePlugin;

/// <summary>
/// 示例插件：验证三条加载链路
///   1. 径向菜单槽位注册（ui.register）
///   2. 定时任务（scheduler）
///   3. 宠物说话气泡（pet.animate + pet.read）
/// </summary>
public sealed class SamplePlugin : PluginBase
{
    private IScheduleHandle? _timer;
    private int _tickCount;

    public override string Id => "com.lantern.sample";
    public override string Name => "示例插件";

    public override PluginPermissions RequiredPermissions =>
        PluginPermissions.PetAnimate | PluginPermissions.UIRegister;

    public override void OnLoad(IPluginContext ctx)
    {
        base.OnLoad(ctx);

        // 1. 注册径向菜单槽位
        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "sample-wave",
            Label = "打个招呼",
            Icon = "wave",
            Order = 10,
            OnInvoke = () => Pet.Say("喵！我是示例插件"),
        });

        // 2. 每 5 秒存一次计数，验证 Storage 隔离
        _timer = Capability.Scheduler.Every(5000, () =>
        {
            _tickCount++;
            Capability.Storage.Set("tickCount", _tickCount);
            Pet.SetMood(_tickCount % 2 == 0 ? PetMood.Happy : PetMood.Idle);
        });

        System.Diagnostics.Debug.WriteLine($"[SamplePlugin] 加载完成，已注册菜单槽位与定时器");
    }

    public override void OnTick(TimeSpan delta)
    {
        // 主循环 tick（10Hz）—— 可做轻量逻辑
    }

    public override void OnUnload()
    {
        _timer?.Dispose();
    }
}
