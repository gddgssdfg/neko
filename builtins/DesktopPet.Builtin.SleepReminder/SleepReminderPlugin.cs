using DesktopPet.Core;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.SleepReminder;

/// <summary>
/// 睡眠提醒插件：可设定提醒时间（时 + 分，默认 23:00），每分钟检查一次，到点一次性通知 + 气泡。
/// 右键菜单「HH:mm 睡眠提醒」开启 / 「取消睡眠提醒」关闭。设置页可改时间（持久化到 Storage）。
/// </summary>
public sealed class SleepReminderPlugin : PluginBase
{
    private const string HourKey = "remindHour";
    private const string MinuteKey = "remindMinute";
    private const int DefaultHour = 23;
    private const int DefaultMinute = 0;

    private IScheduleHandle? _timer;

    public override string Id => "com.lantern.sleep";
    public override string Name => "睡眠提醒";

    public override PluginPermissions RequiredPermissions =>
        PluginPermissions.PetAnimate | PluginPermissions.UIRegister
        | PluginPermissions.Scheduler | PluginPermissions.Notify | PluginPermissions.Storage;

    public override void OnLoad(IPluginContext ctx)
    {
        base.OnLoad(ctx);

        var (hour, minute) = ReadSettings();
        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "sleep-set",
            Label = $"{hour:D2}:{minute:D2} 睡眠提醒",
            Icon = "sleep",
            Order = 24,
            OnInvoke = () => Start(),
        });
        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "sleep-cancel",
            Label = "取消睡眠提醒",
            Icon = "close",
            Order = 25,
            OnInvoke = Stop,
        });

        UI.RegisterSettingsPage(new SleepReminderSettingsPage(Capability.Storage));
    }

    private (int Hour, int Minute) ReadSettings()
    {
        var hour = Capability.Storage.Contains(HourKey) ? Capability.Storage.Get<int>(HourKey) : DefaultHour;
        var minute = Capability.Storage.Contains(MinuteKey) ? Capability.Storage.Get<int>(MinuteKey) : DefaultMinute;
        return (Math.Clamp(hour, 0, 23), Math.Clamp(minute, 0, 59));
    }

    private void Start()
    {
        Stop();
        var (hour, minute) = ReadSettings();
        Pet.Say($"好的，{hour:D2}:{minute:D2} 会提醒你睡觉", 2500);
        _timer = Capability.Scheduler.Every(60_000, () =>
        {
            var now = DateTime.Now;
            if (now.Hour == hour && now.Minute == minute)
            {
                Stop();
                Capability.System.Notify("Neko", $"🌙 {hour:D2}:{minute:D2} 了，该睡觉啦！");
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
