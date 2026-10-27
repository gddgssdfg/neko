using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DesktopPet.App.Core;

namespace DesktopPet.App.Windows;

public sealed partial class PetWindow
{
    private HwndSource? _residencySource;
    private DispatcherTimer? _displayRecovery;
    private bool _sessionLocked, _systemSuspended, _onBattery, _sessionUnavailable;
    private long _lastNoticeMs = long.MinValue;
    public bool NotificationsSuppressed => _config.QuietMode || _sessionLocked || _systemSuspended || _gameFullscreenActive || !IsVisible;
    private bool MotionSuspended => _sessionLocked || _systemSuspended || !IsVisible;

    public void SetQuietMode(bool enabled)
    {
        _config.QuietMode = enabled;
        if (enabled) _bubble?.Dismiss();
        SaveConfigFile();
    }

    // A shared gate for host speech and tray notifications; suppressed notices are discarded.
    public bool TryBeginNotification()
    {
        if (NotificationsSuppressed) return false;
        var now = Environment.TickCount64;
        if (_lastNoticeMs != long.MinValue && now - _lastNoticeMs < 10000) return false;
        _lastNoticeMs = now;
        return true;
    }

    private void InitializeResidency()
    {
        _residencySource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _residencySource?.AddHook(ResidencyMessage);
        if (_residencySource is not null) WTSRegisterSessionNotification(_residencySource.Handle, 0);
        _displayRecovery = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _displayRecovery.Tick += (_, _) => { _displayRecovery.Stop(); RecoverDesktop(); };
    }

    private IntPtr ResidencyMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_isDisposed) return IntPtr.Zero;
        if (msg is 0x007E or 0x001A or 0x02E0) QueueDesktopRecovery(); // display, work area, DPI
        if (msg == 0x02B1 && wParam.ToInt32() is 7 or 8) // WTS lock / unlock
        {
            _sessionLocked = wParam.ToInt32() == 7;
            ApplySessionState();
        }
        if (msg == 0x0218) // power broadcast
        {
            if (wParam.ToInt32() == 4) { _systemSuspended = true; ApplySessionState(); }
            else if (wParam.ToInt32() is 7 or 18) { _systemSuspended = false; ApplySessionState(); }
        }
        return IntPtr.Zero;
    }

    private void QueueDesktopRecovery()
    {
        _cachedWorkArea = null;
        _displayRecovery?.Stop();
        _displayRecovery?.Start();
    }

    private void ApplySessionState()
    {
        var unavailable = _sessionLocked || _systemSuspended;
        if (unavailable != _sessionUnavailable)
        {
            _sessionUnavailable = unavailable;
            _eventBus.Emit(unavailable ? DesktopPet.Core.PetEvents.AppSleep : DesktopPet.Core.PetEvents.AppWake);
        }
        _lastTickMs = Environment.TickCount64;
        if (_sessionLocked || _systemSuspended)
        {
            StopDesktopMotion();
            CloseRadialMenu();
            _mirrorWindow?.Hide();
            _bubble?.Dismiss();
            _hearts?.Dismiss();
        }
        else { QueueDesktopRecovery(); UpdateMirrorVisibility(); }
    }

    private void StopDesktopMotion()
    {
        var left = Left; var top = Top;
        BeginAnimation(LeftProperty, null); BeginAnimation(TopProperty, null);
        Left = left; Top = top;
        _moveAnimating = false;
        var falling = _fallTimer is not null;
        _fallTimer?.Stop(); _fallTimer = null;
        _dragging = false; ReleaseMouseCapture();
        _stateMachine.EndWalk();
        if (falling) _stateMachine.Play(_config.AppMode == "work" ? DesktopPet.Core.PetState.Work : DesktopPet.Core.PetState.Idle);
    }

    public static Point ClampDesktopPosition(Point position, Size size, Rect work)
    {
        var x = double.IsFinite(position.X) ? position.X : work.X;
        var y = double.IsFinite(position.Y) ? position.Y : work.Y;
        return new Point(Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - size.Width)),
            Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - size.Height)));
    }

    public void RecoverDesktop()
    {
        if (_isDisposed) return;
        StopDesktopMotion();
        _cachedWorkArea = null;
        var work = GetWorkArea();
        var p = ClampDesktopPosition(new Point(Left, Top), new Size(Width, Height), new Rect(work.X, work.Y, work.Width, work.Height));
        if (Math.Abs(p.X - Left) > .01 || Math.Abs(p.Y - Top) > .01) _edgeAttached = false;
        Left = p.X; Top = p.Y;
        _lastTickMs = Environment.TickCount64;
        SyncFollowers();
        SavePosition();
    }

    public void FindCat()
    {
        StopDesktopMotion();
        _edgeAttached = false;
        ResetPosition();
        if (!_sessionLocked && !_systemSuspended && !_hiddenByGame) Show();
        RecoverDesktop();
        UpdateMirrorVisibility();
    }

    private void ResidencyPulse()
    {
        if (_config.GameModeEnabled && !_sessionLocked && !_systemSuspended) CheckGameMode();
        _onBattery = GetSystemPowerStatus(out var power) && power.ACLineStatus == 0;
        UpdateLoopFrequency();
    }

    private void DisposeResidency()
    {
        _displayRecovery?.Stop();
        if (_residencySource is not null)
        {
            WTSUnRegisterSessionNotification(_residencySource.Handle);
            _residencySource.RemoveHook(ResidencyMessage);
            _residencySource = null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public uint BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("wtsapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WTSRegisterSessionNotification(IntPtr hwnd, uint flags);
    [DllImport("wtsapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);
}
