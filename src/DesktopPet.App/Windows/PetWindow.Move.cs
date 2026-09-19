// v0.1.84：从 PetWindow.cs 按功能区域拆出（审计 P2-11）——平滑位移 + 位置记忆 + 缩放
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
    /// <summary>
    /// v0.1.39：平滑位移到目标点（替代直接赋值 Left/Top，根治"猫瞬移"）。
    /// 凡是程序主动发起的猫位移都走这里；用户拖拽不走（拖拽必须跟手）。
    /// 坑：WPF 动画结束后必须立即更新本地值——HoldEnd 状态下后续对 Left/Top 的赋值全被动画覆盖（表现为猫粘住再也拖不动）。
    /// v0.1.57：按动作分类缓动——
    ///   Walk = 走路/插件 MoveTo（带轻微 overshoot 的 BackEase EaseOut，幅度根据距离动态收敛到 ≤ ~6px）
    ///   Snap = 边缘吸附（同样 BackEase，幅度上限 4px 让猫"撞墙"微回弹即可）
    ///   落体 = 物理自由落体（重力 + 反弹，自带 bounce 感，不走此处）
    ///   拖拽 = 直接赋值 Left/Top（线性跟手，无缓动）
    ///   UI 高亮（80ms，InputMirrorWindow 爪子）保持 QuadraticEase 不变
    /// </summary>
    private enum MoveStyle { Walk, Snap }

    // ⚠️ 注意：本区域配套的缓动常量 BackEaseMaxAmplitude / BackEaseScanSamples 因「拆分不移动字段」约束，
    //    仍声明在 PetWindow.cs 主文件（const 亦属字段）——见主文件对应注释。

    // v0.1.57(fix)：BackEase.EaseOut 峰值过冲与 Amplitude 的关系不是注释里旧公式 4a³/(27(a+1)²)
    // （那是经典 easeOutBack 模型）。WPF 实际实现为 EaseOut(t)=1-EaseIn(1-t)、EaseInCore=t³−A·t·sin(πt)，
    // 实测 A=1 时过冲比例 ≈0.379（比旧公式大约 10 倍）。因此不再用解析式反解，
    // 而是用 BackEase 对象本身扫 Ease(t) 实测峰值过冲比例，再二分求出“过冲px ≤ capPx”的最大 Amplitude。

    /// <summary>实测 BackEase.EaseOut 的峰值过冲比例 = max(Ease(t))-1，t∈(0,1)。EaseOut 终点必为 1，故过冲=曲线超出 1 的峰值。</summary>
    private static double MeasureBackOvershootRatio(BackEase ease, int samples = BackEaseScanSamples)
    {
        double peak = 0.0;
        for (int i = 1; i < samples; i++)
        {
            double v = ease.Ease(i / (double)samples) - 1.0;
            if (v > peak) peak = v;
        }
        return peak;
    }

    /// <summary>二分反解：过冲比例 r(A) 随 A 单调递增，求 r(A)·distancePx ≤ capPx 的最大 A∈[0,1]。
    /// 粗扫二分后换更细网格复核峰值，若仍超限则降档 5%（防御峰值落在粗扫采样点之间的边界情况）。</summary>
    private static double FindBackEaseAmplitude(double distancePx, double capPx)
    {
        var maxRatio = (capPx * 0.95) / distancePx;
        var probe = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = BackEaseMaxAmplitude };
        if (MeasureBackOvershootRatio(probe) <= maxRatio) return BackEaseMaxAmplitude; // 距离足够短：满幅度也不超 cap

        double lo = 0.0, hi = BackEaseMaxAmplitude;
        for (int i = 0; i < 24; i++)
        {
            var mid = (lo + hi) / 2.0;
            probe.Amplitude = mid;
            if (MeasureBackOvershootRatio(probe) <= maxRatio) lo = mid; else hi = mid;
        }

        probe.Amplitude = lo;
        for (int k = 0; k < 4 && MeasureBackOvershootRatio(probe, BackEaseScanSamples * 4) > maxRatio; k++)
            lo *= 0.95;
        return lo;
    }

    /// <summary>v0.1.57：按位移距离生成带“轻微 overshoot”的 BackEase(EaseOut)，并把真实峰值过冲约束在 capPx 内
    /// （Walk cap=6px / Snap cap=4px，均低于规格“过冲≤6-10px”）。距离过短时退化为 QuadraticEase.EaseOut。</summary>
    private static IEasingFunction MakeMoveEase(double distancePx, double capPx)
    {
        if (distancePx < 1) return new QuadraticEase { EasingMode = EasingMode.EaseOut }; // 已到位时退化 EaseOut（避免 amp 无意义）
        var amp = FindBackEaseAmplitude(distancePx, capPx);
        return new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = amp };
    }

    private void AnimateMoveTo(double left, double top, int ms = 280, MoveStyle style = MoveStyle.Walk)
    {
        var work = GetWorkArea();
        left = Math.Max(work.X, Math.Min(left, work.X + work.Width - Width));
        top = Math.Max(work.Y, Math.Min(top, work.Y + work.Height - Height));
        if (Math.Abs(left - Left) < 0.5 && Math.Abs(top - Top) < 0.5) return;  // 已到位，省一次动画

        CancelMoveAnim();   // 打断上一次未完的位移，把当前视觉位置固化成新起点
        _moveAnimating = true;
        var dx = left - Left;
        var dy = top - Top;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        var capPx = style == MoveStyle.Snap ? 4.0 : 6.0;   // Snap 撞墙回弹克制，Walk 走路稍微大点
        var ease = MakeMoveEase(dist, capPx);
        var dur = TimeSpan.FromMilliseconds(ms);
        var ax = new DoubleAnimation(Left, left, dur) { EasingFunction = ease };
        var ay = new DoubleAnimation(Top, top, dur) { EasingFunction = ease };
        ax.Completed += (_, _) =>
        {
            BeginAnimation(Window.LeftProperty, null);
            BeginAnimation(Window.TopProperty, null);
            Left = left;
            Top = top;
            _moveAnimating = false;
            SavePosition();
        };
        BeginAnimation(Window.LeftProperty, ax);
        BeginAnimation(Window.TopProperty, ay);
    }

    /// <summary>打断位移动画：先读当前动画值固化成本地值再撤动画，否则会"啪"地跳回起点</summary>
    private void CancelMoveAnim()
    {
        if (!_moveAnimating) return;
        var cur = new Point(Left, Top);   // 动画进行中读属性拿到的就是当前动画值
        BeginAnimation(Window.LeftProperty, null);
        BeginAnimation(Window.TopProperty, null);
        Left = cur.X;
        Top = cur.Y;
        _moveAnimating = false;
    }

    /// <summary>位置落盘（BongoCat 位置记忆）：拖拽松手 / 落体静止 / 缩放后调用，启动时恢复</summary>
    private void SavePosition()
    {
        _config.PosX = Left;
        _config.PosY = Top;
        ConfigManager.Save(Paths.ConfigFile, _config);
    }

    /// <summary>回到默认位置（右下角）：托盘调用；清除记忆，避免下次启动又回到旧位置</summary>
    public void ResetPosition()
    {
        _config.PosX = null;
        _config.PosY = null;
        Left = SystemParameters.WorkArea.Right - Width - 20;
        Top = SystemParameters.WorkArea.Bottom - Height - 40;
        ConfigManager.Save(Paths.ConfigFile, _config);
    }

    /// <summary>缩放（BongoCat 借鉴）：调整宠物显示尺寸，中心保持不动，持久化到配置。</summary>
    public void SetScale(double scale)
    {
        scale = Math.Clamp(scale, 1.0, 8.0);
        var cx = Left + Width / 2;
        var cy = Top + Height / 2;
        _config.Scale = scale;
        Width = SpriteAtlas.FrameW * scale;
        Height = SpriteAtlas.FrameH * scale;
        _renderer.Scale = scale;
        Left = cx - Width / 2;
        Top = cy - Height / 2;
        SyncFollowers();   // v0.1.39：尺寸变了，浮层重新对齐
        SavePosition(); // v0.1.26 位置记忆：缩放后中心不动，位置随尺寸变化也落盘
        ConfigManager.Save(Paths.ConfigFile, _config);
    }
}
