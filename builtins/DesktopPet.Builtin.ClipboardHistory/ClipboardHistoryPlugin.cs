using System.Runtime.InteropServices;
using System.Windows.Interop;
using DesktopPet.Core;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.ClipboardHistory;

/// <summary>
/// 剪贴板历史插件：<b>事件驱动</b>记录最近 20 条复制过的文本。
/// 通过 <c>AddClipboardFormatListener</c> 在宿主 UI 线程上建一个不可见消息窗口接收
/// <c>WM_CLIPBOARDUPDATE</c>，变更即达，<b>不再轮询</b>（轮询是有损的：短时间内连续复制会漏记）。
/// 右键菜单「剪贴板」打开历史窗口，双击条目复制回剪贴板。
/// </summary>
/// <remarks>
/// v0.1.84：由 <c>Scheduler.Every(3000)</c> 轮询改为 <see cref="HwndSource"/> + <c>WM_CLIPBOARDUPDATE</c> 事件驱动。
/// <list type="bullet">
/// <item><description><b>线程亲和</b>：<see cref="HwndSource"/> 在<b>创建它的线程</b>（= 宿主 UI 线程，STA）上派发消息，
/// 故 <c>WndProc</c> 天然跑在 UI 线程，剪贴板读取满足 STA 要求。</description></item>
/// <item><description><b>成对清理</b>：<see cref="OnUnload"/> 里先 <c>RemoveClipboardFormatListener</c>（句柄仍有效时），
/// 再摘钩子、<c>Dispose</c> 消息窗口并置空——热重载不能留下引用旧插件类型的窗口/句柄（否则钉住旧 ALC）。</description></item>
/// <item><description><b>绝不静默失效</b>：事件监听建不起来时（非 STA / 无 WPF / 注册失败）降级为长间隔轮询兜底并 Warn 留痕。</description></item>
/// </list>
/// </remarks>
public sealed class ClipboardHistoryPlugin : PluginBase, ISettingsPage
{
    public const string HistoryKey = "history";
    private ClipboardStore _store = null!;
    private bool _loaded;
    private IScheduleHandle? _expiryTimer;
    public string Title => Id;

    /// <summary>降级轮询间隔（仅当事件监听不可用时启用）。比旧的 3000ms 更长——它只是兜底，不是主路径。</summary>
    private const int FallbackPollMs = 10_000;

    /// <summary><c>WM_CLIPBOARDUPDATE</c>（winuser.h）：剪贴板内容变化时系统投递给已注册窗口的消息。</summary>
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    /// <summary>不可见消息窗口（仅用于接收 <c>WM_CLIPBOARDUPDATE</c>）；UI 线程创建，卸载时成对销毁。</summary>
    private HwndSource? _messageWindow;

    /// <summary>降级轮询句柄；仅当事件监听不可用时非空。</summary>
    private IScheduleHandle? _fallbackPoller;

    /// <summary>最近一次已记录的剪贴板文本（连续重复去重用；空串 = 尚未记录）。</summary>
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

