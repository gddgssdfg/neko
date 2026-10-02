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

/// <summary>
/// 主宠物窗口：透明、无边框、无标题栏、置顶。
/// 承载 PetRenderer，处理左键抚摸 / 右键径向菜单 / 拖拽物理。
/// </summary>
/// <remarks>
/// v0.1.84（审计 P2-11「超长文件」）：本类已按内聚的功能区域拆成多个 <c>partial</c> 文件，便于维护与评审。
/// <para><b>⚠️ 拆分纪律：所有【字段】声明（含 <c>const</c> / <c>static readonly</c> / 事件 / 自动属性）一律保留在本文件。</b>
/// 原因：C# 语言规范未定义「跨多个 partial 文件时字段初始化器的执行顺序」，把字段挪到别的 partial 文件有引入
/// 顺序依赖 bug 的风险。因此拆分只移动【方法 / 属性访问器 / 事件处理器】，绝不移动字段。</para>
/// <para>拆分出去的区域（每个文件顶部均标注来源与用途）：</para>
/// <list type="bullet">
/// <item><description><c>PetWindow.EdgeSnap.cs</c> —— 边缘吸附 + 落体物理</description></item>
/// <item><description><c>PetWindow.Drag.cs</c> —— 拖拽 / 鼠标事件 / 抚摸 / 输入反应</description></item>
/// <item><description><c>PetWindow.Move.cs</c> —— 平滑位移 + 位置记忆</description></item>
/// <item><description><c>PetWindow.Followers.cs</c> —— 浮层跟随 + 气泡对话</description></item>
/// <item><description><c>PetWindow.Loop.cs</c> —— 主循环 + 眼神追踪 + 心情</description></item>
/// <item><description><c>PetWindow.GameMode.cs</c> —— 游戏模式（全屏低干扰）</description></item>
/// <item><description><c>PetWindow.Mirror.cs</c> —— 键鼠映射窗 + 双模态（work/slack）</description></item>
/// <item><description><c>PetWindow.Plugins.cs</c> —— 插件加载 + 径向菜单</description></item>
/// <item><description><c>PetWindow.Settings.cs</c> —— 运行时设置项（皮肤 / 热键 / 缩放 / 透明度…）</description></item>
/// </list>
/// </remarks>
public sealed partial class PetWindow : Window
{
    private readonly AppConfig _config;
    private readonly PetRenderer _renderer;
    private readonly PetStateMachine _stateMachine;
    private readonly UIApiImpl _uiApi;
    private readonly PluginHost _pluginHost;
    private readonly EventBusImpl _eventBus;
    private readonly System.Windows.Threading.DispatcherTimer _loopTimer;
    // v0.1.58(任务5)：主循环用单调毫秒钟（Environment.TickCount64，免系统时钟跳变/免分配）。
    // 每 tick 算真实 elapsed 再推进各计时器；DispatcherTimer 触发抖动只影响"何时评估"，不影响累计时长。
    private long _lastTickMs = Environment.TickCount64;

    /// <summary>UI 注册中心（托盘菜单动态重建用）</summary>
    public UIApiImpl UIApi => _uiApi;
    /// <summary>插件宿主（托盘展示已加载插件用）</summary>
    public PluginHost PluginHost => _pluginHost;
    /// <summary>配置（设置窗口读写）</summary>
    public AppConfig Config => _config;
    /// <summary>当前是否暂停动画</summary>
    public bool IsPaused { get; private set; }

    /// <summary>当前是否鼠标穿透（点击穿透模式）</summary>
    public bool IsMousePassthrough { get; private set; }

    /// <summary>当前缩放倍率（供托盘菜单显示/调整）</summary>
    public double CurrentScale => _config.Scale;

    private bool _dragging;
    private Point _dragOffset;
    private Point _dragStartPos; // v0.1.1：按下起点，用于「点击 vs 拖拽」判定（总位移，而非末帧速度）
    private Vector _dragVelocity;

