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
    public bool Loop { get; set; } = true;
    public bool IsComplete { get; private set; }

    /// <summary>当前状态的播放倍率（加速/减速）</summary>
    public double Playback { get; set; } = 1.0;

    /// <summary>切换状态并重置帧计时</summary>
    public void SetState(SpriteState? state) => SetState(state, restart: false);

    public void SetState(SpriteState? state, bool restart)
    {
        if (_state == state && !restart) return;
        _state = state;
        _frame = 0;
        _accumulator = 0;
        IsComplete = false;
    }

    /// <summary>推进一帧。delta 秒。返回是否需要重绘。</summary>
    public bool Tick(TimeSpan delta)
    {
        if (_state is null || _state.Frames.Length == 0 || IsComplete) return false;

        var fps = _state.Fps * Playback;
        if (!double.IsFinite(fps) || fps <= 0 || delta <= TimeSpan.Zero) return false;

        _accumulator += delta.TotalSeconds * fps;
        // Reduce in floating point before converting: resume after sleep must not overflow int
        // or leave a backlog that keeps spinning through complete cycles on every subsequent tick.
        if (!double.IsFinite(_accumulator)) { _accumulator = 0; return false; }
        var wholeFrames = Math.Floor(_accumulator);
        if (wholeFrames < 1) return false;
        _accumulator -= wholeFrames;
        if (!Loop)
        {
            var previous = _frame;
            var remaining = _state.Frames.Length - 1 - _frame;
            if (wholeFrames > remaining) IsComplete = true;
            _frame += (int)Math.Min(wholeFrames, remaining);
            return _frame != previous || IsComplete;
        }
        var step = (int)(wholeFrames % _state.Frames.Length);
        if (step == 0) return false;
        _frame = (_frame + step) % _state.Frames.Length;
        return true;
    }
}
