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
            Pet();
            SavePosition(); // v0.1.26 位置记忆
            return;
        }

        // 落体物理：初速 = 释放时速度，重力加速度，触底反弹
        StartFall(_dragVelocity);
        _eventBus.Emit(DesktopPet.Core.PetEvents.PetLanded, null);
    }

/// <summary>键鼠反应（v0.1.40 BongoCat 键盘映射借鉴 + v0.1.47 打字互动）：打字/点击时猫短暂"瞪眼"，
    /// 同时身体呼吸晃动 + 眼球下移看键盘。KeyboardReactionsEnabled 控制总开关（关=全部不响应）。v0.1.57：立即唤醒主循环（InputResponse）。</summary>
    private void OnInputReaction()
    {
        if (!_config.KeyboardReactionsEnabled) return;
        try
        {
            _renderer.FlashEyes(300);
            _renderer.SetTypingActive(TimeSpan.FromSeconds(1));   // v0.1.47：保持打字活跃 1s
            RequestActiveLoop();                                  // v0.1.57：闲置 → 活动立即唤醒一次主循环
        }
        catch { /* 打字/点击反应只是视觉点缀：失败绝不能污染输入响应链（fire-and-forget，最后防线）*/ }
    }

    /// <summary>抚摸：播放抚摸动画 + 冒爱心粒子 + 广播 pet:petted 事件（供好感度等插件消费）</summary>
    private void Pet()
    {
        _stateMachine.Play(DesktopPet.Core.PetState.Pet);
        _eventBus.Emit(DesktopPet.Core.PetEvents.PetPetted, null);
        ShowHearts();   // v0.1.59(⑧)：抚摸时猫头顶上方冒 3~5 颗爱心
        // 抚摸动画结束后回到待机
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PetAnimationMs) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            _stateMachine.Play(DesktopPet.Core.PetState.Idle);
        };
        t.Start();
    }

    /// <summary>v0.1.59(⑧)：冒爱心粒子。先定位浮层到猫头顶，再在窗口内生成粒子簇。</summary>
    private void ShowHearts()
    {
        if (_hearts is null)
        {
            _hearts = new HeartParticleWindow();
            _hearts.Closed += (_, _) => _hearts = null;
        }
        SyncHeartPosition();   // 先定位（Show 前后都要，保证粒子落点正确）
        _hearts.Burst(new Point(Left + Width / 2, Top + Height * 0.30));
        SyncHeartPosition();
    }

    private void OnMouseRightDown(object sender, MouseButtonEventArgs e)
    {
        // 右键：打开径向菜单（含插件槽位）
        OpenRadialMenu();
    }
}
