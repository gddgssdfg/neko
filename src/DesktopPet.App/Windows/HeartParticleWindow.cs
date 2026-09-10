using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DesktopPet.App.Windows;

/// <summary>
/// v0.1.59(⑧) 抚摸爱心粒子浮层（独立无边框 topmost 窗口）。
///
/// 为什么独立窗口：猫主窗口的绘制区被 DrawingVisualHost.MeasureOverride 钉死在 32×Scale
/// （像素精灵尺寸），粒子飘出猫身会被裁切；放大主窗口又会破坏像素对齐。故按 BubbleWindow /
/// RadialMenu 的既有范式开独立透明浮层，粒子在本窗口内绘制。
///
/// 行为：抚摸猫时在猫头部上方冒出 3~5 颗爱心，向上飘 + 淡出（约 800ms），播完自动隐藏（不常驻）。
/// 爱心程序化绘制（Path Geometry 贝塞尔），不引入新 PNG；颜色粉红/浅红。
/// 窗口随猫移动由宿主 SyncHeartPosition 每帧同步；不抢焦点、不拦鼠标。
/// </summary>
public sealed class HeartParticleWindow : Window
{
    private const double ParticleLifeMs = 800;       // 单颗爱心寿命
    private const double RiseDistance = 46;          // 向上飘的像素（DIP）
    private const double WindowW = 120;              // 浮层尺寸（容下猫头顶上方的爱心）
    private const double WindowH = 80;
    private const int TickMs = 16;                   // ~60Hz 平滑

    private static readonly Color HeartFill = Color.FromRgb(0xFF, 0x6B, 0x9A);    // 粉红
    private static readonly Color HeartOutline = Color.FromRgb(0xD6, 0x3B, 0x6E); // 浅红描边

    private sealed class Particle
    {
        public required Path Shape { get; init; }
        public double StartX { get; init; }
        public double Vy { get; init; }        // 向上速度（px/s，负=上）
        public double Vx { get; init; }        // 横向漂移（px/s）
        public double BornMs { get; init; }    // 生成时刻（本窗口单调 ms 基准）
    }

    private readonly Canvas _canvas = new();
    private readonly Random _rng = new();
    private readonly List<Particle> _particles = new();
    private DispatcherTimer? _timer;
    // 窗口级单调毫秒钟：自窗口创建起持续累加（每次 OnTick 累加真实 elapsed），跨 Burst 不归零。
    // Dismiss() 也不归零——这是有意的：BornMs 与 _clockMs 同源，跨 Burst 仍自洽。
    // 后人若要归零 _clockMs，必须同时清空 _particles，否则 age 会算出负值。
    private double _clockMs;
    private long _lastTickMs;   // v0.1.59：上次 OnTick 的真实时刻（TickCount64），用于算真实 dt
    // v0.1.59 修复：诊断计数（UiProbe 反射读取，验证 OnTick 是否真的在跑）
    private int _tickCount;