        _store = new ClipboardStore(Capability.Storage);
        _loaded = true;
        UI.RegisterSettingsPage(this);
        _expiryTimer = Capability.Scheduler.Every(60_000, () =>
        { try { _store.Read(); } catch { /* retry on next read */ } });
        if (_store.Recording) StartListening();
    }

    private void StartListening()
    {
        if (!_loaded || _messageWindow is not null || _fallbackPoller is not null) return;
        _last = Capability.System.ReadClipboard() ?? "";

        var eventDriven = false;
        string reason = "未知";
        try
        {
            eventDriven = TryStartClipboardListener();
            if (!eventDriven) reason = "消息窗口创建或 AddClipboardFormatListener 注册失败";
        }
        catch (Exception ex)
        {
            // 含「测试宿主无 WPF → HwndSource 类型/JIT 不可用」这类装配期异常。
            reason = $"{ex.GetType().Name}: {ex.Message}";
        }

        if (eventDriven)
        {
            Capability.System.Log(PluginLogLevel.Info, "剪贴板历史已启用事件驱动监听（WM_CLIPBOARDUPDATE）");
            return;
        }

        // 防御性降级：**绝不让剪贴板历史静默地永远不再更新**（这正是本项目整轮在打的"静默失效"）。
        // 事件监听不可用时退回长间隔轮询兜底——有损（短时间连续复制可能漏记），但功能仍在，且明确留痕。
        Capability.System.Log(PluginLogLevel.Warn,
            $"剪贴板事件监听不可用（{reason}），已降级为 {FallbackPollMs / 1000}s 轮询兜底（短时间内连续复制可能漏记）");
        _fallbackPoller = Capability.Scheduler.Every(FallbackPollMs, PollOnce);
    }

    /// <summary>
    /// 建消息窗口并注册系统级剪贴板监听。必须在 UI 线程调用（<see cref="HwndSource"/> 的创建与消息派发都锚在创建线程）。
    /// 任何失败都清理干净并返回 false（不清空调用方的日志责任）。
    /// </summary>
    private bool TryStartClipboardListener()
    {
        HwndSource? source = null;
        try
        {
            // WindowStyle=0 → 无边框/无标题/不可见、不抢焦点、不上任务栏的裸窗口；0×0 尺寸。
            // 它唯一用途就是接收 WM_CLIPBOARDUPDATE（该消息会派发到此窗口所属线程 = UI 线程）。
            source = new HwndSource(new HwndSourceParameters("NekoClipboardListener")
            {
                WindowStyle = 0,
                ExtendedWindowStyle = 0,
                Width = 0,
                Height = 0,
            });
            source.AddHook(WndProc);

            if (!AddClipboardFormatListener(source.Handle))
            {
                source.RemoveHook(WndProc);
                source.Dispose();
                return false;
            }

            _messageWindow = source;
            return true;
        }
        catch
        {
            // 半成品窗口务必拆掉，否则会留下一个引用本插件类型的 HWND（热重载时钉住旧 ALC）。
            try { source?.Dispose(); } catch { /* 清理失败尽力而为 */ }
            throw;   // 交给 OnLoad 统一记录原因 + 触发降级
        }
    }

    /// <summary>消息窗口过程：只处理 <c>WM_CLIPBOARDUPDATE</c>。运行在消息窗口所属线程（宿主 UI 线程）。</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE && _loaded && _store.Recording)
        {
            // 该回调在 UI 线程（STA）上执行 → Clipboard 读取天然合规（无需跨线程封送）。
            // ⚠️ 竞争：通知是在写方调完 SetClipboardData、但**可能尚未 CloseClipboard** 时投递的。
            //   此刻直读有概率撞上剪贴板被占用（CLIPBRD_E_CANT_OPEN）或正换格式（读不到文本）——
            //   那样这一次变更会被**永久丢掉**（正是我们要消灭的"静默失效"；实测已复现一次）。
            //   故：直读一次；读不到就延后一拍再读一次（见 ScheduleDeferredRead，不阻塞 UI）。
            var text = Capability.System.ReadClipboard();
            if (string.IsNullOrEmpty(text)) { ScheduleDeferredRead(); return IntPtr.Zero; }
            Record(text);
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// 延后一拍重读一次剪贴板（一次为限，<b>不阻塞 UI 线程</b>），兜住"通知早于写方释放剪贴板"的竞争。
    /// 测试宿主无 <see cref="System.Windows.Application"/> 时静默跳过（那条路径下本就不该有真实剪贴板事件）。
    /// </summary>
    private void ScheduleDeferredRead()
    {
        try
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null) return;
            dispatcher.BeginInvoke(
                new Action(() => { if (_loaded && _store.Recording) Record(Capability.System.ReadClipboard()); }),
                System.Windows.Threading.DispatcherPriority.Background);
        }
        catch { /* 重试投递失败：不影响本次通知的主流程 */ }
    }

    /// <summary>
    /// 记一条剪贴板文本（最新在最前，上限 20 条）。
    /// <list type="bullet">
    /// <item><description><b>忽略空/纯空白</b>：多为误复制（选中拖拽带出的空格/换行），否则会挤占上限、顶掉真实内容。</description></item>
    /// <item><description><b>连续重复不记两遍</b>：与上一条相同（含系统对同一次复制的重复通知）直接跳过，也省一次写盘。</description></item>
    /// <item><description><b>再次复制已有内容 → 移到最前</b>（不新增）：同一内容在历史里只留一条，且体现"最近使用"。</description></item>
    /// </list>
    /// </summary>
    private void Record(string? text)
    {
        if (!_loaded || !_store.Recording || text == _last) return;
        try { _store.Record(text); _last = text ?? ""; }
        catch (Exception ex) { Capability.System.Log(PluginLogLevel.Warn, $"保存剪贴板历史失败：{ex.GetType().Name}"); }
    }

    /// <summary>降级路径：长间隔轮询（仅在事件监听不可用时挂上）。</summary>
    private void PollOnce() { if (_loaded && _store.Recording) Record(Capability.System.ReadClipboard()); }

    /// <summary>成对清理消息窗口与系统级监听。顺序不可反：Window 销毁后 Handle 失效，注销会失败并留下系统级监听。</summary>
    private void StopClipboardListener()
    {
        if (_messageWindow is null) return;

        try { RemoveClipboardFormatListener(_messageWindow.Handle); }
        catch (Exception ex) { Capability.System.Log(PluginLogLevel.Warn, $"注销剪贴板监听失败：{ex.Message}"); }

        try { _messageWindow.RemoveHook(WndProc); } catch { /* 摘钩子失败不影响销毁，尽力而为 */ }

        _messageWindow.Dispose();
        _messageWindow = null;
    }

    public object BuildContent() => BuildControls(() => { });

    private System.Windows.FrameworkElement BuildControls(Action refreshed)
    {
        var panel = new System.Windows.Controls.StackPanel();
        panel.Children.Add(DesktopPet.Sdk.PluginUi.Text("开启后会保存复制的文本，包括可能的密码和令牌。仅本地保存，最多 20 条。", sub: true));
        var enabled = new System.Windows.Controls.CheckBox { Content = "记录剪贴板（关闭即暂停监听）", IsChecked = _store.Recording,
            Margin = new System.Windows.Thickness(0, 12, 0, 12) };
        enabled.Click += (_, _) =>
        {
            try
            {
                bool recording = enabled.IsChecked == true;
                if (!recording) { StopClipboardListener(); _fallbackPoller?.Dispose(); _fallbackPoller = null; }
                _store.SetRecording(recording);
                if (recording) StartListening();
                refreshed();
            }
            catch (Exception) { enabled.IsChecked = _store.Recording; System.Windows.MessageBox.Show("隐私设置无法保存，请检查数据目录权限。本次暂停仍生效，但重启可能读取原设置。"); }
        };
        panel.Children.Add(enabled);
        var days = new System.Windows.Controls.ComboBox { ItemsSource = new[] { "1 天后清理", "7 天后清理", "30 天后清理", "永久保留" },
            SelectedIndex = Array.IndexOf(new[] { 1, 7, 30, 0 }, _store.RetentionDays), Margin = new System.Windows.Thickness(0, 0, 0, 12) };
        days.SelectionChanged += (_, _) => { if (days.SelectedIndex < 0) return; try { _store.SetRetention(new[] { 1, 7, 30, 0 }[days.SelectedIndex]); refreshed(); }
            catch { System.Windows.MessageBox.Show("清理设置保存失败，请重试。"); } };
        panel.Children.Add(days);
        var clear = DesktopPet.Sdk.PluginUi.Button("清空全部历史");
        clear.Click += (_, _) => { try { _store.Clear(); _last = ""; refreshed(); } catch { System.Windows.MessageBox.Show("清空失败，请重试。"); } };
        panel.Children.Add(clear);
        return panel;
    }

    private void OpenHistory()
    {
        ClipboardWindow? window = null;
        var controls = BuildControls(() => window?.RefreshHistory());
        List<string> Read() => _store.Read().Select(e => e.Text).ToList();
        window = new ClipboardWindow(Read(), t => Capability.System.WriteClipboard(t), Capability.System, controls, Read, () => _store.Recording);
        window.Show();
    }

    public override void OnUnload()
    {
        _loaded = false;
        _expiryTimer?.Dispose(); _expiryTimer = null;
        // 方法体不直接引用 HwndSource —— 把对它的引用隔离在 StopClipboardListener 内并用 try/catch 兜住，
        // 这样即使宿主环境根本没有 WPF（JIT/装配期异常），OnUnload 也不会因清理而抛。
        try { StopClipboardListener(); }
        catch (Exception ex)
        {
            try { Capability.System.Log(PluginLogLevel.Warn, $"清理剪贴板监听异常：{ex.GetType().Name}: {ex.Message}"); }
            catch { /* 日志设施异常绝不外抛 */ }
        }

        _fallbackPoller?.Dispose();
        _fallbackPoller = null;

        base.OnUnload();
    }
}
