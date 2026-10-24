using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DesktopPet.App.Windows;

/// <summary>
/// v0.1.47 像素云朵气泡（灯灯选定样式 A：一整朵自然圆滑云，里面一句话）。
///
/// 实现：用 Geometry.Combine(Union) 把主身圆角矩形 + 左/右上凸圆角矩形 + 底部小尾巴
/// 合并成**一个完整的云形 Geometry**，Path 一次 fill+stroke——没有 v0.1.40 方块密铺的
/// 锯齿内线，也没有椭圆叠加的交叉描边，就是一朵干净的云。
/// 宽度按文字实测自适应；文字 13px 深棕居中，比旧版更大更清晰（旧版字小且被方块边干扰）。
///
/// 行为不变：独立透明窗口、不抢焦点、不拦鼠标、跟随猫（PetWindow.SyncBubblePosition）、
/// 淡入 120ms → 停留 durationMs → 淡出 200ms；已有气泡时新内容直接替换。
/// </summary>
public sealed class BubbleWindow : Window
{
    private const double MaxTextWidth = 180;     // 文字换行阈值（云朵单行上限约 200px）
    private static readonly Color CloudFill = Color.FromRgb(0xFF, 0xF9, 0xEC);
    private static readonly Color CloudStroke = Color.FromRgb(0x6B, 0x53, 0x34);
    private static readonly Color TextC = Color.FromRgb(0x4A, 0x37, 0x28);

    private readonly Canvas _canvas = new();
    private readonly TextBlock _text = new()
    {
        FontSize = 13,                          // v0.1.47：9→13px，字明显更大更清楚
        Foreground = new SolidColorBrush(TextC),
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = MaxTextWidth,
        TextAlignment = TextAlignment.Center,
        LineHeight = 18,
    };
    private DispatcherTimer? _hideTimer;
    private bool _hiding;

    public BubbleWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = null!;
        IsHitTestVisible = false;    // 气泡不拦鼠标：点击穿透到猫/桌面
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Focusable = false;
        _canvas.IsHitTestVisible = false;
        Content = _canvas;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowEx.HideFromAltTab(this);   // v0.1.50：气泡也不出现在 Alt+Tab
    }

    /// <summary>显示一条气泡。已显示时替换内容并重置计时。</summary>
    public void ShowBubble(string text, int durationMs)
    {
        _hideTimer?.Stop();
        _hiding = false;
        Rebuild(text);
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
        }
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(900, durationMs)) };
        _hideTimer.Tick += (_, _) => FadeOut();
        _hideTimer.Start();
    }

    /// <summary>立即隐藏（外部如关闭菜单/游戏模式时调用）</summary>
    public void Dismiss()
    {
        _hideTimer?.Stop();
        if (IsVisible) Hide();
        _hiding = false;
    }

    private void FadeOut()
    {
        if (_hiding) return;
        _hiding = true;
        _hideTimer?.Stop();
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (_, _) => { if (Opacity < 0.5) Hide(); };
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>重建云朵：实测文字宽 → 主身宽 → 圆角矩形合并成云形 → 放文字。</summary>
    private void Rebuild(string text)
    {
        _canvas.Children.Clear();

        _text.Text = text;
        _text.Measure(new Size(MaxTextWidth, double.PositiveInfinity));
        double textW = Math.Ceiling(_text.DesiredSize.Width);
        double textH = Math.Ceiling(_text.DesiredSize.Height);
        double bodyW = Math.Max(68, textW + 32);
        double bodyH = Math.Max(38, textH + 22);
        const double inset = 3, step = 6, tail = 8;
        // Stepped silhouette: crisp corners echo the cat's native pixel artwork.
        var points = new[] { new Point(inset+step,inset), new Point(inset+bodyW-step,inset),
            new Point(inset+bodyW-step,inset+step), new Point(inset+bodyW,inset+step),
            new Point(inset+bodyW,inset+bodyH-step), new Point(inset+bodyW-step,inset+bodyH-step),
            new Point(inset+bodyW-step,inset+bodyH), new Point(inset+bodyW/2+6,inset+bodyH),
            new Point(inset+bodyW/2+6,inset+bodyH+tail), new Point(inset+bodyW/2-2,inset+bodyH+tail),
            new Point(inset+bodyW/2-2,inset+bodyH), new Point(inset+step,inset+bodyH),
            new Point(inset+step,inset+bodyH-step), new Point(inset,inset+bodyH-step),
            new Point(inset,inset+step), new Point(inset+step,inset+step) };
        var outline = new StreamGeometry();
        using (var ctx = outline.Open()) { ctx.BeginFigure(points[0], true, true); ctx.PolyLineTo(points.Skip(1).ToArray(), true, false); }
        outline.Freeze();
        _canvas.Children.Add(new System.Windows.Shapes.Path { Data = outline, Fill = new SolidColorBrush(CloudFill),
            Stroke = new SolidColorBrush(CloudStroke), StrokeThickness = 1 });
        Canvas.SetLeft(_text, inset + (bodyW-textW)/2);
        Canvas.SetTop(_text, inset + (bodyH-textH)/2);
        _canvas.Children.Add(_text);
        Width = bodyW + inset*2;
        Height = bodyH + tail + inset*2;
        _canvas.Width = Width;
        _canvas.Height = Height;
    }
}
