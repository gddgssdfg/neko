using System.Windows;
using System.Windows.Threading;
using DesktopPet.App.Core;
using DesktopPet.App.Services;
using DesktopPet.App.Windows;

namespace DesktopPet.App;

public partial class App : Application
{
    private TrayManager? _tray;
    private PetWindow? _petWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((_, args) => { if (args.OriginalSource is Window window) BrandIcons.Apply(window); }));
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // A settings-initiated restart waits for plugin/data flushes in the old process.
        var waitArg = e.Args.FirstOrDefault(a => a.StartsWith("--wait-for-process=", StringComparison.Ordinal));
        if (waitArg is not null && int.TryParse(waitArg.Split('=', 2)[1], out var previousId) && previousId != Environment.ProcessId)
        {
            try
            {
                using var previous = System.Diagnostics.Process.GetProcessById(previousId);
                if (!previous.WaitForExit(10000))
                {
                    MessageBox.Show("旧进程尚未退出，请关闭后重新启动 Neko。", "重启未完成");
                    Shutdown(); return;
                }
            }
            catch (ArgumentException) { /* Already exited. */ }
            catch (Exception ex) { MessageBox.Show("无法等待旧进程退出：" + ex.Message, "重启未完成"); Shutdown(); return; }
        }

        // 1. 初始化目录（v0.1.98：程序根与**数据根**分离，数据根默认在 %AppData%\Neko）
        //    先做一次性迁移：把旧版「程序根/data」里的用户数据（config + 插件数据，含好感度）
        //    复制到新数据根。必须排在 EnsureCreated / ConfigManager.Load 之前——否则会先建出空目录、
        //    再读到空配置，迁移等于白做（旧配置被"默认值"覆盖写回，且不留痕）。
        var dataMigration = Paths.MigrateLegacyData();
        Paths.EnsureCreated();

        bool safeMode = e.Args.Contains("--safe-mode", StringComparer.OrdinalIgnoreCase);
        string? importFailure = null;
        if (!safeMode)
        {
            try { SettingsBackup.ApplyPending(SettingsBackup.PendingPath, Paths.ConfigFile); }
            catch (Exception ex) { importFailure = ex.Message; }
        }

        // 加载配置（先读配置拿到 LogLevel，日志才能按配置过滤）
        var config = ConfigManager.Load(Paths.ConfigFile);
        config.SafeMode = safeMode;
        DesktopPet.Sdk.NekoDesign.LightTheme = config.Theme == "light";

        // v0.1.61：初始化文件日志（级别来自配置；默认 warn 及以上落盘 <数据根>/logs）
        Log.Initialize(config.LogLevel);
        if (importFailure is not null)
            MessageBox.Show("设置导入未完成：" + importFailure + "\n待导入文件仍保留，可在设置中取消待导入。", "Neko", MessageBoxButton.OK, MessageBoxImage.Warning);

        // v0.1.98：「数据到底在哪」必须事后可查——迁移结果用 Warn（默认落盘），位置用 Info
        if (dataMigration is not null)
            Log.Warn("数据目录迁移：" + dataMigration);
        Log.Info($"数据根：{Paths.Data}（来源 {Paths.DataKind}）");

        // 全局异常兜底：写入日志并提示一次，避免未处理异常导致整体崩溃
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("未处理的 UI 线程异常", args.Exception);
            MessageBox.Show(
                $"Neko 遇到未处理异常，已记录到日志：\n{Paths.Logs}\n\n{args.Exception.Message}",
                "Neko", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true; // 保住宿主进程不退出
        };

        // v0.1.61：后台线程兜底——DispatcherUnhandledException 只覆盖 UI 线程，
        // 后台 Task / 非 UI 线程异常若不拦截会静默杀死进程（见 P0-1）。
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("未观测的 Task 异常", args.Exception);
            args.SetObserved(); // 防止进程被 CLR 终止
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            // 此事件无法阻止进程退出，仅用于「留下遗言」
            Log.Error("非 UI 线程未处理异常（进程即将退出）", args.ExceptionObject as Exception);
        };

        Paths.EnsurePluginsCreated();

        // v0.1.41：首启引导（封包需求核心——首次安装时需设置「猫如何称呼自己」+「给猫取名」）
        // CatName 或 OwnerTitle 任一为空视为首次启动；引导窗口置顶显示在屏幕中央
        // v0.1.45：开发测试阶段用 `Neko.exe --dev` 跳过引导（灯灯：别每次测试都要输名字），
        // ChatScheduler 内部有 "主人"/"Neko" 兜底，跳过引导不影响功能
        bool skipOnboarding = safeMode || e.Args.Any(a => a is "--dev" or "--skip-onboarding");
        if (!skipOnboarding && (string.IsNullOrWhiteSpace(config.CatName) || string.IsNullOrWhiteSpace(config.OwnerTitle)))
        {
            var onboarding = new FirstRunWindow(config);
            if (onboarding.ShowDialog() != true)
            {
                // 用户点退出 = 整个程序退出（首启没设置就不让"带着没名字的猫"凑合）
                Shutdown();
                return;
            }
            // config 已被 FirstRunWindow 内部保存，引用同一对象无需 reload
        }

        // 同步自启状态：用户可能直接改了 config.json，注册表也以 config 为准
        try
        {
            if (!safeMode && config.StartWithWindows && !Autostart.IsEnabled()) Autostart.SetEnabled(true);
            if (!safeMode && !config.StartWithWindows && Autostart.IsEnabled()) Autostart.SetEnabled(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"自启同步失败: {ex.Message}");
        }

        // 3. 主宠物窗口（透明置顶）
        _petWindow = new PetWindow(config);

        // 4. 托盘
        _tray = new TrayManager(_petWindow);
        _tray.Initialize();

        // v0.1.62：绑定插件通知落地——Capability.System.Notify → 托盘气泡
        SystemImpl.NotifySink = (title, body) => _tray.ShowBalloon(title, body);

        // 5. 主窗口加载完成后再加载插件（避免渲染阻塞启动），随后刷新托盘菜单以展示插件托盘项
        _petWindow.Loaded += (_, _) =>
        {
            _petWindow.LoadPlugins();
            _tray?.RefreshMenu();
        };

        // v0.1.91 P1+P2+R1：把「镜像窗创建(≈365ms) + chat/tracker 初始化(≈112ms)」移出启动关键路径。
        // 首帧（ContentRendered）上屏后补做；PetWindow.InitializeAfterFirstFrame 内部再排 Background 优先级，
        // 确保这些重活排在渲染之后，绝不抢首帧。运行时切换（SetAppMode / 设置页开关）仍走原同步路径。
        bool afterFirstFrameDone = false;
        _petWindow.ContentRendered += (_, _) =>
        {
            if (afterFirstFrameDone) return;   // 多显示器/重渲染可能多次触发，只做一次
            afterFirstFrameDone = true;
            _petWindow.InitializeAfterFirstFrame();
        };

        _petWindow.Show();

        // v0.1.82：开发用 `Neko.exe --settings[=栏目]` —— 启动后直接打开设置页（可指定栏目，
        // 如 --settings=input 打开"输入"页），方便改完 UI 截图核对。
        var settingsArg = e.Args.FirstOrDefault(a => a == "--settings" || a.StartsWith("--settings="));
        if (settingsArg is not null)
        {
            var section = settingsArg.Contains('=') ? settingsArg.Split('=', 2)[1] : null;
            _petWindow.Dispatcher.BeginInvoke(new Action(() => _tray?.OpenSettingsForDev(section)),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        // v0.1.77：每日静默检查更新（阶段1）——延迟 + 后台 + 只气泡轻提示，不弹模态打扰。
        ScheduleDailyUpdateCheck(config);
    }

    /// <summary>
    /// 每日一次的后台更新检查（v0.1.77·阶段1）：
    ///   - 关闭开关 或 今天已查过 → 不查；
    ///   - 延迟数秒（避开启动 / 插件加载高峰）后用 <see cref="DispatcherTimer"/> 触发一次，不阻塞 UI；
    ///   - 无论成败都写回 <c>LastUpdateCheckDay = 今天</c>，避免一天内反复查；
    ///   - 发现新版本 → 托盘气泡轻提示（**不弹模态框**）。
    /// </summary>
    private void ScheduleDailyUpdateCheck(AppConfig config)
    {
        if (config.SafeMode || !config.UpdateCheckEnabled) return;

        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (string.Equals(config.LastUpdateCheckDay, today, StringComparison.Ordinal)) return;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            await RunDailyUpdateCheckAsync(config);
        };
        timer.Start();
    }

    /// <summary>跑一次后台检查并把结果（无论成败）标记为「今天已查」。</summary>
    private async Task RunDailyUpdateCheckAsync(AppConfig config)
    {
        try
        {
            var latest = await UpdateService.FetchLatestAsync(CancellationToken.None);

            // 无论结果如何都记录今天已查（失败也不在一天内反复重试，避免骚扰）
            config.LastUpdateCheckDay = DateTime.Now.ToString("yyyy-MM-dd");
            ConfigManager.Save(Paths.ConfigFile, config);

            if (latest is not null && UpdateService.IsNewer(UpdateService.CurrentVersion, latest.Version))
            {
                Log.Info($"[更新] 发现新版本 {latest.TagName}（当前 v{UpdateService.CurrentVersion}）");
                _tray?.ShowBalloon($"发现新版本 {latest.TagName}", "可在「设置 → 关于」查看并打开下载页");
            }
        }
        catch (Exception ex)
        {
            // FetchLatestAsync 自身已吞异常，这里仅作最后兜底
            Log.Warn($"[更新] 每日检查失败：{ex.Message}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _petWindow?.Dispose();
        base.OnExit(e);
    }
}
