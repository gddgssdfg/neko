// v0.1.84：从 PetWindow.cs 按功能区域拆出（审计 P2-11）——运行时设置项（皮肤 / 热键 / 缩放 / 透明度 / 穿透…）
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
        ConfigManager.Save(Paths.ConfigFile, _config);
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

    /// <summary>设置开机自启（决策 14）：写注册表 + 持久化到 config.json（App.OnStartup 启动时与注册表同步）</summary>
    public void SetStartWithWindows(bool enable)
    {
        _config.StartWithWindows = enable;
        ConfigManager.Save(Paths.ConfigFile, _config);
    }

    /// <summary>设置径向菜单热键（v0.1.5：设置窗口编辑，立即生效 + 持久化）</summary>
    public void SetMenuHotKey(bool ctrl, bool alt, bool shift, string key)
    {
        var k = string.IsNullOrEmpty(key) ? "N" : key[..1];
        _config.MenuHotKey = new HotKeyConfig { Ctrl = ctrl, Alt = alt, Shift = shift, Key = k };
        _globalInput.MenuHotKey = ToHotKey(_config.MenuHotKey);
        ConfigManager.Save(Paths.ConfigFile, _config);
    }

    /// <summary>设置显隐老板键（v0.1.26：设置窗口编辑，立即生效 + 持久化）</summary>
    public void SetTogglePetHotKey(bool ctrl, bool alt, bool shift, string key)
    {
        var k = string.IsNullOrEmpty(key) ? "H" : key[..1];
        _config.TogglePetHotKey = new HotKeyConfig { Ctrl = ctrl, Alt = alt, Shift = shift, Key = k };
        _globalInput.SecondaryHotKey = ToHotKey(_config.TogglePetHotKey);
        ConfigManager.Save(Paths.ConfigFile, _config);
    }

    /// <summary>设置窗口透明度（BongoCat window.opacity 借鉴），clamp 0.2-1.0（低于 0.2 鼠标点击透到下层会失灵）</summary>
    public void SetOpacity(double opacity)
    {
        opacity = Math.Clamp(opacity, 0.2, 1.0);
        _config.Opacity = opacity;
        this.Opacity = opacity;
        ConfigManager.Save(Paths.ConfigFile, _config);
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

    /// <summary>配置的热键 → GlobalInput 的 HotKey（键名转 VK 码，支持方向键名）</summary>
    private static HotKey ToHotKey(HotKeyConfig c)
    {
        return new HotKey { Ctrl = c.Ctrl, Alt = c.Alt, Shift = c.Shift, Key = ToVk(c.Key) };
    }

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
}
