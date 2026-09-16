using DesktopPet.Core;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.Bazi;

/// <summary>
/// 命理助手插件。P0：骨架 + 生日设置页 + lunar 依赖加载验证；P2：每日运势播报；P3：AI 深度咨询；P4：人格测试（MBTI 问卷 + 计分）。
/// </summary>
public sealed class BaziPlugin : PluginBase
{
    /// <summary>播报轮询间隔（v0.1.61 起无 Cron，只有 Every/At，用 Every + 时刻判断）。</summary>
    private const int BroadcastCheckIntervalMs = 60_000;

    /// <summary>防重复播报的日期键（Storage 存 yyyy-MM-dd）。</summary>
    private const string LastBroadcastKey = "lastBroadcastDate";

    private IScheduleHandle? _broadcastTimer;

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

        // 无条件跑一次 lunar 自检 ——
        //  ① 若 lunar.dll 未被插件 ALC 正确解析，这里会抛异常（加载器测试据此判定第三方依赖链）；
        //  ② 顺带完成 lunar 已知用例 + 运势构建器 + Prompt/客户端 的回归校验。
        BaziEngine.VerifyKnownCases();
        DailyFortuneBuilder.SelfCheck();
        ApiKeyProtector.SelfCheck();
        MbtiQuiz.SelfCheck();
        FortunePrompt.SelfCheck();
        DeepSeekClient.SelfCheck();

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

        UI.RegisterSettingsPage(new BaziSettingsPage(Capability.Storage));

        // 每日定时播报：60s 轮询 + 时刻匹配（无 Cron）
        _broadcastTimer = Capability.Scheduler.Every(BroadcastCheckIntervalMs, CheckBroadcast);
    }

    public override void OnUnload()
    {
        // 铁律 2：必须 Dispose 定时器句柄，否则热重载会重复注册
        _broadcastTimer?.Dispose();
        _broadcastTimer = null;
        // v0.1.82：释放共享 HttpClient —— 否则它的连接池定时器会从进程级根钉住本插件类型，
        // 导致整个 ALC 无法回收（实测每次热重载泄漏 ~8.5MB）。
        DeepSeekClient.DisposeShared();
    }

    /// <summary>定时回调（线程池线程）：到点则播报。失败静默降级，绝不拖垮宿主（铁律 7）。</summary>
    private void CheckBroadcast()
    {
        try
        {
            // 每次重新读最新档案，避免设置页改动后缓存陈旧
            var profile = BaziProfile.Load(Capability.Storage);
            if (!profile.DailyBroadcastEnabled || !profile.HasBirth) return;   // 未开启 / 档案不全 → 静默跳过

            var now = DateTime.Now;
            if (now.ToString("HH:mm") != profile.BroadcastTime) return;        // 未到点

            var todayKey = now.ToString("yyyy-MM-dd");
            if (Capability.Storage.Get<string>(LastBroadcastKey) == todayKey) return;  // 当天已播报过

            Capability.Storage.Set(LastBroadcastKey, todayKey);
            Broadcast(profile, now);
        }
        catch
        {
            // 铁律 7：任何失败都静默，不影响宿主
        }
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
