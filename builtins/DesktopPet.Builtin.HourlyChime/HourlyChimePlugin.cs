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

    // v0.1.82：整点报时配置的内存缓存（开始/结束时段 + 上次已报时的小时）。
    // 避免每 tick（60s）做 4 次 Storage.Contains/Get；仅在 RefreshConfig()（加载 / 设置保存）时刷新。
    private int _startHour = DefaultStartHour;
    private int _endHour = DefaultEndHour;
    private int _lastHour;

    public override string Id => "com.lantern.hourly";
    public override string Name => "整点报时";

    public override PluginPermissions RequiredPermissions =>
        PluginPermissions.PetAnimate | PluginPermissions.UIRegister
        | PluginPermissions.Storage | PluginPermissions.Scheduler;

    public override void OnLoad(IPluginContext ctx)
    {
        base.OnLoad(ctx);

        _enabled = Capability.Storage.Get<bool>(EnabledKey);
        RefreshConfig();   // 载入 start/end/上次报时小时的缓存（tick 路径不再读盘）
        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "hourly-toggle",
            Label = $"整点报时（{(_enabled ? "开" : "关")}）",
            Icon = "clock",
            Order = 23,
            OnInvoke = Toggle,
        });

        // 设置页保存后回调 RefreshConfig()：新时段立即生效，无需下次 tick 才读盘同步。
        UI.RegisterSettingsPage(new HourlyChimeSettingsPage(Capability.Storage, () => _enabled, v => _enabled = v, RefreshConfig));

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

    /// <summary>
    /// 从 Storage 重新载入「时段配置 + 上次报时小时」到内存缓存。
    /// 仅在插件加载与设置页保存时调用（<b>不在 tick 路径</b>），改动即时生效。
    /// </summary>
    private void RefreshConfig()
    {
        var start = Capability.Storage.Contains(StartHourKey) ? Capability.Storage.Get<int>(StartHourKey) : DefaultStartHour;
        var end = Capability.Storage.Contains(EndHourKey) ? Capability.Storage.Get<int>(EndHourKey) : DefaultEndHour;
        _startHour = Math.Clamp(start, 0, 23);
        _endHour = Math.Clamp(end, 0, 23);
        _lastHour = Capability.Storage.Get<int>(LastHourKey);
    }

    private void CheckHour()
    {
        var now = DateTime.Now;

        // v0.1.82：时段 / 上次报时小时一律走内存缓存 —— tick 路径零读盘（原来每 tick 4 次 Contains/Get）。
        // 时段外不播报（start > end 视为跨天，如 22-6 点）
        var inWindow = _startHour <= _endHour ? (now.Hour >= _startHour && now.Hour < _endHour)
                                              : (now.Hour >= _startHour || now.Hour < _endHour);
        if (!inWindow) return;

        if (now.Hour == _lastHour) return;

        _lastHour = now.Hour;
        Capability.Storage.Set(LastHourKey, now.Hour);   // 仍持久化 lastHour：跨插件重载不重复报时
        Pet.Say($"现在是 {now:HH:mm} 啦", 3000);
    }

    public override void OnUnload() => StopTimer();
}
