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
    private static readonly TimeSpan WalkDuration = TimeSpan.FromSeconds(2); // 单次走动时长

    public PetStateMachine(IPetApi api)
    {
        _api = api;
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

        // 待机状态下随机走动
        if (Current == PetState.Idle && _rng.NextDouble() < 0.02)
        {
            Transition(PetState.Walk);
        }

        // v0.1.1 fix：补上 Walk → Idle 回路。原先只有 Idle→Walk，宠物一走就要原地走到
        // SitAfter(30s) 才切 Sit，看起来像原地踏步很久。现在走一小段就回待机。
        if (Current == PetState.Walk)
        {
            _walkTimer += delta;
            if (_walkTimer >= WalkDuration) Transition(PetState.Idle);
        }
        else
        {
            _walkTimer = TimeSpan.Zero;
        }
    }

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
