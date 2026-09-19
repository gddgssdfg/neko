// v0.1.84：从 PetWindow.cs 按功能区域拆出（审计 P2-11）——主循环 + 眼神追踪 + 心情
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DesktopPet.App.Behavior;
using DesktopPet.App.Core;
using DesktopPet.App.Renderer;
using DesktopPet.App.Services;
using DesktopPet.Core;

namespace DesktopPet.App.Windows;

public sealed partial class PetWindow : Window
{
    private void OnLoopTick(object? sender, EventArgs e)
    {
        // v0.1.58(任务5)：真实 elapsed（单调 ms 差）。delta 是本次 tick 到上次 tick 的真实墙钟间隔，
        // 下游所有计时器按 delta 累进/累减——tick 频率抖动不再放大或缩短动画时长。
        var nowMs = Environment.TickCount64;
        var deltaMs = Math.Max(0L, nowMs - _lastTickMs);
        _lastTickMs = nowMs;
        var delta = TimeSpan.FromMilliseconds(deltaMs);

        _stateMachine.Tick(delta);

        // ---- v0.1.59(⑨)：心情驱动动画（好感度等级 → 帧率倍率，60s 缓存免每 tick 读盘）----
        UpdateMoodFromAffection();

        if (!IsPaused) _renderer.Tick(delta);

        // ---- 眼神追踪：全局鼠标方向 → 眼球偏移（宿主渲染层反馈） ----
        UpdateEyeTracking();

        // ---- 边缘吸附：贴边悬挂检测 ----
        UpdateEdgeSnap();

        // ---- 广播鼠标事件（低频节流，供插件订阅） ----
        EmitMouseMoved(delta);

        // ---- v0.1.39：浮层跟随猫（径向菜单 + 键鼠映射窗都以猫为锚点）----
        SyncFollowers();

        // v0.1.57：根据"活动窗口"动态调整主循环频率（纯闲置 50→100ms，瞬时活动立即唤醒 100→50ms）
        UpdateLoopFrequency();
    }

    /// <summary>v0.1.57：活动窗口检测——任一持续性交互或瞬时动画进行中均视为"活动"，保持 50ms tick 灵敏。</summary>
    private bool IsBusyActiveWindow() =>
        _dragging ||
        _moveAnimating ||
        _fallTimer is not null ||
        _renderer.HasTransientVisual ||
        _stateMachine.Current is PetState.Walk or PetState.Pet or PetState.Drag or PetState.Fall;

    /// <summary>v0.1.57：根据 IsBusyActiveWindow 调整主循环频率。仅在区间实际变化时改写 Interval（避免反复重启定时器）。</summary>
    private void UpdateLoopFrequency()
    {
        var desired = IsBusyActiveWindow() ? LoopIntervalMs : IdleLoopIntervalMs;
        var current = (int)_loopTimer.Interval.TotalMilliseconds;
        if (desired != current)
            _loopTimer.Interval = TimeSpan.FromMilliseconds(desired);
    }

    /// <summary>v0.1.59(⑨)：心情驱动动画——按好感度等级（60s 缓存）映射到 PetRenderer 心情，
    /// 影响帧率倍率（越亲密越精神）。等级越高 → Happy（1.3×），低等级 → Idle（1.0×）。
    /// 暂停时不覆盖（SetPaused 已设 Sleepy，暂停优先）。</summary>
    private void UpdateMoodFromAffection()
    {
        if (IsPaused) return;   // 暂停态心情由 SetPaused 掌管（Sleepy），不在此覆盖

        var nowMs = Environment.TickCount64;
        if (nowMs < _nextAffectionReadMs) return;   // 60s 缓存：未到点不读盘
        _nextAffectionReadMs = nowMs + AffectionRefreshMs;

        var level = AffectionReader.ReadLevel(
            System.IO.Path.Combine(Paths.Data, "plugins", "com.lantern.affection", "score.json"));
        if (level == _affectionLevel) return;   // 等级未变 → 不重设心情（避免无谓刷新）
        _affectionLevel = level;

        // 映射：好感度越高越精神。L2 亲密 / L3 挚友 → Happy（帧率 1.3×）；其余 → Idle（1.0×）
        _renderer.SetMood(level >= 2 ? DesktopPet.Core.PetMood.Happy : DesktopPet.Core.PetMood.Idle);
    }

    /// <summary>v0.1.57：输入钩子触发时立即唤醒一次主循环（间隔回 50ms + 当帧 OnLoopTick）。
    /// 兼顾"input → immediate response 不延迟"约束。</summary>
    private void RequestActiveLoop()
    {
        if ((int)_loopTimer.Interval.TotalMilliseconds != LoopIntervalMs)
            _loopTimer.Interval = TimeSpan.FromMilliseconds(LoopIntervalMs);
        try { OnLoopTick(this, EventArgs.Empty); }
        catch { /* 输入回调里吞异常，保持 UI 线程响应 */
        }
    }

    /// <summary>眼神追踪：计算宠物头部 → 鼠标的方向向量，驱动眼球偏移</summary>
    private void UpdateEyeTracking()
    {
        var cursor = GlobalInput.GetCursorPosition();
        // v0.1.59(P3)：鼠标位置未变且无瞬时视觉（眨眼/打字晃动等）时，眼球偏移必然不变 → 跳过计算。
        // 眼神追踪本身依赖鼠标移动，鼠标不动时结果必然相同，行为等价。
        if (_lastCursorPos == cursor && !_renderer.HasTransientVisual) return;
        _lastCursorPos = cursor;

        // 宠物头部参考点：窗口中心（眼球层锚点在头部 17,11 附近，取窗口中心近似）
        var head = new Point(Left + Width / 2, Top + Height / 2);

        var dx = cursor.X - head.X;
        var dy = cursor.Y - head.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1) return;

        // ---- v0.1.59(⑩) 鼠标路过张望：鼠标进入猫附近半径内 → 眼球偏移增益放大（好奇）----
        UpdatePeek(dx, dy, len);

        // 归一化方向（限幅 ±1），渲染器内部再 ×2px（张望时再 ×增益）
        var norm = new Vector(dx / len, dy / len);
        _renderer.SetEyeOffset(norm, _peekActive ? PeekEyeGain : 1.0);
    }

    /// <summary>v0.1.59(⑩)：鼠标进入/离开猫附近半径时做一次边界响应（好奇张望）。
    /// 只在「进入 / 离开」的边界切换，避免每 tick 触发状态切换。</summary>
    private void UpdatePeek(double dx, double dy, double len)
    {
        bool near = len <= PeekRadius;
        if (near == _peekActive) return;   // 边界未变化 → 不动（节流）
        _peekActive = near;
    }

    /// <summary>低频节流广播鼠标全局位置（供插件订阅 mouse:moved）</summary>
    private void EmitMouseMoved(TimeSpan delta)
    {
        _mouseEmitAccum += delta.TotalMilliseconds;
        if (_mouseEmitAccum >= MouseEmitIntervalMs)
        {
            _mouseEmitAccum = 0;
            var p = GlobalInput.GetCursorPosition();
            // v0.1.59(P3)：位置未变则不 Emit——闲置态鼠标不动时省掉插件的 mouse:moved 回调。行为等价。
            if (_lastEmitCursorPos == p) return;
            _lastEmitCursorPos = p;
            _eventBus.Emit(DesktopPet.Core.PetEvents.MouseMoved, new MouseMovedArgs(p.X, p.Y));
        }
    }
}
