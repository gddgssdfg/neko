using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using DesktopPet.App.Core;
using DesktopPet.Core;

namespace DesktopPet.App.Services;

/// <summary>系统能力实现</summary>
public sealed class SystemImpl : ISystem
{
    /// <summary>
    /// 通知落地钩子（v0.1.62）：宿主在 TrayManager 创建后绑定到此静态字段，
    /// 让插件的 Capability.System.Notify 真正弹出托盘气泡。
    /// 用静态字段而非实例注入，是因为 SystemImpl 在 PetWindow / PluginHost 多处 new，
    /// 改静态字段可零改动构造点。
    /// </summary>
    public static Action<string, string>? NotifySink;

    public void Notify(string title, string body)
    {
        // 托盘气泡通知（v0.1.62：接入 TrayManager.ShowBalloon；sink 未绑定时退化为日志）
        // v0.1.74 隐私收紧：不落通知正文（body 可能含用户内容，如待办标题），且降为 Debug 级
        // （默认 warn 级别本就不落盘；此改动保证用户主动开 debug 时也不会把内容写进日志）。
        Log.Debug($"[通知] {title}");
        NotifySink?.Invoke(title, body);
    }

    public string? ReadClipboard()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
        catch (Exception ex) { Log.Warn($"剪贴板读取失败: {ex.Message}"); return null; }
    }

    public void WriteClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch (Exception ex) { Log.Warn($"剪贴板写入失败: {ex.Message}"); }
    }

    public void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"打开链接失败: {ex.Message}"); }
    }

    public string? PickFile(string filter = "")
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = string.IsNullOrEmpty(filter) ? "所有文件 (*.*)|*.*" : filter,
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }
}
