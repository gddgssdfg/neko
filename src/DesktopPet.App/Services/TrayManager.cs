using System.IO;
using System.Windows;
using DesktopPet.App.Core;
using DesktopPet.App.Windows;
using Hardcodet.Wpf.TaskbarNotification;

namespace DesktopPet.App.Services;

/// <summary>系统托盘管理：右键菜单（设置/暂停恢复/关于/退出）</summary>
public sealed class TrayManager : IDisposable
{
    private readonly TaskbarIcon _tray;
    private readonly PetWindow _petWindow;
    private string _iconPath;

    /// <summary>v0.1.82：当前打开的设置窗（单实例保护 + 重复点托盘时激活它）。</summary>
    private SettingsWindow? _settingsWindow;

    public TrayManager(PetWindow petWindow)
    {
        _petWindow = petWindow;
        _iconPath = Path.Combine(Paths.Assets, "tray.ico");
        _tray = new TaskbarIcon
        {
            ToolTipText = "Neko 桌宠",
            Icon = LoadIcon(_iconPath),
        };
        // v0.1.27：双击托盘图标 = 显示/隐藏猫（老板键的鼠标等价）
        _tray.TrayMouseDoubleClick += (_, _) => _petWindow.TogglePetVisible();
        // v0.1.34：deferred 插件异步加载完后刷新托盘菜单（让"插件…"子菜单出现 deferred 项）
        _petWindow.DeferredPluginsLoaded += () => RefreshMenu();
    }

    public void Initialize()
    {
        _tray.ContextMenu = BuildMenu();
    }

    /// <summary>
    /// v0.1.62：插件通知入口。SystemImpl.NotifySink 绑定到此处，把插件 Capability.System.Notify
    /// 变成真正的托盘气泡。用 Dispatcher 兜底跨线程（插件定时器跑线程池，托盘对象属 UI 线程）。
    /// </summary>
    public void ShowBalloon(string title, string body)
    {
        try
        {
            if (_tray.Dispatcher.CheckAccess())
            {
                if (_petWindow.TryBeginNotification()) _tray.ShowBalloonTip(title, body, BalloonIcon.Info);
            }
            else
            {
                _tray.Dispatcher.Invoke(() => { if (_petWindow.TryBeginNotification()) _tray.ShowBalloonTip(title, body, BalloonIcon.Info); });
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"托盘气泡弹出失败: {ex.Message}");
        }
    }

    /// <summary>插件加载后调用：基于最新插件状态重建托盘菜单（托盘项来自插件注册）。</summary>
    public void RefreshMenu()
    {
        _tray.ContextMenu = BuildMenu();
    }

    private System.Windows.Controls.ContextMenu BuildMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        // v0.1.30：托盘菜单大幅瘦身（灯灯要求"只留常用"——其它全下沉设置页）

        var settings = new System.Windows.Controls.MenuItem { Header = "设置" };
        settings.Click += (_, _) => OpenSettings();
        menu.Items.Add(settings);

        // v0.1.39：暂停做成真开关——点了暂停，这一项就地变成「开始动画」。
        // 关键：点完必须 RefreshMenu 重建菜单，否则菜单还是上次构建时的旧文案（点了没反应）。
        var pause = new System.Windows.Controls.MenuItem { Header = _petWindow.IsPaused ? "开始动画" : "暂停动画" };
        pause.Click += (_, _) => { TogglePause(); RefreshMenu(); };
        menu.Items.Add(pause);

        // v0.1.48：与设置页/右键菜单统一——「工作模式」checkable，联动 SetAppMode（同时切键盘映射开关）
        var workMode = new System.Windows.Controls.MenuItem
        {
            Header = "工作模式",
            IsCheckable = true,
            IsChecked = _petWindow.Config.AppMode == "work",
        };
        workMode.Click += (_, _) =>
        {
            var newMode = workMode.IsChecked ? "work" : "slack";
            _petWindow.SetAppMode(newMode);
        };
        menu.Items.Add(workMode);

        var togglePet = new System.Windows.Controls.MenuItem { Header = "显示/隐藏猫咪" };
        togglePet.Click += (_, _) => _petWindow.TogglePetVisible();
        menu.Items.Add(togglePet);
        var find = new System.Windows.Controls.MenuItem { Header = "找回猫咪" };
        find.Click += (_, _) => _petWindow.FindCat(); menu.Items.Add(find);
        var quiet = new System.Windows.Controls.MenuItem { Header = "安静模式", IsCheckable = true, IsChecked = _petWindow.Config.QuietMode };
        quiet.Click += (_, _) => _petWindow.SetQuietMode(quiet.IsChecked); menu.Items.Add(quiet);
        menu.Opened += (_, _) => quiet.IsChecked = _petWindow.Config.QuietMode;