    // ---- v0.1.59(P3)：闲置态系统调用按需化缓存（行为等价，只是少做无谓调用）----
    private Point? _lastCursorPos;          // UpdateEyeTracking：鼠标位置未变则跳过 GetCursorPosition
    private Point? _lastEmitCursorPos;      // EmitMouseMoved：位置未变则不 Emit（省插件回调）
    private DesktopPet.Core.Rect2? _cachedWorkArea; // SyncMirrorToCat/GetWorkArea：工作区缓存
    private double _lastWorkAreaLeft = double.NaN;  // 猫位置快照：未变则复用 _cachedWorkArea
    private double _lastWorkAreaTop = double.NaN;

    // ---- v0.1.59(⑩)：鼠标路过张望。鼠标进入猫附近半径 → 眼球偏移增益放大（好奇），离开则恢复 ----
    private bool _peekActive;                        // 当前是否处于"张望"半径内（边界切换时更新）
    private const double PeekRadius = 120.0;         // 触发张望的鼠标距离（DIP）
    private const double PeekEyeGain = 1.6;          // 张望时眼球偏移增益（在基础 ×2px 之上再放大）

    // ---- v0.1.59(⑨)：心情驱动动画。好感度等级 → 心情 → 动画帧率倍率（走/表情更精神）----
    private int _affectionLevel = -1;                // 上次读到的好感度等级（-1 = 未读）
    private long _nextAffectionReadMs;               // 下次读 score.json 的绝对时刻（60s 缓存，避免每 tick 读盘）
    private const long AffectionRefreshMs = 60_000;  // 好感度刷新周期 60s

    // ---- v0.1.39 平滑位移（根治"呼出键盘时猫会瞬移"）----
    private bool _moveAnimating;      // 位移动画进行中：期间吸附/落体逻辑不改写 Left/Top，避免打架
    private const double MirrorGap = 2.0;   // v0.1.43：猫 ↔ 映射窗间隙 6→2（猫几乎趴在键盘上，融合感更强）

    // ---- v0.1.91 走路位移（PetWindow.Loop.cs 的 UpdateWalk）----
    // _walkTravel：本次走动已实际位移(px)；_walkReversed：本次走动是否已掉过头。
    // 用途：猫贴在墙边时，随机方向有 50% 概率朝墙 → 若直接结束，会"抽一帧 + 白等 40~60s"。
    // 规则：撞墙时若「本次几乎没走」（< 半个身位）→ 掉头一次（猫撞墙会转身）；否则正常回 Idle。
    // 归零时机：非 Walk（WalkVelocityX==0）的那次 UpdateWalk。
    private double _walkTravel;
    private bool _walkReversed;

    private readonly GlobalInput _globalInput;
    private RadialMenu? _radialMenu;

    // 边缘吸附状态
    private bool _edgeAttached;
    private double _edgeLeft;   // 吸附时宠物窗口 Left
    private double _edgeTop;    // 吸附时宠物窗口 Top
    private int _edgeSide;      // 0=左,1=右,2=上,3=下

    // 鼠标事件广播节流
    private double _mouseEmitAccum;

    // 落体动画定时器（字段持有，避免多次抓放/脱离边缘导致定时器叠加——同一时刻只应有一个物理模拟）
    private System.Windows.Threading.DispatcherTimer? _fallTimer;

    // ---- v0.1.27 游戏模式（全屏低干扰，BongoCat 借鉴）----
    private readonly System.Windows.Threading.DispatcherTimer _gameTimer;
    private bool _gameFullscreenActive; // 当前处于"全屏应用在前台"状态
    private bool _hiddenByGame;         // 是游戏模式藏的猫（区别于老板键手动藏，退出全屏只恢复自己藏的）
    private bool _passthroughByGame;    // 是游戏模式开的穿透

    // ---- v0.1.28 键鼠映射窗（BongoCat 键盘映射核心视觉）----
    private InputMirrorWindow? _mirrorWindow;

    // ---- v0.1.91 冷启动优化：启动关键路径之外的一次性初始化标记 ----
    // ctor 里只做「记待办」，等首帧（ContentRendered）后由 InitializeAfterFirstFrame() 在
    // Background 优先级补做；这样 ShowMirror/chat-init 这些重活不再计入「进程→首帧」。
    private bool _startupMirrorPending;   // P1：启动需显示映射窗（AppMode==work 或 KeyboardMirrorEnabled）
    private bool _startupChatPending;     // P2：启动需初始化 chat/tracker（ActivityTracker + ChatScheduler）

