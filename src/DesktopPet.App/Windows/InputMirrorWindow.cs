using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using DesktopPet.App.Core;
using DesktopPet.App.Services;

namespace DesktopPet.App.Windows;

/// <summary>
/// 键鼠映射窗口（v0.1.28，BongoCat 核心视觉借鉴）：
/// 桌面显示一块像素风键盘+鼠标，真实敲键/点击时对应键位实时高亮，松开还原。
/// 纯 WPF 绘制（Border+TextBlock），零图片素材；底盘风格与径向菜单统一（95% 白+深棕描边）。
/// 可拖动，位置记忆（MirrorPosX/Y），clamp 进工作区；不抢焦点、不进任务栏。
/// v0.1.86：视觉优化——底部留白 36→18px（爪顶贴键盘底边、不挡键语义不变）；键帽加立体前脸 +
/// 受光渐变 + 按下下沉（键程感）、文字加深加粗；鼠标加受光渐变 / 左右键中缝 / 滚轮凹槽；LED 排与键盘左对齐。
/// </summary>
public sealed class InputMirrorWindow : Window
{
    // ---- 视觉常量（v0.1.30：整体缩小约 35%；v0.1.44：再缩小 20% 并更紧凑，参考 BongoCat 键盘不抢戏）----
    private const double KeyW = 18.0;          // 1u 键宽（34→22→18）
    private const double KeyH = 18.0;          // 键高
    private const double KeyGap = 1.5;         // 键间距
    private const double KeyDepth = 3.0;       // v0.1.86：键帽立体“前脸”厚度——键帽面比底座矮 3px，露出暗色下缘（键程层次）
    private const double PlatePad = 6.0;       // 底盘内边距（8→6，更紧凑贴合）
    private const double MouseW = 46.0;        // 鼠标区宽（74→56→46）
    private const double MouseH = 68.0;        // 鼠标区高（185→82→68，保持 1:1.4 真实比例）
    private const double MouseGap = 8.0;       // 键盘↔鼠标间距（10→8）

