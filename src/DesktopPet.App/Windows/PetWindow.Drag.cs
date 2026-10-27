// v0.1.84：从 PetWindow.cs 按功能区域拆出（审计 P2-11）——拖拽 / 鼠标事件 / 抚摸 / 输入反应
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
    private double _petRemainingMs;
    private long _petRevision;

    private void UpdatePetReaction(TimeSpan delta)
    {
        if (_petRemainingMs <= 0) return;
        if (_stateMachine.Revision != _petRevision || _stateMachine.Current != PetState.Pet) { _petRemainingMs = 0; return; }
        _petRemainingMs -= delta.TotalMilliseconds;
        if (_petRemainingMs <= 0) _stateMachine.Play(_config.AppMode == "work" ? PetState.Work : PetState.Idle);
    }
    /// <summary>v0.2.4：摸到下半身（爪/尾）时的"瞪一眼"时长。</summary>
    private const int LowerPetEyeFlashMs = 350;

    /// <summary>
    /// v0.2.4：最近一次抚摸命中区域。**保留字段本身有价值**：
    /// ① UiProbe 可反射读到，断言"点不同高度 → 不同区域"；② 便于现场排查"为什么这次没冒爱心"。
    /// </summary>
    private PetRegion _lastPetRegion = PetRegion.Body;

    /// <summary>v0.1.40：键鼠输入 → 活跃度统计（闲聊调度数据源）</summary>
    private void OnTrackerKey(uint vk, bool down)
    {
        if (down) _activity?.NotifyInput();
    }

    private void OnTrackerMouse(int btn, bool down)
    {
        if (down) _activity?.NotifyInput();
    }

    private void OnMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(this);
        if (pos.X < 0 || pos.Y < 0) return;

        CancelMoveAnim();   // v0.1.39：动画途中抓猫——先停动画并固化当前位置，否则 HoldEnd 状态会吃掉拖拽赋值（表现为拖不动）
        _dragging = true;
        _dragOffset = pos;
        _dragStartPos = pos; // 记录起点，抬起时用它算总位移判定点击 vs 拖拽
        _dragVelocity = new Vector(0, 0);
        // v0.1.1 fix：抓起时必须停掉落体物理，否则 _fallTimer 还在改 Top，与鼠标位置打架 → 拖拽不跟手/抖动
        if (_fallTimer is not null) { _fallTimer.Stop(); _fallTimer = null; }
        _stateMachine.Play(DesktopPet.Core.PetState.Drag);
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

// v0.1.1 fix：坐标系统一。PointToScreen 返回物理像素，而 Left/Top 是 WPF 逻辑单位(DIP)。
            // 125% 缩放下两者差 1.25 倍，直接相减会让偏差逐帧累积 → 拖拽「不跟手」，宠物越拖越偏甚至跑出屏幕。
            var physical = PointToScreen(e.GetPosition(this));
            var dip = PhysicalToDip(physical);
            var nextLeft = dip.X - _dragOffset.X;
            var nextTop = dip.Y - _dragOffset.Y;

        // v0.1.1 fix：钳制在工作区内，防止宠物被拖到屏幕外「消失」
        var work = GetWorkArea();
        nextLeft = Math.Max(work.X, Math.Min(nextLeft, work.X + work.Width - Width));
        nextTop = Math.Max(work.Y, Math.Min(nextTop, work.Y + work.Height - Height));

        _dragVelocity = new Vector(nextLeft - Left, nextTop - Top);
        Left = nextLeft;
        Top = nextTop;
        _eventBus.Emit(DesktopPet.Core.PetEvents.PetDragged, new PetDraggedArgs(nextLeft, nextTop));
        SyncFollowers();   // v0.1.39：拖猫时轮盘和键盘实时跟手（主循环 20Hz 不够跟手，这里补一次）
    }

    /// <summary>物理像素 → WPF 逻辑单位(DIP)。拖拽定位必须用同一坐标系，否则高 DPI 下会偏移。</summary>
    private Point PhysicalToDip(Point physical)
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is not null)
            return source.CompositionTarget.TransformFromDevice.Transform(physical);
        return physical; // 拿不到时退化（100% 缩放下两者相同）
    }

    private void OnMouseLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();

        // 抚摸判定：用「按下→抬起总位移」而非末帧速度。
        // v0.1.1 fix：原先用 _dragVelocity（仅最后一帧位移），拖完停住再松手会因速度≈0 被误判成抚摸；
        // 而真正点击时手抖超过阈值又会被误判成拖拽触发落体 → 宠物掉落「消失」。
        var totalMove = (e.GetPosition(this) - _dragStartPos).Length;
        if (totalMove < PetClickThreshold)
        {
            Pet(e.GetPosition(this));   // v0.2.4：把点击位置带进去 → 分区反应
            SavePosition(); // v0.1.26 位置记忆
            return;
        }

        // 落体物理：初速 = 释放时速度，重力加速度，触底反弹
        StartFall(_dragVelocity);
    }

