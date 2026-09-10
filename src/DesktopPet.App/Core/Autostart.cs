using System.Diagnostics;
using Microsoft.Win32;

namespace DesktopPet.App.Core;

/// <summary>
/// 开机自启（BongoCat 借鉴 + 决策 14）：通过 HKCU\...\Run 注册表项控制
/// 不写 HKLM（不需管理员权限；HKCU 足够个人自启）
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "Neko";

    /// <summary>当前是否已注册自启</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(AppName) is string;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>启用/禁用自启（写注册表）</summary>
    public static void SetEnabled(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("无法打开 HKCU Run 键，请检查权限");
        if (enable)
        {
            // 用 exe 完整路径（带引号处理空格）；发布形态下指向 Neko.exe
            var exe = Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("无法获取当前 exe 路径");
            key.SetValue(AppName, $"\"{exe}\"");
        }
        else
        {
            if (key.GetValue(AppName) != null)
                key.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }
}