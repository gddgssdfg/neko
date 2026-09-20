using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using DesktopPet.App.Core;
using DesktopPet.Core;
// 本类 v0.1.83 起新增了名为 Log 的成员方法；C# 在成员访问里同名方法会遮蔽同名类型，
// 故用别名消歧，保证宿主日志（DesktopPet.App.Core.Log）调用不受影响。
using HostLog = DesktopPet.App.Core.Log;

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

    /// <summary>所属插件 id；null = 宿主内部使用（v0.1.83）。由 PluginHost.BuildContext 按插件注入，用于给插件日志加可定位前缀。</summary>
    private readonly string? _pluginId;

    /// <summary>宿主内部构造：日志不带插件 id 前缀。</summary>
    public SystemImpl() { }

    /// <summary>插件侧构造：<see cref="Log"/> 会带 <c>[插件:&lt;id&gt;]</c> 前缀（v0.1.83）。</summary>
    /// <param name="pluginId">插件唯一 id（如 com.lantern.weather）。</param>
    public SystemImpl(string pluginId) => _pluginId = pluginId;

    public void Notify(string title, string body)
    {
        // 托盘气泡通知（v0.1.62：接入 TrayManager.ShowBalloon；sink 未绑定时退化为日志）
        // v0.1.74 隐私收紧：不落通知正文（body 可能含用户内容，如待办标题），且降为 Debug 级
        // （默认 warn 级别本就不落盘；此改动保证用户主动开 debug 时也不会把内容写进日志）。
        HostLog.Debug($"[通知] {title}");
        NotifySink?.Invoke(title, body);
    }

    public string? ReadClipboard()
    {
        // v0.1.82：**必须封送到 UI 线程**。System.Windows.Clipboard 要求 STA，
        // 而插件定时器回调在线程池（MTA）——原先直接调用 + catch 吞掉，
        // 结果是剪贴板历史插件的 1.5s 轮询静默失败、历史**永远是空的**且毫无提示。
        return OnUi(() =>
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch (Exception ex)
            {
                // v0.1.86：**区分「瞬时竞争」与「真失败」**。
                // CLIPBRD_E_CANT_OPEN(0x800401D0) 的语义是"**别的程序正拿着剪贴板**"——
                // 这是正常竞争（任何程序复制时都会短暂独占），重试即可，**不是剪贴板坏了**。
                // 尤其事件驱动监听下：WM_CLIPBOARDUPDATE 可能在写方 SetClipboardData 之后、
                // 尚未 CloseClipboard 时投递 → 首次直读**必然有概率**撞上它。
                // 若不降级，用户每复制一次就留一条 WARN → 日志被淹没、真问题反而看不见（狼来了效应）。
                // 实测证据：连写两条即产生 1 条 WARN（且业务结果是正确的，已由延后重读兜住）。
                if ((uint)ex.HResult == 0x800401D0u)
                    HostLog.Debug($"剪贴板暂时被占用（瞬时竞争，调用方可重试）: {ex.Message}");
                else
                    HostLog.Warn($"剪贴板读取失败: {ex.Message}");
                return null;
            }
        }, null);
    }

    public void WriteClipboard(string text)
    {
        OnUi(() =>
        {
            try { Clipboard.SetText(text); }
            catch (Exception ex)
            {
                // v0.1.86：同 ReadClipboard —— 写入也会撞 CLIPBRD_E_CANT_OPEN（别的程序占着），
                // 属瞬时竞争、降为 Debug；其它失败仍是 Warn。
                if ((uint)ex.HResult == 0x800401D0u)
                    HostLog.Debug($"剪贴板暂时被占用（瞬时竞争，写入未生效）: {ex.Message}");
                else
                    HostLog.Warn($"剪贴板写入失败: {ex.Message}");
            }
        });
    }

    /// <summary>v0.1.82：剪贴板等 STA/WPF 亲和操作统一封送到 UI 线程（无 App 时直接执行，便于测试）。</summary>
    private static void OnUi(Action action)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) { action(); return; }
        try { d.Invoke(action); }
        catch (Exception ex) { HostLog.Warn($"[System] UI 线程执行失败: {ex.Message}"); }
    }

    private static T OnUi<T>(Func<T> f, T fallback)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) return f();
        try { return d.Invoke(f); }
        catch (Exception ex)
        {
            HostLog.Warn($"[System] UI 线程执行失败: {ex.Message}");
            return fallback;
        }
    }

    public void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { HostLog.Warn($"打开链接失败: {ex.Message}"); }
    }

    public string? PickFile(string filter = "")
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = string.IsNullOrEmpty(filter) ? "所有文件 (*.*)|*.*" : filter,
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    /// <summary>
    /// v0.1.83：插件日志入口。附加插件 id 前缀（<c>[插件:&lt;id&gt;]</c>）后转交宿主日志设施，
    /// 使插件内的失败不再"静默"（此前插件无任何日志通道，只能用 Debug.WriteLine——Release 下等于静默）。
    /// 全程由宿主 <see cref="HostLog"/> 内部 try/catch 兜底，<b>永不抛</b>。
    /// </summary>
    public void Log(PluginLogLevel level, string message, Exception? exception = null)
    {
        var line = $"{Tag} {message}";
        switch (level)
        {
            case PluginLogLevel.Debug: HostLog.Debug(line); break;
            case PluginLogLevel.Info: HostLog.Info(line); break;
            case PluginLogLevel.Warn: HostLog.Warn(line); break;
            default: HostLog.Error(line, exception); break;
        }
    }

    /// <summary>日志前缀：有插件 id 时带 id 便于定位，否则退化为通用标记。</summary>
    private string Tag => _pluginId is null ? "[插件]" : $"[插件:{_pluginId}]";
}
