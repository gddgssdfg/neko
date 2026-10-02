// v0.1.84：从 PetWindow.cs 按功能区域拆出（审计 P2-11）——键鼠映射窗 + 双模态（work/slack）
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
    // ---- v0.1.28：键鼠映射窗开关（托盘/设置页调用）----

    /// <summary>显示映射窗（懒创建）。v0.1.39：创建后立刻摆到猫下方，避免开局在旧位置闪一帧再跳过去。</summary>
    private void ShowMirror()
    {
        if (_mirrorWindow is null)
        {
            _mirrorWindow = new InputMirrorWindow(_globalInput, _config, SaveConfigFile, OnMirrorRelocated);
            AlignMirrorBeforeShow();   // v0.1.91：Show 前先摆好（含同步位置记忆），首帧即在正确位置 → 无"旧位置闪一下"
            _mirrorWindow.Show();
        }
        else if (!_mirrorWindow.IsVisible)
        {
            AlignMirrorBeforeShow();   // v0.1.91：重新显示前同样先对齐
            _mirrorWindow.Show();
        }
        SyncMirrorToCat();   // v0.1.39：映射窗来就猫，不是猫去就映射窗（Show 后兜底同步一次，保持等价）
    }

    /// <summary>映射窗总开关：v0.1.30 联动 AppMode（开=work 模式，关=slack）</summary>
    public void SetKeyboardMirror(bool enabled) => SetAppMode(enabled ? "work" : "slack");

    /// <summary>
    /// v0.1.39 双模态：切换要真正改变猫的行为（此前只控制映射窗显隐，猫纹丝不动——灯灯点名的 bug）。
    ///   work  = 猫留在原地，键鼠映射窗贴到猫正下方（键盘以猫视角为主，猫趴键盘后）
    ///   slack = 收起映射窗，猫平滑滑到最近的屏幕边缘挂住（不挡视线）
    /// </summary>
    public void SetAppMode(string mode)
    {
        if (mode != "work" && mode != "slack") mode = "slack";
        _config.AppMode = mode;
        _config.KeyboardMirrorEnabled = mode == "work";
        ConfigManager.Save(Paths.ConfigFile, _config);

        if (mode == "work")
        {
            if (_edgeAttached) DetachEdge();   // 挂着没法趴键盘，先让它下来
            ShowMirror();                       // 内部会 SyncMirrorToCat()
            // v0.1.48：BongoCat 化——切到 Work 姿态（猫伏低前探+前爪伸向下方键盘），不是 Sit
            _stateMachine.Play(DesktopPet.Core.PetState.Work);
        }
        else
        {
            _mirrorWindow?.Hide();
            _stateMachine.Play(DesktopPet.Core.PetState.Idle);  // v0.1.43：摸鱼模式站姿
            if (_config.SlackEdgeAttach) AttachToNearestEdge();
        }
    }

    /// <summary>
    /// v0.1.39：映射窗位置由猫派生——水平居中于猫、贴在猫正下方（键盘以猫视角为主）。
    /// 这是"呼出键盘时猫会瞬移"的根治：以前是猫去追映射窗的位置记忆，现在反过来映射窗来就猫。
    /// </summary>
    private void SyncMirrorToCat()
    {
        if (_config.AppMode != "work") return;
        if (_mirrorWindow is null || !_mirrorWindow.IsVisible) return;

        if (ComputeMirrorTarget() is not { } target) return;
        var (mx, my) = target;
        MoveWindowIfNeeded(_mirrorWindow, mx, my);   // v0.1.82：同值短路，别再每 tick 空挪一次窗口
        // 同步位置记忆（只改内存不落盘）：否则下次切回 work 时映射窗会先在旧位置闪一帧再跳过来
        _config.MirrorPosX = mx;
        _config.MirrorPosY = my;
    }

    /// <summary>
    /// v0.1.91：算出映射窗「应该待」的位置（猫下方居中；放不下改挂猫上方；最后 clamp 进工作区）。
    /// 复用给 <see cref="SyncMirrorToCat"/>（可见时同步）与 <see cref="AlignMirrorBeforeShow"/>（Show 前预设）。
    /// 返回 <c>null</c> 表示当前不该定位（非 work 模式，或映射窗尚未创建）。
    /// </summary>
    private (double X, double Y)? ComputeMirrorTarget()
    {
        if (_config.AppMode != "work") return null;
        if (_mirrorWindow is null) return null;

        var work = GetWorkArea();
        double mx = Left + Width / 2 - _mirrorWindow.Width / 2;
        double my = Top + Height + MirrorGap;
        // 猫太靠下放不下键盘时，改挂到猫上方（不出屏优先于位置好看）
        if (my + _mirrorWindow.Height > work.Y + work.Height) my = Top - _mirrorWindow.Height - MirrorGap;
        mx = Math.Max(work.X, Math.Min(mx, work.X + work.Width - _mirrorWindow.Width));
        my = Math.Max(work.Y, Math.Min(my, work.Y + work.Height - _mirrorWindow.Height));
        return (mx, my);
    }

    /// <summary>
    /// v0.1.91：在 <c>Show()</c> 之前把映射窗摆到目标位（work 模式）并**同步进位置记忆**——
    /// <see cref="InputMirrorWindow.OnSourceInitialized"/> 会读 <c>_config.MirrorPosX/Y</c> 定位，
    /// 先把记忆写成"猫下方"就能让窗口首帧即出现在正确位置，杜绝"先按上次残留位置闪一帧再跳过去"。
    /// 非 work 模式（如 slack 但 KeyboardMirrorEnabled=true）不干预，沿用记忆/默认位置（与旧行为一致）。
    /// </summary>
    private void AlignMirrorBeforeShow()
    {
        if (ComputeMirrorTarget() is not { } target) return;
        _config.MirrorPosX = target.X;
        _config.MirrorPosY = target.Y;
    }

    /// <summary>v0.1.39：键盘朝向镜像（设置页调用，即时生效 + 持久化）。false=朝我，true=朝猫</summary>
    public void SetKeyboardMirrorFlipped(bool flipped)
    {
        _config.KeyboardMirrorFlipped = flipped;
        _mirrorWindow?.SetMirrored(flipped);   // 映射窗未创建时忽略，创建时会自己读配置
        ConfigManager.Save(Paths.ConfigFile, _config);
    }

    /// <summary>
    /// v0.1.39：用户主动拖了键鼠映射窗 → 猫平滑滑到键盘上方（不再是瞬移）。
    /// 平时是映射窗跟着猫，只有用户主动挪键盘时才反向带猫。
    /// </summary>
    private void OnMirrorRelocated()
    {
        if (_config.AppMode != "work") return;
        if (_mirrorWindow is null || !_mirrorWindow.IsVisible) return;
        if (_dragging) return;   // 用户正在拖猫，别抢方向盘
        AnimateMoveTo(
            _mirrorWindow.Left + (_mirrorWindow.Width - Width) / 2,
            _mirrorWindow.Top - Height - MirrorGap, 220, MoveStyle.Walk);   // v0.1.57：走路缓动（轻微 overshoot）
    }

    /// <summary>老板键藏猫时联动藏映射窗；唤回时若功能开着则一并恢复</summary>
    private void UpdateMirrorVisibility()
    {
        if (_mirrorWindow is null) return;
        if (IsVisible && _config.KeyboardMirrorEnabled) _mirrorWindow.Show();
        else _mirrorWindow.Hide();
    }

    private void SaveConfigFile()
    {
        // v0.1.82 P1-8：ConfigManager.Save 内部已 try/catch（见 Core/Config.cs），永不抛 → 外层 try/catch 纯冗余，去掉
        ConfigManager.Save(Paths.ConfigFile, _config);
    }
}
