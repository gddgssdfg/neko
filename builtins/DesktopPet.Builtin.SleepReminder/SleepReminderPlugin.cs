using DesktopPet.Core;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.SleepReminder;

/// <summary>
/// 睡眠提醒插件：可设定提醒时间（时 + 分，默认 23:00），到点通知 + 气泡。
/// 径向菜单「HH:mm 睡眠提醒」开启 / 「取消睡眠提醒」关闭。设置页可改时间（持久化到 Storage）。
/// <para>v0.1.84：调度由「每分钟轮询 + <c>now.Minute == minute</c> 比对」改为 <b>精确一次性调度</b>
/// （<see cref="IScheduler.At"/>；到点触发后由 <see cref="OnReminderDue"/> 续排次日同刻）——
/// 与 Bazi 每日播报同源的修法。轮询模型只要目标那一分钟恰好进程挂起 / 休眠恢复 / 时钟跳变 / 主循环卡顿，
/// 就会<b>整次漏提醒</b>（下次要等 24 小时）；绝对时刻调度则在进程恢复调度时「补触发」，不再丢事件。</para>
/// </summary>
public sealed class SleepReminderPlugin : PluginBase
{
    private const string HourKey = "remindHour";
    private const string MinuteKey = "remindMinute";
    private const int DefaultHour = 23;
    private const int DefaultMinute = 0;

    /// <summary>防「同一天重复提醒」的日期键（Storage 存 yyyy-MM-dd）。</summary>
    private const string LastReminderKey = "lastReminderDate";

    /// <summary>当前排期句柄（一次性绝对时刻；触发后由 <see cref="OnReminderDue"/> 续排次日）。</summary>
    private IScheduleHandle? _timer;

    /// <summary>
    /// 用户是否「已开启」提醒。<b>与 <see cref="_timer"/> 分开</b>：取消动作会先清本标志、再 Dispose 句柄，
    /// 这样即便有一个「已出队但尚未执行完」的到点回调在途，它收尾时看到 <c>_armed == false</c> 也不会把已取消的
    /// 提醒又排回来（用「句柄是否非空」当开启标志会有这个竞态洞）。
    /// </summary>
    private volatile bool _armed;

    /// <summary>
    /// OnUnload 后置 true：阻止「在途的到点回调」在卸载后又排一个新定时器 ——
    /// 否则热重载会遗留一个引用旧插件类型的定时器，钉住旧 ALC（与 Bazi 同款防护）。
    /// </summary>
    private volatile bool _unloaded;

    public override string Id => "com.lantern.sleep";
    public override string Name => "睡眠提醒";

    public override PluginPermissions RequiredPermissions =>
        PluginPermissions.PetAnimate | PluginPermissions.UIRegister
        | PluginPermissions.Scheduler | PluginPermissions.Notify | PluginPermissions.Storage;

