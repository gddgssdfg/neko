using DesktopPet.Core;

namespace DesktopPet.App.Windows;

public sealed partial class PetWindow
{
    private FocusSnapshot? _focus;
    private Action? _removeFocusSubscription;
    private bool _focusPosePending;
    private bool FocusManaged => _focus is { Stage: not FocusStage.Idle };
    private PetState CompanionRestPose => FocusManaged
        ? _focus is { Stage: FocusStage.Focusing, Paused: false } ? PetState.Work : PetState.Sit
        : _config.AppMode == "work" ? PetState.Work : PetState.Idle;

    private void InitializeFocusCompanion()
    {
        _removeFocusSubscription = _eventBus.On(CompanionEvents.FocusChanged, payload =>
        {
            if (payload is not FocusSnapshot state || _isDisposed) return;
            void Apply()
            {
                if (_isDisposed) return;
                if ((FocusManaged || state.Stage != FocusStage.Idle)
                    && (_focus?.Stage != state.Stage || _focus?.Paused != state.Paused)) _focusPosePending = true;
                _focus = state;
                SyncFocusPose();
            }
            if (Dispatcher.CheckAccess()) Apply();
            else Dispatcher.BeginInvoke(new Action(Apply));
        });
    }

    private void SyncFocusPose()
    {
        if ((!FocusManaged && !_focusPosePending) || MotionSuspended || IsPaused || _edgeAttached
            || _dragging || _moveAnimating || _fallTimer is not null || _renderer.IsLanding) return;
        // 用户抚摸、拖动与行走优先，等自然收势后再回到陪伴姿态。
        if (_stateMachine.Current is not (PetState.Idle or PetState.Sit or PetState.Work or PetState.Sleep)) return;
        if (_stateMachine.Current == PetState.Sleep && !_focusPosePending) return;
        var desired = CompanionRestPose;
        if (_stateMachine.Current != desired) _stateMachine.Play(desired);
        _focusPosePending = false;
    }
}
