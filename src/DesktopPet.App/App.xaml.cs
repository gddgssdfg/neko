using System.IO;
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
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 1. 初始化目录
        Paths.EnsureCreated();

        // 全局异常兜底：写入日志并提示一次，避免未处理异常导致整体崩溃
        DispatcherUnhandledException += (_, args) =>
        {
            File.AppendAllText(
                Path.Combine(Paths.Data, "errors.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {args.Exception}\n\n");
            MessageBox.Show(
                $"Neko 遇到未处理异常，已记录到 data\\errors.log：\n{args.Exception.Message}",
                "Neko", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true; // 保住宿主进程不退出
        };

        // 2. 加载配置
        var config = ConfigManager.Load(Paths.ConfigFile);
        Paths.EnsurePluginsCreated();

        // v0.1.41：首启引导（封包需求核心——首次安装时需设置「猫如何称呼自己」+「给猫取名」）
        // CatName 或 OwnerTitle 任一为空视为首次启动；引导窗口置顶显示在屏幕中央
        // v0.1.45：开发测试阶段用 `Neko.exe --dev` 跳过引导（灯灯：别每次测试都要输名字），
        // ChatScheduler 内部有 "主人"/"Neko" 兜底，跳过引导不影响功能
        bool skipOnboarding = e.Args.Any(a => a is "--dev" or "--skip-onboarding");
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
            if (config.StartWithWindows && !Autostart.IsEnabled()) Autostart.SetEnabled(true);
            if (!config.StartWithWindows && Autostart.IsEnabled()) Autostart.SetEnabled(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"自启同步失败: {ex.Message}");
        }

        // 3. 主宠物窗口（透明置顶）
        _petWindow = new PetWindow(config);

        // 4. 托盘
        _tray = new TrayManager(_petWindow);
        _tray.Initialize();

        // 5. 主窗口加载完成后再加载插件（避免渲染阻塞启动），随后刷新托盘菜单以展示插件托盘项
        _petWindow.Loaded += (_, _) =>
        {
            _petWindow.LoadPlugins();
            _tray?.RefreshMenu();
        };

        _petWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _petWindow?.Dispose();
        base.OnExit(e);
    }
}
