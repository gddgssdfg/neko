using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopPet.App.Services;
using DesktopPet.Core;
using DesktopPet.App.Core;

namespace DesktopPet.App.Windows;

/// <summary>
/// 径向菜单：鼠标右键 / 全局热键呼出的圆形快捷菜单。
/// v0.1.25 定稿（推翻 v0.1.9 旁边定位，回归 v0.1.2 的"围绕猫一圈"）：
///   1. 菜单圆心 = 猫中心，中心挖空（洞半径 = 猫半宽 + 余量，由 PetWindow 传入），
///      洞区不放任何可命中元素 → 鼠标事件穿透到宠物窗口，猫可见、可抚摸/拖拽。
///   2. 每页最多 8 个槽位（用户要求），超出的按 Order 分页，滚轮/点顶部胶囊翻页。
///      8 槽位角距 45°，相邻弦长 ≈ 59px > 槽位 38px，不再重叠。
///   3. 底盘为 95% 不透明白色圆环 + 深色描边，任何壁纸上都看得清。
/// 槽位来自插件通过 IUIApi.RegisterRadialSlot 注册；生命周期：单例复用，Hide() 而非 Close()。
/// 点击穿透原理：窗口与根画布 Background 均为 null（WPF 中 null 不参与命中测试），
/// 只有圆环 Path / 槽位 / 翻页胶囊接收输入，中心空洞天然穿透，无需 SetWindowRgn（顺带规避 DPI 换算）。
/// </summary>
public sealed class RadialMenu : Window
{
    private readonly UIApiImpl _uiApi;
    private readonly GlobalInput _input;
    private readonly AppConfig _config;          // v0.1.71：径向菜单配置（顺序/显隐/数量上限）
    private readonly Action? _onToggleMirror;   // v0.1.30：键盘 toggle 按钮回调（开/关键鼠映射）
    private readonly Canvas _canvas;
    // v0.1.7：槽位图标 PNG 缓存（用 Pillow 预渲染 emoji → icons/*.png，运行时不再依赖系统 emoji 字体）
    private readonly Dictionary<string, BitmapImage> _iconCache = new();

    // ---- v0.1.25 布局常量 ----
    private const double SlotSize = 38.0;   // 槽位方块边长（图标 18 + 标签 9px）
    private const int MaxPerPage = 8;       // 每页槽位上限（用户明确要求 8 个）
    private const double HoleGap = 3.0;     // 洞边缘 → 槽位内边缘 的白色间隔
    private const double OuterMargin = 8.0; // 槽位外边缘 → 底盘外缘 的留白
    private const double HeaderH = 26.0;    // 翻页胶囊条高度（仅多页时存在）
    private const double DefaultHole = 55.0;// 未传洞半径时的默认值（适配 3.2x 猫半宽 51）
    private const int CloseDelayMs = 300;   // 鼠标离开后延迟隐藏，避免划过缝隙误关
    private const string IconFallback = "default"; // 槽位未匹配图标 key 时回退到 default.png

