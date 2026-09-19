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
    // v0.1.84：volatile —— 现在会被 tick 回调（线程池）读（CheckHour 的「关闭即不报时」早返回），
    // 写发生在 UI 线程（Toggle / 设置页保存）。保证开关切换对定时器线程立即可见。
    private volatile bool _enabled;

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
        RefreshSlot();     // 注册径向槽位（文案含开关状态）

        // 设置页保存后回调：刷新内存配置（时段立即生效，无需下次 tick 才读盘同步）+ 刷新径向槽位文案
        // （开关状态可能在设置页里被改过 —— 审计 P2-2）+ 让开关立即生效（按新值启停计时器 —— SyncTimer）。
        UI.RegisterSettingsPage(new HourlyChimeSettingsPage(
            Capability.Storage, () => _enabled, v => _enabled = v,
            () => { RefreshConfig(); RefreshSlot(); SyncTimer(); }));

        SyncTimer();
    }

    /// <summary>
    /// 注册 / 刷新「整点报时」径向槽位。固定使用同一个 Id（<c>hourly-toggle</c>），
    /// 依靠宿主按 (scope, Id) 幂等 upsert：在状态变化点重复调用只更新 <see cref="RadialSlot.Label"/>，
    /// 不会新增槽位，也不会像「先 UnregisterAll 再注册」那样误清掉本插件其它注册。
    /// <para>调用点：OnLoad、开关切换（<see cref="Toggle"/>）、设置页保存。均为 UI 线程，
    /// 与宿主 <c>[ThreadStatic]</c> scope 及 <c>RadialMenu</c> 的读取同线程，无需额外封送。</para>
    /// </summary>
    private void RefreshSlot()
    {
        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "hourly-toggle",
            Label = $"整点报时（{(_enabled ? "开" : "关")}）",
            Icon = "clock",
            Order = 23,
            OnInvoke = Toggle,
        });
    }

    private void Toggle()
    {
        _enabled = !_enabled;
        Capability.Storage.Set(EnabledKey, _enabled);
        SyncTimer();     // 开关状态变化 → 计时器立即启停
        RefreshSlot();   // 开关状态变化 → 同步径向槽位文案
        Pet.Say(_enabled ? "整点报时已开启" : "整点报时已关闭", 2500);
    }

    /// <summary>
    /// 让计时器与当前 <see cref="_enabled"/> 对齐（<b>幂等</b>）：
    /// 开 = 确保在跑（已在跑则不动，避免无谓重建把 60s 轮询节奏打乱）；关 = 停掉。
    /// <para>为什么需要它：<see cref="_enabled"/> 有<b>两个</b>入口 —— 径向槽位 <see cref="Toggle"/> 与设置页保存。
    /// 原先只有 Toggle 动计时器，设置页只改标志位 → 出现「设置页里关了，到点还在报」（改了设置不生效）。
    /// 抽成一个方法复用，让两个入口行为一致。</para>
    /// </summary>
    private void SyncTimer()
    {
        if (_enabled) { if (_timer is null) StartTimer(); }
        else StopTimer();
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
        // 关闭后即便有「已出队但尚未执行完」的旧 tick 回调在途，也一律不报时（保证「关了到点就不响」）。
        if (!_enabled) return;

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
