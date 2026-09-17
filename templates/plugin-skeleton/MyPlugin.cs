using DesktopPet.Core;
using DesktopPet.Sdk;

namespace MyPlugin;

/// <summary>
/// 最小可用插件骨架。
///
/// 复制后要改的地方：
///   1) 目录名（= plugin.toml 的 id）
///   2) 本文件：Id / Name / RequiredPermissions / 功能
///   3) plugin.toml：id / name / entry
///   4) MyPlugin.csproj：&lt;AssemblyName&gt;（= entry 去掉 .dll）
///
/// 本骨架刻意演示了三条最容易踩的规则：
///   · OnLoad 第一行必须 base.OnLoad(ctx)
///   · 定时器句柄必须在 OnUnload 里 Dispose
///   · 任何"自己创建的 UI 亲和对象"操作必须封送到 UI 线程（见 SafeUpdateTitle 示例）
/// </summary>
public sealed class MyPlugin : PluginBase
{
    private const string CountKey = "tickCount";

    private IScheduleHandle? _timer;
    private int _count;

    // ---- 以下三处必须与 plugin.toml 一致 ----
    public override string Id => "com.yourname.myplugin";   // 必须 == 插件目录名
    public override string Name => "我的插件";

    public override PluginPermissions RequiredPermissions =>
        PluginPermissions.PetAnimate      // pet.animate
        | PluginPermissions.UIRegister    // ui.register
        | PluginPermissions.Storage       // storage.local
        | PluginPermissions.Scheduler;    // scheduler

    public override void OnLoad(IPluginContext ctx)
    {
        base.OnLoad(ctx);   // ⚠️ 必须第一行；否则 Pet / UI / Capability 为 null

        // 1) 注册一个径向菜单槽位（必须在 OnLoad 内注册）
        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "hello",               // 槽位 id（建议唯一）
            Label = "打个招呼",          // 显示文字
            Icon = "wave",              // 见文档 §7.1 的可用 key；未知 key 静默回退 default
            Order = 100,                // 越小越靠前
            OnInvoke = Greet,           // 点击回调（在 UI 线程）
        });

        // 2) 读一次持久化数据（data/plugins/<id>/tickCount.json）
        _count = Capability.Storage.Contains(CountKey)
            ? Capability.Storage.Get<int>(CountKey)
            : 0;

        // 3) 起一个定时器：回调跑在线程池（MTA），别在这里直接碰 WPF 对象
        _timer = Capability.Scheduler.Every(10_000, OnTimer);
    }

    /// <summary>径向菜单点击回调（UI 线程）。Pet.* 已由宿主封送到 UI 线程，可直接调。</summary>
    private void Greet()
    {
        Pet.Say($"你好！已经运行了 {_count} 次", 3000);
        Pet.SetMood(PetMood.Happy);
    }

    /// <summary>定时器回调 —— ⚠️ 跑在 System.Threading.Timer 的线程池线程上。</summary>
    private void OnTimer()
    {
        _count++;
        Capability.Storage.Set(CountKey, _count);

        // Pet.Say / Storage 都是安全的（宿主已封送 / 纯 IO）。
        // 但如果你要操作"自己创建的 WPF 窗口 / 剪贴板 / 未 Freeze 的画刷"，必须自己封送：
        //   OnUi(() => _myWindow.Title = $"已运行 {_count} 次");
        // 否则会抛 "调用线程无法访问此对象"，且常被 catch 吞成静默失效。
    }

    /// <summary>
    /// 把动作封送到 UI 线程（示例工具方法）。
    /// 「发后不理」用 BeginInvoke，且异常必须自己兜住——异步路径上的异常无人接管，会直接崩进程。
    /// </summary>
    private static void OnUi(Action action)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) { action(); return; }
        d.BeginInvoke(new Action(() =>
        {
            try { action(); }
            catch { /* TODO: 记录你自己的日志，绝不外抛 */ }
        }));
    }

    /// <summary>
    /// 卸载清理。OnUnload 抛异常会被宿主吞掉（不影响卸载），但会中断你的后续清理 —— 逐项 try/catch。
    /// 这里必须做的三件事：停定时器、解绑事件、释放自建资源。
    /// </summary>
    public override void OnUnload()
    {
        try { _timer?.Dispose(); } catch { }
        _timer = null;

        // 若订阅过事件总线，务必在此调用取消订阅委托：
        // try { _offPet?.Invoke(); } catch { }   _offPet = null;

        // 若自建了 HttpClient / 窗口 / DispatcherTimer，在这里 Dispose / Close。
    }
}