    public HeartParticleWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = null!;              // 真穿透：WPF 挖洞铁律（Background=null 才不画底）
        IsHitTestVisible = false;        // 不拦鼠标：点击穿透到猫/桌面
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;           // 不抢焦点
        Focusable = false;
        _canvas.IsHitTestVisible = false;
        Width = WindowW;
        Height = WindowH;
        _canvas.Width = WindowW;
        _canvas.Height = WindowH;
        Content = _canvas;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowEx.HideFromAltTab(this);   // 与其他浮层一致：不出现在 Alt+Tab
    }

    /// <summary>冒出一簇爱心（3~5 颗）。已显示时追加，不打断上一簇。</summary>
    public void Burst(Point catHeadCenter)
    {
        if (!IsVisible) Show();
        // 把猫头中心（屏幕 DIP 坐标）转成窗口内相对坐标
        double localX = catHeadCenter.X - Left;
        double localY = catHeadCenter.Y - Top;

        int count = 3 + _rng.Next(0, 3);   // 3~5 颗
        for (int i = 0; i < count; i++)
        {
            double jitterX = (_rng.NextDouble() - 0.5) * 36;   // 横向散布
            double x = Math.Clamp(localX + jitterX, 8, WindowW - 8);
            double y = Math.Clamp(localY, 6, WindowH - 6);

            var shape = MakeHeart();
            shape.Opacity = 0;
            Canvas.SetLeft(shape, x);
            Canvas.SetTop(shape, y);
            _canvas.Children.Add(shape);

            _particles.Add(new Particle
            {
                Shape = shape,
                StartX = x,
                Vy = -RiseDistance / (ParticleLifeMs / 1000.0),   // 寿命内恰好上升 RiseDistance
                Vx = (_rng.NextDouble() - 0.5) * 24,              // 轻微横向漂移
                BornMs = _clockMs,
            });
        }
        EnsureTimer();
    }

    /// <summary>v0.1.59(⑧)：与猫解耦——浮层是独立的固定尺寸窗口，宿主用 SyncHeartPosition 把它
    /// 对齐到猫头顶上方；粒子在窗口内自由飘。窗口不随粒子移动，故此处只负责开/停定时器。</summary>
    private void EnsureTimer()
    {
        if (_timer is null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickMs) };
            _timer.Tick += (_, _) => OnTick();
        }
        if (!_timer.IsEnabled) _timer.Start();
    }

    private void OnTick()
    {
        _tickCount++;   // v0.1.59 诊断计数（UiProbe 反射验证 tick 真的在跑）

        // v0.1.59 修复：改用真实单调 elapsed（与 v0.1.58 任务5 主循环同款口径）。
        // 原实现每 tick 固定 +16ms 推进 _clockMs，但 dispatcher 会合并/延迟 timer tick（实测 1100ms
        // 墙钟只跑出 44 tick ≈ 704ms 仿真时间）→ 粒子寿命被拉长、超出预期窗口。用真实 dt 后，
        // 无论 tick 疏密，_clockMs 始终≈墙钟，800ms 寿命语义准确。
        var nowMs = Environment.TickCount64;
        if (_lastTickMs == 0) _lastTickMs = nowMs;   // 首次 tick 无前值
        double dtMs = Math.Max(0, nowMs - _lastTickMs);
        _lastTickMs = nowMs;
        _clockMs += dtMs;
        double dt = dtMs / 1000.0;

        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            double age = _clockMs - p.BornMs;
            if (age >= ParticleLifeMs)
            {
                _canvas.Children.Remove(p.Shape);
                _particles.RemoveAt(i);
                continue;
            }

            // 位置：向上匀速 + 横向漂移
            double x = p.StartX + p.Vx * age / 1000.0;
            double y = (Canvas.GetTop(p.Shape)) + p.Vy * dt;
            Canvas.SetLeft(p.Shape, x);
            Canvas.SetTop(p.Shape, y);

            // 透明度：先淡入（前 20%）后淡出（后 60%），中段全显
            double t = age / ParticleLifeMs;
            double opacity = t < 0.2 ? t / 0.2 : (t > 0.4 ? (1 - (t - 0.4) / 0.6) : 1.0);
            p.Shape.Opacity = Math.Clamp(opacity, 0, 1);
            // 缩放：由小变大再略缩（用 RenderTransform 系数）
            double s = 0.7 + 0.5 * Math.Sin(Math.PI * Math.Clamp(t, 0, 1));
            p.Shape.RenderTransform = new ScaleTransform(s, s, 7, 6);
        }

        // 全部消散 → 停定时器并隐藏（不常驻）
        if (_particles.Count == 0)
        {
            _timer?.Stop();
            Hide();
        }
    }

    /// <summary>立即隐藏并清空（游戏模式/关闭时调用）。</summary>
    public void Dismiss()
    {
        _timer?.Stop();
        _canvas.Children.Clear();
        _particles.Clear();
        if (IsVisible) Hide();
    }

    /// <summary>程序化爱心：两个上凸的半圆 + 底部尖角，用贝塞尔拼成一颗心。约 14×12 DIP。</summary>
    private static Path MakeHeart()
    {
        var geo = new PathGeometry();
        var fig = new PathFigure { StartPoint = new Point(7, 12), IsClosed = true, IsFilled = true };
        // 左半边：从底部尖角 → 左肩 → 顶部凹点
        fig.Segments.Add(new BezierSegment(new Point(0, 7), new Point(0, 0), new Point(7, 3), true));
        // 右半边：顶部凹点 → 右肩 → 底部尖角
        fig.Segments.Add(new BezierSegment(new Point(14, 0), new Point(14, 7), new Point(7, 12), true));
        geo.Figures.Add(fig);
        return new Path
        {
            Data = geo,
            Fill = new SolidColorBrush(HeartFill),
            Stroke = new SolidColorBrush(HeartOutline),
            StrokeThickness = 1.0,
        };
    }
}
