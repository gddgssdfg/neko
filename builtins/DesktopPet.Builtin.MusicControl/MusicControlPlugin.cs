using System.Runtime.InteropServices;
using DesktopPet.Core;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.MusicControl;

/// <summary>
/// 音乐控制插件：通过 Win32 keybd_event 发送系统媒体键（上一曲/播放暂停/下一曲），
/// 对任意播放器（QQ音乐/网易云/浏览器）生效。
/// 展示宿主 ISystem 之外的能力——插件可直接 P/Invoke（权限声明 process）。
/// </summary>
public sealed class MusicControlPlugin : PluginBase
{
    private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
    private const byte VK_MEDIA_PREV_TRACK = 0xB1;
    private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    public override string Id => "com.lantern.music";
    public override string Name => "音乐控制";

    public override PluginPermissions RequiredPermissions =>
        PluginPermissions.UIRegister | PluginPermissions.MediaKey;   // v0.1.80：由粗 token Process 细化为 MediaKey（低危，不再被误判拒载）

    public override void OnLoad(IPluginContext ctx)
    {
        base.OnLoad(ctx);

        UI.RegisterRadialSlot(new RadialSlot { Id = "music-prev", Label = "上一曲", Icon = "prev", Order = 2, OnInvoke = () => PressMediaKey(VK_MEDIA_PREV_TRACK) });
        UI.RegisterRadialSlot(new RadialSlot { Id = "music-play", Label = "播放暂停", Icon = "play", Order = 3, OnInvoke = () => PressMediaKey(VK_MEDIA_PLAY_PAUSE) });
        UI.RegisterRadialSlot(new RadialSlot { Id = "music-next", Label = "下一曲", Icon = "next", Order = 4, OnInvoke = () => PressMediaKey(VK_MEDIA_NEXT_TRACK) });
    }

    private static void PressMediaKey(byte vk)
    {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }
}
