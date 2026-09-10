using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DesktopPet.App.Windows;

/// <summary>
/// v0.1.50 窗口扩展样式工具：给 Neko 的全部常驻窗口加 WS_EX_TOOLWINDOW，
/// 让 Alt+Tab 切换任务时不出现（BongoCat 同理——它不是普通窗口）。
/// 任务栏已 ShowInTaskbar=false；ToolWindow 样式再让 Alt+Tab 列表隐藏。
/// 注意：FirstRunWindow 是首启引导窗，不应用（用户首次要能找到它）。
/// </summary>
public static class WindowEx
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>给窗口加 ToolWindow 样式（Alt+Tab 不再显示；幂等，可重复调用）</summary>
    public static void HideFromAltTab(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
    }
}
