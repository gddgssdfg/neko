// v0.1.84：从 PetWindow.cs 按功能区域拆出（审计 P2-11）——游戏模式（全屏低干扰）
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
        ConfigManager.Save(Paths.ConfigFile, _config);
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

    // 注：ShellWindowClasses（static readonly 字段）按「拆分不移动字段」约束仍声明在 PetWindow.cs 主文件。

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
}
