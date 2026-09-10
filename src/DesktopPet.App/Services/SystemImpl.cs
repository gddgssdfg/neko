using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using DesktopPet.App.Core;
using DesktopPet.Core;

namespace DesktopPet.App.Services;

/// <summary>系统能力实现</summary>
public sealed class SystemImpl : ISystem
{
    public void Notify(string title, string body)
    {
        // 托盘气泡通知（由 TrayManager 挂载，v1 先留占位）
        Log.Info($"[通知] {title}: {body}");
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