/// <summary>键鼠反应（v0.1.40 BongoCat 键盘映射借鉴 + v0.1.47 打字互动）：打字/点击时猫短暂"瞪眼"，
    /// 同时身体呼吸晃动 + 眼球下移看键盘。KeyboardReactionsEnabled 控制总开关（关=全部不响应）。v0.1.57：立即唤醒主循环（InputResponse）。</summary>
    private void OnInputReaction()
    {
        if (MotionSuspended || IsPaused) return;
        if (!_config.KeyboardReactionsEnabled) return;
        try
        {
            if (_stateMachine.Current is PetState.Sleep or PetState.Sit)
                _stateMachine.Play(_config.AppMode == "work" ? PetState.Work : PetState.Idle);
            else _stateMachine.NoteInteraction();
            _renderer.FlashEyes(300);
            _renderer.SetTypingActive(TimeSpan.FromSeconds(1));   // v0.1.47：保持打字活跃 1s
            RequestActiveLoop();                                  // v0.1.57：闲置 → 活动立即唤醒一次主循环
        }
        catch { /* 打字/点击反应只是视觉点缀：失败绝不能污染输入响应链（fire-and-forget，最后防线）*/ }
    }

    /// <summary>
    /// 抚摸（v0.2.4 起**分区**）：播放抚摸动画 + 广播 <c>pet:petted</c>，并按点击位置给不同反应。
    ///
    /// <list type="bullet">
    ///   <item><b>头</b> → 爱心 3~5 颗（最受用的位置）</item>
    ///   <item><b>躯干</b> → 爱心 1~3 颗（次之）</item>
    ///   <item><b>爪 / 尾（下半身）</b> → **不冒爱心**，改为"瞪你一眼"（<see cref="PetRenderer.FlashEyes"/> 350ms）</item>
    /// </list>
    ///
    /// <b>刻意不做惩罚</b>：摸到猫不情愿的位置时**不扣好感度**（好感度由插件按 <c>pet:petted</c> 计分），
    /// 只让"反应"不同 —— 桌宠是陪伴物，不该因为用户点错位置而"罚"他。
    /// 区域判定见 <see cref="PetRegionMath.Classify"/>（纯函数，跨皮肤按归一化高度分层）。
    /// </summary>
    private void Pet(Point clickPos)
    {
        var region = PetRegionMath.Classify(clickPos.Y / Math.Max(Height, 1.0));
        _lastPetRegion = region;

        _stateMachine.Play(DesktopPet.Core.PetState.Pet);
        _eventBus.Emit(DesktopPet.Core.PetEvents.PetPetted, null);

        switch (region)
        {
            case PetRegion.Head:
                ShowHearts(3, 5);
                break;
            case PetRegion.Body:
                ShowHearts(1, 3);
                break;
            default:
                // 爪 / 尾：不冒爱心，瞪一下表示"别摸这儿"（视觉反馈，不扣好感度）
                try { _renderer.FlashEyes(LowerPetEyeFlashMs); }
                catch { /* 视觉点缀失败绝不污染抚摸链路 */ }
                break;
        }

        // 抚摸动画结束后回到待机
        _petRemainingMs = PetAnimationMs;
        _petRevision = _stateMachine.Revision;
    }

    /// <summary>v0.1.59(⑧) / v0.2.4：冒爱心粒子（<paramref name="min"/>~<paramref name="max"/> 颗）。</summary>
    private void ShowHearts(int min, int max)
    {
        if (_hearts is null)
        {
            _hearts = new HeartParticleWindow();
            _hearts.Closed += (_, _) => _hearts = null;
        }
        // v0.2.4 修复（A4 实机取证抓出）：顺序必须是「先 Show → 再定位 → 再 Burst」。
        // Burst 用浮层窗口的 Left/Top 把锚点换算成画布坐标；而窗口**首次 Show 之前** Left/Top 还没确定
        // （WPF 默认 NaN，Show 后被系统摆到任意位置），算出的 localX/localY 会被 `Math.Clamp` 夹到窗口
        // 边角 —— 多颗爱心因此**叠成一颗**、并挂在浮层右下角（肉眼就是"只冒了半颗爱心在猫右边"）。
        // 先 Show 让窗口存在，SyncHeartPosition 才能把 Left/Top 写到"中心对齐锚点"，Burst 紧接着读到正确原点。
        if (!_hearts.IsVisible) _hearts.Show();
        SyncHeartPosition();
        _hearts.Burst(new Point(Left + Width / 2, Top + Height * 0.30), min, max);
        SyncHeartPosition();
    }

    private void OnMouseRightDown(object sender, MouseButtonEventArgs e)
    {
        // 右键：打开径向菜单（含插件槽位）
        OpenRadialMenu();
    }
}
