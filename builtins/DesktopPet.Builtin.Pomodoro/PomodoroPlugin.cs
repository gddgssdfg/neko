using DesktopPet.Core;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.Pomodoro;

/// <summary>
/// 番茄钟插件（dogfooding 示范）：展示 IScheduler + ISystem.Notify 能力。
/// 右键菜单「番茄钟 25分」开始倒计时，结束用系统通知 + 宠物气泡提醒；「停止番茄」取消。
/// </summary>
public sealed class PomodoroPlugin : PluginBase
{
    private const int WorkMinutes = 25;

    private IScheduleHandle? _timer;
    private DateTime _endTime;

    public override string Id => "com.lantern.pomodoro";
    public override string Name => "番茄钟";

    public override PluginPermissions RequiredPermissions =>
        PluginPermissions.PetAnimate | PluginPermissions.UIRegister
        | PluginPermissions.Scheduler | PluginPermissions.Notify;

    public override void OnLoad(IPluginContext ctx)
    {
        base.OnLoad(ctx);

        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "pomo-start",
            Label = $"番茄钟 {WorkMinutes}分",
            Icon = "clock",
            Order = 7,
            OnInvoke = Start,
        });
        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "pomo-stop",
            Label = "停止番茄",
            Icon = "close",
            Order = 8,
            OnInvoke = Stop,
        });
    }

    private void Start()
    {
        Stop();
        _endTime = DateTime.Now.AddMinutes(WorkMinutes);
        Pet.Say($"番茄钟开始，{WorkMinutes} 分钟后提醒你休息", 3000);

        // 每秒检查一次剩余时间（IScheduler 演示）
        _timer = Capability.Scheduler.Every(1000, () =>
        {
            var remain = _endTime - DateTime.Now;
            if (remain <= TimeSpan.Zero)
            {
                Stop();
                Capability.System.Notify("Neko", "🍅 番茄钟结束，起来活动一下吧！");
                Pet.Say("番茄钟结束，休息一下吧！", 4000);
            }
        });
    }

    private void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public override void OnUnload() => Stop();
}
