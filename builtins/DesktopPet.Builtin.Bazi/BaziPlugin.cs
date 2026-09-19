using DesktopPet.Core;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.Bazi;

/// <summary>
/// 命理助手插件。P0：骨架 + 生日设置页 + lunar 依赖加载验证；P2：每日运势播报；P3：AI 深度咨询；P4：人格测试（MBTI 问卷 + 计分）。
/// <para>v0.1.84 两项改动：
/// ① 每日播报由「每分钟轮询 + 字符串比对 HH:mm」改为 <b>精确一次性调度</b>（<see cref="IScheduler.At"/>，播报后重排次日同刻）——
///    轮询模型只要目标那一分钟恰好进程挂起 / 休眠恢复 / 时钟跳变 / 主循环卡顿，就会<b>整天漏播</b>（下次要等 24 小时）；
/// ② OnLoad 的 6 项自检改为受环境变量 <c>NEKO_PLUGIN_SELFTEST</c> 控制的开关（默认不跑），
///    生产启动不再白跑 ~8MB / 启动 CPU 大头（详见 <see cref="ShouldRunSelfTest"/>）。</para>
/// </summary>
public sealed class BaziPlugin : PluginBase
{
    /// <summary>防重复播报的日期键（Storage 存 yyyy-MM-dd）。</summary>
    private const string LastBroadcastKey = "lastBroadcastDate";

    /// <summary>OnLoad 自检开关的环境变量名（值取 1/true/yes/on 均视为开启），详见 <see cref="ShouldRunSelfTest"/>。</summary>
    private const string SelfTestEnvVar = "NEKO_PLUGIN_SELFTEST";

    /// <summary>每日播报的调度句柄（一次性；触发后由 <see cref="OnBroadcastDue"/> 续排下一日）。</summary>
    private IScheduleHandle? _broadcastTimer;

    /// <summary>
    /// OnUnload 后置 true：阻止「在途的播报回调」在卸载后又被重排 ——
    /// 否则热重载会遗留一个引用旧插件类型的定时器，钉住旧 ALC（违背热重载铁律）。
    /// </summary>
    private volatile bool _unloaded;

    public override string Id => "com.lantern.bazi";
    public override string Name => "命理助手";

    /// <summary>与 plugin.toml 的 permissions 段保持一致（两处都要写，手册 4.1）。</summary>
    public override PluginPermissions RequiredPermissions =>
        PluginPermissions.PetAnimate | PluginPermissions.PetRead
        | PluginPermissions.UIRegister | PluginPermissions.Storage
        | PluginPermissions.Scheduler | PluginPermissions.Notify | PluginPermissions.Network;