    // ---- v0.1.40 气泡对话（像素云朵 + 猫主动闲聊）----
    private BubbleWindow? _bubble;
    private ActivityTracker? _activity;
    private ChatScheduler? _chatScheduler;

    // ---- v0.1.59(⑧) 抚摸爱心粒子浮层（独立窗口，随猫跟随）----
    private HeartParticleWindow? _hearts;

    /// <summary>v0.1.34：deferred 插件异步加载完成后触发（让 TrayManager 刷新托盘子菜单）</summary>
    public event Action? DeferredPluginsLoaded;

    // ---- v0.1.34 插件懒加载：延后加载的插件 ID 集合（联网/重资源） ----
    // 原则：径向菜单核心 12 个 + 托盘常用 5 个立即加载（用户右键猫能立刻看到槽位）；联网/截图类延后
    private static readonly IReadOnlySet<string> DeferredPluginIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "com.lantern.weather",     // 联网查天气
        "com.lantern.quote",       // 联网拉一言
        "com.lantern.wordcard",    // 联网拉单词
        "com.lantern.screenshot",  // 截图重操作
        "com.lantern.fortune",     // 联网签文
        "com.lantern.hourly",      // 文件读取+模板渲染
    };

    // ============ 物理/交互常量（集中管理，便于调参） ============
    private const double SnapThreshold = 8.0;   // 距边缘多近触发吸附(px)
    private const double DetachDistance = 40.0; // 鼠标离吸附点多远脱离(px)
    private const double PetClickThreshold = 8.0; // 按下→抬起总位移低于此视为抚摸而非拖拽(px)；6 太敏感，手抖会误判成拖拽触发落体
    private const int PetAnimationMs = 600;     // 抚摸动画时长
    private const int LoopIntervalMs = 50;      // 主循环间隔(≈20Hz)
    private const int IdleLoopIntervalMs = 100; // v0.1.57：纯闲置降频（位移/眨眼/打字/落体/拖拽 全部停 → 100ms），活动窗口恢复 50ms
    private const double MouseEmitIntervalMs = 200; // 鼠标事件广播节流
    private const double Gravity = 1800.0;      // 落体重力加速度(px/s²)
    private const double Bounce = 0.35;         // 触底反弹系数
    private const double GroundFriction = 0.6;  // v0.1.59(⑦)：触地时水平速度摩擦衰减系数
    private const double RestVelocity = 100.0;  // 低于此速度视为静止
    private const double MaxThrowSpeed = 900.0; // 抛出初速上限(px/s)，防止异常速度把宠物甩出屏幕

    // ---- v0.1.57 平滑位移缓动常量（供 PetWindow.Move.cs 的 BackEase 反解使用）----
    // 按「拆分不移动字段」约束（const 亦属字段）保留在本文件；方法实现见 PetWindow.Move.cs。
    private const double BackEaseMaxAmplitude = 1.0;
    private const int BackEaseScanSamples = 2048;
    // 给二分目标留 5% 余量：二进制搜索会尽量贴上限，若恰好顶到 capPx，真实窗口的亚像素/取整可能把
    // 采样值推过断言线。目标降到 0.95×cap 后实际过冲 ≈0.95cap，肉眼不可辨但稳定 ≤ cap。

    public PetWindow(AppConfig config)
    {
        _config = config;
        InitializeWindow();

        // 素材（v0.1.74：扫 neko-assets/<skinId>/skin.json 数据驱动加载；未注册的皮肤回退默认 pixel）
        if (!SkinRegistry.Exists(Paths.Assets, _config.Skin)) { _config.Skin = SkinRegistry.DefaultSkinId; }
        var atlas = SpriteAtlas.LoadSkin(Paths.Assets, _config.Skin);

        // 事件总线（宿主广播，插件订阅）
        _eventBus = new EventBusImpl();

        // UI 注册（径向菜单槽位）
        _uiApi = new UIApiImpl(_ => null);

        // 渲染器（缩放倍率跟随配置，保证窗口尺寸与绘制一致）
        _renderer = new PetRenderer(atlas) { Scale = config.Scale };

        // 宠物 API：桥接到渲染器和窗口
        var petApi = new PetApiImpl(
            _renderer,
            () => new Point(Left, Top),
            p => { Left = p.X; Top = p.Y; },
            () => GetWorkArea(),
            ui: Dispatcher);

        // 状态机
        _stateMachine = new PetStateMachine(petApi);
        // v0.1.94：闲时走动开关从配置接线（默认 false → 不自动走动；置 true 即恢复）
        _stateMachine.WalkEnabled = _config.WalkEnabled;

        // 能力
        var cap = new CapabilityApiImpl
        {
            Storage = new JsonPluginStorage(Paths.PluginDataDir("__host__")),
            Scheduler = new SchedulerImpl(),
            System = new SystemImpl(),
            Events = _eventBus,
        };

        // 插件宿主（v0.1.34：传 deferredIds 集合——联网/重资源插件延后到主窗口显示后异步加载，压冷启动）
        _pluginHost = new PluginHost(
            Paths.Plugins,
            new HashSet<string>(config.DisabledPlugins),
            hostVersion: typeof(PetWindow).Assembly.GetName().Version ?? new Version(1, 0, 0),
            dataRoot: Paths.Data,
            petApi: petApi,
            uiApi: _uiApi,
            capability: cap,
            deferredIds: DeferredPluginIds);

        // 主循环：低频推进状态机 + 帧动画
        _loopTimer = new System.Windows.Threading.DispatcherTimer();
        _loopTimer.Interval = TimeSpan.FromMilliseconds(LoopIntervalMs);
        _loopTimer.Tick += OnLoopTick;

        // 全局输入：键盘热键钩子（热键从 config.json 读取，BongoCat 借鉴的可配置化）
        _globalInput = new GlobalInput
        {
            MenuHotKey = ToHotKey(config.MenuHotKey),
            SecondaryHotKey = ToHotKey(config.TogglePetHotKey), // v0.1.26：显隐老板键
        };
        _globalInput.Install();
        _globalInput.HotKeyPressed += _ => ToggleRadialMenu();
        _globalInput.SecondaryHotKeyPressed += TogglePetVisible;

        // v0.1.27 附加热键（BongoCat 借鉴 Ctrl+Shift+方向键调透明度/缩放；
        // 默认改用 Ctrl+Alt+Shift 组合——Ctrl+Shift+方向键是编辑器选中文本/移行，全局吞键会毁掉打字）
        // 附加热键回调运行在 UI 线程（LL 钩子在安装线程的消息循环上回调），可直接碰窗口
        // v0.1.27 附加热键（BongoCat 借鉴 Ctrl+Shift+方向键调透明度/缩放；
        // 默认改用 Ctrl+Alt+Shift 组合——Ctrl+Shift+方向键是编辑器选中文本/移行，全局吞键会毁掉打字）
        // v0.1.33：改用 id 覆盖式注册（设置页改热键后 SetExtraHotKey 同 id 重注册即热更新，无需重启）
        // 附加热键回调运行在 UI 线程（LL 钩子在安装线程的消息循环上回调），可直接碰窗口
        _globalInput.RegisterExtraHotKey("opacity_up",   ToHotKey(config.OpacityUpHotKey),   () => SetOpacity(_config.Opacity + 0.1));
        _globalInput.RegisterExtraHotKey("opacity_down", ToHotKey(config.OpacityDownHotKey), () => SetOpacity(_config.Opacity - 0.1));
        _globalInput.RegisterExtraHotKey("scale_up",     ToHotKey(config.ScaleUpHotKey),     () => SetScale(_config.Scale + 0.4));
        _globalInput.RegisterExtraHotKey("scale_down",   ToHotKey(config.ScaleDownHotKey),   () => SetScale(_config.Scale - 0.4));
        _globalInput.RegisterExtraHotKey("reset_pos",    ToHotKey(config.ResetPosHotKey),    ResetPosition);

        // 游戏模式轮询（BongoCat 借鉴）：每秒检测前台是否全屏应用
        _gameTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _gameTimer.Tick += (_, _) => CheckGameMode();
        if (_config.GameModeEnabled) _gameTimer.Start();
        // 键鼠反应（BongoCat 键盘映射借鉴，v0.1.25 补齐鼠标）：
        // 打字/点击任意处时猫短暂瞪眼（300ms 节流由 GlobalInput 内部做）
        // 设置窗口可关闭（KeyboardReactionsEnabled=false）
        _globalInput.KeyPressed += OnInputReaction;
        _globalInput.MousePressed += OnInputReaction;

        // v0.1.30 双模态：work 模式启动即开映射窗 + 猫贴附；slack 模式启动只猫
        // v0.1.91 P1：映射窗创建（InputMirrorWindow ctor + Show 实测 ≈365ms、占冷启动 30%）**移出启动关键路径**——
        // 这里只记「待办」，等首帧上屏后由 InitializeAfterFirstFrame() 在 Background 优先级补做。
        // 运行时 SetAppMode("work") 仍同步调用 ShowMirror()（用户切模式须立即见效，不受此延后影响）。
        _startupMirrorPending = _config.AppMode == "work" || _config.KeyboardMirrorEnabled;

        // v0.1.40：气泡对话——Say 通道 + 活跃度跟踪 + 闲聊调度（开关可运行时切换）
        _renderer.SpeechRequested += (text, ms) => ShowBubble(text, ms);
        // 输入跟踪一直开（活跃度数据便宜且未来有用）；调度器按开关启停
        _globalInput.RawKeyEvent += OnTrackerKey;
        _globalInput.RawMouseButtonEvent += OnTrackerMouse;
        // v0.1.91 P2：chat/tracker 初始化（ActivityTracker.Load 读盘 + ChatScheduler 计时器 + config 落盘
        // 实测 ≈112ms、占冷启动 9%）**移出启动关键路径**——首条闲聊本就在 8~20 分钟后，无需现在付这笔钱。
        // 上面两行输入订阅保留在 ctor：成本低，且活跃度数据越早开始越准。运行时设置页切换仍走 ApplyChatEnabled 同步路径。
        _startupChatPending = _config.ChatEnabled;

        // 内容（NearestNeighbor：3.2 倍非整数缩放，用最近邻保像素锐利，否则像素风会糊）
        var host = new DrawingVisualHost(_renderer);
        RenderOptions.SetBitmapScalingMode(host, BitmapScalingMode.NearestNeighbor);
        Content = host;
        Background = Brushes.Transparent;
    }

    private void InitializeWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        // 固定显式尺寸（不用 SizeToContent：内容宿主默认测量为 0×0，会把窗口缩没）
        Width = SpriteAtlas.FrameW * _config.Scale;
        Height = SpriteAtlas.FrameH * _config.Scale;
        // v0.1.6：启动时应用 config.Opacity（持久化透明度）
        Opacity = Math.Clamp(_config.Opacity, 0.2, 1.0);
        // v0.1.26：位置记忆（BongoCat 借鉴）——有记录则恢复上次位置，无则默认右下角
        if (_config.PosX is double px && _config.PosY is double py)
        {
            Left = px;
            Top = py;
        }
        else
        {
            Left = SystemParameters.WorkArea.Right - Width - 20;
            // v0.1.1 fix：距底边 40px（之前 8px 太贴任务栏，肉眼几乎和系统托盘融在一起）
            Top = SystemParameters.WorkArea.Bottom - Height - 40;
        }

        // 输入
        MouseLeftButtonDown += OnMouseLeftDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftUp;
        MouseRightButtonDown += OnMouseRightDown;
    }

    /// <summary>
    /// 句柄创建后：把恢复/默认的位置 clamp 进当前显示器工作区（v0.1.26 位置记忆配套）。
    /// 处理换分辨率/拔显示器后猫"消失在屏幕外"的情况。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // v0.1.50：Alt+Tab 不显示（BongoCat 同理——非普通窗口）
        WindowEx.HideFromAltTab(this);
        var work = GetWorkArea();
        Left = Math.Max(work.X, Math.Min(Left, work.X + work.Width - Width));
        Top = Math.Max(work.Y, Math.Min(Top, work.Y + work.Height - Height));
    }

    /// <summary>
    /// v0.1.91 冷启动优化：首帧（ContentRendered）之后补做「不在启动关键路径」的初始化。
    /// <para>由 <c>App.OnStartup</c> 在 <see cref="Window.Show"/> 之后经 ContentRendered 钩子调用恰好一次。</para>
    /// <para>三块工作全部排入 <see cref="DispatcherPriority.Background"/>——排在渲染之后，**绝不抢首帧**；
    /// 延后期间用户可见行为不变（镜像窗/闲聊本就有各自的惰性时序，运行时切换仍走同步路径）。</para>
    /// </summary>
    public void InitializeAfterFirstFrame()
    {
        // ---- P1：镜像窗（InputMirrorWindow ctor+Show 实测 ≈365ms，占冷启 30%）----
        if (_startupMirrorPending)
        {
            _startupMirrorPending = false;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // 延后期间用户可能已切到 slack（SetAppMode）→ 不再补显，避免"刚关又弹"
                if (_config.AppMode == "work" || _config.KeyboardMirrorEnabled) ShowMirror();
            }), DispatcherPriority.Background);
        }

        // ---- P2：chat/tracker（ActivityTracker.Load 读盘 + ChatScheduler + config 落盘 实测 ≈112ms，占 9%）----
        if (_startupChatPending)
        {
            _startupChatPending = false;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // 若延后期间用户已在设置页动过开关（ApplyChatEnabled 已跑）→ 不重复建
                if (_config.ChatEnabled && _chatScheduler is null) ApplyChatEnabled(true);
            }), DispatcherPriority.Background);
        }

        // ---- R1：径向菜单预热（首次右键一次性 ≈80ms：ctor 扫 icons/ 解码 + 首次建窗 HWND）----
        // 见 PetWindow.Plugins.cs 的 PrewarmRadialMenu。只构建 + 预建句柄，全程不 Show、不闪现。
        Dispatcher.BeginInvoke(new Action(PrewarmRadialMenu), DispatcherPriority.Background);
    }

    /// <summary>显隐老板键（BongoCat 借鉴 Ctrl+Shift+B，Neko 默认 Ctrl+Shift+H）：一键藏猫/唤回</summary>
    public void TogglePetVisible()
    {
        if (IsVisible)
        {
            CloseRadialMenu();
            Hide();
        }
        else
        {
            Show();
            Activate();
        }
        UpdateMirrorVisibility(); // v0.1.28：老板键联动键鼠映射窗
    }

    private static readonly HashSet<string> ShellWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" }; // 桌面/任务栏不算全屏应用（供 PetWindow.GameMode.cs 的 IsForegroundFullscreen 使用）

    // ============ Win32 互操作（P/Invoke 与相关常量 / 结构体，集中保留在本文件） ============

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    private const int GWL_EXSTYLE = -20;
    private const int GWL_STYLE = -16;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_CAPTION = 0x00C00000;

    // ---- v0.1.27 游戏模式 Win32 ----
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int maxCount);

    // ---- 多显示器工作区（v0.1.26，BongoCat 借鉴）----
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    public void Dispose()
    {
        _loopTimer.Stop();
        _chatScheduler?.Stop();    // v0.1.40
        _globalInput.Dispose();
        CloseRadialMenu();
        // v0.1.91：R1 预热会在首帧后预建径向菜单 HWND（EnsureHandle），而 HideMenu 对不可见窗口直接返回、
        // 不会销毁句柄——这里显式 Close 一次，避免退出时残留一个顶层句柄。
        _radialMenu?.Close();
        _mirrorWindow?.Close(); // v0.1.28
        _bubble?.Close();        // v0.1.40
        _hearts?.Close();        // v0.1.59(⑧)
        _pluginHost.Dispose();
    }
}
