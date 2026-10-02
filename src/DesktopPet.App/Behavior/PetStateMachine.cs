using DesktopPet.Core;

namespace DesktopPet.App.Behavior;

/// <summary>
/// 宠物状态机：驱动空闲行为（待机→走动→坐下→睡觉）和物理（拖拽→落下→落地）。
/// 纯逻辑，不依赖 WPF。通过 IPetApi 的实现（由宿主注入）施加动作。
/// </summary>
public sealed class PetStateMachine
{
    private readonly IPetApi _api;
    private readonly Random _rng = new();

    public PetState Current { get; private set; } = PetState.Idle;
    public PetMood Mood { get; private set; } = PetMood.Idle;

    // 空闲行为阈值
    private TimeSpan _idleTimer;
    private TimeSpan _walkTimer; // v0.1.1：Walk 状态已持续时间，用于走一小段就回待机
    private static readonly TimeSpan SitAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SleepAfter = TimeSpan.FromMinutes(5);

    // ================= v0.1.91：走路（真位移 + 降频）=================
    // 背景：此前 Walk 只是「播 2 秒腿动画 + 零位移」= 原地踏步，观感是"在放动画，很蠢"；
    // 且 Idle 下 2%/tick×10tick/s = 20%/s ≈ 每 5 秒走一次，约 40% 时间在踏步。
    // 现在：①进入 Walk 时随机左右、匀速真位移（位移由宿主 PetWindow 按 WalkVelocityX 施加）；
    //       ②走动频率降到平均 40~60 秒一次（用倒计时，不用每 tick 概率）。

    /// <summary>
    /// 走路速度（px/s）。
    /// <para>推导（与 walk 动画腿速匹配，减少"滑步"）：walk 素材 = 8 帧 @10fps → 0.8 s/循环；
    /// 设「1 个动画循环 = 2 步」，步长取 ≈20px（≈0.16 体长，猫窗 ~128px）→ 速度 = 2×20 / 0.8 = <b>50 px/s</b>
    /// ≈ 0.39 体长/秒（慢悠悠地走，落在 40~70 px/s 的建议量级中值）。
    /// 要更快/更慢只改这一个常量；设 0 = 只播腿不移动（会退回"原地踏步"，不推荐）。</para>
    /// </summary>
    public const double WalkSpeed = 50.0;

    // 单次走动时长（每次随机 → 距离随机）：50px/s × [1.6,3.0]s = [80,150]px
    private static readonly TimeSpan WalkMinDuration = TimeSpan.FromSeconds(1.6);
    private static readonly TimeSpan WalkMaxDuration = TimeSpan.FromSeconds(3.0);
    // 两次走动之间的间隔（平均 40~60s 一次）
    private static readonly TimeSpan WalkGapMin = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan WalkGapMax = TimeSpan.FromSeconds(60);

    private double _walkDirection;                                  // -1=左 / +1=右（仅 Walk 有效）
    private TimeSpan _walkDuration = WalkMinDuration;               // 本次走动时长（进入 Walk 时随机）
    private TimeSpan _nextWalkIn = TimeSpan.FromSeconds(45);        // 距下次走动的倒计时（ctor 里再随机化）

    /// <summary>当前行走的水平速度（px/s，含方向符号）；非 Walk 状态恒为 0。宿主按此 ×dt 平移窗口。</summary>
    public double WalkVelocityX => Current == PetState.Walk ? _walkDirection * WalkSpeed : 0.0;

    public PetStateMachine(IPetApi api)
    {
        _api = api;
        _nextWalkIn = RandomWalkGap();
    }

    public void Tick(TimeSpan delta)
    {
        // 空闲计时推进（拖拽/抚摸等交互会重置）
        if (Current is PetState.Idle or PetState.Sit or PetState.Walk)
        {
            _idleTimer += delta;
            if (_idleTimer >= SleepAfter) Transition(PetState.Sleep);
            else if (_idleTimer >= SitAfter && Current is not (PetState.Sit or PetState.Sleep)) Transition(PetState.Sit);
        }

        // v0.1.91：随机走动——用「倒计时」而非「每 tick 概率」触发。
        // 为什么不用概率：主循环频率是 50/100ms 两档（IsBusyActiveWindow 决定），
        // 若写 `_rng < p` 会随 tick 率/负载漂移，且把概率按错的 tick 率算就完全跑偏。
        // 倒计时与 tick 率无关：只在「Idle 或 Sit」（即猫闲着的两种站姿）时递减，
        // 排到点就开始走，平均间隔 = (WalkGapMin+WalkGapMax)/2 = 50s → 目标 40~60s。
        // （参考：若坚持用概率，100ms/tick × 10 tick/s，要 50s 平均需 p ≈ 1/(50×10) = 0.002。）
        if (Current is PetState.Idle or PetState.Sit)
        {
            _nextWalkIn -= delta;
            if (_nextWalkIn <= TimeSpan.Zero) BeginWalk();
        }

        // v0.1.1：Walk → Idle 回路（走一小段就回待机，不再原地走到 SitAfter）
        if (Current == PetState.Walk)
        {
            _walkTimer += delta;
            if (_walkTimer >= _walkDuration) Transition(PetState.Idle);
        }
        else
        {
            _walkTimer = TimeSpan.Zero;
        }
    }