    public override void OnLoad(IPluginContext ctx)
    {
        base.OnLoad(ctx);

        RefreshSlot();   // sleep-set：文案 = 当前设定时间

        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "sleep-cancel",
            Label = "取消睡眠提醒",
            Icon = "close",
            Order = 25,
            OnInvoke = Stop,
        });

        // 设置页保存后回调 OnSettingsSaved()：刷新槽位文案，且若提醒在运行则立即按新时刻重排。
        UI.RegisterSettingsPage(new SleepReminderSettingsPage(Capability.Storage, OnSettingsSaved));
    }

    // ---- 径向槽位 ----

    /// <summary>
    /// 注册 / 刷新「HH:mm 睡眠提醒」径向槽位。固定 Id（<c>sleep-set</c>），
    /// 依靠宿主按 (scope, Id) 幂等 upsert：设置页改时间后重新调用只刷新 <see cref="RadialSlot.Label"/>。
    /// <para>调用点：OnLoad、设置页保存。均为 UI 线程，与宿主 <c>[ThreadStatic]</c> scope 同线程，无需封送。</para>
    /// </summary>
    private void RefreshSlot()
    {
        var (hour, minute) = ReadSettings();
        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "sleep-set",
            Label = $"{hour:D2}:{minute:D2} 睡眠提醒",
            Icon = "sleep",
            Order = 24,
            OnInvoke = () => Start(),
        });
    }

    /// <summary>
    /// 设置页保存后的统一收尾：刷新槽位文案 +（若提醒在运行）按新时刻重排。
    /// <para><b>语义选择</b>：运行中改时间 = 重排到「下一个新时刻」（取消旧的、按新值重新排），
    /// 而非「仅当次提醒后生效」。理由：用户进设置页改时间，本意就是让它按新时间响；
    /// 例：22:00 已按 23:00 布防，改成 08:00 → 下一次命中即次日 08:00（今天 08:00 已过），符合直觉。</para>
    /// <para>未开启（<see cref="_armed"/> 为 false）则不擅自替用户开启。</para>
    /// </summary>
    private void OnSettingsSaved()
    {
        RefreshSlot();
        if (_armed)
        {
            var (hour, minute) = ReadSettings();
            Arm(hour, minute);
        }
    }

    // ---- 调度（v0.1.84：精确一次性调度，替代每分钟轮询） ----

    private (int Hour, int Minute) ReadSettings()
    {
        var hour = Capability.Storage.Contains(HourKey) ? Capability.Storage.Get<int>(HourKey) : DefaultHour;
        var minute = Capability.Storage.Contains(MinuteKey) ? Capability.Storage.Get<int>(MinuteKey) : DefaultMinute;
        return (Math.Clamp(hour, 0, 23), Math.Clamp(minute, 0, 59));
    }

    /// <summary>开启提醒（径向槽位「HH:mm 睡眠提醒」点击）。</summary>
    private void Start()
    {
        var (hour, minute) = ReadSettings();
        _armed = true;
        Pet.Say($"好的，{hour:D2}:{minute:D2} 会提醒你睡觉", 2500);
        Arm(hour, minute);
    }

    /// <summary>
    /// 取消提醒（径向槽位「取消睡眠提醒」点击）。
    /// <b>先清 <see cref="_armed"/> 再 Dispose</b>：保证在途回调收尾时不会把提醒排回来。
    /// </summary>
    private void Stop()
    {
        _armed = false;
        DisposeTimer();
    }

    /// <summary>
    /// 按给定时/分排定<b>下一个绝对时刻</b>的一次性调度：先取消旧句柄（防重复注册 / 防双定时器），
    /// 再 <see cref="IScheduler.At"/>。<b>已卸载则直接返回</b>，绝不新建调度（热重载铁律）。
    /// <para>线程无关：可被 UI 线程（OnLoad / 开启 / 设置保存）与调度线程（到点回调收尾）调用。</para>
    /// </summary>
    private void Arm(int hour, int minute)
    {
        if (_unloaded) return;   // 已卸载：绝不新建调度
        DisposeTimer();          // 先取消旧排期，避免重复注册 / 两个定时器并存
        var next = NextReminderTime(DateTime.Now, hour, minute);
        _timer = Capability.Scheduler.At(next, OnReminderDue);
    }

    /// <summary>
    /// 到点回调（调度线程池线程）：当天未提醒过则提醒一次，随后<b>续排次日同刻</b>。
    /// <para>与 Bazi 的 <c>OnBroadcastDue</c> 同构 —— 一次性绝对调度在进程恢复时会补触发，
    /// 但必须自己在触发后接上下一日。用日期键防「同一天重复提醒」。</para>
    /// </summary>
    private void OnReminderDue()
    {
        if (_unloaded) return;   // 已卸载：不再提醒、也不再排期
        try
        {
            var (hour, minute) = ReadSettings();
            var now = DateTime.Now;
            var todayKey = now.ToString("yyyy-MM-dd");
            // 同一天只提醒一次（防「重排 / 把时间改到更晚」造成的重复提醒）
            if (Capability.Storage.Get<string>(LastReminderKey) != todayKey)
            {
                Capability.Storage.Set(LastReminderKey, todayKey);
                Capability.System.Notify("Neko", $"🌙 {hour:D2}:{minute:D2} 了，该睡觉啦！");
                Pet.Say("夜深了，早点休息吧，明天见～", 5000);
            }
        }
        catch (Exception ex)
        {
            // 铁律 7：到点处理失败静默降级（仅日志留痕），绝不影响宿主
            try { Capability.System.Log(PluginLogLevel.Warn, $"睡眠提醒到点处理失败：{ex.GetType().Name}: {ex.Message}"); }
            catch { /* 日志通道合约上不抛；双保险 */ }
        }
        finally
        {
            // 无论本次是否真的提醒，都要续上下一次（Arm 内部按 _armed / _unloaded 自行判断是否排期）
            if (_armed)
            {
                var (hour, minute) = ReadSettings();
                Arm(hour, minute);
            }
        }
    }

    private void DisposeTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>
    /// 计算 <paramref name="now"/> 之后<b>严格更晚</b>的下一次 HH:mm 绝对时刻
    /// （与 Bazi 的 <c>NextBroadcastTime</c> 同款边界）。
    /// <para>当天时刻已过（或恰为此刻）→ 顺延到<b>次日</b>同刻；用 <see cref="DateTime.AddDays"/> 进位，
    /// 跨月 / 跨年自动处理。中国不实行夏令时，无需处理 DST 跳变。</para>
    /// <para><b>「恰为整点也顺延次日」</b>：<c>todaySlot &gt; now</c> 为严格大于，故当 <c>now</c> 恰好等于目标时刻时
    /// 返回次日，避免刚排好就立即触发（也避免与「刚触发完再排期」形成同一时刻的重触发）。</para>
    /// </summary>
    internal static DateTime NextReminderTime(DateTime now, int hour, int minute)
    {
        var todaySlot = new DateTime(now.Year, now.Month, now.Day, hour, minute, 0);
        return todaySlot > now ? todaySlot : todaySlot.AddDays(1);
    }

    public override void OnUnload()
    {
        _unloaded = true;   // 先置位：阻止在途回调在卸载后重排（避免遗留定时器钉住旧 ALC）
        DisposeTimer();     // 铁律：Dispose 排期句柄，热重载不重复注册
    }
}
