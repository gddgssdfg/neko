using DesktopPet.Core;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.ClipboardHistory;

/// <summary>
/// 剪贴板历史插件：IScheduler 轮询剪贴板（3000ms），文本变化时入栈到 Storage（最近 20 条）。
/// 右键菜单「剪贴板」打开历史窗口，双击条目复制回剪贴板。
/// 展示 ISystem.ReadClipboard / WriteClipboard + Scheduler 轮询。
/// </summary>
public sealed class ClipboardHistoryPlugin : PluginBase
{
    public const string HistoryKey = "history";
    private const int MaxItems = 20;
    // v0.1.59(P2)：轮询间隔 800→1500ms。剪贴板历史对实时性要求低，减少 ReadClipboard
    //   （跨进程 Win32 调用）在闲置态的调用频次。
    // v0.1.82：1500→3000ms。审计实测轮询剪贴板仍是「闲置 CPU」第 2 名，进一步降频。
    //   理想方案是 AddClipboardFormatListener(WM_CLIPBOARDUPDATE) 事件驱动，但需要常驻消息窗口，
    //   本轮不扩大改动面，先降频缓解。
    private const int PollIntervalMs = 3000;

    private IScheduleHandle? _poller;
    private string _last = "";

    public override string Id => "com.lantern.clipboard";
    public override string Name => "剪贴板";

    public override PluginPermissions RequiredPermissions =>
        PluginPermissions.UIRegister | PluginPermissions.Storage
        | PluginPermissions.Scheduler | PluginPermissions.Clipboard;

    public override void OnLoad(IPluginContext ctx)
    {
        base.OnLoad(ctx);

        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "clip-open",
            Label = "剪贴板",
            Icon = "copy",
            Order = 19,
            OnInvoke = OpenHistory,
        });

        // 启动轮询：检测剪贴板变化
        _last = Capability.System.ReadClipboard() ?? "";
        _poller = Capability.Scheduler.Every(PollIntervalMs, Poll);
    }

    private void Poll()
    {
        try
        {
            var text = Capability.System.ReadClipboard();
            if (string.IsNullOrEmpty(text) || text == _last) return;
            _last = text;

            var list = Capability.Storage.Get<List<string>>(HistoryKey) ?? new List<string>();
            list.Insert(0, text);
            if (list.Count > MaxItems) list.RemoveRange(MaxItems, list.Count - MaxItems);
            Capability.Storage.Set(HistoryKey, list);
        }
        catch { /* 剪贴板忙等偶发异常忽略 */ }
    }

    private void OpenHistory()
    {
        var list = Capability.Storage.Get<List<string>>(HistoryKey) ?? new List<string>();
        new ClipboardWindow(list, pick: t => Capability.System.WriteClipboard(t)).Show();
    }

    public override void OnUnload()
    {
        _poller?.Dispose();
        _poller = null;
    }
}
