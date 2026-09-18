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

        // 2. 周期逻辑的正确写法 = Capability.Scheduler（Every / At）。
        //    ⚠️ 不要用 IPetPlugin.OnTick —— 宿主当前【不会调用】该方法（见 IPetPlugin.OnTick 的接口注释），
        //    把周期逻辑写进 OnTick 会永不执行。此处 Scheduler.Every 才是真实可用的示范。
        _timer = Capability.Scheduler.Every(5000, () =>
        {
            _tickCount++;
            Capability.Storage.Set("tickCount", _tickCount);
            Pet.SetMood(_tickCount % 2 == 0 ? PetMood.Happy : PetMood.Idle);
        });

        // 3. 日志走 Capability.System.Log：宿主会附加 [插件:<id>] 前缀，便于定位是哪个插件在报
        Capability.System.Log(PluginLogLevel.Info, "加载完成，已注册菜单槽位与定时器");
    }

    public override void OnUnload()
    {
        _timer?.Dispose();
    }
}
