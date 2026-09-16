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
public sealed class PetWindow : Window
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

    /// <summary>设置暂停状态（托盘菜单调用）</summary>
    public void SetPaused(bool paused)
    {
        if (IsPaused == paused) return;
        IsPaused = paused;
        if (paused)
        {
            _renderer.SetMood(DesktopPet.Core.PetMood.Sleepy);
        }
        else
        {
            _renderer.SetMood(DesktopPet.Core.PetMood.Idle);
        }
    }

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
        if (_config.AppMode == "work" || _config.KeyboardMirrorEnabled) ShowMirror();

        // v0.1.40：气泡对话——Say 通道 + 活跃度跟踪 + 闲聊调度（开关可运行时切换）
        _renderer.SpeechRequested += (text, ms) => ShowBubble(text, ms);
        // 输入跟踪一直开（活跃度数据便宜且未来有用）；调度器按开关启停
        _globalInput.RawKeyEvent += OnTrackerKey;
        _globalInput.RawMouseButtonEvent += OnTrackerMouse;
        ApplyChatEnabled(_config.ChatEnabled);

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

    // ---- v0.1.28：键鼠映射窗开关（托盘/设置页调用）----

    /// <summary>显示映射窗（懒创建）。v0.1.39：创建后立刻摆到猫下方，避免开局在旧位置闪一帧再跳过去。</summary>
    private void ShowMirror()
    {
        if (_mirrorWindow is null)
        {
            _mirrorWindow = new InputMirrorWindow(_globalInput, _config, SaveConfigFile, OnMirrorRelocated);
            _mirrorWindow.Show();
        }
        else if (!_mirrorWindow.IsVisible)
        {
            _mirrorWindow.Show();
        }
        SyncMirrorToCat();   // v0.1.39：映射窗来就猫，不是猫去就映射窗
    }

    /// <summary>映射窗总开关：v0.1.30 联动 AppMode（开=work 模式，关=slack）</summary>
    public void SetKeyboardMirror(bool enabled) => SetAppMode(enabled ? "work" : "slack");

    /// <summary>
    /// v0.1.39 双模态：切换要真正改变猫的行为（此前只控制映射窗显隐，猫纹丝不动——灯灯点名的 bug）。
    ///   work  = 猫留在原地，键鼠映射窗贴到猫正下方（键盘以猫视角为主，猫趴键盘后）
    ///   slack = 收起映射窗，猫平滑滑到最近的屏幕边缘挂住（不挡视线）
    /// </summary>
    public void SetAppMode(string mode)
    {
        if (mode != "work" && mode != "slack") mode = "slack";
        _config.AppMode = mode;
        _config.KeyboardMirrorEnabled = mode == "work";
        try { ConfigManager.Save(Paths.ConfigFile, _config); } catch { }

        if (mode == "work")
        {
            if (_edgeAttached) DetachEdge();   // 挂着没法趴键盘，先让它下来
            ShowMirror();                       // 内部会 SyncMirrorToCat()
            // v0.1.48：BongoCat 化——切到 Work 姿态（猫伏低前探+前爪伸向下方键盘），不是 Sit
            _stateMachine.Play(DesktopPet.Core.PetState.Work);
        }
        else
        {
            _mirrorWindow?.Hide();
            _stateMachine.Play(DesktopPet.Core.PetState.Idle);  // v0.1.43：摸鱼模式站姿
            if (_config.SlackEdgeAttach) AttachToNearestEdge();
        }
    }

    /// <summary>
    /// v0.1.39：映射窗位置由猫派生——水平居中于猫、贴在猫正下方（键盘以猫视角为主）。
    /// 这是"呼出键盘时猫会瞬移"的根治：以前是猫去追映射窗的位置记忆，现在反过来映射窗来就猫。
    /// </summary>
    private void SyncMirrorToCat()
    {
        if (_config.AppMode != "work") return;
        if (_mirrorWindow is null || !_mirrorWindow.IsVisible) return;

        var work = GetWorkArea();
        double mx = Left + Width / 2 - _mirrorWindow.Width / 2;
        double my = Top + Height + MirrorGap;
        // 猫太靠下放不下键盘时，改挂到猫上方（不出屏优先于位置好看）
        if (my + _mirrorWindow.Height > work.Y + work.Height) my = Top - _mirrorWindow.Height - MirrorGap;
        mx = Math.Max(work.X, Math.Min(mx, work.X + work.Width - _mirrorWindow.Width));
        my = Math.Max(work.Y, Math.Min(my, work.Y + work.Height - _mirrorWindow.Height));
        MoveWindowIfNeeded(_mirrorWindow, mx, my);   // v0.1.82：同值短路，别再每 tick 空挪一次窗口
        // 同步位置记忆（只改内存不落盘）：否则下次切回 work 时映射窗会先在旧位置闪一帧再跳过来
        _config.MirrorPosX = mx;
        _config.MirrorPosY = my;
    }

    /// <summary>v0.1.39：键盘朝向镜像（设置页调用，即时生效 + 持久化）。false=朝我，true=朝猫</summary>
    public void SetKeyboardMirrorFlipped(bool flipped)
    {
        _config.KeyboardMirrorFlipped = flipped;
        _mirrorWindow?.SetMirrored(flipped);   // 映射窗未创建时忽略，创建时会自己读配置
        try { ConfigManager.Save(Paths.ConfigFile, _config); } catch { }
    }

    /// <summary>
    /// v0.1.39：用户主动拖了键鼠映射窗 → 猫平滑滑到键盘上方（不再是瞬移）。
    /// 平时是映射窗跟着猫，只有用户主动挪键盘时才反向带猫。
    /// </summary>
    private void OnMirrorRelocated()
    {
        if (_config.AppMode != "work") return;
        if (_mirrorWindow is null || !_mirrorWindow.IsVisible) return;
        if (_dragging) return;   // 用户正在拖猫，别抢方向盘
        AnimateMoveTo(
            _mirrorWindow.Left + (_mirrorWindow.Width - Width) / 2,
            _mirrorWindow.Top - Height - MirrorGap, 220, MoveStyle.Walk);   // v0.1.57：走路缓动（轻微 overshoot）
    }

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

    // v0.1.57(fix)：BackEase.EaseOut 峰值过冲与 Amplitude 的关系不是注释里旧公式 4a³/(27(a+1)²)
    // （那是经典 easeOutBack 模型）。WPF 实际实现为 EaseOut(t)=1-EaseIn(1-t)、EaseInCore=t³−A·t·sin(πt)，
    // 实测 A=1 时过冲比例 ≈0.379（比旧公式大约 10 倍）。因此不再用解析式反解，
    // 而是用 BackEase 对象本身扫 Ease(t) 实测峰值过冲比例，再二分求出“过冲px ≤ capPx”的最大 Amplitude。
    private const double BackEaseMaxAmplitude = 1.0;
    private const int BackEaseScanSamples = 2048;
    // 给二分目标留 5% 余量：二进制搜索会尽量贴上限，若恰好顶到 capPx，真实窗口的亚像素/取整可能把
    // 采样值推过断言线。目标降到 0.95×cap 后实际过冲 ≈0.95cap，肉眼不可辨但稳定 ≤ cap。

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

    /// <summary>
    /// v0.1.39：所有浮层都以猫为锚点跟随（灯灯要求"所有东西都最好能绑定猫的位置去生成，不要乱飘"）。
    /// 覆盖拖拽 / 落体 / 边缘吸附 / 平滑位移 全部位移来源。
    /// </summary>
    private void SyncFollowers()
    {
        if (_radialMenu is { IsVisible: true })
            _radialMenu.MoveTo(Left + Width / 2, Top + Height / 2);
        SyncMirrorToCat();
        SyncBubblePosition();
        SyncHeartPosition();   // v0.1.59(⑧)：爱心浮层随猫（头部锚点）
    }

    /// <summary>
    /// v0.1.82：**浮层窗口定位的同值短路**。
    /// <para>为什么需要：WPF 的 <c>Window.Left/Top</c> setter **不做同值判断** —— 只要赋值就走一遍
    /// Win32 `SetWindowPos`（会触发窗口重排/合成）。而主循环是 20Hz，<see cref="SyncFollowers"/>
    /// 每 tick 都会调下面三个 Sync：猫不动的时候，键盘映射窗（work 模式下是个 600×200 的分层窗口）
    /// 每秒被无意义地"挪"20 次。这里比上一次的位置，没变就直接返回。</para>
    /// <para>容差 0.01 DIP：小于一个设备像素的差异不补，避免浮点噪声导致短路失效。</para>
    /// </summary>
    private static void MoveWindowIfNeeded(Window w, double left, double top)
    {
        if (Math.Abs(w.Left - left) < 0.01 && Math.Abs(w.Top - top) < 0.01) return;
        w.Left = left;
        w.Top = top;
    }

    /// <summary>v0.1.59(⑧)：爱心浮层对齐到猫头顶上方——浮层中心 = 猫头中心略上。</summary>
    private void SyncHeartPosition()
    {
        if (_hearts is not { IsVisible: true }) return;
        // 头部锚点约在窗口上方 1/4 处（眼球层 HEAD_CY≈11/32）；浮层以自己的中心对齐该点
        double anchorX = Left + Width / 2;
        double anchorY = Top + Height * 0.30;
        MoveWindowIfNeeded(_hearts,
            anchorX - _hearts.Width / 2,
            anchorY - _hearts.Height / 2);
    }

    /// <summary>
    /// v0.1.40：气泡贴猫头顶上方居中（尾巴朝下指向猫），随猫移动。
    /// 由 SyncFollowers 每帧调用，覆盖拖拽/落体/吸附/平滑位移所有位移来源。
    /// </summary>
    private void SyncBubblePosition()
    {
        if (_bubble is not { IsVisible: true }) return;
        MoveWindowIfNeeded(_bubble,
            Left + Width / 2 - _bubble.Width / 2,
            Top - _bubble.Height - 4);
    }

    /// <summary>v0.1.40：插件 Say → 像素云朵气泡（所有插件通知统一走这里；开关关闭则静默）</summary>
    private void ShowBubble(string text, int durationMs)
    {
        if (!_config.ChatEnabled) return;
        _bubble ??= new BubbleWindow();
        _bubble.ShowBubble(text, durationMs);
        SyncBubblePosition();   // 立即定位（不等主循环下一帧）
    }

    /// <summary>v0.1.40：运行时切换气泡对话（设置页调用）。关=停调度+清气泡；开=建 tracker/调度器。</summary>
    public void ApplyChatEnabled(bool enabled)
    {
        _config.ChatEnabled = enabled;
        try { ConfigManager.Save(Paths.ConfigFile, _config); } catch { }
        if (!enabled)
        {
            _chatScheduler?.Stop();
            _bubble?.Dismiss();
            return;
        }
        _activity ??= new ActivityTracker(System.IO.Path.Combine(Paths.Data, "host", "activity.json"));
        _chatScheduler ??= new ChatScheduler(
            ShowBubble, _activity,
            System.IO.Path.Combine(Paths.Data, "plugins", "com.lantern.affection", "score.json"),
            _config.ChatRestMinutes,
            _config.CatName, _config.OwnerTitle);   // v0.1.41：注入称呼/猫名到台词模板
        _chatScheduler.Start();
    }

    /// <summary>v0.1.41：设置页改了称呼/猫名后调用——重建调度器使新值生效（空 = 兜底默认）</summary>
    public void RebuildChatScheduler()
    {
        if (!_config.ChatEnabled) return;
        _chatScheduler?.Stop();
        _chatScheduler = null;
        ApplyChatEnabled(true);
    }

    /// <summary>v0.1.40：键鼠输入 → 活跃度统计（闲聊调度数据源）</summary>
    private void OnTrackerKey(uint vk, bool down)
    {
        if (down) _activity?.NotifyInput();
    }

    private void OnTrackerMouse(int btn, bool down)
    {
        if (down) _activity?.NotifyInput();
    }

    /// <summary>老板键藏猫时联动藏映射窗；唤回时若功能开着则一并恢复</summary>
    private void UpdateMirrorVisibility()
    {
        if (_mirrorWindow is null) return;
        if (IsVisible && _config.KeyboardMirrorEnabled) _mirrorWindow.Show();
        else _mirrorWindow.Hide();
    }

    private void SaveConfigFile()
    {
        try { ConfigManager.Save(Paths.ConfigFile, _config); } catch { }
    }

    /// <summary>v0.1.74：运行时切换皮肤（支持任意已注册皮肤，含用户自制；未知 id 回退默认 pixel）。</summary>
    public void SetSkin(string skin)
    {
        if (!SkinRegistry.Exists(Paths.Assets, skin)) skin = SkinRegistry.DefaultSkinId;

        // v0.1.82：把「素材是否需要重载」与「是否要套用推荐缩放」解耦。
        // 陷阱（v0.1.81 踩过）：旧代码在「已是同一皮肤」时直接 return，导致
        // 设置页对「已选中项」也触发回调时（鼠标点击无条件调 onChange），推荐缩放不重套——
        // 用户手动把缩放调回 2 后再点 hd，应被拉回作者的推荐值 4，却仍是 2。
        bool atlasChanged = _config.Skin != skin || _renderer.Atlas.Skin != skin;
        if (!atlasChanged)
        {
            // 同一皮肤被重复选择：不重载素材，但若作者声明了推荐缩放且当前值不同，仍套用。
            // RecommendedScale <= 0 的皮肤（pixel/soft）→ 完全无副作用（不改缩放、不写配置）。
            double rec = _renderer.Atlas.RecommendedScale;
            if (rec > 0 && Math.Abs(_config.Scale - rec) > 1e-9) SetScale(rec);
            return;
        }

        _config.Skin = skin;
        var newAtlas = SpriteAtlas.LoadSkin(Paths.Assets, skin);
        _renderer.SetAtlas(newAtlas);
        _stateMachine.Play(DesktopPet.Core.PetState.Idle);   // 强制从新 atlas 重拉（避免 _animator 缓存旧 SpriteState）
        // v0.1.81：皮肤可声明推荐缩放（recommendedScale）。>0 时自动套用——
        // 否则高清皮肤（源图 256px）会被缩进 32×Scale 的逻辑框，用户必须手动把缩放开到 4 才 1:1。
        // SetScale 内部已 clamp(1–8) 并落盘（此时 _config.Skin 已是新值，故 Save 也带上皮肤）；
        // 未声明推荐缩放时保持用户缩放不动，只需在末尾落盘皮肤。
        if (newAtlas.RecommendedScale > 0)
            SetScale(newAtlas.RecommendedScale);
        else
            SaveConfigFile();
    }

    /// <summary>v0.1.33：运行时改附加热键（按 id 重注册即热更新，无需重启）</summary>
    public void SetExtraHotKey(string id, bool ctrl, bool alt, bool shift, string key)
    {
        // 1) 写回 config 对应字段
        var hk = new HotKeyConfig { Ctrl = ctrl, Alt = alt, Shift = shift, Key = string.IsNullOrWhiteSpace(key) ? "" : key[..1].ToUpperInvariant() };
        switch (id)
        {
            case "opacity_up":   _config.OpacityUpHotKey = hk; break;
            case "opacity_down": _config.OpacityDownHotKey = hk; break;
            case "scale_up":     _config.ScaleUpHotKey = hk; break;
            case "scale_down":   _config.ScaleDownHotKey = hk; break;
            case "reset_pos":    _config.ResetPosHotKey = hk; break;
            default: return;
        }
        try { ConfigManager.Save(Paths.ConfigFile, _config); } catch { }
        // 2) 重注册（按 id 覆盖，GlobalInput 同 id 二次注册会替换）
        var hotkey = ToHotKey(hk);
        Action cb = id switch
        {
            "opacity_up" => () => SetOpacity(_config.Opacity + 0.1),
            "opacity_down" => () => SetOpacity(_config.Opacity - 0.1),
            "scale_up" => () => SetScale(_config.Scale + 0.4),
            "scale_down" => () => SetScale(_config.Scale - 0.4),
            "reset_pos" => ResetPosition,
            _ => () => { },
        };
        _globalInput.RegisterExtraHotKey(id, hotkey, cb);
    }

    /// <summary>位置落盘（BongoCat 位置记忆）：拖拽松手 / 落体静止 / 缩放后调用，启动时恢复</summary>
    private void SavePosition()
    {
        _config.PosX = Left;
        _config.PosY = Top;
        try { ConfigManager.Save(Paths.ConfigFile, _config); } catch { }
    }

    /// <summary>回到默认位置（右下角）：托盘调用；清除记忆，避免下次启动又回到旧位置</summary>
    public void ResetPosition()
    {
        _config.PosX = null;
        _config.PosY = null;
        Left = SystemParameters.WorkArea.Right - Width - 20;
        Top = SystemParameters.WorkArea.Bottom - Height - 40;
        try { ConfigManager.Save(Paths.ConfigFile, _config); } catch { }
    }

    // ============================ v0.1.27 游戏模式（BongoCat 借鉴） ============================

    /// <summary>设置游戏模式（设置窗口/托盘调用）：enabled + 动作（hide=藏猫 / through=穿透）</summary>
    public void SetGameMode(bool enabled, string action)
    {
        _config.GameModeEnabled = enabled;
        _config.GameModeAction = string.IsNullOrWhiteSpace(action) ? "hide" : action;
        // 运行中切换：先恢复现场再按新状态来，避免"藏了没人还"
        if (_gameFullscreenActive)
        {
            _gameFullscreenActive = false;
            RestoreFromGame();
        }
        if (enabled && !_gameTimer.IsEnabled) _gameTimer.Start();
        if (!enabled) _gameTimer.Stop();
        try { ConfigManager.Save(Paths.ConfigFile, _config); } catch { }
    }

    /// <summary>每秒轮询：前台是全屏应用 → 低干扰（藏猫/穿透）；退出全屏 → 恢复现场</summary>
    private void CheckGameMode()
    {
        var fs = IsForegroundFullscreen();
        if (fs && !_gameFullscreenActive)
        {
            _gameFullscreenActive = true;
            if (_config.GameModeAction == "through")
            {
                _passthroughByGame = !IsMousePassthrough;
                if (_passthroughByGame) SetMousePassthrough(true);
            }
            else
            {
                _hiddenByGame = IsVisible; // 老板键先藏了就不代管，退出全屏不抢着显示
                if (_hiddenByGame) { CloseRadialMenu(); Hide(); }
            }
            _mirrorWindow?.Hide(); // v0.1.28：游戏模式联动——映射窗也藏（游戏时它纯属干扰）
            _hearts?.Dismiss();    // v0.1.59(⑧)：游戏模式也收起爱心浮层
        }
        else if (!fs && _gameFullscreenActive)
        {
            _gameFullscreenActive = false;
            RestoreFromGame();
        }
    }

    private void RestoreFromGame()
    {
        if (_config.GameModeAction == "through")
        {
            if (_passthroughByGame) SetMousePassthrough(false);
            _passthroughByGame = false;
        }
        else
        {
            if (_hiddenByGame) Show();
            _hiddenByGame = false;
        }
        // v0.1.28：退出全屏恢复映射窗（猫可见且功能开着才还）
        if (IsVisible && _config.KeyboardMirrorEnabled) _mirrorWindow?.Show();
    }

    private static readonly HashSet<string> ShellWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" }; // 桌面/任务栏不算全屏应用

    /// <summary>前台窗口是否为全屏应用：无标题栏 + 矩形盖满猫所在显示器 ≥98%</summary>
    private bool IsForegroundFullscreen()
    {
        var fg = GetForegroundWindow();
        var self = new WindowInteropHelper(this).Handle;
        if (fg == IntPtr.Zero || fg == self) return false;

        var buf = new System.Text.StringBuilder(256);
        _ = GetClassName(fg, buf, 256);
        if (ShellWindowClasses.Contains(buf.ToString())) return false;

        // 有标题栏 = 普通窗口（最大化也带 caption），排除
        if ((GetWindowLong(fg, GWL_STYLE) & WS_CAPTION) == WS_CAPTION) return false;

        if (!GetWindowRect(fg, out var r)) return false;
        var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(self, MONITOR_DEFAULTTONEAREST), ref mi)) return false;
        return CoversMonitor(r.Left, r.Top, r.Right, r.Bottom,
            mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom);
    }

    /// <summary>全屏判定纯逻辑（公开静态，供 UiProbe 回归）：窗口矩形与显示器的交集 ≥ 98% 显示器面积。
    /// 用交集而非窗口面积——最小化窗口坐标是 -32000 偏移，交集法天然排除。</summary>
    public static bool CoversMonitor(int l, int t, int r, int b, int ml, int mt, int mr, int mb, double ratio = 0.98)
    {
        var ix = Math.Max(0, Math.Min(r, mr) - Math.Max(l, ml));
        var iy = Math.Max(0, Math.Min(b, mb) - Math.Max(t, mt));
        var mon = (double)(mr - ml) * (mb - mt);
        return mon > 0 && ix * iy >= mon * ratio;
    }

    /// <summary>配置键名 → VK 码：单字符按字面；支持方向键/翻页键名（热键用箭头不占字母）</summary>
    private static uint ToVk(string? name)
    {
        if (string.IsNullOrEmpty(name)) return 0x4E; // 缺省 N
        if (name.Length == 1) return (uint)char.ToUpperInvariant(name[0]);
        switch (name.ToLowerInvariant())
        {
            case "up": return 0x26;
            case "down": return 0x28;
            case "left": return 0x25;
            case "right": return 0x27;
            case "home": return 0x24;
            case "end": return 0x23;
            case "pgup": return 0x21;
            case "pgdn": return 0x22;
            default: return 0x4E;
        }
    }

    private void OnLoopTick(object? sender, EventArgs e)
    {
        // v0.1.58(任务5)：真实 elapsed（单调 ms 差）。delta 是本次 tick 到上次 tick 的真实墙钟间隔，
        // 下游所有计时器按 delta 累进/累减——tick 频率抖动不再放大或缩短动画时长。
        var nowMs = Environment.TickCount64;
        var deltaMs = Math.Max(0L, nowMs - _lastTickMs);
        _lastTickMs = nowMs;
        var delta = TimeSpan.FromMilliseconds(deltaMs);

        _stateMachine.Tick(delta);

        // ---- v0.1.59(⑨)：心情驱动动画（好感度等级 → 帧率倍率，60s 缓存免每 tick 读盘）----
        UpdateMoodFromAffection();

        if (!IsPaused) _renderer.Tick(delta);

        // ---- 眼神追踪：全局鼠标方向 → 眼球偏移（宿主渲染层反馈） ----
        UpdateEyeTracking();

        // ---- 边缘吸附：贴边悬挂检测 ----
        UpdateEdgeSnap();

        // ---- 广播鼠标事件（低频节流，供插件订阅） ----
        EmitMouseMoved(delta);

        // ---- v0.1.39：浮层跟随猫（径向菜单 + 键鼠映射窗都以猫为锚点）----
        SyncFollowers();

        // v0.1.57：根据"活动窗口"动态调整主循环频率（纯闲置 50→100ms，瞬时活动立即唤醒 100→50ms）
        UpdateLoopFrequency();
    }

    /// <summary>v0.1.57：活动窗口检测——任一持续性交互或瞬时动画进行中均视为"活动"，保持 50ms tick 灵敏。</summary>
    private bool IsBusyActiveWindow() =>
        _dragging ||
        _moveAnimating ||
        _fallTimer is not null ||
        _renderer.HasTransientVisual ||
        _stateMachine.Current is PetState.Walk or PetState.Pet or PetState.Drag or PetState.Fall;

    /// <summary>v0.1.57：根据 IsBusyActiveWindow 调整主循环频率。仅在区间实际变化时改写 Interval（避免反复重启定时器）。</summary>
    private void UpdateLoopFrequency()
    {
        var desired = IsBusyActiveWindow() ? LoopIntervalMs : IdleLoopIntervalMs;
        var current = (int)_loopTimer.Interval.TotalMilliseconds;
        if (desired != current)
            _loopTimer.Interval = TimeSpan.FromMilliseconds(desired);
    }

    /// <summary>v0.1.59(⑨)：心情驱动动画——按好感度等级（60s 缓存）映射到 PetRenderer 心情，
    /// 影响帧率倍率（越亲密越精神）。等级越高 → Happy（1.3×），低等级 → Idle（1.0×）。
    /// 暂停时不覆盖（SetPaused 已设 Sleepy，暂停优先）。</summary>
    private void UpdateMoodFromAffection()
    {
        if (IsPaused) return;   // 暂停态心情由 SetPaused 掌管（Sleepy），不在此覆盖

        var nowMs = Environment.TickCount64;
        if (nowMs < _nextAffectionReadMs) return;   // 60s 缓存：未到点不读盘
        _nextAffectionReadMs = nowMs + AffectionRefreshMs;

        var level = AffectionReader.ReadLevel(
            System.IO.Path.Combine(Paths.Data, "plugins", "com.lantern.affection", "score.json"));
        if (level == _affectionLevel) return;   // 等级未变 → 不重设心情（避免无谓刷新）
        _affectionLevel = level;

        // 映射：好感度越高越精神。L2 亲密 / L3 挚友 → Happy（帧率 1.3×）；其余 → Idle（1.0×）
        _renderer.SetMood(level >= 2 ? DesktopPet.Core.PetMood.Happy : DesktopPet.Core.PetMood.Idle);
    }

    /// <summary>v0.1.57：输入钩子触发时立即唤醒一次主循环（间隔回 50ms + 当帧 OnLoopTick）。
    /// 兼顾"input → immediate response 不延迟"约束。</summary>
    private void RequestActiveLoop()
    {
        if ((int)_loopTimer.Interval.TotalMilliseconds != LoopIntervalMs)
            _loopTimer.Interval = TimeSpan.FromMilliseconds(LoopIntervalMs);
        try { OnLoopTick(this, EventArgs.Empty); }
        catch { /* 输入回调里吞异常，保持 UI 线程响应 */
        }
    }

    /// <summary>眼神追踪：计算宠物头部 → 鼠标的方向向量，驱动眼球偏移</summary>
    private void UpdateEyeTracking()
    {
        var cursor = GlobalInput.GetCursorPosition();
        // v0.1.59(P3)：鼠标位置未变且无瞬时视觉（眨眼/打字晃动等）时，眼球偏移必然不变 → 跳过计算。
        // 眼神追踪本身依赖鼠标移动，鼠标不动时结果必然相同，行为等价。
        if (_lastCursorPos == cursor && !_renderer.HasTransientVisual) return;
        _lastCursorPos = cursor;

        // 宠物头部参考点：窗口中心（眼球层锚点在头部 17,11 附近，取窗口中心近似）
        var head = new Point(Left + Width / 2, Top + Height / 2);

        var dx = cursor.X - head.X;
        var dy = cursor.Y - head.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1) return;

        // ---- v0.1.59(⑩) 鼠标路过张望：鼠标进入猫附近半径内 → 眼球偏移增益放大（好奇）----
        UpdatePeek(dx, dy, len);

        // 归一化方向（限幅 ±1），渲染器内部再 ×2px（张望时再 ×增益）
        var norm = new Vector(dx / len, dy / len);
        _renderer.SetEyeOffset(norm, _peekActive ? PeekEyeGain : 1.0);
    }

    /// <summary>v0.1.59(⑩)：鼠标进入/离开猫附近半径时做一次边界响应（好奇张望）。
    /// 只在「进入 / 离开」的边界切换，避免每 tick 触发状态切换。</summary>
    private void UpdatePeek(double dx, double dy, double len)
    {
        bool near = len <= PeekRadius;
        if (near == _peekActive) return;   // 边界未变化 → 不动（节流）
        _peekActive = near;
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
        if (_dragging || _moveAnimating || _fallTimer is not null)
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

    /// <summary>低频节流广播鼠标全局位置（供插件订阅 mouse:moved）</summary>
    private void EmitMouseMoved(TimeSpan delta)
    {
        _mouseEmitAccum += delta.TotalMilliseconds;
        if (_mouseEmitAccum >= MouseEmitIntervalMs)
        {
            _mouseEmitAccum = 0;
            var p = GlobalInput.GetCursorPosition();
            // v0.1.59(P3)：位置未变则不 Emit——闲置态鼠标不动时省掉插件的 mouse:moved 回调。行为等价。
            if (_lastEmitCursorPos == p) return;
            _lastEmitCursorPos = p;
            _eventBus.Emit(DesktopPet.Core.PetEvents.MouseMoved, new MouseMovedArgs(p.X, p.Y));
        }
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

    /// <summary>配置的热键 → GlobalInput 的 HotKey（键名转 VK 码，支持方向键名）</summary>
    private static HotKey ToHotKey(HotKeyConfig c)
    {
        return new HotKey { Ctrl = c.Ctrl, Alt = c.Alt, Shift = c.Shift, Key = ToVk(c.Key) };
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
        catch { }
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

    /// <summary>切换径向菜单开关（全局热键调用）</summary>
    public void ToggleRadialMenu()
    {
        if (_radialMenu is { IsVisible: true })
        {
            CloseRadialMenu();
        }
        else
        {
            OpenRadialMenu();
        }
    }

    /// <summary>打开径向菜单（读取所有插件注册的槽位）</summary>
    public void OpenRadialMenu()
    {
        // 单例复用 + Hide() 关闭：反复 Show/Hide 不会触发"窗口关闭期间 Show"异常
        _radialMenu ??= new RadialMenu(_uiApi, _globalInput, _config, onToggleMirror: () => SetKeyboardMirror(!_config.KeyboardMirrorEnabled));
        // v0.1.25：撤销 v0.1.9 的「旁边定位」（用户要求改回围绕猫一圈）。
        // 菜单圆心 = 猫中心，中心挖空（洞半径 = 猫半宽 + 余量）透出猫，
        // 猫可见、可直接抚摸/拖拽，槽位在猫外围一圈不再盖猫。
        // v0.1.32：洞半径 4→10（猫不再贴洞边，视觉更舒适）
        var holeR = Width / 2 + 10;
        _radialMenu.ShowAt(Left + Width / 2, Top + Height / 2, holeR);
    }

    public void CloseRadialMenu() => _radialMenu?.HideMenu();

    /// <summary>点击穿透（BongoCat 借鉴）：猫纯展示不挡鼠标。开启后鼠标事件穿透到下层窗口，拖拽/抚摸/菜单暂时失效。</summary>
    public void SetMousePassthrough(bool enable)
    {
        IsMousePassthrough = enable;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        ex = enable ? (ex | WS_EX_TRANSPARENT) : (ex & ~WS_EX_TRANSPARENT);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
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
        try { ConfigManager.Save(Paths.ConfigFile, _config); } catch { }
    }

    /// <summary>设置开机自启（决策 14）：写注册表 + 持久化到 config.json（App.OnStartup 启动时与注册表同步）</summary>
    public void SetStartWithWindows(bool enable)
    {
        _config.StartWithWindows = enable;
        try { ConfigManager.Save(Paths.ConfigFile, _config); } catch { }
    }

    /// <summary>设置径向菜单热键（v0.1.5：设置窗口编辑，立即生效 + 持久化）</summary>
    public void SetMenuHotKey(bool ctrl, bool alt, bool shift, string key)
    {
        var k = string.IsNullOrEmpty(key) ? "N" : key[..1];
        _config.MenuHotKey = new HotKeyConfig { Ctrl = ctrl, Alt = alt, Shift = shift, Key = k };
        _globalInput.MenuHotKey = ToHotKey(_config.MenuHotKey);
        try { ConfigManager.Save(Paths.ConfigFile, _config); } catch { }
    }

    /// <summary>设置显隐老板键（v0.1.26：设置窗口编辑，立即生效 + 持久化）</summary>
    public void SetTogglePetHotKey(bool ctrl, bool alt, bool shift, string key)
    {
        var k = string.IsNullOrEmpty(key) ? "H" : key[..1];
        _config.TogglePetHotKey = new HotKeyConfig { Ctrl = ctrl, Alt = alt, Shift = shift, Key = k };
        _globalInput.SecondaryHotKey = ToHotKey(_config.TogglePetHotKey);
        try { ConfigManager.Save(Paths.ConfigFile, _config); } catch { }
    }

    /// <summary>设置窗口透明度（BongoCat window.opacity 借鉴），clamp 0.2-1.0（低于 0.2 鼠标点击透到下层会失灵）</summary>
    public void SetOpacity(double opacity)
    {
        opacity = Math.Clamp(opacity, 0.2, 1.0);
        _config.Opacity = opacity;
        this.Opacity = opacity;
        try { ConfigManager.Save(Paths.ConfigFile, _config); } catch { }
    }

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

    /// <summary>加载插件（窗口显示后调用）</summary>
    public void LoadPlugins()
    {
        _pluginHost.LoadCore(); // v0.1.34：只立即加载核心插件（deferred 集合外的）；延迟插件主窗口显示后异步加载

        // v0.1.34：窗口首次显示完成后 Background 加载 deferred 插件（不抢首屏渲染）
        // ⚠️ v0.1.82 修：原来无条件 `Loaded += ...`，但本方法是被 App 的 Loaded 处理器调用的，
        // 也就是「在 Loaded 事件的触发过程中再挂一个 Loaded 处理器」——WPF 不会在本次触发里调用
        // 新挂的处理器，且 Loaded 不会重放，于是 LoadDeferred() **永远不执行**：
        // weather/quote/wordcard/screenshot/fortune/hourly 这 6 个延迟插件从来没被加载过。
        // 正确做法：已加载完就直接排后台任务；否则才挂一次性处理器。
        void QueueDeferredLoad() => Dispatcher.BeginInvoke(new Action(() =>
        {
            _pluginHost.LoadDeferred();
            DeferredPluginsLoaded?.Invoke();   // 通知 App/TrayManager 等刷新
            PruneRadialMenuConfig();           // v0.1.71：插件全加载完后清一次径向菜单配置残留 slotId
        }), DispatcherPriority.Background);

        if (IsLoaded) QueueDeferredLoad();
        else Loaded += (_, _) => QueueDeferredLoad();

        _eventBus.Emit(DesktopPet.Core.PetEvents.AppReady, null); // v0.1.0+：插件初始化完成，订阅 AppReady 可替代 OnLoad 做延迟初始化
        _stateMachine.Play(DesktopPet.Core.PetState.Idle);
        _loopTimer.Start(); // v0.1.1 fix：之前忘了 Start()，主循环从未跑（动画、眼神追踪、边缘吸附全部失效 → 用户只看到冻住的死图猫）
    }

    /// <summary>
    /// v0.1.71：插件全加载完后清一次径向菜单配置里的残留 slotId（卸载/改名插件后不留垃圾 key）。
    /// 必须在 LoadDeferred 之后调用（否则 AllRadialSlots 未注册完，会误删合法 key）。仅在确有残留时才落盘，避免每次启动重写 config.json。
    /// </summary>
    private void PruneRadialMenuConfig()
    {
        try
        {
            var before = _config.RadialMenu.Order.Count + _config.RadialMenu.Visible.Count;
            RadialMenuResolver.Prune(_uiApi.AllRadialSlots, _config.RadialMenu);
            var after = _config.RadialMenu.Order.Count + _config.RadialMenu.Visible.Count;
            if (before != after)
                ConfigManager.Save(Paths.ConfigFile, _config);
        }
        catch { }
    }

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

    /// <summary>
    /// 工作区（v0.1.26：多显示器——猫在哪块屏就取哪块屏的工作区；此前固定主屏，拖到副屏会被主屏边界弹回）。
    /// Win32 返回物理像素，必须经 TransformFromDevice 转 DIP（v0.1.1 同款坐标系教训）。
    /// </summary>
    private DesktopPet.Core.Rect2 GetWorkArea()
    {
        // v0.1.59(P3)：工作区缓存——猫未移动（Left/Top 与上次相同）时直接复用上次结果，
        // 省掉 SyncMirrorToCat/SyncFollowers 每帧的 GetMonitorInfo Win32 调用。
        // 失效条件：猫位移（Left/Top 变化）必然命中新快照并重算；显示器分辨率变化不处理，
        // 与原逻辑行为等价（原逻辑本身也不响应分辨率变化）。
        if (_cachedWorkArea is { } cached && Left == _lastWorkAreaLeft && Top == _lastWorkAreaTop)
            return cached;
        _lastWorkAreaLeft = Left;
        _lastWorkAreaTop = Top;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref mi))
            {
                double sx = 1.0, sy = 1.0;
                var source = PresentationSource.FromVisual(this);
                if (source?.CompositionTarget is not null)
                {
                    sx = source.CompositionTarget.TransformFromDevice.M11;
                    sy = source.CompositionTarget.TransformFromDevice.M22;
                }
                _cachedWorkArea = new DesktopPet.Core.Rect2(
                    mi.rcWork.Left * sx, mi.rcWork.Top * sy,
                    (mi.rcWork.Right - mi.rcWork.Left) * sx, (mi.rcWork.Bottom - mi.rcWork.Top) * sy);
                return _cachedWorkArea.Value;
            }
        }
        var wa = SystemParameters.WorkArea; // 句柄未建时回退主屏（WPF 已是 DIP）
        _cachedWorkArea = new DesktopPet.Core.Rect2(wa.X, wa.Y, wa.Width, wa.Height);
        return _cachedWorkArea.Value;
    }

    public void Dispose()
    {
        _loopTimer.Stop();
        _chatScheduler?.Stop();    // v0.1.40
        _globalInput.Dispose();
        CloseRadialMenu();
        _mirrorWindow?.Close(); // v0.1.28
        _bubble?.Close();        // v0.1.40
        _hearts?.Close();        // v0.1.59(⑧)
        _pluginHost.Dispose();
    }
}
