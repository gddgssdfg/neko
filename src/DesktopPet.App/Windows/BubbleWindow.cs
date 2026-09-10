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

        var ft = new FormattedText(
            text ?? "",
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            13,
            new SolidColorBrush(TextC),
            1.0);
        double textW = Math.Min(ft.Width, MaxTextWidth);
        int lines = Math.Max(1, (int)Math.Ceiling(ft.Width / MaxTextWidth));
        double textH = lines * 18 + 6;

        // 云朵几何（单位 DIP，圆心对齐文字区）
        double bodyW = textW + 44;                       // 主身宽 = 文字宽 + 左右留白
        double bodyH = Math.Max(34, textH + 14);         // 主身高（≥34 保证云形比例）
        double leftPad = 12;                             // 左右凸起探出的空间
        double topPad = 14;                              // 上凸探出空间
        double tailH = 10;                               // 尾巴高度
        double W = bodyW + leftPad * 2;
        double H = bodyH + topPad + 6 + tailH;           // 凸起在上方，尾巴在下方

        // 三个圆角矩形 + 尾巴 = 合并成一个云形
        var main = new RectangleGeometry(new Rect(leftPad, topPad, bodyW, bodyH), 18, 18);
        var left = new RectangleGeometry(new Rect(leftPad - 7, topPad - 6, 34, 30), 15, 15);   // 左上凸
        var right = new RectangleGeometry(new Rect(W - leftPad - 27, topPad - 12, 42, 34), 17, 17); // 右上凸（略大更自然）
        // 尾巴：底部中央圆角矩形（指向下方猫）
        double tailX = W / 2 - 9;
        var tail = new RectangleGeometry(new Rect(tailX, H - tailH - 2, 18, tailH + 2), 5, 5);

        var cloud = Geometry.Combine(Geometry.Combine(Geometry.Combine(main, left, GeometryCombineMode.Union, null),
            right, GeometryCombineMode.Union, null), tail, GeometryCombineMode.Union, null);

        _canvas.Children.Add(new System.Windows.Shapes.Path
        {
            Data = cloud,
            Fill = new SolidColorBrush(CloudFill),
            Stroke = new SolidColorBrush(CloudStroke),
            StrokeThickness = 1.5,
        });

        // 文字：水平居中于主身
        _text.Text = text;
        double textX = leftPad + (bodyW - Math.Min(ft.Width, MaxTextWidth)) / 2;
        Canvas.SetLeft(_text, Math.Max(2, textX));
        Canvas.SetTop(_text, topPad + (bodyH - textH) / 2);
        _canvas.Children.Add(_text);

        Width = W + 2;
        Height = H + 2;
        _canvas.Width = Width;
        _canvas.Height = Height;
    }
}
