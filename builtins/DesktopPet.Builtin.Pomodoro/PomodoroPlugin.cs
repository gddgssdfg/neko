using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DesktopPet.Core;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.Pomodoro;

public sealed class PomodoroPlugin : PluginBase
{
    private readonly FocusClock _clock;
    private IScheduleHandle? _timer;
    private Dispatcher _dispatcher = null!;
    private PomodoroWindow? _window;
    private Action? _removeSleep, _removeTask;
    private bool _loaded;
    public PomodoroPlugin() : this(new FocusClock()) { }
    public PomodoroPlugin(FocusClock clock) => _clock = clock;
    public override string Id => "com.lantern.pomodoro";
    public override string Name => "番茄钟";
    public override PluginPermissions RequiredPermissions => PluginPermissions.PetAnimate | PluginPermissions.UIRegister
        | PluginPermissions.Scheduler | PluginPermissions.Notify | PluginPermissions.Storage;

    public override void OnLoad(IPluginContext ctx)
    {
        base.OnLoad(ctx);
        _dispatcher = Dispatcher.CurrentDispatcher;
        _loaded = true;
        RegisterMenu();
        UI.RegisterRadialSlot(new RadialSlot { Id = "pomo-stop", Label = "停止番茄", Icon = "close", Order = 8, OnInvoke = () => Change(_clock.Stop) });
        UI.RegisterTrayMenuItem(new TrayMenuItem { Id = "pomo-panel", Label = "专注计时", OnClick = OpenPanel });
        UI.RegisterSettingsPage(new PomodoroSettingsPage(Capability.Storage, RegisterMenu));
        _removeSleep = Capability.Events.On(PetEvents.AppSleep, _ => Dispatch(() => Change(_clock.Pause)));
        _removeTask = Capability.Events.On(CompanionEvents.FocusTaskRequested, payload =>
        {
            if (!_loaded || payload is not FocusTaskRequest request || !_dispatcher.CheckAccess()) return;
            Change(() => request.Accepted = _clock.StartFocus(ReadMinutes("workMinutes", 25, 120), request.TaskTitle));
            request.Message = request.Accepted ? "已开始专注；完成计时后仍需手动勾选待办。" : "已有计时进行中，请先停止或完成当前一轮。";
            OpenPanel();
        });
    }

    private int ReadMinutes(string key, int fallback, int max) => Math.Clamp(
        Capability.Storage.Contains(key) ? Capability.Storage.Get<int>(key) : fallback, 1, max);
    private void RegisterMenu() => UI.RegisterRadialSlot(new RadialSlot { Id = "pomo-start",
        Label = $"专注 {ReadMinutes("workMinutes", 25, 120)}分", Icon = "clock", Order = 7, OnInvoke = OpenPanel });
    private void Dispatch(Action action)
    {
        if (!_loaded) return;
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(new Action(() => { if (_loaded) action(); }));
    }
    private void Change(Action action)
    {
        if (!_loaded) return;
        var before = _clock.Snapshot;
        action();
        var after = _clock.Snapshot;
        if (!_clock.Running) { _timer?.Dispose(); _timer = null; }
        else _timer ??= Capability.Scheduler.Every(1000, () => Dispatch(() => Change(_clock.Advance)));
        if (after != before) Capability.Events.Emit(CompanionEvents.FocusChanged, after);
        _window?.Update(after);
        if (before.Stage == FocusStage.Focusing && after.Stage == FocusStage.RestReady)
            Capability.System.Notify("Neko", "专注完成，起来活动一下吧。休息计时需要手动开始。");
        else if (before.Stage == FocusStage.Resting && after.Stage == FocusStage.FocusReady)
            Capability.System.Notify("Neko", "休息结束，准备好后再开始下一轮。");
    }
    private void Primary()
    {
        Change(() =>
        {
            var state = _clock.Snapshot;
            if (state.Stage == FocusStage.RestReady) _clock.StartRest(ReadMinutes("restMinutes", 5, 30));
            else if (state.Stage is FocusStage.Idle or FocusStage.FocusReady) _clock.StartFocus(ReadMinutes("workMinutes", 25, 120));
            else if (state.Paused) _clock.Resume();
            else _clock.Pause();
        });
    }
    private void OpenPanel()
    {
        if (!_loaded) return;
        if (_window is null)
        {
            _window = new PomodoroWindow(Primary, () => Change(_clock.Stop),
                () => Change(() => _clock.StartFocus(ReadMinutes("workMinutes", 25, 120))));
            _window.Closed += (_, _) => _window = null;
        }
        _window.Update(_clock.Snapshot);
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }
    public override void OnUnload()
    {
        if (!_loaded) return;
        _loaded = false;
        _timer?.Dispose(); _timer = null;
        _removeSleep?.Invoke(); _removeTask?.Invoke();
        _removeSleep = _removeTask = null;
        _window?.Close(); _window = null;
        _clock.Stop();
        Capability.Events.Emit(CompanionEvents.FocusChanged, _clock.Snapshot);
    }
    public override void Dispose() => OnUnload();
}