    public override void OnLoad(IPluginContext ctx)
    {
        base.OnLoad(ctx);   // 铁律 1：第一行，否则 Pet / UI / Capability 全为 null

        // OnLoad 自检：默认不跑，仅当显式打开开关时（见 ShouldRunSelfTest）。
        //  ① 开启时：若 lunar.dll 未被插件 ALC 正确解析，这里会抛异常（加载器测试据此判定第三方依赖链）；
        //  ② 关闭时（生产默认）：不触碰 lunar / MBTI / DPAPI，消除每次启动的 ~8MB 与启动 CPU 白跑。
        if (ShouldRunSelfTest())
            RunSelfChecks();

        // 径向槽位「今日运势」：手动立即播报一次（便于验证 + 用户实用入口）
        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "bazi-today",
            Label = "今日运势",
            Icon = "book",
            Order = 30,
            OnInvoke = () => BroadcastManual(),
        });

        // 径向槽位「问问猫」：AI 深度咨询入口（P3）
        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "bazi-ask",
            Label = "问问猫",
            Icon = "book",
            Order = 31,
            OnInvoke = () => OpenChat(),
        });

        // 设置页：注入「保存后重排播报」回调 —— 用户改动播报时间 / 开关后，立即取消旧排期、按新值重排。
        UI.RegisterSettingsPage(new BaziSettingsPage(Capability.Storage, RescheduleBroadcast));

        // 每日定时播报：精确一次性调度（到点触发一次 → 播报 → 续排次日同刻）
        RescheduleBroadcast();
    }

    public override void OnUnload()
    {
        // 先置位：阻止在途回调在卸载后重排（避免遗留定时器钉住旧 ALC）
        _unloaded = true;
        // 铁律 2：必须 Dispose 定时器句柄，否则热重载会重复注册
        _broadcastTimer?.Dispose();
        _broadcastTimer = null;
        // v0.1.82：释放共享 HttpClient —— 否则它的连接池定时器会从进程级根钉住本插件类型，
        // 导致整个 ALC 无法回收（实测每次热重载泄漏 ~8.5MB）。
        DeepSeekClient.DisposeShared();
    }

    // ---- 每日播报调度（v0.1.84：精确一次性调度，替代每分钟轮询） ----

    /// <summary>
    /// 重排下一次「每日播报」：先取消旧句柄（若有），再按当前档案算出<b>下一个绝对时刻</b>并排一次性调度。
    /// <para>触发时机：OnLoad、设置页保存后、以及每次播报完成后。未开启播报 / 未填生日时<b>不排期</b>
    /// （待用户在设置页开启或填好档案保存时，由保存回调再次驱动本方法）。</para>
    /// <para>线程无关：可被 UI 线程（OnLoad / 保存回调）与调度线程（播报完成后）调用。</para>
    /// </summary>
    private void RescheduleBroadcast()
    {
        try
        {
            if (_unloaded) return;   // 已卸载：绝不新建调度

            // 先取消旧排期，避免重复注册（热重载铁律）
            _broadcastTimer?.Dispose();
            _broadcastTimer = null;

            // 每次重新读最新档案，避免设置页改动后缓存陈旧
            var profile = BaziProfile.Load(Capability.Storage);
            if (!profile.DailyBroadcastEnabled) return;
            if (!profile.HasBirth) return;   // 档案不全 → 排了也播不出，等保存回调再驱动

            if (!TryParseHhMm(profile.BroadcastTime, out var hour, out var minute))
            {
                Capability.System.Log(PluginLogLevel.Warn,
                    $"每日播报时间格式非法「{profile.BroadcastTime}」（期望 HH:mm），本次不排期");
                return;
            }

            var now = DateTime.Now;
            var next = NextBroadcastTime(now, hour, minute);
            _broadcastTimer = Capability.Scheduler.At(next, OnBroadcastDue);
            Capability.System.Log(PluginLogLevel.Info, $"每日播报已排期：下次 {next:yyyy-MM-dd HH:mm}");
        }
        catch (Exception ex)
        {
            // 铁律 7：排期失败不拖垮宿主、也不影响设置页保存
            Capability.System.Log(PluginLogLevel.Warn, $"每日播报排期失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 到点回调（调度线程池线程）：当天未播过则播报一次，随后<b>立即续排次日同刻</b>。
    /// <para>为什么在回调末尾自己续排、而非依赖周期定时器：一次性绝对调度不会因挂起 / 休眠 / 时钟跳变而
    /// 「整分钟漏掉」（进程恢复调度时定时器会补触发），但必须自己在触发后接上下一日。</para>
    /// </summary>
    private void OnBroadcastDue()
    {
        try
        {
            var profile = BaziProfile.Load(Capability.Storage);
            var now = DateTime.Now;
            if (profile.DailyBroadcastEnabled && profile.HasBirth)
            {
                var todayKey = now.ToString("yyyy-MM-dd");
                // 当天已播过则跳过（同时防「重排 + 进程重启」造成的重复播报）
                if (Capability.Storage.Get<string>(LastBroadcastKey) != todayKey)
                {
                    Capability.Storage.Set(LastBroadcastKey, todayKey);
                    Broadcast(profile, now);
                }
            }
        }
        catch (Exception ex)
        {
            // 铁律 7：播报失败静默降级（仅日志留痕），绝不影响宿主
            try { Capability.System.Log(PluginLogLevel.Warn, $"每日播报失败：{ex.GetType().Name}: {ex.Message}"); }
            catch { /* 日志通道合约上不抛；双保险 */ }
        }
        finally
        {
            // 无论本次是否真的播报，都要续上下一次（内部会按「已卸载 / 已关闭播报」自行判断是否排期）
            RescheduleBroadcast();
        }
    }

    /// <summary>
    /// 计算 <paramref name="now"/> 之后<b>严格更晚</b>的下一次 HH:mm 绝对时刻。
    /// <para>当天时刻已过（或恰为此刻）→ 顺延到<b>次日</b>同刻；用 <see cref="DateTime.AddDays"/> 进位，
    /// 跨月 / 跨年自动处理。中国不实行夏令时，无需处理 DST 跳变。</para>
    /// <para><b>为何不用「轮询 + 字符串比 HH:mm」</b>：那种模型只要目标那一分钟恰好进程挂起 / 休眠恢复 /
    /// 时钟跳变 / 主循环卡顿，就会整天漏播。改成本函数算出的绝对时刻 + <see cref="IScheduler.At"/> 一次性调度后，
    /// 定时器在进程恢复调度时会「补触发」，不再丢事件。</para>
    /// </summary>
    internal static DateTime NextBroadcastTime(DateTime now, int hour, int minute)
    {
        var todaySlot = new DateTime(now.Year, now.Month, now.Day, hour, minute, 0);
        return todaySlot > now ? todaySlot : todaySlot.AddDays(1);
    }

    /// <summary>解析 "HH:mm"；非法（含越界）返回 false，绝不抛异常。</summary>
    private static bool TryParseHhMm(string? text, out int hour, out int minute)
    {
        hour = 0;
        minute = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split(':', StringSplitOptions.TrimEntries);
        return parts.Length == 2
            && int.TryParse(parts[0], out hour)
            && int.TryParse(parts[1], out minute)
            && hour is >= 0 and <= 23
            && minute is >= 0 and <= 59;
    }

    // ---- OnLoad 自检开关（v0.1.84） ----

    /// <summary>
    /// 是否运行 OnLoad 自检（默认否）。
    /// <para><b>为什么用环境变量而不是 <c>#if DEBUG</c></b>：本项目的 <c>tests/LoaderTest</c> 在 Debug 与 Release 下
    /// 都会运行，且它依赖「OnLoad 触碰 lunar → 第三方依赖解析失败即抛」来验证插件 ALC 的依赖链，
    /// 并在加载后读取各 <c>LastSelfTest</c> 静态结果。纯 <c>#if DEBUG</c> 会在 Release 下把自检整段编译掉，
    /// 导致那些断言在 Release 下无法通过、也无法在运行期开启。环境变量在两种配置下行为一致，
    /// 测试只需在加载插件前设置 <c>NEKO_PLUGIN_SELFTEST=1</c> 即可保持原断言有效。</para>
    /// </summary>
    private static bool ShouldRunSelfTest()
    {
        var v = Environment.GetEnvironmentVariable(SelfTestEnvVar);
        return v is not null
            && (v == "1"
                || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(v, "on", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 运行 6 项本地自检（回归 / 教学用途，受 <see cref="ShouldRunSelfTest"/> 控制）。
    /// <para>刻意<b>不</b> try/catch <see cref="BaziEngine.VerifyKnownCases"/>：若 lunar.dll 在插件 ALC 下
    /// 无法解析，此处会直接抛出 —— 这正是加载器测试判定「第三方依赖可加载」的决定性信号。</para>
    /// </summary>
    private static void RunSelfChecks()
    {
        BaziEngine.VerifyKnownCases();
        DailyFortuneBuilder.SelfCheck();
        ApiKeyProtector.SelfCheck();
        MbtiQuiz.SelfCheck();
        FortunePrompt.SelfCheck();
        DeepSeekClient.SelfCheck();
    }

    /// <summary>手动触发（径向槽位「今日运势」点击）。</summary>
    private void BroadcastManual()
    {
        try
        {
            var profile = BaziProfile.Load(Capability.Storage);
            if (!profile.HasBirth)
            {
                Pet.Say("先在设置里填写出生日期，我才能推算运势~", 3000);
                return;
            }
            Broadcast(profile, DateTime.Now);
        }
        catch
        {
            Pet.Say("运势推算出了点小状况，稍后再试~", 3000);
        }
    }

    /// <summary>核心播报：短句走气泡，完整内容走系统通知（气泡装不下多段文本）。</summary>
    private void Broadcast(BaziProfile profile, DateTime now)
    {
        var chart = BaziEngine.ComputeProfile(profile);
        if (!chart.HasBirth) return;   // 防御：档案在两次读之间失效

        var today = BaziEngine.ComputeToday(now);
        var fortune = DailyFortuneBuilder.Build(profile, chart, today);

        Pet.Say(fortune.Short, 5000);
        Capability.System.Notify("Neko", fortune.Full);
    }

    // ---- P3：AI 深度咨询 ----

    /// <summary>
    /// 打开咨询窗口（径向槽位「问问猫」点击）。未填档案 / 未配 Key 时给可读提示、不弹窗。
    /// 纯 UI 打开，不做网络调用（网络在用户每次提问时异步发生）。
    /// </summary>
    private void OpenChat()
    {
        try
        {
            var profile = BaziProfile.Load(Capability.Storage);
            if (!profile.HasBirth)
            {
                Pet.Say("先在设置里填写出生日期，我才能结合命盘解读~", 3500);
                return;
            }
            if (DeepSeekClient.ValidateApiKey(profile.ApiKey) is not null)
            {
                Pet.Say("先在设置页填写 DeepSeek API Key，我才能开启 AI 咨询~", 4000);
                return;
            }

            var chart = BaziEngine.ComputeProfile(profile);
            var today = BaziEngine.ComputeToday(DateTime.Now);
            var history = ChatHistory.Load(Capability.Storage);

            var window = new ChatWindow(history, question => AskAsync(chart, today, question));
            window.Show();
        }
        catch
        {
            // 铁律 7：打开窗口失败也静默降级
            Pet.Say("打开咨询窗口出了点小状况，稍后再试~", 3000);
        }
    }

    /// <summary>
    /// 一次提问的异步处理：重读最新档案（Key 可能刚改）→ 组装 Prompt → 调 DeepSeek → 成功则落历史。
    /// 全程 async，不阻塞 UI；任何失败折成可读 <see cref="ChatResult"/>。
    /// </summary>
    private async Task<ChatResult> AskAsync(BaziChart chart, BaziToday today, string question)
    {
        try
        {
            var profile = BaziProfile.Load(Capability.Storage);
            if (DeepSeekClient.ValidateApiKey(profile.ApiKey) is string keyError)
                return ChatResult.Failure(keyError);

            var history = ChatHistory.Load(Capability.Storage);
            var messages = FortunePrompt.BuildMessages(profile, chart, today, history, question);
            var result = await DeepSeekClient.ChatAsync(profile.ApiKey, messages);

            if (result.Ok)
                ChatHistory.Append(Capability.Storage, question, result.Text);

            return result;
        }
        catch (Exception ex)
        {
            return ChatResult.Failure($"咨询暂时不可用：{ex.Message}");
        }
    }
}
