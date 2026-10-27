using DesktopPet.Core;

namespace DesktopPet.Builtin.Pomodoro;

/// <summary>单调时钟计时；暂停不累计，完成后等待用户开始下一阶段。</summary>
public sealed class FocusClock(Func<long>? now = null)
{
    private readonly Func<long> _now = now ?? (() => Environment.TickCount64);
    private long _last, _remaining;
    private FocusSnapshot _state = new(FocusStage.Idle, false, 0, 0, 0, "");
    public FocusSnapshot Snapshot => _state with { RemainingSeconds = (int)((_remaining + 999) / 1000) };
    public bool Running => !_state.Paused && _state.Stage is FocusStage.Focusing or FocusStage.Resting;

    public bool StartFocus(int minutes, string title = "")
    {
        if (_state.Stage is FocusStage.Focusing or FocusStage.Resting) return false;
        title = title.Replace('\r', ' ').Replace('\n', ' ').Trim();
        Start(FocusStage.Focusing, Math.Clamp(minutes, 1, 120), title[..Math.Min(title.Length, 200)]);
        return true;
    }

    public void StartRest(int minutes)
    {
        if (_state.Stage == FocusStage.RestReady) Start(FocusStage.Resting, Math.Clamp(minutes, 1, 30), _state.TaskTitle);
    }

    private void Start(FocusStage stage, int minutes, string title)
    {
        _remaining = minutes * 60000L;
        _last = _now();
        _state = new(stage, false, minutes * 60, minutes * 60, _state.CompletedSessions, title);
    }

    public void Advance()
    {
        if (!Running) return;
        var nowMs = _now();
        _remaining = Math.Max(0, _remaining - Math.Max(0, nowMs - _last));
        _last = nowMs;
        if (_remaining != 0) return;
        var focus = _state.Stage == FocusStage.Focusing;
        _state = _state with { Stage = focus ? FocusStage.RestReady : FocusStage.FocusReady,
            CompletedSessions = _state.CompletedSessions + (focus ? 1 : 0) };
    }

    public void Pause()
    {
        Advance();
        if (Running) _state = _state with { Paused = true };
    }

    public void Resume()
    {
        if (!_state.Paused) return;
        _last = _now();
        _state = _state with { Paused = false };
    }

    public void Stop()
    {
        _remaining = 0;
        _state = new(FocusStage.Idle, false, 0, 0, _state.CompletedSessions, "");
    }
}
