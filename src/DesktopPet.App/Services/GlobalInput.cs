using System.Runtime.InteropServices;
using System.Windows;

namespace DesktopPet.App.Services;

/// <summary>
/// 全局输入服务：
///   1. WH_KEYBOARD_LL 低级键盘钩子 —— 捕获全局热键 / 键盘反应事件（即使窗口无焦点）。
///   2. WH_MOUSE_LL 低级鼠标钩子 —— 键鼠反应的点击事件（v0.1.25 补齐）。
///   3. 全局鼠标位置轮询 —— 供眼神追踪 / 边缘吸附等宿主能力使用。
/// 钩子回调必须尽量轻量，仅转发事件，不做耗时逻辑。
/// </summary>
public sealed class GlobalInput : IDisposable
{
    // ---- Win32 常量与结构 ----
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0102;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MBUTTONUP = 0x0208;
    private const int WM_MOUSEWHEEL = 0x020A;

    private const uint VK_CONTROL = 0x11;
    private const uint VK_MENU = 0x12;      // Alt
    private const uint VK_SHIFT = 0x10;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>热键触发回调（参数：修饰键按下组合、主键）</summary>
    public event Action<HotKey>? HotKeyPressed;

    /// <summary>任意非修饰键按下（已节流，供"键鼠反应"能力使用——打字时猫做小反应）</summary>
    public event Action? KeyPressed;

    /// <summary>任意鼠标键按下（已节流，v0.1.25 新增——点击时猫做小反应）</summary>
    public event Action? MousePressed;

    // ---- v0.1.28：键鼠映射可视化用的原始事件（无节流——逐键高亮必须每次都报，回调必须轻量） ----
    /// <summary>任意键盘键按下/松开（vk=虚拟键码，down=true 按下）。含被热键吞掉的按键。</summary>
    public event Action<uint, bool>? RawKeyEvent;
    /// <summary>鼠标键按下/松开（0=左 1=右 2=中，down=true 按下）</summary>
    public event Action<int, bool>? RawMouseButtonEvent;
    /// <summary>滚轮滚动（任意方向）</summary>
    public event Action? RawMouseWheelEvent;

    private readonly LowLevelKeyboardProc _proc;
    private readonly LowLevelMouseProc _mouseProc;
    private IntPtr _hookHandle = IntPtr.Zero;
    private IntPtr _mouseHookHandle = IntPtr.Zero;

    // 键盘反应节流：打字时按键频率高，钩子回调必须轻量，300ms 内只报一次
    private DateTime _lastKeyEvent = DateTime.MinValue;
    private const int KeyReactionMs = 300;

    // 鼠标反应节流（连点同样高频）
    private DateTime _lastMouseEvent = DateTime.MinValue;
    private const int MouseReactionMs = 300;

    /// <summary>当前生效的菜单热键：Ctrl+Alt+N（0x4E = 'N'）。后续可从配置覆盖。</summary>
    public HotKey MenuHotKey { get; set; } = new() { Ctrl = true, Alt = true, Key = 0x4E };

    /// <summary>第二全局热键（v0.1.26：显隐老板键 Ctrl+Shift+H）。null = 禁用。</summary>
    public HotKey? SecondaryHotKey { get; set; }

    /// <summary>第二热键触发回调（老板键：显示/隐藏猫）</summary>
    public event Action? SecondaryHotKeyPressed;

    // ---- v0.1.27：附加热键（调透明度/缩放/重置位置等，宿主自行注册回调） ----
    // v0.1.33：改 Dictionary<string, …> 覆盖式——同 id 二次注册会替换热键和回调，实现运行时热更新（设置页改附加热键不需要重启）
    private readonly Dictionary<string, (HotKey Key, Action Callback)> _extraHotKeys = new();

    /// <summary>注册附加热键（v0.1.33：按 id 覆盖式；同 id 再次注册会替换原热键+回调，实现热更新）</summary>
    public void RegisterExtraHotKey(string id, HotKey key, Action callback) => _extraHotKeys[id] = (key, callback);

    public GlobalInput()
    {
        _proc = HookCallback;
        _mouseProc = MouseHookCallback;
    }

