using DesktopPet.App.Core;
using DesktopPet.Core;
using DesktopPet.App.Renderer;

namespace DesktopPet.App.Services;

/// <summary>
/// IPetApi 的宿主实现：把 Core API 调用转发到渲染器和窗口。
/// 插件只能通过它影响宠物，无法触碰窗口内部。
/// </summary>
public sealed class PetApiImpl : IPetApi
{
    private readonly PetRenderer _renderer;
    private readonly Func<System.Windows.Point> _getWindowPos;
    private readonly Action<System.Windows.Point> _setWindowPos;
    private readonly Func<DesktopPet.Core.Rect2> _getWorkArea;

    /// <summary>
    /// UI 线程 Dispatcher。<b>显式注入</b>而不是去查 <c>Application.Current</c>：
    /// 后者在无 App 的场景（测试宿主、LoaderTest）为 null，会让封送静默退化成"直接调用"，
    /// 于是跨线程 bug 在测试里测不出来。注入窗口自己的 Dispatcher 则任何场景都成立。
    /// </summary>
    private readonly System.Windows.Threading.Dispatcher? _ui;

    public PetApiImpl(
        PetRenderer renderer,
        Func<System.Windows.Point> getWindowPos,
        Action<System.Windows.Point> setWindowPos,
        Func<DesktopPet.Core.Rect2> getWorkArea,
        System.Windows.Threading.Dispatcher? ui = null)
    {
        _renderer = renderer;
        _getWindowPos = getWindowPos;
        _setWindowPos = setWindowPos;
        _getWorkArea = getWorkArea;
        _ui = ui;
    }

    /// <summary>
    /// v0.1.82：**把调用封送到 UI 线程**（本类是插件影响宠物的唯一入口，所以一处封送 = 全部插件受益）。
    /// <para>为什么必须：插件的定时器回调跑在 <c>System.Threading.Timer</c> 的**线程池**线程上
    /// （见 <see cref="SchedulerImpl"/>），而 WPF 的窗口 / 气泡 / 可视化树 / 剪贴板全都有**线程亲和性**。
    /// 不封送就会抛「调用线程无法访问此对象」，而且这类异常常被上层 catch 吞掉 ——
    /// 表现为**静默失效**：到点时系统通知弹了、**猫的气泡却不弹**（还可能导致该定时器连续失败后被自动停用）。</para>
    /// <para>对照：<see cref="TrayManager"/> 的 Notify 早就有 <c>CheckAccess</c>，只有 IPetApi 这条链漏了。</para>
    /// </summary>
    private void OnUi(Action action)
    {
        var d = _ui ?? System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) { action(); return; }
        // 用 **BeginInvoke（不阻塞）** 而不是 Invoke：
        //   Say / SetMood / PlayAnimation 都是"发后不理"，没必要让线程池线程傻等 UI 线程；
        //   若多个插件定时器同时回调，阻塞式封送会迅速耗尽线程池（线程池饥饿）。
        // 注意：异步路径上的异常没人接得住（会变成 Dispatcher 未处理异常直接崩进程）→ 必须在这里兜住。
        d.BeginInvoke(new Action(() =>
        {
            try { action(); }
            catch (Exception ex) { Log.Warn($"[PetApi] UI 回调执行失败: {ex.Message}"); }
        }));
    }

    /// <summary>带返回值的封送：必须用阻塞式 <c>Invoke</c>（要拿结果）。
    /// 只在调用者本来就需要同步结果时使用（GetPosition / GetWorkArea），不会造成线程池饥饿。</summary>
    private T OnUi<T>(Func<T> f, T fallback)
    {
        var d = _ui ?? System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) return f();
        try { return d.Invoke(f); }
        catch (Exception ex)
        {
            Log.Warn($"[PetApi] UI 线程执行失败: {ex.Message}");
            return fallback;
        }
    }

    public Task PlayAnimationAsync(PetState state, AnimationOptions? opts = null)
    {
        OnUi(() => _renderer.Play(state, opts?.Loop ?? true));
        return Task.CompletedTask;
    }

    public void SetMood(PetMood mood)
    {
        OnUi(() => _renderer.SetMood(mood));
    }

    public void Say(string text, int durationMs = 3000)
    {
        OnUi(() => _renderer.Say(text, durationMs));
    }

    public Point2 GetPosition()
    {
        return OnUi(() =>
        {
            var p = _getWindowPos();
            return new Point2(p.X, p.Y);
        }, default);
    }

    public Task MoveToAsync(Point2 target, int durationMs = 500)
    {
        OnUi(() => _setWindowPos(new System.Windows.Point(target.X, target.Y)));
        return Task.CompletedTask;
    }

    public Rect2 GetWorkArea()
    {
        return OnUi(_getWorkArea, default);
    }
}