public sealed class PomodoroWindow : Window
{
    private readonly TextBlock _status = PluginUi.Text(""), _time = PluginUi.Text(""),
        _task = PluginUi.Text("", sub: true), _rounds = PluginUi.Text("", sub: true);
    private readonly Button _primary = PluginUi.Button("开始专注"), _stop = PluginUi.Button("停止"), _skip = PluginUi.Button("跳过休息，开始下一轮");
    public PomodoroWindow(Action primary, Action stop, Action skip)
    {
        Title = "Neko 专注计时"; Width = 440; Height = 490; MinWidth = 360; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        NekoDesign.Apply(this);
        var stack = new StackPanel { Margin = new Thickness(24) };
        stack.Children.Add(NekoDesign.ToolHeader("陪你专注一会儿", "专注和休息，都按自己的节奏来。"));
        _status.Margin = new Thickness(0, 20, 0, 8);
        _time.FontSize = 52; _time.HorizontalAlignment = HorizontalAlignment.Center;
        _task.TextWrapping = TextWrapping.Wrap; _task.Margin = new Thickness(0, 12, 0, 8);
        foreach (var text in new[] { _status, _time, _task, _rounds }) stack.Children.Add(text);
        foreach (var button in new[] { _primary, _stop, _skip }) { button.Margin = new Thickness(0, 12, 0, 0); stack.Children.Add(button); }
        _primary.Click += (_, _) => primary(); _stop.Click += (_, _) => stop(); _skip.Click += (_, _) => skip();
        var hint = PluginUi.Text("关闭面板后计时继续。锁屏或休眠会暂停，回来后手动继续。进度仅保留在本次运行中。", sub: true);
        hint.TextWrapping = TextWrapping.Wrap; hint.Margin = new Thickness(0, 16, 0, 0); stack.Children.Add(hint);
        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    public void Update(FocusSnapshot state)
    {
        _status.Text = state.Paused ? "已暂停" : state.Stage switch { FocusStage.Focusing => "正在专注", FocusStage.RestReady => "专注完成，休息一下", FocusStage.Resting => "正在休息", FocusStage.FocusReady => "休息结束", _ => "准备开始" };
        _time.Text = $"{state.RemainingSeconds / 60:00}:{state.RemainingSeconds % 60:00}";
        _task.Text = string.IsNullOrEmpty(state.TaskTitle) ? "一次只专注一件事" : state.TaskTitle;
        _rounds.Text = $"本次运行已完成 {state.CompletedSessions} 轮专注";
        _primary.Content = state.Paused ? "继续" : state.Stage switch { FocusStage.Focusing or FocusStage.Resting => "暂停", FocusStage.RestReady => "开始休息", _ => "开始专注" };
        _stop.IsEnabled = state.Stage != FocusStage.Idle;
        _skip.Visibility = state.Stage == FocusStage.RestReady ? Visibility.Visible : Visibility.Collapsed;
    }
}