    /// <summary>安装键盘 + 鼠标钩子。必须在 UI 线程调用。</summary>
    public void Install()
    {
        if (_hookHandle != IntPtr.Zero) return;
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        if (curModule is null) return;
        var hMod = GetModuleHandle(curModule.ModuleName);
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, hMod, 0);
        _mouseHookHandle = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, hMod, 0);
    }

    /// <summary>卸载钩子</summary>
    public void Uninstall()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
        if (_mouseHookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHookHandle);
            _mouseHookHandle = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            // v0.1.28 键鼠映射：按下/松开都先发原始事件（含将被热键吞掉的键，映射窗照常高亮）
            if (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN)
            {
                var downData = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                RawKeyEvent?.Invoke(downData.vkCode, true);
            }
            else if (wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP)
            {
                var upData = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                RawKeyEvent?.Invoke(upData.vkCode, false);
                return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }

            if (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN)
            {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
            var alt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
            var shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;

            // 仅处理热键的主键按下（避免长按重复触发）；命中即吞键
            if (data.vkCode == MenuHotKey.Key
                && ctrl == MenuHotKey.Ctrl && alt == MenuHotKey.Alt && shift == MenuHotKey.Shift)
            {
                HotKeyPressed?.Invoke(MenuHotKey);
                return (IntPtr)1;
            }

            // v0.1.26：第二热键（显隐老板键）
            var sec = SecondaryHotKey;
            if (sec is not null && data.vkCode == sec.Key
                && ctrl == sec.Ctrl && alt == sec.Alt && shift == sec.Shift)
            {
                SecondaryHotKeyPressed?.Invoke();
                return (IntPtr)1;
            }

            // v0.1.27：附加热键（调透明度/缩放/重置位置）
            foreach (var (hk, cb) in _extraHotKeys.Values)
            {
                if (data.vkCode == hk.Key
                    && ctrl == hk.Ctrl && alt == hk.Alt && shift == hk.Shift)
                {
                    cb.Invoke();
                    return (IntPtr)1;
                }
            }

            // 键盘反应（BongoCat 借鉴）：任意非修饰键按下 → 通知宿主（打字时猫做小反应）
            // 注意：不吞按键，正常传递给其他应用；节流避免高频打字触发过密
            if (data.vkCode is not (VK_CONTROL or VK_MENU or VK_SHIFT))
            {
                var now = DateTime.Now;
                if ((now - _lastKeyEvent).TotalMilliseconds >= KeyReactionMs)
                {
                    _lastKeyEvent = now;
                    KeyPressed?.Invoke();
                }
            }
            }
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    /// <summary>鼠标低级钩子：左/右/中键按下 → 节流后通知（不吞按键，正常传递）</summary>
    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            // v0.1.28 键鼠映射：按下/松开/滚轮先发原始事件（无节流）
            switch (wParam.ToInt64())
            {
                case WM_LBUTTONDOWN: RawMouseButtonEvent?.Invoke(0, true); break;
                case WM_LBUTTONUP: RawMouseButtonEvent?.Invoke(0, false); break;
                case WM_RBUTTONDOWN: RawMouseButtonEvent?.Invoke(1, true); break;
                case WM_RBUTTONUP: RawMouseButtonEvent?.Invoke(1, false); break;
                case WM_MBUTTONDOWN: RawMouseButtonEvent?.Invoke(2, true); break;
                case WM_MBUTTONUP: RawMouseButtonEvent?.Invoke(2, false); break;
                case WM_MOUSEWHEEL: RawMouseWheelEvent?.Invoke(); break;
            }

            if (wParam == (IntPtr)WM_LBUTTONDOWN || wParam == (IntPtr)WM_RBUTTONDOWN || wParam == (IntPtr)WM_MBUTTONDOWN)
            {
            var now = DateTime.Now;
            if ((now - _lastMouseEvent).TotalMilliseconds >= MouseReactionMs)
            {
                _lastMouseEvent = now;
                MousePressed?.Invoke();
            }
            }
        }
        return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(uint vKey);

    /// <summary>获取全局鼠标位置（虚拟桌面坐标）</summary>
    public static Point GetCursorPosition()
    {
        GetCursorPos(out var pt);
        return new Point(pt.X, pt.Y);
    }

    public void Dispose()
    {
        Uninstall();
        HotKeyPressed = null;
        SecondaryHotKeyPressed = null;
        _extraHotKeys.Clear();
        KeyPressed = null;
        MousePressed = null;
        RawKeyEvent = null;
        RawMouseButtonEvent = null;
        RawMouseWheelEvent = null;
    }
}

/// <summary>热键描述</summary>
public sealed record HotKey
{
    public bool Ctrl { get; init; }
    public bool Alt { get; init; }
    public bool Shift { get; init; }
    /// <summary>主键虚拟键码</summary>
    public uint Key { get; init; }
}
