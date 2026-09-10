using DesktopPet.Core;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.Countdown;

/// <summary>
/// 倒计时插件：快捷 5/10 分钟倒计时，结束用系统通知 + 气泡提醒。
/// 与番茄钟的区别：更短的时间段（煮面/泡茶/眼保健操等场景）。
/// </summary>
public sealed class CountdownPlugin : PluginBase
{
    private IScheduleHandle? _timer;
    private DateTime _endTime;

    public override string Id => "com.lantern.countdown";
    public override string Name => "倒计时";

    public override PluginPermissions RequiredPermissions =>
        PluginPermissions.PetAnimate | PluginPermissions.UIRegister
        | PluginPermissions.Scheduler | PluginPermissions.Notify;

    public override void OnLoad(IPluginContext ctx)
    {
        base.OnLoad(ctx);

        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "cd-5",
            Label = "倒计时 5分",
            Icon = "clock",
            Order = 13,
            OnInvoke = () => Start(5),
        });
        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "cd-10",
            Label = "倒计时 10分",
            Icon = "clock",
            Order = 14,
            OnInvoke = () => Start(10),
        });
        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "cd-stop",
            Label = "停止倒计时",
            Icon = "close",
            Order = 15,
            OnInvoke = Stop,
        });
    }

    private void Start(int minutes)
    {
        Stop();
        _endTime = DateTime.Now.AddMinutes(minutes);
        Pet.Say($"倒计时 {minutes} 分钟开始", 2500);

        _timer = Capability.Scheduler.Every(1000, () =>
        {
            var remain = _endTime - DateTime.Now;
            if (remain <= TimeSpan.Zero)
            {
                Stop();
                Capability.System.Notify("Neko", $"⏱ {minutes} 分钟倒计时结束！");
                Pet.Say("时间到！", 4000);
            }
        });
    }

    private void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public override void OnUnload() => Stop();
}
