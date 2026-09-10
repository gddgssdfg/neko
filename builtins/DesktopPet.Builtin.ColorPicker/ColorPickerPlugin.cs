using System.Runtime.InteropServices;
using DesktopPet.Core;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.ColorPicker;

/// <summary>
/// 屏幕取色插件：鼠标移到目标位置 → 右键菜单「取色」→ 读该点像素颜色 → 猫播报 HEX/RGB 并复制到剪贴板。
/// 展示插件直接 P/Invoke GetPixel + ISystem.WriteClipboard。
/// </summary>
public sealed class ColorPickerPlugin : PluginBase
{
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr hdc, int x, int y);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    public override string Id => "com.lantern.colorpicker";
    public override string Name => "取色器";

    public override PluginPermissions RequiredPermissions =>
        PluginPermissions.PetAnimate | PluginPermissions.UIRegister | PluginPermissions.Clipboard;

    public override void OnLoad(IPluginContext ctx)
    {
        base.OnLoad(ctx);

        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "color-pick",
            Label = "取色",
            Icon = "info",
            Order = 28,
            OnInvoke = PickColor,
        });
    }

    private void PickColor()
    {
        try
        {
            if (!GetCursorPos(out var pt)) { Pet.Say("取色失败", 2000); return; }
            var hdc = GetDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero) { Pet.Say("取色失败", 2000); return; }
            var color = GetPixel(hdc, pt.X, pt.Y); // COLORREF: 0x00BBGGRR
            ReleaseDC(IntPtr.Zero, hdc);

            var r = (int)(color & 0xFF);
            var g = (int)((color >> 8) & 0xFF);
            var b = (int)((color >> 16) & 0xFF);
            var hex = $"#{r:X2}{g:X2}{b:X2}";

            Capability.System.WriteClipboard(hex);
            Pet.Say($"颜色 {hex}（RGB {r},{g},{b}）已复制", 3500);
        }
        catch
        {
            Pet.Say("取色失败", 2000);
        }
    }
}
