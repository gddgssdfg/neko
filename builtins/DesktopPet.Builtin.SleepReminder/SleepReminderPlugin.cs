using DesktopPet.Core;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.SleepReminder;

/// <summary>
/// 睡眠提醒插件：23:00 提醒睡觉（每分钟检查一次，到点一次性通知 + 气泡）。
/// 右键菜单「23:00 睡眠提醒」开启 / 「取消睡眠提醒」关闭。
/// </summary>
public sealed class SleepReminderPlugin : PluginBase
{
    private const int RemindHour = 23;

    private IScheduleHandle? _timer;

    public override string Id => "com.lantern.sleep";
    public override string Name => "睡眠提醒";

    public override PluginPermissions RequiredPermissions =>
        PluginPermissions.PetAnimate | PluginPermissions.UIRegister
        | PluginPermissions.Scheduler | PluginPermissions.Notify;

    public override void OnLoad(IPluginContext ctx)
    {
        base.OnLoad(ctx);

        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "sleep-set",
            Label = "23:00 睡眠提醒",
            Icon = "sleep",
            Order = 24,
            OnInvoke = () => Start(RemindHour),
        });
        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "sleep-cancel",
            Label = "取消睡眠提醒",
            Icon = "close",
            Order = 25,
            OnInvoke = Stop,
        });
    }

    private void Start(int hour)
    {
        Stop();
        Pet.Say($"好的，{hour}:00 会提醒你睡觉", 2500);
        _timer = Capability.Scheduler.Every(60_000, () =>
        {
            var now = DateTime.Now;
            if (now.Hour == hour && now.Minute == 0)
            {
                Stop();
                Capability.System.Notify("Neko", $"🌙 {hour}:00 了，该睡觉啦！");
                Pet.Say("夜深了，早点休息吧，明天见～", 5000);
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
