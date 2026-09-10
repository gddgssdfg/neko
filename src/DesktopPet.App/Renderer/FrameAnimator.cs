using DesktopPet.Core;

namespace DesktopPet.App.Renderer;

/// <summary>
/// 帧动画计时器。按状态帧率推进帧索引。
/// 空闲时用低频计时，播放动作时恢复高频。
/// </summary>
public sealed class FrameAnimator
{
    private SpriteState? _state;
    private int _frame;
    private double _accumulator;

    public SpriteState? State => _state;
    public int FrameIndex => _frame;

    /// <summary>当前状态的播放倍率（加速/减速）</summary>
    public double Playback { get; set; } = 1.0;

    /// <summary>切换状态并重置帧计时</summary>
    public void SetState(SpriteState? state)
    {
        if (_state == state) return;
        _state = state;
        _frame = 0;
        _accumulator = 0;
    }

    /// <summary>推进一帧。delta 秒。返回是否需要重绘。</summary>
    public bool Tick(TimeSpan delta)
    {
        if (_state is null || _state.Frames.Length == 0) return false;

        var fps = _state.Fps * Playback;
        if (fps <= 0) return false;

        _accumulator += delta.TotalSeconds * fps;
        // v0.1.1 fix：clamp 防溢出。delta 极大时 (int)_accumulator 会溢出成 Int32.MinValue，
        // 导致 _frame = (0 + Int32.MinValue) % 4 = 0 永远停住（PetWindow._lastTick 已修，此处加防御深度）
        var step = Math.Min((int)_accumulator, _state.Frames.Length);
        if (step <= 0) return false;

        _accumulator -= step;
        _frame = (_frame + step) % _state.Frames.Length;
        return true;
    }
}
