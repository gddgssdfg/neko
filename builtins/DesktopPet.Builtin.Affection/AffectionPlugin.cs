using DesktopPet.Core;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.Affection;

/// <summary>
/// 内置插件：好感度（dogfooding 示范）。
/// 消费 pet:petted 事件，用 Storage 持久化好感度分数，
/// 随等级提升触发不同的宠物反应（说话气泡 + 情绪 + 径向槽位展示）。
/// 完全走第三方插件的加载路径（ALC 隔离 + 权限校验 + 事件订阅）。
/// </summary>
public sealed class AffectionPlugin : PluginBase
{
    private const string ScoreKey = "score";
    private const int LevelFamiliar = 10;   // 熟悉
    private const int LevelIntimate = 50;   // 亲密
    private const int LevelBestFriend = 100;// 挚友

    private readonly Random _rng = new();
    private int _score;
    private Action? _unsubscribePetted;

    // v0.1.82：pet:petted 在 UI 线程高频触发（每次抚摸），原来每次都同步 Storage.Set 写盘 → 卡 UI。
    // 改为「内存即时更新 + 去抖落盘」：分数与等级判定一律用内存 _score（升级提示仍即时生效），
    // 磁盘写入最多每 FlushIntervalMs 一次（兜底定时器），并在 OnUnload 时强制落盘一次。
    // ⚠️ 已知代价：进程被强杀时，最多丢失最近 FlushIntervalMs 内的少数几次好感度 —— 换取 UI 不卡顿。
    private const int FlushIntervalMs = 5000;
    private readonly object _flushLock = new();
    private volatile bool _dirty;
    private DateTime _lastFlushUtc = DateTime.MinValue;
    private System.Threading.Timer? _flushTimer;

    public override string Id => "com.lantern.affection";
    public override string Name => "好感度";

    public override PluginPermissions RequiredPermissions =>
        PluginPermissions.PetAnimate | PluginPermissions.PetRead
        | PluginPermissions.UIRegister | PluginPermissions.Storage;

    public override void OnLoad(IPluginContext ctx)
    {
        base.OnLoad(ctx);

        // 读取持久化的好感度（跨会话保留）
        _score = Capability.Storage.Get<int>(ScoreKey);

        // 兜底定时器：即使不再抚摸，也保证内存里的好感度最迟每 5s 落盘一次（trailing flush）。
        _flushTimer = new System.Threading.Timer(_ => FlushIfDirty(), null, FlushIntervalMs, FlushIntervalMs);

        // 订阅抚摸事件：好感度增长入口
        _unsubscribePetted = Capability.Events.On(PetEvents.PetPetted, _ => OnPetted());

        // 注册径向槽位：点击可查询当前好感度（标签为初始等级，v1 不做动态刷新）
        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "affection-status",
            Label = LevelName(GetLevel(_score)),
            Icon = "heart",
            Order = 1,
            OnInvoke = () => Pet.Say($"好感度：{_score}（{LevelName(GetLevel(_score))}）"),
        });

        // 注册自定义设置页（v0.1.5 ISettingsPage dogfooding 示范）—— 嵌入宿主设置窗口的「插件」tab
        UI.RegisterSettingsPage(new AffectionSettingsPage(Capability.Storage, () => _score));

        System.Diagnostics.Debug.WriteLine($"[好感度] 加载完成，当前分数={_score}");
    }

    public override void OnUnload()
    {
        _unsubscribePetted?.Invoke();
        _unsubscribePetted = null;

        // 停止兜底定时器，并在卸载前强制落盘一次 —— 保证这几秒内累积的好感度不丢。
        _flushTimer?.Dispose();
        _flushTimer = null;
        try { FlushNow(); } catch { /* 卸载期落盘失败也不影响卸载（铁律 7） */ }
    }

    /// <summary>
    /// v0.1.82：立即把内存中的好感度落盘（显式触发一次 flush）。
    /// <para>语义 = 既有的 OnUnload 落盘逻辑抽出的公开入口；仍经 <see cref="_flushLock"/> 串行化，
    /// <b>不绕过锁</b>。用于：生命周期收尾（OnUnload），以及测试显式触发落盘。</para>
    /// <para><b>设计取舍</b>：<c>pet:petted</c> 在 UI 线程高频触发，故采用「内存即时更新 + 去抖落盘」
    /// （兜底最多每 <see cref="FlushIntervalMs"/> 一次 + OnUnload 强制一次）以避免每次抚摸同步写盘卡 UI；
    /// 代价是<b>进程被强杀时最多丢最近 ≤5s 的好感度</b>（已与用户取向一致地接受）。</para>
    /// </summary>
    public void FlushNow() => Flush();

    private void OnPetted()
    {
        var oldLevel = GetLevel(_score);
        _score++;
        _dirty = true;

        // 距上次落盘已 ≥5s 才立即写盘（响应更快）；否则留给 5s 兜底定时器，避免每次抚摸都写盘卡 UI。
        // 等级判定用内存里的 _score，与是否已落盘无关 → 升级提示仍然即时生效。
        if ((DateTime.UtcNow - _lastFlushUtc).TotalMilliseconds >= FlushIntervalMs) Flush();

        // 升到新等级：特殊庆祝；否则普通友好反应
        if (GetLevel(_score) > oldLevel) OnLevelUp(GetLevel(_score));
        else OnNormalPet();
    }

    /// <summary>
    /// 把内存中的好感度落盘。用 <see cref="_flushLock"/> 串行化：UI 线程（立即 flush）与兜底
    /// 定时器（线程池）可能并发，避免把 JSON 文件写坏。失败保留 dirty，等下次兜底 flush 重试。
    /// </summary>
    private void Flush()
    {
        lock (_flushLock)
        {
            try
            {
                Capability.Storage.Set(ScoreKey, _score);
                _lastFlushUtc = DateTime.UtcNow;
                _dirty = false;
            }
            catch
            {
                // 落盘失败：保留 _dirty，等待下一次兜底 flush 重试（绝不抛，铁律 7）。
            }
        }
    }

    /// <summary>兜底定时器回调（线程池）：仅在确有未落盘变更时才写盘。</summary>
    private void FlushIfDirty()
    {
        if (!_dirty) return;
        Flush();
    }

    private void OnNormalPet()
    {
        switch (_rng.Next(4))
        {
            case 0: Pet.Say("喵~", 1500); break;
            case 1: Pet.SetMood(PetMood.Happy); break;
            case 2: Pet.Say("咕噜咕噜~", 1500); break;
            default: Pet.Say("（蹭蹭）", 1200); break;
        }
    }

    private void OnLevelUp(int level)
    {
        var msg = level switch
        {
            1 => "好感度提升！我们熟起来了~",
            2 => "好感度提升！你是我最喜欢的人类！",
            3 => "好感度提升！我们是挚友了！",
            _ => "好感度满级！一辈子都要在一起！",
        };
        Pet.Say(msg, 2600);
        Pet.SetMood(PetMood.Happy);
    }

    private static int GetLevel(int score) => score switch
    {
        < LevelFamiliar => 0,    // 陌生
        < LevelIntimate => 1,    // 熟悉
        < LevelBestFriend => 2,  // 亲密
        _ => 3,                  // 挚友
    };

    private static string LevelName(int level) => level switch
    {
        0 => "陌生",
        1 => "熟悉",
        2 => "亲密",
        _ => "挚友",
    };
}