    /// <summary>开始一次走动：随机左右方向 + 随机时长（→随机距离），排下次走动的间隔。</summary>
    private void BeginWalk()
    {
        // ⚠️ 关键：走动属于「活动」，必须把空闲计时清零。
        // 否则必踩这个坑——间隙(40~60s) > SitAfter(30s)，所以走动几乎总是从 **Sit** 状态下起走，
        // 此刻 _idleTimer 已 ≥30s；若不清零，下一个 Tick 的
        //   `else if (_idleTimer >= SitAfter && Current is not (Sit or Sleep))` 命中（当前是 Walk）
        // → 立刻 Transition(Sit)，走动只活一个 tick（≈50ms）就被打断，位移几乎为 0。
        // （这是修复「空地踏步」时暴露出来的旧逻辑下被零位移掩盖的隐患。）
        _idleTimer = TimeSpan.Zero;
        _walkDirection = _rng.Next(2) == 0 ? -1.0 : 1.0;
        _walkDuration = TimeSpan.FromSeconds(
            WalkMinDuration.TotalSeconds + _rng.NextDouble() * (WalkMaxDuration - WalkMinDuration).TotalSeconds);
        _walkTimer = TimeSpan.Zero;
        _nextWalkIn = RandomWalkGap();   // 先排下一次，避免刚走完就再触发
        Transition(PetState.Walk);
    }

    /// <summary>宿主调用：走动撞到工作区边缘时提前结束（回 Idle，绝不越界）。</summary>
    public void EndWalk()
    {
        if (Current != PetState.Walk) return;
        _walkTimer = TimeSpan.Zero;
        Transition(PetState.Idle);
    }

    /// <summary>
    /// 宿主调用：**起走阶段就被墙挡住**（猫正贴着墙、又随机到了朝墙的方向，本次位移还不足半个身位）时掉头反向，
    /// 而不是白白结束这次走动。
    /// <para>背景：猫常在走了几步后停在距墙 margin 处（见 PetWindow.UpdateWalk），下一次随机方向有 50%
    /// 概率朝墙 → 很快越界。若此处直接 EndWalk：①这次走动全废，得再等 40~60s，间隔会漂到 100s+；
    /// ②腿动画只闪一帧就切回 Idle，又变成"抽一下"。掉头一次最自然（猫撞墙会转身走开）。</para>
    /// <para>仅对「起走没多久就撞墙（位移 &lt; 半个身位）」生效（由宿主按累计位移判定）；
    /// 走过半个身位才碰到墙的仍按 EndWalk 回 Idle；同一次走动最多掉头一次（防窄空间来回抖）。</para>
    /// </summary>
    public void ReverseWalk()
    {
        if (Current != PetState.Walk) return;
        _walkDirection = -_walkDirection;
    }

    private TimeSpan RandomWalkGap() => TimeSpan.FromSeconds(
        WalkGapMin.TotalSeconds + _rng.NextDouble() * (WalkGapMax - WalkGapMin).TotalSeconds);

    /// <summary>交互事件重置空闲计时</summary>
    public void NoteInteraction()
    {
        _idleTimer = TimeSpan.Zero;
        if (Current is PetState.Sit or PetState.Sleep or PetState.Walk)
            Transition(PetState.Idle);
    }

    public void Play(PetState state, bool interrupt = true)
    {
        if (!interrupt && Current == state) return;
        _idleTimer = TimeSpan.Zero;
        Transition(state);
    }

    private void Transition(PetState next)
    {
        // 注意：不能因 Current == next 就早返回。
        // 初始 Current 就是 Idle，若此处跳过，渲染器永远收不到首次 Play，
        // _animator.State 恒为 null → 什么都不画（窗口一片黑）。
        Current = next;
        _ = _api.PlayAnimationAsync(next, new AnimationOptions { Loop = true });
    }
}
