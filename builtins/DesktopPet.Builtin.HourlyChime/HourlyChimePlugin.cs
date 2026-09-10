using DesktopPet.Core;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.HourlyChime;

/// <summary>
/// 整点报时插件（dogfooding：插件手册里的完整示例原样落地为真实内置插件）。
/// 每分钟检查一次，跨整点播报；可开关 + 可设定提醒时段（默认仅 9-18 点播报）。
/// </summary>
public sealed class HourlyChimePlugin : PluginBase
{
    private const string LastHourKey = "lastHour";
    private const string EnabledKey = "enabled";
    private const string StartHourKey = "startHour";
    private const string EndHourKey = "endHour";
    private const int DefaultStartHour = 9;
    private const int DefaultEndHour = 18;

    private IScheduleHandle? _timer;
    private bool _enabled;

    public override string Id => "com.lantern.hourly";
    public override string Name => "整点报时";

    public override PluginPermissions RequiredPermissions =>
        PluginPermissions.PetAnimate | PluginPermissions.UIRegister
        | PluginPermissions.Storage | PluginPermissions.Scheduler;

    public override void OnLoad(IPluginContext ctx)
    {
        base.OnLoad(ctx);

        _enabled = Capability.Storage.Get<bool>(EnabledKey);
        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "hourly-toggle",
            Label = $"整点报时（{(_enabled ? "开" : "关")}）",
            Icon = "clock",
            Order = 23,
            OnInvoke = Toggle,
        });

        UI.RegisterSettingsPage(new HourlyChimeSettingsPage(Capability.Storage, () => _enabled, v => _enabled = v));

        if (_enabled) StartTimer();
    }

    private void Toggle()
    {
        _enabled = !_enabled;
        Capability.Storage.Set(EnabledKey, _enabled);
        if (_enabled) StartTimer();
        else StopTimer();
        Pet.Say(_enabled ? "整点报时已开启" : "整点报时已关闭", 2500);
    }

    private void StartTimer()
    {
        StopTimer();
        _timer = Capability.Scheduler.Every(60_000, CheckHour);
    }

    private void StopTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void CheckHour()
    {
        var now = DateTime.Now;
        var start = Capability.Storage.Contains(StartHourKey) ? Capability.Storage.Get<int>(StartHourKey) : DefaultStartHour;
        var end = Capability.Storage.Contains(EndHourKey) ? Capability.Storage.Get<int>(EndHourKey) : DefaultEndHour;
        start = Math.Clamp(start, 0, 23);
        end = Math.Clamp(end, 0, 23);

        // 时段外不播报（start > end 视为跨天，如 22-6 点）
        var inWindow = start <= end ? (now.Hour >= start && now.Hour < end)
                                    : (now.Hour >= start || now.Hour < end);
        if (!inWindow) return;

        var last = Capability.Storage.Get<int>(LastHourKey);
        if (now.Hour == last) return;

        Capability.Storage.Set(LastHourKey, now.Hour);
        Pet.Say($"现在是 {now:HH:mm} 啦", 3000);
    }

    public override void OnUnload() => StopTimer();
}
