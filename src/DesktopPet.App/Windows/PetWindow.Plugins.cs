// v0.1.84：从 PetWindow.cs 按功能区域拆出（审计 P2-11）——插件加载 + 径向菜单
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
    private bool _pluginsStarted;
    private bool _mirrorHiddenForMenu;
    private bool _isDisposed;
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
        if (MotionSuspended || _gameFullscreenActive) return;
        // 单例复用 + Hide() 关闭：反复 Show/Hide 不会触发"窗口关闭期间 Show"异常
        var menu = EnsureRadialMenu();
        _bubble?.Dismiss();
        menu.Placement = PlaceCompanionMenu;
        menu.ShowAt(Left + Width / 2, Top + Height / 2, 32);
    }

    private Point PlaceCompanionMenu(Size size)
    {
        var obstacles = new List<Rect> { new Rect(Left, Top, Width, Height) };
        if (_mirrorWindow is { IsVisible: true } mirror)
            obstacles.Add(new Rect(mirror.Left, mirror.Top, mirror.Width, mirror.Height));
        if (_hearts is { IsVisible: true } hearts)
            obstacles.Add(new Rect(hearts.Left, hearts.Top, hearts.Width, hearts.Height));
        var work = GetWorkArea();
        var area = new Rect(work.X, work.Y, work.Width, work.Height);
        var position = CompanionLayout.Place(size, area, obstacles);
        // On very small screens there may be no simultaneous layout. Temporarily
        // clear the keyboard rather than cover its keys with menu commands.
        if (_mirrorWindow is { IsVisible: true } keyboard &&
            new Rect(position, size).IntersectsWith(new Rect(keyboard.Left, keyboard.Top, keyboard.Width, keyboard.Height)))
        {
            _mirrorHiddenForMenu = true;
            keyboard.Hide();
            obstacles.RemoveAt(1);
            position = CompanionLayout.Place(size, area, obstacles);
        }
        return position;
    }

    /// <summary>径向菜单单例（懒创建）。抽出供 <see cref="OpenRadialMenu"/> 与 <see cref="PrewarmRadialMenu"/> 共用，
    /// 避免两处重复构造表达式导致回调/参数漂移。</summary>
    private RadialMenu EnsureRadialMenu()
    {
        if (_radialMenu is not null) return _radialMenu;
        _radialMenu = new RadialMenu(_uiApi, _globalInput, _config,
            onToggleMirror: () => SetKeyboardMirror(!_config.KeyboardMirrorEnabled));
        _radialMenu.IsVisibleChanged += (_, _) =>
        {
            if (_radialMenu.IsVisible || !_mirrorHiddenForMenu) return;
            _mirrorHiddenForMenu = false;
            if (!_isDisposed && IsVisible && _config.KeyboardMirrorEnabled) ShowMirror();
        };
        return _radialMenu;
    }

    /// <summary>
    /// v0.1.91 R1：预热径向菜单单例——首次右键的一次性成本（实测 ≈80ms：ctor 扫 neko-assets/icons/
    /// 解码 PNG + 首次创建本窗 HWND）提前到「首帧后的 Background」付掉。
    /// <para>构建时不 Show（无闪现）；再用 <see cref="System.Windows.Interop.WindowInteropHelper.EnsureHandle"/>
    /// 预建窗口句柄（同样不显示、不可见），把"首次 Show 的建窗成本"也一并前置。
    /// 用户真正首次右键时只剩 <c>ShowAt</c>（≈1.5ms）。槽位在每次 ShowAt 时从 UIApi 现读，预热不影响插件注册。</para>
    /// </summary>
    public void PrewarmRadialMenu()
    {
        if (_isDisposed) return;
        var menu = EnsureRadialMenu();
        try
        {
            // 预建 HWND：不 Show、不激活、不可见，仅把建窗 + OnSourceInitialized 的一次性成本前移。
            _ = new System.Windows.Interop.WindowInteropHelper(menu).EnsureHandle();
        }
        catch (Exception ex)
        {
            // 预热失败不影响功能：首次右键会照常懒建。仅留痕。
            Log.Warn($"[径向菜单] 预热建窗失败（首次打开时再建）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    public void CloseRadialMenu() => _radialMenu?.HideMenu();

    /// <summary>加载插件（窗口显示后调用）</summary>
    public void LoadPlugins()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_pluginsStarted) return;
        _pluginsStarted = true;
        _pluginHost.LoadCore(); // v0.1.34：只立即加载核心插件（deferred 集合外的）；延迟插件主窗口显示后异步加载

        // v0.1.34：窗口首次显示完成后 Background 加载 deferred 插件（不抢首屏渲染）
        // ⚠️ v0.1.82 修：原来无条件 `Loaded += ...`，但本方法是被 App 的 Loaded 处理器调用的，
        // 也就是「在 Loaded 事件的触发过程中再挂一个 Loaded 处理器」——WPF 不会在本次触发里调用
        // 新挂的处理器，且 Loaded 不会重放，于是 LoadDeferred() **永远不执行**：
        // weather/quote/wordcard/screenshot/fortune/hourly 这 6 个延迟插件从来没被加载过。
        // 正确做法：已加载完就直接排后台任务；否则才挂一次性处理器。
        void QueueDeferredLoad() => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isDisposed) return;
            _pluginHost.LoadDeferred();
            DeferredPluginsLoaded?.Invoke();   // 通知 App/TrayManager 等刷新
            PruneRadialMenuConfig();           // v0.1.71：插件全加载完后清一次径向菜单配置残留 slotId
            // v0.1.95（审计 P2-2 修复）：AppReady 必须在 **deferred 插件也已 OnLoad 之后** 才广播。
            // 旧实现把这行放在本方法末尾【同步】执行，早于 LoadDeferred（后者排到 Background 异步）——
            // 于是 6 个延迟插件（weather/quote/wordcard/screenshot/fortune/hourly）必然漏收，
            // 与 ICapabilityApi.PetEvents.AppReady 记载的语义（"订阅可替代 OnLoad 做延迟初始化"）不符。
            // 挪到此处：核心插件（已同步 OnLoad、订阅就位）与延迟插件都能收到；AppReady 仍【只发一次】。
            _eventBus.Emit(DesktopPet.Core.PetEvents.AppReady, null);
        }), DispatcherPriority.Background);

        if (IsLoaded) QueueDeferredLoad();
        else Loaded += (_, _) => QueueDeferredLoad();

        _stateMachine.Play(DesktopPet.Core.PetState.Idle);
        _loopTimer.Start(); // v0.1.1 fix：之前忘了 Start()，主循环从未跑（动画、眼神追踪、边缘吸附全部失效 → 用户只看到冻住的死图猫）
    }

    /// <summary>
    /// v0.1.71：插件全加载完后清一次径向菜单配置里的残留 slotId（卸载/改名插件后不留垃圾 key）。
    /// 必须在 LoadDeferred 之后调用（否则 AllRadialSlots 未注册完，会误删合法 key）。仅在确有残留时才落盘，避免每次启动重写 config.json。
    /// </summary>
    private void PruneRadialMenuConfig()
    {
        if (_config.SafeMode) return; // Missing registrations are intentional in a recovery session.
        try
        {
            var before = _config.RadialMenu.Order.Count + _config.RadialMenu.Visible.Count;
            RadialMenuResolver.Prune(_uiApi.AllRadialSlots, _config.RadialMenu);
            var after = _config.RadialMenu.Order.Count + _config.RadialMenu.Visible.Count;
            if (before != after)
                ConfigManager.Save(Paths.ConfigFile, _config);
        }
        catch (Exception ex)
        {
            // v0.1.82 P1-8：清理失效槽位的逻辑异常（如配置结构损坏）会导致脏槽位残留，需留痕
            Log.Warn($"[径向菜单] 清理失效槽位失败：{ex.GetType().Name}: {ex.Message}");
        }
    }
}