    private static readonly Color PlateFillC = Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF);   // 95% 白
    private static readonly Color PlateStrokeC = Color.FromRgb(0x6B, 0x53, 0x34);       // 深棕
    private static readonly Color KeyFillC = Color.FromRgb(0xFF, 0xFF, 0xFF);           // 键帽面（松开还原/动画目标色）
    private static readonly Color KeyEdgeC = Color.FromRgb(0xC8, 0xB2, 0x8C);           // 浅棕描边
    private static readonly Color KeyBaseC = Color.FromRgb(0xD9, 0xC3, 0x9C);           // v0.1.86：键帽底座/前脸（暗米棕，露在键帽下缘 = 立体感）
    private static readonly Color KeyShadeC = Color.FromRgb(0xEC, 0xE0, 0xC7);          // v0.1.86：键帽面底部柔和阴影（渐变下端，模拟受光）
    private static readonly Color KeyTextC = Color.FromRgb(0x33, 0x24, 0x1A);           // v0.1.86：深棕文字（#4A3728→#33241A，提对比）
    private static readonly Color KeyActiveC = Color.FromRgb(0xFF, 0xB3, 0x47);         // 高亮橙（悬停同款）

    private readonly GlobalInput _input;
    private readonly AppConfig _config;
    private readonly Action _saveConfig;
    private readonly Action? _onRelocated;   // v0.1.30：拖动完成后回调（work 模式让猫跟随重定位）

    /// <summary>vk 码 → 键位 Border（一个 vk 只对应一个可视键；左右 Shift/Ctrl/Alt 各自独立 vk 不会撞）</summary>
    private readonly Dictionary<uint, Border> _keyByVk = new();
    private readonly Dictionary<int, Border> _mouseKeys = new(); // 0=左 1=右 2=中
    private Border? _wheelFlash;
    private System.Windows.Threading.DispatcherTimer? _wheelTimer;

    // ---- v0.1.29：猫爪动画（BongoCat 爪子借鉴，素材 tools/generate_sprites.py 程序化生成）----
    private ImageSource? _pawUp;
    private ImageSource? _pawDown;
    private Image? _kbdPaw;      // 键盘爪：敲键瞬间滑到该键上拍一下，130ms 后举回
    private Image? _mousePaw;    // 鼠标爪：按下压住、松开抬起（跟按钮走）
    private int _mousePawDownBtn = -1;
    private double _kbdPawTargetLeft = KeyboardPawLeft;   // v0.1.43：键盘爪悬停 x（跟随最后按的键）
    private double _mousePawTargetLeft = MousePawLeft;    // v0.1.43：鼠标爪悬停 x（跟随最后按的按钮）
    private System.Windows.Threading.DispatcherTimer? _kbdPawTimer;
    private System.Windows.Threading.DispatcherTimer? _wheelPawTimer;
    private const double PawSize = 30.0;       // v0.1.44：爪子 36→30，配合缩小
    private const double KeyboardPawTop = -2;   // 拍键时的落点（爪尖探出键面上沿）
    private const double KeyboardPawLeft = 140; // 初始悬停 x（键盘 291 中段偏左，G/H 区）
    private const double MousePawTop = -2;
    private const double MousePawLeft = 322;    // 键盘 291 + 间距 8 + 鼠标居中 23
    // v0.1.72：猫坐在对面——爪子从"窗体底边之外"伸上来。
    // v0.1.44 的"不挡键"原则保留——只是从"顶"挪到"底"。
    // ⚠️ 必须是 const（保持编译期常量）。之前改成实例字段导致 UiProbe 回归 NRE（layout pass 触发 null 访问）。
    // v0.1.86：底部留白收窄——举手位贴到键盘底边（KbH=96），底部额外预留 36→18（减半）。
    //          爪顶仍在键盘底边（≥KbH，不挡键语义不变）；爪底 96+30=126 ≤ 内容高 129，不截断。
    private const double PawRaisedTop = KbH;       // = 96：爪顶贴键盘底边（键盘之外，不挡键）
    // v0.1.87：底部预留高度 = 恰好容纳"举起的爪"。
    // 爪顶在 PawRaisedTop(=KbH)，爪高 PawSize(=30) → 爪底在 KbH+30；
    // 而高度公式里 KbH+LedH+PlatePad*2+4 已经给出 KbH+31 的内容底 → 底部只需再留 ~2。
    // 之前取 18 是"加在高度公式上"的，内容却从顶部排 → 那 18 全变成了爪底以下的纯留白（实测 19px）。
    private const double PawRaisedExtra = 2.0;

    // ---- v0.1.44：按键"按着才亮、松开就白"兜底——真实键盘观感 ----
    // GlobalInput 的 UP 事件链路是好的，但连打/热键路径下偶发 UP 丢失会导致键一直亮。
    // 记录每个高亮键的按下时刻，_ledTimer 200ms 轮询：亮着超过 300ms 且期间没再输入则强制还原。
    private readonly Dictionary<Border, DateTime> _litKeys = new();

    // ---- v0.1.30：锁键 LED（Caps/Num/Scroll Lock 状态指示，200ms 轮询）----
    private const uint VK_CAPITAL = 0x14;
    private const uint VK_NUMLOCK = 0x90;
    private const uint VK_SCROLL  = 0x91;
    private static readonly Color LedOnC = Color.FromRgb(0xFF, 0xB3, 0x47);   // 锁键开=橙
    private static readonly Color LedOffC = Color.FromRgb(0xB0, 0xA8, 0x95);  // 锁键关=暗灰
    private Ellipse _ledCaps = null!;
    private Ellipse _ledNum = null!;
    private Ellipse _ledScrLk = null!;   // ctor 末 BuildContent 初始化
    private System.Windows.Threading.DispatcherTimer? _ledTimer;

    // v0.1.39：内容根（镜像开关作用于它）——键帽层 + 爪子层一起翻转，不能只翻键帽
    // 注意：不能是 readonly（BuildContent 是普通方法不是 ctor，readonly 会报 CS0191）
    private Grid _rootGrid = null!;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(uint nVirtKey);

    /// <summary>锁键开时返回 true（GetKeyState 低位 bit 0 = toggled）</summary>
    private static bool IsLockOn(uint vk) => (GetKeyState(vk) & 0x0001) != 0;

    // 拖动（DragMove 系统托管，松手后落盘位置）
    private void OnDragDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
        SaveMirrorPosition();
        _onRelocated?.Invoke();   // v0.1.30：work 模式下让猫跟着映射窗重定位
    }

    public InputMirrorWindow(GlobalInput input, AppConfig config, Action saveConfig, Action? onRelocated = null)
    {
        _input = input;
        _config = config;
        _saveConfig = saveConfig;
        _onRelocated = onRelocated;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;   // 不抢焦点
        Focusable = false;

        BuildContent();

        // 输入事件（LL 钩子在 UI 线程回调，直接改 UI 安全）
        _input.RawKeyEvent += OnRawKey;
        _input.RawMouseButtonEvent += OnRawMouseButton;
        _input.RawMouseWheelEvent += OnRawMouseWheel;

        // 拖动（DragMove 托管，松手即落盘）
        MouseLeftButtonDown += OnDragDown;

        // v0.1.30：锁键 LED 200ms 轮询
        _ledTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _ledTimer.Tick += (_, _) => RefreshLeds();
        RefreshLeds();
        _ledTimer.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowEx.HideFromAltTab(this);   // v0.1.50：Alt+Tab 不显示（BongoCat 同理）
        // 位置：记忆优先，否则默认右下角（猫的左上方，与猫不重叠）
        if (_config.MirrorPosX is double mx && _config.MirrorPosY is double my)
        {
            Left = mx;
            Top = my;
        }
        else
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - Width - 20;
            Top = wa.Bottom - Height - 160;  // 猫高 ~102 + 间隙
        }
        // clamp 进工作区（换分辨率防出屏，同 PetWindow 范式）
        var work = GetWorkArea();
        Left = Math.Max(work.X, Math.Min(Left, work.X + work.Width - Width));
        Top = Math.Max(work.Y, Math.Min(Top, work.Y + work.Height - Height));
    }

    // ================= 内容构建 =================

    private const double Unit = KeyW + KeyGap;                       // 1u 步进（24）
    private const double KbW = 15 * Unit - KeyGap;                   // 键盘画布宽（15u 行 = 358）
    private const double KbH = 5 * (KeyH + KeyGap) - KeyGap;         // 键盘画布高（5 行 = 118）
    private const double LedH = 15;                                  // LED 排高

    /// <summary>v0.1.36 修复：键盘改用 Canvas 绝对定位——旧 Grid 列+Margin 方案里 1.5u/2.25u 宽键
    /// （34px）远超 1u 列宽（24px），导致 Tab 盖 Q、Back/Enter 溢出截断（"键盘显示不完整"的真因）。</summary>
    private void BuildContent()
    {
        var kb = new Canvas { Width = KbW, Height = KbH };

        // ---- Row 0：Esc + F1-F12（保留分组间隙）----
        double r0 = 0;
        AddKey(kb, 0, ref r0, "Esc", 0x1B, 1.0);
        r0 = 2 * Unit;   // F1 前空 1u
        AddKey(kb, 0, ref r0, "F1", 0x70, 1.0); AddKey(kb, 0, ref r0, "F2", 0x71, 1.0); AddKey(kb, 0, ref r0, "F3", 0x72, 1.0); AddKey(kb, 0, ref r0, "F4", 0x73, 1.0);
        r0 += 0.5 * Unit; // F5 组间隙
        AddKey(kb, 0, ref r0, "F5", 0x74, 1.0); AddKey(kb, 0, ref r0, "F6", 0x75, 1.0); AddKey(kb, 0, ref r0, "F7", 0x76, 1.0); AddKey(kb, 0, ref r0, "F8", 0x77, 1.0);
        r0 = 11 * Unit;  // F9 组间隙
        AddKey(kb, 0, ref r0, "F9", 0x78, 1.0); AddKey(kb, 0, ref r0, "F10", 0x79, 1.0); AddKey(kb, 0, ref r0, "F11", 0x7A, 1.0); AddKey(kb, 0, ref r0, "F12", 0x7B, 1.0);

        // ---- Row 1：` 1..0 - = Back ----
        double r1 = 0;
        AddKey(kb, 1, ref r1, "`", 0xC0, 1.0);
        for (uint i = 0; i < 10; i++) AddKey(kb, 1, ref r1, ((char)('1' + i)).ToString(), 0x30 + i, 1.0);
        AddKey(kb, 1, ref r1, "-", 0xBD, 1.0); AddKey(kb, 1, ref r1, "=", 0xBB, 1.0);
        AddKey(kb, 1, ref r1, "Back", 0x08, 2.0);

        // ---- Row 2：Tab QWERTY ----
        double r2 = 0;
        AddKey(kb, 2, ref r2, "Tab", 0x09, 1.5);
        AddKey(kb, 2, ref r2, "Q", 0x51, 1.0); AddKey(kb, 2, ref r2, "W", 0x57, 1.0); AddKey(kb, 2, ref r2, "E", 0x45, 1.0); AddKey(kb, 2, ref r2, "R", 0x52, 1.0);
        AddKey(kb, 2, ref r2, "T", 0x54, 1.0); AddKey(kb, 2, ref r2, "Y", 0x59, 1.0); AddKey(kb, 2, ref r2, "U", 0x55, 1.0); AddKey(kb, 2, ref r2, "I", 0x49, 1.0);
        AddKey(kb, 2, ref r2, "O", 0x4F, 1.0); AddKey(kb, 2, ref r2, "P", 0x50, 1.0);
        AddKey(kb, 2, ref r2, "[", 0xDB, 1.0); AddKey(kb, 2, ref r2, "]", 0xDD, 1.0);
        AddKey(kb, 2, ref r2, "\\", 0xDC, 1.5);

        // ---- Row 3：Caps ASDF ----
        double r3 = 0;
        AddKey(kb, 3, ref r3, "Caps", 0x14, 1.75);
        AddKey(kb, 3, ref r3, "A", 0x41, 1.0); AddKey(kb, 3, ref r3, "S", 0x53, 1.0); AddKey(kb, 3, ref r3, "D", 0x44, 1.0); AddKey(kb, 3, ref r3, "F", 0x46, 1.0);
        AddKey(kb, 3, ref r3, "G", 0x47, 1.0); AddKey(kb, 3, ref r3, "H", 0x48, 1.0); AddKey(kb, 3, ref r3, "J", 0x4A, 1.0); AddKey(kb, 3, ref r3, "K", 0x4B, 1.0);
        AddKey(kb, 3, ref r3, "L", 0x4C, 1.0);
        AddKey(kb, 3, ref r3, ";", 0xBA, 1.0); AddKey(kb, 3, ref r3, "'", 0xDE, 1.0);
        AddKey(kb, 3, ref r3, "Enter", 0x0D, 2.25);

        // ---- Row 4：Shift ZXCV + 右 Shift ----
        double r4 = 0;
        AddKey(kb, 4, ref r4, "Shift", 0xA0, 2.25);        // L Shift
        AddKey(kb, 4, ref r4, "Z", 0x5A, 1.0); AddKey(kb, 4, ref r4, "X", 0x58, 1.0); AddKey(kb, 4, ref r4, "C", 0x43, 1.0); AddKey(kb, 4, ref r4, "V", 0x56, 1.0);
        AddKey(kb, 4, ref r4, "B", 0x42, 1.0); AddKey(kb, 4, ref r4, "N", 0x4E, 1.0); AddKey(kb, 4, ref r4, "M", 0x4D, 1.0);
        AddKey(kb, 4, ref r4, ",", 0xBC, 1.0); AddKey(kb, 4, ref r4, ".", 0xBE, 1.0); AddKey(kb, 4, ref r4, "/", 0xBF, 1.0);
        AddKey(kb, 4, ref r4, "Shift", 0xA1, 2.75);        // R Shift

        // ---- 鼠标（右侧，垂直居中于键盘）----
        var mouse = BuildMouse();
        mouse.VerticalAlignment = VerticalAlignment.Center;

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(kb);
        row.Children.Add(mouse);

        // ---- 锁键 LED 排（v0.1.30：Caps/Num/ScrLk 状态指示，紧贴键盘下方）----
        // v0.1.86：左缘与键盘左缘对齐（去掉 2px 偏移）；固定高 = LedH（消除行高差造成的底部空档）；
        //          单元间距收紧（10→7、点字距 4→3），让 LED 排与键盘读成一个整体。
        _ledCaps = MakeLed();
        _ledNum = MakeLed();
        _ledScrLk = MakeLed();
        var ledBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Height = LedH,
            Margin = new Thickness(0, 0, 0, 0),
        };
        ledBar.Children.Add(MakeLedUnit("CAPS", _ledCaps));
        ledBar.Children.Add(MakeLedUnit("NUM", _ledNum));
        ledBar.Children.Add(MakeLedUnit("SCR", _ledScrLk));

        // 整张外层（行 = row / LED 排 两行）
        var outerStack = new StackPanel();
        outerStack.Children.Add(row);
        outerStack.Children.Add(ledBar);

        // 爪子覆盖层：不参与命中测试，铺在键盘+鼠标上方
        var pawLayer = new Canvas { IsHitTestVisible = false };
        _rootGrid = new Grid();     // v0.1.39：提为字段，镜像开关改它的 RenderTransform
        _rootGrid.Children.Add(outerStack);
        _rootGrid.Children.Add(pawLayer);

        // 底盘
        var plate = new Border
        {
            Background = new SolidColorBrush(PlateFillC),
            BorderBrush = new SolidColorBrush(PlateStrokeC),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(PlatePad),
            Child = _rootGrid,
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.25 },
        };

        LoadPaws(pawLayer);
        SetMirrored(_config.KeyboardMirrorFlipped);   // v0.1.39：应用持久化的朝向设置

        Width = KbW + MouseW + MouseGap + PlatePad * 2 + 4;
        Height = KbH + LedH + PlatePad * 2 + 4 + PawRaisedExtra;  // 底部预留 PawRaisedExtra 容纳举起的爪（v0.1.86：36→18，收窄底部留白）
        Content = plate;
    }

    /// <summary>
    /// v0.1.39：键盘朝向。false = 朝我（Esc 在左上，用户视角，默认）；
    /// true = 朝猫（水平镜像，模拟猫坐在对面看到的布局）。
    /// 用 RenderTransform 而非 LayoutTransform——只翻渲染不影响测量，窗口尺寸不变，
    /// 键盘区和鼠标区整体左右对调；键帽层 + 爪子层都挂在 _rootGrid 下，会一并翻转。
    /// </summary>
    public void SetMirrored(bool flipped)
    {
        _rootGrid.RenderTransformOrigin = new Point(0.5, 0.5);
        _rootGrid.RenderTransform = new ScaleTransform(flipped ? -1.0 : 1.0, 1.0);
    }

    /// <summary>v0.1.36：Canvas 绝对定位添加键帽。x 为行内游标（自动累加 span 个 u），杜绝 Grid 列溢出重叠。</summary>
    /// <remarks>v0.1.86：两段式键帽——底座（<see cref="KeyBaseC"/>，满高）画在下、键帽面（矮 <see cref="KeyDepth"/>）画在上，
    /// 露出的暗色下缘即"前脸/键程"；键帽面叠一层透明→浅阴影渐变（<see cref="KeyShadeC"/>）模拟受光。
    /// 高亮动画仍作用在键帽面（存入 <see cref="_keyByVk"/> 的那个 Border），语义不变。</remarks>
    private void AddKey(Canvas canvas, int row, ref double x, string label, uint vk, double span)
    {
        double w = KeyW * span + KeyGap * (span - 1);
        double top = row * (KeyH + KeyGap);

        // 底座/前脸：满高、暗米棕，画在键帽面之下；键帽面比它矮 KeyDepth → 底部露出暗色下缘
        var keyBase = new Border
        {
            Width = w,
            Height = KeyH,
            Background = new SolidColorBrush(KeyBaseC),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(keyBase, x);
        Canvas.SetTop(keyBase, top);
        canvas.Children.Add(keyBase);

        // 键帽面内容：底部柔和阴影（透明→浅阴影渐变，不动画背景，因而不影响高亮换色）+ 居中标号
        var capContent = new Grid();
        capContent.Children.Add(new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x00, KeyShadeC.R, KeyShadeC.G, KeyShadeC.B), 0.00),
                    new GradientStop(Color.FromArgb(0x00, KeyShadeC.R, KeyShadeC.G, KeyShadeC.B), 0.55),
                    new GradientStop(Color.FromArgb(0x99, KeyShadeC.R, KeyShadeC.G, KeyShadeC.B), 1.00),
                },
            },
            CornerRadius = new CornerRadius(3),
            IsHitTestVisible = false,
        });
        capContent.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = label.Length > 2 ? 8 : 9,   // v0.1.44：18px 键配 8/9px 字
            FontWeight = FontWeights.SemiBold,     // v0.1.86：Medium→SemiBold，小字更清晰
            Foreground = new SolidColorBrush(KeyTextC),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });

        // 键帽面（高亮动画对象）：白面 + 浅棕描边，比底座矮 KeyDepth → 底部露出暗色前脸
        var key = new Border
        {
            Width = w,
            Height = KeyH - KeyDepth,
            Background = new SolidColorBrush(KeyFillC),
            BorderBrush = new SolidColorBrush(KeyEdgeC),
            BorderThickness = new Thickness(1.0),
            CornerRadius = new CornerRadius(4),
            Child = capContent,
            RenderTransform = new TranslateTransform(0, 0),   // v0.1.86：按下下沉（键程反馈）
            Tag = vk,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Canvas.SetLeft(key, x);
        Canvas.SetTop(key, top);
        canvas.Children.Add(key);
        x += span * Unit;
        // 同 vk 多键位（如左右 Shift 用不同 vk，不会撞；万一撞了保留第一个）
        _keyByVk.TryAdd(vk, key);
    }

    /// <summary>三键鼠标 + 滚轮区（v0.1.30：1:1.4 真实比例，外形更扁圆）</summary>
    /// <remarks>v0.1.86：更拟真——机身竖向渐变（受光）、左右键中缝竖线、按键/机身分界横线、
    /// 滚轮改为带描边的凹槽 + 中缝高光。仍是纯 WPF 绘制，无图片素材。</remarks>
    private FrameworkElement BuildMouse()
    {
        var canvas = new Canvas { Width = MouseW, Height = MouseH };

        // 鼠标外形（下半圆、上方小圆角）——v0.1.72：圆头朝下（灯灯"猫坐在对面"心智模型，
        // 用户视觉上鼠标头指向自己），把大半圆放在底边。
        // v0.1.86：填充改竖向渐变（上亮下暗）→ 一点体积感，不再是一块死白。
        var body = new Border
        {
            Width = MouseW,
            Height = MouseH,
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromRgb(0xFF, 0xFF, 0xFF), 0.00),
                    new GradientStop(Color.FromRgb(0xFF, 0xFF, 0xFF), 0.45),
                    new GradientStop(KeyShadeC, 1.00),
                },
            },
            BorderBrush = new SolidColorBrush(KeyEdgeC),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(12, 12, MouseW / 2, MouseW / 2),
        };
        Canvas.SetLeft(body, 0);
        Canvas.SetTop(body, 0);
        canvas.Children.Add(body);

        var lh = 0.45 * MouseH;   // 左右键半区高

        // v0.1.86：左右键中缝（竖线）+ 按键/机身分界（横线），让"三键"读得出来
        var vDiv = new Border
        {
            Width = 1.2,
            Height = lh - 2,
            Background = new SolidColorBrush(KeyEdgeC),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(vDiv, MouseW / 2 - 0.6);
        Canvas.SetTop(vDiv, 3);
        canvas.Children.Add(vDiv);

        var hDiv = new Border
        {
            Width = MouseW - 10,
            Height = 1.2,
            Background = new SolidColorBrush(KeyEdgeC),
            CornerRadius = new CornerRadius(1),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(hDiv, 5);
        Canvas.SetTop(hDiv, lh);
        canvas.Children.Add(hDiv);

        // 左键（0）/ 右键（1）——透明命中/高亮层，盖在机身上（不遮挡上面的分割线）
        var left = MakeMouseKey(0);
        Canvas.SetLeft(left, 0);
        Canvas.SetTop(left, 0);
        var right = MakeMouseKey(1);
        Canvas.SetLeft(right, MouseW / 2);
        Canvas.SetTop(right, 0);
        canvas.Children.Add(left);
        canvas.Children.Add(right);

        // 中键（2）命中层 + 滚轮（v0.1.86：带描边的凹槽 + 中缝高光，更像真滚轮）
        var mid = MakeMouseKey(2);
        var midW = MouseW - 6;
        Canvas.SetLeft(mid, 3);
        Canvas.SetTop(mid, lh + 1);
        canvas.Children.Add(mid);

        var wheel = new Border
        {
            Width = 7,
            Height = 18,
            Background = new SolidColorBrush(KeyBaseC),
            BorderBrush = new SolidColorBrush(KeyEdgeC),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3.5),
        };
        _wheelFlash = wheel;
        double wheelLeft = 3 + (midW - 7) / 2;
        Canvas.SetLeft(wheel, wheelLeft);
        Canvas.SetTop(wheel, lh + 2);
        canvas.Children.Add(wheel);

        var wheelLine = new Border
        {
            Width = 1,
            Height = 10,
            Background = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(0.5),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(wheelLine, wheelLeft + 3);
        Canvas.SetTop(wheelLine, lh + 6);
        canvas.Children.Add(wheelLine);

        return canvas;
    }

    private Border MakeMouseKey(int btn)
    {
        var halfW = MouseW / 2;   // v0.1.30：左/右键 上下分（lh 是垂直半高）
        var b = new Border
        {
            Width = halfW,
            Height = 0.45 * MouseH,
            // v0.1.35：不能用 Brushes.Transparent（系统冻结刷，AnimateBg 无法 BeginAnimation）——用非冻结透明刷
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            CornerRadius = new CornerRadius(6),
            Tag = btn,
        };
        _mouseKeys[btn] = b;
        return b;
    }

    // ================= 锁键 LED（v0.1.30）=================

    private Ellipse MakeLed()
    {
        var e = new Ellipse
        {
            Width = 7, Height = 7,
            Fill = new SolidColorBrush(LedOffC),
            Margin = new Thickness(0, 0, 3, 0),   // v0.1.86：点↔字距 4→3
            VerticalAlignment = VerticalAlignment.Center,
        };
        return e;
    }

    private FrameworkElement MakeLedUnit(string label, Ellipse led)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 7, 0) };   // v0.1.86：单元间距 10→7
        sp.Children.Add(led);
        sp.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 8,
            Foreground = new SolidColorBrush(KeyTextC),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return sp;
    }

    private void RefreshLeds()
    {
        // v0.1.59(P1)：窗口隐藏时直接早返回——SetAppMode/游戏模式只调 Hide() 不停 _ledTimer，
        // 隐藏态仍会 5Hz 跑 3×GetKeyState + new 3 个 SolidColorBrush + 遍历 _litKeys。
        // 早返回同时覆盖 Hide 后的首帧残留；Show 后下一次 tick（≤200ms）自然恢复刷新。
        if (!IsVisible) return;

        _ledCaps.Fill = new SolidColorBrush(IsLockOn(VK_CAPITAL) ? LedOnC : LedOffC);
        _ledNum.Fill = new SolidColorBrush(IsLockOn(VK_NUMLOCK) ? LedOnC : LedOffC);
        _ledScrLk.Fill = new SolidColorBrush(IsLockOn(VK_SCROLL) ? LedOnC : LedOffC);

        // v0.1.44：按键"按着才亮、松开就白"兜底——若 UP 事件丢失（热键路径偶发）键会一直亮。
        // 亮着超过 300ms 且期间没再输入（长按会持续刷新按下时刻）则强制还原，模拟真实键盘。
        if (_litKeys.Count > 0)
        {
            var now = DateTime.Now;
            List<Border>? stale = null;
            foreach (var (k, t) in _litKeys)
            {
                if ((now - t).TotalMilliseconds > 300)
                {
                    stale ??= new List<Border>();
                    stale.Add(k);
                }
            }
            if (stale is not null)
            {
                foreach (var k in stale)
                {
                    _litKeys.Remove(k);
                    // 鼠标键（Tag 是 int btn）还原为透明；键盘键还原为白
                    bool isMouse = k.Tag is int;
                    AnimateBg(k, isMouse ? Color.FromArgb(0, 0, 0, 0) : KeyFillC);
                    if (!isMouse) AnimateKeySink(k, false);   // v0.1.86：兜底还原时同步弹回
                }
            }
        }
    }

    // ================= 动画丝滑（v0.1.32）=================

    private const double AniMs = 80;
    private static readonly QuadraticEase AniEase = new() { EasingMode = EasingMode.EaseOut };

    /// <summary>Border 背景色 80ms 平滑过渡（按键高亮不再瞬变）</summary>
    /// <remarks>v0.1.35 修复：Brush 已冻结（如 Brushes.Transparent 系统刷）时 BeginAnimation 抛 InvalidOperationException，
    /// 直接换新刷（跳过动画）。鼠标键初始 Background 也必须是非冻结的 new SolidColorBrush，不能给 Brushes.Transparent。</remarks>
    private static void AnimateBg(Border b, Color target)
    {
        if (b.Background is not SolidColorBrush scb)
        {
            b.Background = new SolidColorBrush(target);
            return;
        }
        if (scb.IsFrozen)
        {
            b.Background = new SolidColorBrush(target);   // 冻结刷不能动画，替换
            return;
        }
        scb.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(target, TimeSpan.FromMilliseconds(AniMs)) { EasingFunction = AniEase });
    }

    /// <summary>v0.1.86：键帽按下下沉 KeyDepth（80ms 位移），松开弹回——配合露出的暗色前脸形成"键程"。</summary>
    private static void AnimateKeySink(Border key, bool down)
    {
        if (key.RenderTransform is TranslateTransform tt)
        {
            tt.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(down ? KeyDepth : 0.0, TimeSpan.FromMilliseconds(AniMs)) { EasingFunction = AniEase });
        }
    }

    /// <summary>Canvas.Top / Left 80ms 平滑位移（爪子拍下/回弹不再瞬切）</summary>
    private static void AnimateCanvasPos(FrameworkElement el, double top, double? left = null)
    {
        el.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(top, TimeSpan.FromMilliseconds(AniMs)) { EasingFunction = AniEase });
        if (left.HasValue) el.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(left.Value, TimeSpan.FromMilliseconds(AniMs)) { EasingFunction = AniEase });
    }

    // ================= 猫爪动画 =================

    /// <summary>爪子素材是否加载成功（UiProbe 断言用）</summary>
    public bool PawAssetsLoaded => _pawUp is not null && _pawDown is not null;

    /// <summary>键盘爪当前是否处于压下帧（UiProbe 断言用）</summary>
    public bool IsKeyboardPawDown => _pawDown is not null && _kbdPaw?.Source == _pawDown;

    /// <summary>v0.1.43：键盘爪当前悬停 x（UiProbe 断言"爪跟键走"）</summary>
    public double KeyboardPawLeftPos => _kbdPawTargetLeft;

    /// <summary>v0.1.43：鼠标爪当前悬停 x（UiProbe 断言"爪跟按钮走"）</summary>
    public double MousePawLeftPos => _mousePawTargetLeft;

    /// <summary>鼠标爪当前是否压住（UiProbe 断言用）</summary>
    public bool IsMousePawDown => _mousePawDownBtn >= 0;

    private static ImageSource? LoadPng(string sub, string name)
    {
        try
        {
            var path = System.IO.Path.Combine(Paths.Assets, sub, name);
            if (!System.IO.File.Exists(path)) return null;
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;   // 用完即断流
            bmp.StreamSource = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read);
            bmp.EndInit();
            bmp.Freeze();   // 跨线程安全 + 免拷贝渲染
            return (ImageSource)bmp;
        }
        catch { return null; }
    }

    private void LoadPaws(Canvas layer)
    {
        _pawUp = LoadPng("mirror", "paw_up.png");
        _pawDown = LoadPng("mirror", "paw_down.png");
        if (_pawUp is null || _pawDown is null) return;   // 素材缺失静默降级：只剩键位高亮

        // v0.1.72：猫坐在对面——爪子从"窗体下方"伸上来（举手位在底部），按完落回下方。
        // 不是从窗体顶部按下来。举手位常量 PawRaisedTop 重新定义为"键盘底边之外"。
        _kbdPaw = MakePawImage();
        Canvas.SetLeft(_kbdPaw, KeyboardPawLeft);
        Canvas.SetTop(_kbdPaw, PawRaisedTop);   // 初始举爪（在窗体底部之外）
        layer.Children.Add(_kbdPaw);

        _mousePaw = MakePawImage();
        Canvas.SetLeft(_mousePaw, MousePawLeft);
        Canvas.SetTop(_mousePaw, PawRaisedTop);   // v0.1.72：鼠标爪也从底部伸上来（猫统一从下方伸过来）
        layer.Children.Add(_mousePaw);
    }

    private Image MakePawImage()
    {
        var img = new Image { Width = PawSize, Height = PawSize, Source = _pawUp, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);   // 像素风禁平滑
        return img;
    }

    /// <summary>v0.1.44：敲键 → 键盘爪从"举手位"落到该键上拍一下，130ms 后举回——
    /// 平时爪完全在键盘上方之外不挡键（灯灯：不用键盘时爪子别盖住键盘）。</summary>
    private void TapKeyboardPaw(uint vk)
    {
        if (_kbdPaw is null || _pawDown is null) return;

        // 爪移到被按的键中心上方（Left 动画与 Top 动画并行）；同键连打只落不横移
        if (_keyByVk.TryGetValue(vk, out var key))
        {
            double center = Canvas.GetLeft(key) + key.Width / 2;
            double target = Math.Clamp(center - PawSize / 2, 0, KbW - PawSize);
            if (Math.Abs(target - _kbdPawTargetLeft) > 1)
            {
                _kbdPawTargetLeft = target;
                AnimateCanvasPos(_kbdPaw, KeyboardPawTop, _kbdPawTargetLeft);   // 滑过去 + 落到键面
            }
            else
            {
                AnimateCanvasPos(_kbdPaw, KeyboardPawTop);                        // 从举爪位直接落键面
            }
        }
        _kbdPaw.Source = _pawDown;
        AnimateCanvasPos(_kbdPaw, KeyboardPawTop + 2);   // 拍下（80ms 位移过渡）
        _kbdPawTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(130) };
        _kbdPawTimer.Tick -= OnKbdPawTimerTick;
        _kbdPawTimer.Tick += OnKbdPawTimerTick;
        _kbdPawTimer.Stop();
        _kbdPawTimer.Start();
    }

    private void OnKbdPawTimerTick(object? sender, EventArgs e)
    {
        ((System.Windows.Threading.DispatcherTimer)sender!).Stop();
        if (_kbdPaw is null) return;
        _kbdPaw.Source = _pawUp;
        // v0.1.44：举回"举手位"（键盘上方之外），不再悬停在键上挡键盘
        AnimateCanvasPos(_kbdPaw, PawRaisedTop, _kbdPawTargetLeft);
    }

    private void PressMousePaw(int btn)
    {
        if (_mousePaw is null || _pawDown is null) return;
        // v0.1.43：鼠标爪滑到被按的按钮上方（左/中/右 三键中心）再压下
        double btnCenter = btn switch
        {
            0 => KbW + MouseGap + MouseW / 4,              // 左键中心
            1 => KbW + MouseGap + MouseW / 4 * 3,          // 右键中心
            _ => KbW + MouseGap + MouseW / 2,              // 中键/滚轮中心
        };
        _mousePawTargetLeft = btnCenter - PawSize / 2;
        _mousePaw.Source = _pawDown;
        AnimateCanvasPos(_mousePaw, MousePawTop + 2, _mousePawTargetLeft);   // 落键面 + 压下
        _mousePawDownBtn = btn;
    }

    private void LiftMousePaw()
    {
        if (_mousePaw is null) return;
        _mousePaw.Source = _pawUp;
        // v0.1.44：举回（鼠标上方之外，不挡鼠标）
        AnimateCanvasPos(_mousePaw, PawRaisedTop, _mousePawTargetLeft);
        _mousePawDownBtn = -1;
    }

    // ================= 高亮逻辑 =================

    /// <summary>已注册的可视键数量（Esc+F 区 13 键 + 60% 主区 53 键 = 66；UiProbe 守护断言用）</summary>
    public int RegisteredKeys => _keyByVk.Count;

    /// <summary>v0.1.36：布局自检——任意两键帽矩形重叠返回 true（Grid 列溢出 bug 的回归守护）</summary>
    public bool LayoutHasOverlaps()
    {
        var rects = new List<(double L, double T, double R, double B)>();
        foreach (var b in _keyByVk.Values)
        {
            double l = Canvas.GetLeft(b), t = Canvas.GetTop(b);
            rects.Add((l, t, l + b.Width, t + b.Height));
        }
        for (int i = 0; i < rects.Count; i++)
            for (int j = i + 1; j < rects.Count; j++)
            {
                var a = rects[i]; var c = rects[j];
                bool ox = a.L < c.R - 0.5 && c.L < a.R - 0.5;
                bool oy = a.T < c.B - 0.5 && c.T < a.B - 0.5;
                if (ox && oy) return true;
            }
        return false;
    }

    /// <summary>v0.1.36：布局自检——最右键帽右缘（应 ≤ KbW=358，超出=溢出截断）</summary>
    public double LayoutMaxRight()
    {
        double max = 0;
        foreach (var b in _keyByVk.Values) max = Math.Max(max, Canvas.GetLeft(b) + b.Width);
        return max;
    }

    /// <summary>已注册的鼠标键数量（左/右/中；UiProbe 用）</summary>
    public int RegisteredMouseKeys => _mouseKeys.Count;

    /// <summary>模拟一次按键高亮/还原（UiProbe 回归用，不经过钩子）</summary>
    public void FlashKey(uint vk, bool down) => OnRawKey(vk, down);

    /// <summary>模拟一次鼠标键按下/松开（UiProbe 回归用——v0.1.35 冻结刷崩溃的守护断言）</summary>
    public void FlashMouse(int btn, bool down) => OnRawMouseButton(btn, down);

    private void OnRawKey(uint vk, bool down)
    {
        if (_keyByVk.TryGetValue(vk, out var key))
        {
            if (down)
            {
                _litKeys[key] = DateTime.Now;   // v0.1.44：记录按下时刻（兜底还原用）
                AnimateBg(key, KeyActiveC);
                AnimateKeySink(key, true);      // v0.1.86：按下下沉（键程）
            }
            else
            {
                _litKeys.Remove(key);
                AnimateBg(key, KeyFillC);       // 松开立即白（真实键盘观感）
                AnimateKeySink(key, false);     // v0.1.86：弹回
            }
        }
        if (down) TapKeyboardPaw(vk);   // v0.1.43：爪滑到该键上方拍下
    }

    private void OnRawMouseButton(int btn, bool down)
    {
        if (_mouseKeys.TryGetValue(btn, out var key))
        {
            if (down)
            {
                _litKeys[key] = DateTime.Now;   // v0.1.44：鼠标键同样兜底
                AnimateBg(key, Color.FromArgb(0xB0, KeyActiveC.R, KeyActiveC.G, KeyActiveC.B));
            }
            else
            {
                _litKeys.Remove(key);
                AnimateBg(key, Color.FromArgb(0, 0, 0, 0));   // v0.1.32：80ms 颜色过渡到半透明橙
            }
        }
        if (down) PressMousePaw(btn); else LiftMousePaw();
    }

    private void OnRawMouseWheel()
    {
        if (_wheelFlash is null) return;
        _wheelFlash.Background = new SolidColorBrush(KeyActiveC);
        _wheelTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        // v0.1.95（审计 P1-1 修复）：**订阅必须配平**——旧写法每次滚轮都 `Tick += 匿名 lambda` 且从不 `-=`，
        // 滚 N 次后每次滚动会同时执行 N 个处理器（无界增长的内存/CPU 泄漏）。
        // 改为命名方法 + 覆盖式订阅（与本函数下方 `_wheelPawTimer` 的既有写法一致）。
        _wheelTimer.Tick -= OnWheelFlashTick;
        _wheelTimer.Tick += OnWheelFlashTick;
        _wheelTimer.Stop();
        _wheelTimer.Start();

        // v0.1.29：滚轮 → 鼠标爪轻拍一下
        PressMousePaw(2);
        _wheelPawTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _wheelPawTimer.Tick -= OnWheelPawTimerTick;
        _wheelPawTimer.Tick += OnWheelPawTimerTick;
        _wheelPawTimer.Stop();
        _wheelPawTimer.Start();
    }

    /// <summary>滚轮高亮还原（命名方法，供 <see cref="OnRawMouseWheel"/> 覆盖式订阅）。</summary>
    private void OnWheelFlashTick(object? sender, EventArgs e)
    {
        _wheelFlash!.Background = new SolidColorBrush(KeyBaseC);   // v0.1.86：还原到滚轮底色（不再是描边色）
        _wheelTimer!.Stop();
    }

    /// <summary>v0.1.95：模拟一次滚轮（UiProbe 订阅配平回归用，不经过钩子）。
    /// 暴露为 public 是为了与既有 <see cref="FlashKey"/>/<see cref="FlashMouse"/> 探针钩子保持一致。</summary>
    public void FlashWheel() => OnRawMouseWheel();

    private void OnWheelPawTimerTick(object? sender, EventArgs e)
    {
        ((System.Windows.Threading.DispatcherTimer)sender!).Stop();
        LiftMousePaw();
    }

    // ================= 位置记忆 =================

    private void SaveMirrorPosition()
    {
        _config.MirrorPosX = Left;
        _config.MirrorPosY = Top;
        // v0.1.83（按 team-lead 裁决还原）：这是【委托边界】的最后防线 —— _saveConfig 是外部注入的 Action，
        // 当前注入的是永不抛的 PetWindow.SaveConfigFile，但委托边界不应假设具体实现，故保守保留 catch。
        try { _saveConfig(); } catch { }
    }

    // ================= 工作区（同 PetWindow 范式）=================

    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private System.Windows.Rect GetWorkArea()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref mi))
            {
                double sx = 1.0, sy = 1.0;
                var source = PresentationSource.FromVisual(this);
                if (source?.CompositionTarget is not null)
                {
                    sx = source.CompositionTarget.TransformFromDevice.M11;
                    sy = source.CompositionTarget.TransformFromDevice.M22;
                }
                return new System.Windows.Rect(
                    mi.rcWork.Left * sx, mi.rcWork.Top * sy,
                    (mi.rcWork.Right - mi.rcWork.Left) * sx, (mi.rcWork.Bottom - mi.rcWork.Top) * sy);
            }
        }
        return SystemParameters.WorkArea;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _input.RawKeyEvent -= OnRawKey;
        _input.RawMouseButtonEvent -= OnRawMouseButton;
        _input.RawMouseWheelEvent -= OnRawMouseWheel;
        _wheelTimer?.Stop();
        _kbdPawTimer?.Stop();
        _wheelPawTimer?.Stop();
        _ledTimer?.Stop();
    }
}