    // 主题色（暖白纸感 + 深棕描边，与橘猫协调且对比清晰）
    private static readonly Color PlateFill = Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF); // 底盘 95% 白
    private static readonly Color PlateStroke = Color.FromRgb(0x6B, 0x53, 0x34);      // 深棕描边
    private static readonly Color SlotBorder = Color.FromRgb(0xC8, 0xB2, 0x8C);       // 槽位常态描边
    private static readonly Color SlotHoverFill = Color.FromArgb(0xCC, 0xFF, 0xB3, 0x47);
    private static readonly Color SlotHoverBorder = Color.FromRgb(0xE8, 0x94, 0x0C);
    private static readonly Color SlotNormalFill = Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF);  // 槽位常态背景（半透明白）
    private static readonly Color TextColor = Color.FromRgb(0x4A, 0x37, 0x28);
    private static readonly QuadraticEase AniEase = new() { EasingMode = EasingMode.EaseOut };

    /// <summary>v0.1.32：槽位/胶囊背景色平滑过渡（hover 不再瞬变）</summary>
    private static void BeginBgAnim(SolidColorBrush scb, Color color)
    {
        scb.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color, TimeSpan.FromMilliseconds(120)) { EasingFunction = AniEase });
    }

    /// <summary>v0.1.39：测量文本渲染宽度（判断槽位标签是否会被截断 → 决定是否挂 Tooltip）。
    /// 用 FormattedText 实测而非按字数猜：中英文宽度差一倍，"时钟"和"clipboard"同样是两个/九个字形。</summary>
    private static double MeasureTextWidth(string text, double fontSize)
    {
        var ft = new FormattedText(
            text ?? "",
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            fontSize,
            new SolidColorBrush(TextColor),
            1.0);
        return ft.Width;
    }

    /// <summary>
    /// v0.1.39：槽位逐个入场动画（scale 0.4→1 + 淡入，按序号阶梯延迟）。
    /// 取代原先"整窗一起淡入"——逐个弹出让轮盘的打开过程更有节奏、更顺滑。
    /// </summary>
    private static void BeginSlotEnter(UIElement el, int index)
    {
        var delay = TimeSpan.FromMilliseconds(30 + index * 28);
        var dur = TimeSpan.FromMilliseconds(220);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        if (el.RenderTransform is ScaleTransform st)
        {
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.4, 1.0, dur) { BeginTime = delay, EasingFunction = ease });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.4, 1.0, dur) { BeginTime = delay, EasingFunction = ease });
        }
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)) { BeginTime = delay, EasingFunction = ease });
    }

    private double _holeR = DefaultHole; // 中心洞半径（DIP）
    private int _page;                   // 当前页（0 起）
    private int _pageCount = 1;
    private bool _suppressExternalClose; // 菜单内部点击（槽位/toggle/pill）时短暂屏蔽外部关闭判定

    private double SlotRadius => _holeR + HoleGap + SlotSize / 2;      // 槽位中心到圆心
    private double OuterRadius => SlotRadius + SlotSize / 2 + OuterMargin; // 底盘外缘半径

    public RadialMenu(UIApiImpl uiApi, GlobalInput input, AppConfig config, Action? onToggleMirror = null)
    {
        _uiApi = uiApi;
        _input = input;
        _config = config;
        _onToggleMirror = onToggleMirror;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        // 注意：Window 与 Canvas 的 Background 必须是 null 而非 Brushes.Transparent——
        // Transparent 画刷会参与命中测试（整窗拦截点击），null 才能让中心空洞穿透到猫。
        Background = null!;
        Topmost = true;
        ShowInTaskbar = false;
        IsHitTestVisible = true;
        ShowActivated = false;   // v0.1.30：完全不抢焦点（不调用 Activate），避免 Deactivated 路径触发自动关

        _canvas = new Canvas { Width = 100, Height = 100, Background = null! };
        Content = _canvas;

        // v0.1.30：删除 Deactivated / MouseLeave 自动关——它们是"还没看清就没了"的元凶。
        // 改用"点外部 / Esc 才关"：用 GlobalInput 全局 RawMouseButtonEvent + RawKeyEvent 判断。

        // v0.1.25：滚轮翻页（多页时）——保留
        MouseWheel += (_, e) =>
        {
            if (_pageCount <= 1) return;
            if (e.Delta < 0) NextPage(); else PrevPage();
            e.Handled = true;
        };

        // 加载槽位图标 PNG 缓存（icons/ 目录，启动时一次性加载，运行时不依赖系统字体）
        LoadIcons();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowEx.HideFromAltTab(this);   // v0.1.50：径向菜单也不出现在 Alt+Tab
    }

    private void LoadIcons()
    {
        var dir = System.IO.Path.Combine(DesktopPet.App.Core.Paths.Assets, "icons");
        if (!System.IO.Directory.Exists(dir)) return;
        foreach (var path in System.IO.Directory.GetFiles(dir, "*.png"))
        {
            try
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(path);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new System.Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                _iconCache[name] = bmp;
            }
            catch (Exception ex)
            {
                // v0.1.82 P1-8：图标损坏/权限等问题会导致该项永远缺图标（用户可见），不能再静默吞
                Log.Warn($"[径向菜单] 图标加载失败，该项降级为「•」文字：{path} - {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 在指定屏幕坐标处打开径向菜单（圆心对齐该点，通常为猫中心）。
    /// holeRadius：中心洞半径（DIP），PetWindow 传"猫半宽 + 4"；≤0 用默认值。
    /// v0.1.30：淡入 + 缩放 200ms 动画；订阅全局点击/Esc 关闭。
    /// </summary>
    public void ShowAt(double centerX, double centerY, double holeRadius = DefaultHole)
    {
        _holeR = Math.Max(24, holeRadius);
        _page = 0; // 每次打开回到第一页
        RebuildSlots(); // 先构建（内部确定窗口尺寸），再定位
        Left = centerX - Width / 2;
        Top = centerY - Height / 2;

        // v0.1.30 动画：opacity 0→1 + scale 0.9→1（RenderTransformOrigin 中心）
        _canvas.RenderTransformOrigin = new Point(0.5, 0.5);
        _canvas.RenderTransform = new ScaleTransform(0.9, 0.9);
        Opacity = 0;
        Show();

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
        var scale = new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(200));
        BeginAnimation(OpacityProperty, fade);
        ((ScaleTransform)_canvas.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        ((ScaleTransform)_canvas.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, scale);

        // 全局点击 / Esc → 关闭判定
        // v0.1.95（审计 P1-2 修复）：**幂等订阅**——旧写法无条件 `+=`。菜单打开时猫仍在中心挖空区可被右键
        // （右键入口 OpenRadialMenu 无 IsVisible 守卫），"右键→再右键"会造成重复订阅，而 HideMenu 只 `-=` 一次
        // → 残留永久订阅并随右键次数累积。先 `-=` 再 `+=` 保证恒为 1 个订阅。
        _input.RawMouseButtonEvent -= OnExternalMouseClick;
        _input.RawKeyEvent -= OnExternalKey;
        _input.RawMouseButtonEvent += OnExternalMouseClick;
        _input.RawKeyEvent += OnExternalKey;
    }

    /// <summary>隐藏菜单（v0.1.30：200ms 淡出后真正 Hide）</summary>
    public void HideMenu()
    {
        // v0.1.95（审计 P1-2）：退订提到早返回【之前】——不可见时也要把订阅清掉（配平），
        // 否则「ShowAt 双订阅 → HideMenu 只减一次」会残留一个永久订阅。
        _input.RawMouseButtonEvent -= OnExternalMouseClick;
        _input.RawKeyEvent -= OnExternalKey;
        if (!IsVisible) return;

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) => { if (Opacity < 0.5) Hide(); };
        BeginAnimation(OpacityProperty, fade);
    }

    // ---- v0.1.30 外部事件判定 ----

    private void OnExternalMouseClick(int btn, bool down)
    {
        if (!down) return;
        if (_suppressExternalClose) { _suppressExternalClose = false; return; }
        // 鼠标按下点的屏幕坐标（RawMouseButton 不带坐标，但 GetCursorPos 简单可用）
        if (!IsClickInsideMenu()) HideMenu();
    }

    /// <summary>v0.1.36 修复：GetCursorPos 返回物理像素，窗口 Left/Top/Width/Height 是 DIP——
    /// DPI≠100% 时直接比会误判"点外部"（点翻页胶囊直接关菜单，这就是"无法翻页"的真因）。
    /// 必须用 TransformFromDevice 把物理坐标转 DIP（v0.1.1 同款坐标系教训）。</summary>
    private bool IsClickInsideMenu()
    {
        GetCursorPos(out var physical);
        double sx = 1.0, sy = 1.0;
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget is not null)
        {
            sx = src.CompositionTarget.TransformFromDevice.M11;
            sy = src.CompositionTarget.TransformFromDevice.M22;
        }
        var dip = new Point(physical.X * sx, physical.Y * sy);
        var rect = new Rect(Left, Top, Width, Height);
        return rect.Contains(dip);
    }

    private void OnExternalKey(uint vk, bool down)
    {
        if (down && vk == 0x1B) HideMenu();   // Esc
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out System.Drawing.Point lpPoint);

    private void NextPage() { _page = (_page + 1) % _pageCount; RebuildSlots(); }
    private void PrevPage() { _page = (_page - 1 + _pageCount) % _pageCount; RebuildSlots(); }

    /// <summary>
    /// 重建全部内容：底盘圆环 + 槽位（本页）+ 翻页胶囊。
    /// 同时确定窗口尺寸（多页时顶部多一条 HeaderH 的胶囊条）。
    /// </summary>
    private void RebuildSlots()
    {
        _canvas.Children.Clear();

        var all = RadialMenuResolver.Resolve(_uiApi.AllRadialSlots, _config.RadialMenu); // v0.1.71：过滤隐藏 + 用户排序 + 截断上限
        _pageCount = Math.Max(1, (int)Math.Ceiling(all.Count / (double)MaxPerPage));
        if (_page >= _pageCount) _page = 0;

        bool multi = _pageCount > 1;
        double ring = 2 * (OuterRadius + 2);
        Width = ring;
        Height = ring + (multi ? HeaderH : 0);
        _canvas.Width = Width;
        _canvas.Height = Height;
        double cx = ring / 2;
        double cy = (multi ? HeaderH : 0) + ring / 2;

        // 底盘：外圆 − 中心洞（EvenOdd）；无槽位时不挖洞，画完整圆盘 + 居中提示
        // 注意：System.IO 也在 using 里，Shapes.Path 必须全限定，否则解析成文件 Path 类
        var geo = new GeometryGroup { FillRule = FillRule.EvenOdd };
        geo.Children.Add(new EllipseGeometry(new Rect(cx - OuterRadius, cy - OuterRadius, OuterRadius * 2, OuterRadius * 2)));
        if (all.Count > 0)
            geo.Children.Add(new EllipseGeometry(new Rect(cx - _holeR, cy - _holeR, _holeR * 2, _holeR * 2)));
        _canvas.Children.Add(new System.Windows.Shapes.Path
        {
            Data = geo,
            Fill = new SolidColorBrush(PlateFill),
            Stroke = new SolidColorBrush(PlateStroke),
            StrokeThickness = 1.5,
        });

        if (all.Count == 0)
        {
            var label = new TextBlock
            {
                Text = "无快捷项",
                Foreground = new SolidColorBrush(TextColor),
                FontSize = 12,
            };
            _canvas.Children.Add(label);
            Canvas.SetLeft(label, cx - 30);
            Canvas.SetTop(label, cy - 10);
            return;
        }

        // ---- v0.1.30：键鼠映射 toggle 按钮（菜单顶部左侧，灯灯要求"右键小猫里也能开"）----
        if (_onToggleMirror is not null)
        {
            var toggle = new Border
            {
                Background = new SolidColorBrush(PlateFill),
                BorderBrush = new SolidColorBrush(PlateStroke),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11),
                Width = 22, Height = 22,
                Cursor = Cursors.Hand,
                ToolTip = "切换键鼠映射",
                Child = new TextBlock
                {
                    Text = "⌨",   // U+2328
                    FontSize = 12,
                    FontFamily = new FontFamily("Segoe UI Symbol"),
                    Foreground = new SolidColorBrush(TextColor),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            toggle.MouseLeftButtonUp += (_, _) =>
            {
                _suppressExternalClose = true;   // 屏蔽紧随其后的 UP 事件关菜单
                _onToggleMirror!.Invoke();
            };
            _canvas.Children.Add(toggle);
            Canvas.SetLeft(toggle, multi ? cx - 50 : cx - 11);
            Canvas.SetTop(toggle, 3);
        }

        // 翻页胶囊（多页时）：顶部居中右侧，点击进入下一页；滚轮也可翻页
        if (multi)
        {
            var pill = new Border
            {
                Background = new SolidColorBrush(PlateFill),
                BorderBrush = new SolidColorBrush(PlateStroke),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 1, 8, 1),
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = $"‹ {_page + 1}/{_pageCount} ›",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(TextColor),
                    TextAlignment = TextAlignment.Center,
                },
            };
            pill.MouseLeftButtonUp += (_, _) => NextPage();
            _canvas.Children.Add(pill);
            pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(pill, _onToggleMirror is not null ? cx + 16 : cx - pill.DesiredSize.Width / 2);
            Canvas.SetTop(pill, 3);
        }

        // 本页槽位：起始角 -90°（正上方），顺时针均分（≤8 个 → 角距 ≥45°，绝不重叠）
        var pageSlots = all.Skip(_page * MaxPerPage).Take(MaxPerPage).ToList();
        int n = pageSlots.Count;
        for (int i = 0; i < n; i++)
        {
            var slot = pageSlots[i];
            double angle = (-90 + i * (360.0 / n)) * Math.PI / 180.0;
            double x = cx + SlotRadius * Math.Cos(angle);
            double y = cy + SlotRadius * Math.Sin(angle);

            var view = new SlotView(slot, _iconCache.GetValueOrDefault(slot.Icon ?? IconFallback))
            {
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(0.4, 0.4),
                Opacity = 0,   // 起始态，由 BeginSlotEnter 逐个弹入
            };
            Canvas.SetLeft(view, x - SlotSize / 2);
            Canvas.SetTop(view, y - SlotSize / 2);
            _canvas.Children.Add(view);
            BeginSlotEnter(view, i);   // v0.1.39：阶梯延迟逐个弹出（打开/翻页都更有节奏）
        }
    }

    /// <summary>
    /// v0.1.39：菜单跟随猫移动（PetWindow 主循环在猫位移后调用）。
    /// 只改 Left/Top，不重建内容、不重放动画——拖着猫走时轮盘稳稳贴在猫身上。
    /// </summary>
    public void MoveTo(double centerX, double centerY)
    {
        if (!IsVisible) return;
        // v0.1.82：同值短路。WPF 的 Window.Left/Top setter 不做同值判断，赋值即 SetWindowPos；
        // 菜单跟随是 20Hz 主循环驱动的，猫不动时没必要每秒重设 20 次窗口位置。
        double l = centerX - Width / 2, t = centerY - Height / 2;
        if (Math.Abs(Left - l) < 0.01 && Math.Abs(Top - t) < 0.01) return;
        Left = l;
        Top = t;
    }

    /// <summary>
    /// 单个槽位的可视化 + 交互（v0.1.25：38px 卡片、高对比描边、悬停橙色高亮；
    /// 图标用预渲染 PNG，不再依赖系统 emoji 字体）
    /// v0.1.39：名字被截断的槽位加 Tooltip，鼠标悬浮漏出全名（灯灯要求）。
    /// </summary>
    private sealed class SlotView : Border
    {
        public RadialSlot Slot { get; }
        public SlotView(RadialSlot slot, BitmapImage? icon)
        {
            Slot = slot;
            Width = SlotSize;
            Height = SlotSize;
            CornerRadius = new CornerRadius(7);
            Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
            BorderThickness = new Thickness(1.5);
            BorderBrush = new SolidColorBrush(SlotBorder);
            Cursor = Cursors.Hand;

            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var iconElement = icon is null
                ? (FrameworkElement)new TextBlock { Text = "•", FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, Foreground = new SolidColorBrush(TextColor) }
                : new Image { Source = icon, Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Center };
            var label = new TextBlock
            {
                Text = slot.Label,
                FontSize = 9,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(TextColor),
                MaxWidth = SlotSize - 4,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            stack.Children.Add(iconElement);
            stack.Children.Add(label);
            Child = stack;

            // v0.1.39：实测文本宽度，超出槽位显示宽度才挂 Tooltip（短名字不弹，免得悬浮一下就冒个泡）
            if (MeasureTextWidth(slot.Label, 9) > SlotSize - 4)
            {
                ToolTip = slot.Label;
                ToolTipService.SetInitialShowDelay(this, 250);
            }

            MouseEnter += (_, _) =>
            {
                BeginBgAnim((SolidColorBrush)Background, SlotHoverFill);
                BorderBrush = new SolidColorBrush(SlotHoverBorder);
            };
            MouseLeave += (_, _) =>
            {
                BeginBgAnim((SolidColorBrush)Background, SlotNormalFill);
                BorderBrush = new SolidColorBrush(SlotBorder);
            };
            MouseLeftButtonUp += (_, _) =>
            {
                try { Slot.OnInvoke?.Invoke(); } catch (Exception ex) { Log.Warn($"[径向菜单] 槽位「{Slot.Label}」回调异常：{ex.GetType().Name}: {ex.Message}"); }
                // 点击后隐藏菜单（复用单例，不销毁窗口）
                if (Window.GetWindow(this) is RadialMenu menu) menu.HideMenu();
            };
        }
    }
}
