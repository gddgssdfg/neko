// v0.1.84：从 PetWindow.cs 按功能区域拆出（审计 P2-11）——边缘吸附 + 落体物理
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
    /// <summary>v0.1.39：摸鱼模式——把猫平滑挪到最近的屏幕边缘并挂住（不瞬移）</summary>
    private void AttachToNearestEdge()
    {
        var work = GetWorkArea();
        var distLeft = Left - work.X;
        var distRight = work.X + work.Width - (Left + Width);
        var distTop = Top - work.Y;
        var distBottom = work.Y + work.Height - (Top + Height);
        double min = Math.Min(Math.Min(distLeft, distRight), Math.Min(distTop, distBottom));

        if (distLeft <= min + 0.5) AttachEdge(0, work.X, Top);
        else if (distRight <= min + 0.5) AttachEdge(1, work.X + work.Width - Width, Top);
        else if (distTop <= min + 0.5) AttachEdge(2, Left, work.Y);
        else AttachEdge(3, Left, work.Y + work.Height - Height);
    }

    /// <summary>
    /// 边缘吸附：宠物靠近屏幕边缘时贴边悬挂（Climb 状态），鼠标远离时脱离回落。
    /// 关键节点：吸附=距边缘 &lt; SnapThreshold；脱离=鼠标距吸附点 &gt; DetachDistance。
    /// </summary>
    private void UpdateEdgeSnap()
    {
        // v0.1.39：拖拽中、或平滑位移动画进行中都不做吸附判定——
        // 动画期间 Left/Top 是插值中间值，拿它算"距边缘多近"会在半路误触发/误脱离。
        // v0.1.59(⑦)：落体/抛物线飞行中（_fallTimer != null）也必须跳过——否则吸附逻辑会抢方向盘，
        // 在飞行半路把猫吸到边缘，破坏抛物线轨迹。
        // v0.1.91：走路中（Walk）同样跳过——走路是主动位移，撞边由 UpdateWalk 处理成"停住+回 Idle"，
        // 不能让吸附判定的 Climb 抢走状态（否则一步三摇地往墙上爬）。停稳回 Idle 后吸附才重新生效。
        if (_dragging || _moveAnimating || _fallTimer is not null || _stateMachine.Current is PetState.Walk)
        {
            if (_dragging && _edgeAttached) DetachEdge();
            return;
        }

        var work = GetWorkArea();
        var w = Width;
        var h = Height;

        // 宠物四周到工作区边缘的距离（左/右/上/下）
        var distLeft = Left - work.X;
        var distRight = work.X + work.Width - (Left + w);
        var distTop = Top - work.Y;
        var distBottom = work.Y + work.Height - (Top + h);

        // 最近边缘距离
        double min = Math.Min(Math.Min(distLeft, distRight), Math.Min(distTop, distBottom));

        if (!_edgeAttached)
        {
            // 未吸附：足够近则吸附到最近边缘
            if (min <= SnapThreshold)
            {
                if (distLeft <= min) AttachEdge(0, work.X, Top);
                else if (distRight <= min) AttachEdge(1, work.X + work.Width - w, Top);
                else if (distTop <= min) AttachEdge(2, Left, work.Y);
                else AttachEdge(3, Left, work.Y + work.Height - h);
            }
        }
        else
        {
            // v0.1.39：摸鱼模式下猫就该老实挂边上——鼠标走远也不自动脱离（只有拖拽才脱）。
            // 否则"切摸鱼 → 猫贴边 → 手一移开就掉下来"，等于没吸附。
            if (_config.AppMode == "slack" && _config.SlackEdgeAttach) return;

            // 已吸附：鼠标远离吸附点超阈值则脱离（松手回落）
            var cx = _edgeLeft + w / 2;
            var cy = _edgeTop + h / 2;
            var m = GlobalInput.GetCursorPosition();
            var distance = Math.Sqrt((m.X - cx) * (m.X - cx) + (m.Y - cy) * (m.Y - cy));
            if (distance > DetachDistance) DetachEdge();
        }
    }

    private void AttachEdge(int side, double left, double top)
    {
        _edgeAttached = true;
        _edgeSide = side;
        _edgeLeft = left;
        _edgeTop = top;
        AnimateMoveTo(left, top, 260, MoveStyle.Snap);   // v0.1.39：吸附也走平滑位移，不再"啪"地瞬移到边上；v0.1.57：吸附缓动（撞墙微回弹）
        _stateMachine.Play(DesktopPet.Core.PetState.Climb);
        _eventBus.Emit(DesktopPet.Core.PetEvents.PetEdge, new PetEdgeArgs(side, true));
    }

    private void DetachEdge()
    {
        _edgeAttached = false;
        CancelMoveAnim();   // v0.1.39：可能正滑向边缘，先固化当前位置再回落，避免动画和落体抢 Top
        _stateMachine.Play(DesktopPet.Core.PetState.Idle);
        _eventBus.Emit(DesktopPet.Core.PetEvents.PetEdge, new PetEdgeArgs(_edgeSide, false));
        // 脱离边缘后让宠物落到地面
        StartFall(new Vector(0, 0));
    }

    /// <summary>
    /// 落体动画：从当前速度抛出，水平+竖直抛物线飞行到工作区底部，带惯性 + 触底反弹 + 左右碰边反射。
    /// 关键节点：vy 向下为正；触底 vy ×Bounce 反弹且 vx 受地面摩擦衰减；左右越界 vx 反向 ×Bounce；
    /// vy 低于 RestVelocity 且 vx 归零视为静止。
    /// </summary>
    private void StartFall(Vector initialVelocity)
    {
        // 取消上一次未结束的弹跳（同一时刻只应有一个落体物理模拟在改 Left/Top）
        if (_fallTimer is not null) { _fallTimer.Stop(); _fallTimer = null; }

        var work = GetWorkArea();
        var targetBottom = work.Y + work.Height - SpriteAtlas.FrameH * _config.Scale;

        // v0.1.1 fix：单位统一 + 方向修正 + 限速。
        //   ① _dragVelocity 是「每帧位移」，而 Gravity 是「像素/秒²」，必须 ×60 转成像素/秒，否则初速量级差 60 倍；
        //   ② 原 Math.Min(v.Y, 0) 只保留负值(向上)，与注释「只保留向下」相反；
        //   ③ 加上下限，避免异常大的速度把宠物甩出屏幕（用户反馈的「点击后消失」）。
        var vy = initialVelocity.Y * 60.0;
        vy = Math.Max(-MaxThrowSpeed, Math.Min(vy, MaxThrowSpeed));

        // v0.1.59(⑦)：水平分量。同口径 ×60，同样限速，避免甩出屏幕（左右碰边会反射）。
        var vx = initialVelocity.X * 60.0;
        vx = Math.Max(-MaxThrowSpeed, Math.Min(vx, MaxThrowSpeed));

        // 左右反射边界：以猫中心判定更自然（猫中心到达边缘即反向）
        var minCenterX = work.X + Width / 2;
        var maxCenterX = work.X + work.Width - Width / 2;

        _fallTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        // v0.1.58(任务5)：落体物理同样用真实 dt（单调 ms 差）做重力/位移缩放——16ms tick 抖动或
        // UI 卡顿不会改变落地轨迹；位移 = vy*dt、速度 += Gravity*dt，dt 本身就是真实时间。
        var lastMs = Environment.TickCount64;
        _fallTimer.Tick += (_, _) =>
        {
            var nowMs2 = Environment.TickCount64;
            var dt = Math.Max(0L, nowMs2 - lastMs) / 1000.0;
            lastMs = nowMs2;

            // ---- 水平飞行 + 左右碰边反射 ----
            if (vx != 0)
            {
                var nextLeft = Left + vx * dt;
                var centerX = nextLeft + Width / 2;
                if (centerX <= minCenterX)
                {
                    nextLeft = minCenterX - Width / 2;
                    vx = Math.Abs(vx) * Bounce;   // 撞左墙 → 向右弹
                }
                else if (centerX >= maxCenterX)
                {
                    nextLeft = maxCenterX - Width / 2;
                    vx = -Math.Abs(vx) * Bounce;  // 撞右墙 → 向左弹
                }
                Left = nextLeft;
            }

            // ---- 竖直重力 + 触底反弹 ----
            vy += Gravity * dt;
            var next = Top + vy * dt;
            if (next >= targetBottom)
            {
                Top = targetBottom;
                SyncFollowers();   // v0.1.39：落体/反弹时浮层同步跟随
                vy = -vy * Bounce;
                // v0.1.59(⑦)：触地摩擦——vx 衰减，低速时归零，避免落地后无限横滑
                vx *= GroundFriction;
                if (Math.Abs(vx) < RestVelocity * 0.5) vx = 0;
                if (Math.Abs(vy) < RestVelocity && vx == 0)
                {
                    _fallTimer?.Stop();
                    _fallTimer = null;
                    _stateMachine.Play(DesktopPet.Core.PetState.Idle);
                    SavePosition(); // v0.1.26 位置记忆：落定后的最终位置
                }
            }
            else
            {
                Top = next;
                SyncFollowers();   // v0.1.39：下落途中浮层同步跟随
            }
        };
        _fallTimer.Start();
    }
}