        // 插件注册的托盘菜单项（v0.1.30 折叠进"插件…"子菜单，避免主菜单臃肿）
        var pluginItems = _petWindow.UIApi.AllTrayItems;
        if (pluginItems.Count > 0)
        {
            var pluginMenu = new System.Windows.Controls.MenuItem { Header = "插件…" };
            foreach (var item in pluginItems)
            {
                if (item.IsSeparator)
                {
                    pluginMenu.Items.Add(new System.Windows.Controls.Separator());
                    continue;
                }
                var mi = new System.Windows.Controls.MenuItem { Header = item.Label };
                if (item.OnClick is not null) mi.Click += (_, _) => item.OnClick();
                else mi.IsEnabled = false;
                pluginMenu.Items.Add(mi);
            }
            menu.Items.Add(pluginMenu);
        }

        menu.Items.Add(new System.Windows.Controls.Separator());

        // v0.1.39：托盘加「关于」，标题里直接带版本号（灯灯要求"最少显示版本号"）；点进去打开设置页关于栏
        var about = new System.Windows.Controls.MenuItem { Header = $"关于 Neko  v{VersionText}" };
        about.Click += (_, _) => OpenSettings("about");
        menu.Items.Add(about);

        var exit = new System.Windows.Controls.MenuItem { Header = "退出" };
        exit.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(exit);

        return menu;
    }

    private void TogglePause()
    {
        _petWindow.SetPaused(!_petWindow.IsPaused);
        Core.Log.Info($"暂停状态: {_petWindow.IsPaused}");
    }

    /// <summary>版本号（托盘「关于」显示用，如 0.1.39）</summary>
    private static string VersionText =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

    /// <summary>
    /// 打开设置页；section 非空时直接定位到对应栏（如 "about"）。
    /// <para>v0.1.82：设置窗**故意不设 Owner**。</para>
    /// <para>原因：WPF / Win32 规则 —— <b>被 Topmost 窗口拥有的窗口自身也是 Topmost</b>。
    /// 桌宠是 Topmost，一旦把桌宠当 owner，设置页就会永远压在所有应用之上（灯灯反馈"为什么总在最上面"）。
    /// 早先的改法是"打开设置期间临时把桌宠 Topmost 置 false"，但那会让桌宠在设置打开期间被别的窗口盖住，
    /// 与"桌宠要置顶"冲突。现在改成：<b>桌宠的 Topmost 完全不动（永久置顶），设置窗不带 owner 只做普通窗口</b>。
    /// 代价：设置页会出现在任务栏 —— 这反而是好事，被别的程序盖住时能从任务栏点回来。</para>
    /// <para>同时做单实例保护：重复点托盘只会激活已打开的那个（ShowDialog 的模态消息泵仍会派发托盘点击）。</para>
    /// </summary>
    private void OpenSettings(string? section = null)
    {
        if (_settingsWindow is { IsVisible: true } open)
        {
            if (section is not null) open.ShowSection(section);
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }

        var win = new SettingsWindow(_petWindow);      // 注意：不设 Owner（见上面注释）
        _settingsWindow = win;
        if (section is not null) win.ShowSection(section);
        win.Closed += (_, _) => _settingsWindow = null;
        win.ShowDialog();
    }

    /// <summary>
    /// v0.1.82：开发用入口——`Neko.exe --settings` 启动即打开设置页。
    /// 为什么需要：设置页只能从托盘菜单点开，改完 UI 想截图核对时没有可靠手段
    /// （托盘图标在通知区，程序化点击很脆弱）。
    /// </summary>
    public void OpenSettingsForDev(string? section = null) => OpenSettings(section);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private static System.Drawing.Icon LoadIcon(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var icon = new System.Drawing.Icon(stream, new System.Drawing.Size(Math.Max(16, GetSystemMetrics(49)), Math.Max(16, GetSystemMetrics(50))));
            return (System.Drawing.Icon)icon.Clone();
        }
        catch
        {
            return System.Drawing.SystemIcons.Application;
        }
    }

    public void Dispose()
    {
        _tray.Dispose();
    }
}
