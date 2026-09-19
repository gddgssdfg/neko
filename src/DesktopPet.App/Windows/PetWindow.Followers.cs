// v0.1.84：从 PetWindow.cs 按功能区域拆出（审计 P2-11）——浮层跟随 + 气泡对话
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DesktopPet.App.Behavior;
using DesktopPet.App.Core;
using DesktopPet.App.Renderer;
using DesktopPet.App.Services;
using DesktopPet.Core;

namespace DesktopPet.App.Windows;

public sealed partial class PetWindow : Window
{
    /// <summary>
    /// v0.1.39：所有浮层都以猫为锚点跟随（灯灯要求"所有东西都最好能绑定猫的位置去生成，不要乱飘"）。
    /// 覆盖拖拽 / 落体 / 边缘吸附 / 平滑位移 全部位移来源。
    /// </summary>
    private void SyncFollowers()
    {
        if (_radialMenu is { IsVisible: true })
            _radialMenu.MoveTo(Left + Width / 2, Top + Height / 2);
        SyncMirrorToCat();
        SyncBubblePosition();
        SyncHeartPosition();   // v0.1.59(⑧)：爱心浮层随猫（头部锚点）
    }

    /// <summary>
    /// v0.1.82：**浮层窗口定位的同值短路**。
    /// <para>为什么需要：WPF 的 <c>Window.Left/Top</c> setter **不做同值判断** —— 只要赋值就走一遍
    /// Win32 `SetWindowPos`（会触发窗口重排/合成）。而主循环是 20Hz，<see cref="SyncFollowers"/>
    /// 每 tick 都会调下面三个 Sync：猫不动的时候，键盘映射窗（work 模式下是个 600×200 的分层窗口）
    /// 每秒被无意义地"挪"20 次。这里比上一次的位置，没变就直接返回。</para>
    /// <para>容差 0.01 DIP：小于一个设备像素的差异不补，避免浮点噪声导致短路失效。</para>
    /// </summary>
    private static void MoveWindowIfNeeded(Window w, double left, double top)
    {
        if (Math.Abs(w.Left - left) < 0.01 && Math.Abs(w.Top - top) < 0.01) return;
        w.Left = left;
        w.Top = top;
    }

    /// <summary>v0.1.59(⑧)：爱心浮层对齐到猫头顶上方——浮层中心 = 猫头中心略上。</summary>
    private void SyncHeartPosition()
    {
        if (_hearts is not { IsVisible: true }) return;
        // 头部锚点约在窗口上方 1/4 处（眼球层 HEAD_CY≈11/32）；浮层以自己的中心对齐该点
        double anchorX = Left + Width / 2;
        double anchorY = Top + Height * 0.30;
        MoveWindowIfNeeded(_hearts,
            anchorX - _hearts.Width / 2,
            anchorY - _hearts.Height / 2);
    }

    /// <summary>
    /// v0.1.40：气泡贴猫头顶上方居中（尾巴朝下指向猫），随猫移动。
    /// 由 SyncFollowers 每帧调用，覆盖拖拽/落体/吸附/平滑位移所有位移来源。
    /// </summary>
    private void SyncBubblePosition()
    {
        if (_bubble is not { IsVisible: true }) return;
        MoveWindowIfNeeded(_bubble,
            Left + Width / 2 - _bubble.Width / 2,
            Top - _bubble.Height - 4);
    }

    /// <summary>v0.1.40：插件 Say → 像素云朵气泡（所有插件通知统一走这里；开关关闭则静默）</summary>
    private void ShowBubble(string text, int durationMs)
    {
        if (!_config.ChatEnabled) return;
        _bubble ??= new BubbleWindow();
        _bubble.ShowBubble(text, durationMs);
        SyncBubblePosition();   // 立即定位（不等主循环下一帧）
    }

    /// <summary>v0.1.40：运行时切换气泡对话（设置页调用）。关=停调度+清气泡；开=建 tracker/调度器。</summary>
    public void ApplyChatEnabled(bool enabled)
    {
        _config.ChatEnabled = enabled;
        ConfigManager.Save(Paths.ConfigFile, _config);
        if (!enabled)
        {
            _chatScheduler?.Stop();
            _bubble?.Dismiss();
            return;
        }
        _activity ??= new ActivityTracker(System.IO.Path.Combine(Paths.Data, "host", "activity.json"));
        _chatScheduler ??= new ChatScheduler(
            ShowBubble, _activity,
            System.IO.Path.Combine(Paths.Data, "plugins", "com.lantern.affection", "score.json"),
            _config.ChatRestMinutes,
            _config.CatName, _config.OwnerTitle);   // v0.1.41：注入称呼/猫名到台词模板
        _chatScheduler.Start();
    }

    /// <summary>v0.1.41：设置页改了称呼/猫名后调用——重建调度器使新值生效（空 = 兜底默认）</summary>
    public void RebuildChatScheduler()
    {
        if (!_config.ChatEnabled) return;
        _chatScheduler?.Stop();
        _chatScheduler = null;
        ApplyChatEnabled(true);
    }
}
