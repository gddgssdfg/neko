using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using DesktopPet.App.Behavior;
using DesktopPet.App.Core;
using DesktopPet.App.Services;
using DesktopPet.App.Windows;
using DesktopPet.Builtin.Pomodoro;
using DesktopPet.Builtin.TodoList;
using DesktopPet.Core;

internal static partial class Program
{
    private static void RunFocusChecks()
    {
        Check("focus clock excludes paused time, never resets active work and completes once", () =>
        {
            long now = 0; var clock = new FocusClock(() => now);
            Require(clock.StartFocus(1, "task"), "start rejected");
            now = 30500; clock.Pause();
            Require(clock.Snapshot.Paused && clock.Snapshot.RemainingSeconds == 30, "pause lost elapsed time");
            Require(!clock.StartFocus(25), "double start reset paused work");
            now += 3600000; clock.Advance();
            Require(clock.Snapshot.RemainingSeconds == 30, "away time counted");
            clock.Resume(); now += 29499; clock.Advance();
            Require(clock.Snapshot.Stage == FocusStage.Focusing && clock.Snapshot.RemainingSeconds == 1, "ended early");
            now++; clock.Advance(); clock.Advance();
            Require(clock.Snapshot.Stage == FocusStage.RestReady && clock.Snapshot.CompletedSessions == 1, "completion repeated or missing");
            now += 3600000; clock.Advance();
            Require(clock.Snapshot.Stage == FocusStage.RestReady, "rest auto started");
            clock.StartRest(1); now += 60000; clock.Advance();
            Require(clock.Snapshot.Stage == FocusStage.FocusReady && clock.Snapshot.CompletedSessions == 1, "rest awarded work or auto restarted");
            clock.StartFocus(1); clock.Stop(); now += 600000; clock.Advance();
            Require(clock.Snapshot.Stage == FocusStage.Idle && clock.Snapshot.CompletedSessions == 1 && clock.Snapshot.TaskTitle == "", "stop awarded or retained task");
        });
        Check("focus plugin pauses on lock, notifies once and unload cancels queued work", () =>
        {
            long now = 0; var clock = new FocusClock(() => now);
            var scheduler = new FocusScheduler(); var system = new FocusSystem(); var bus = new EventBusImpl();
            var ui = new UIApiImpl(_ => null); using var scope = ui.PushScope("focus-test");
            var plugin = new PomodoroPlugin(clock);
            var data = Path.Combine(Paths.Data, "focus-test");
            var storage = new JsonPluginStorage(data); storage.Set("workMinutes", 1);
            FocusSnapshot? last = null; var remove = bus.On(CompanionEvents.FocusChanged, p => last = (FocusSnapshot)p!);
            plugin.OnLoad(new PluginContextImpl { HostVersion = new Version(1, 0), DataRoot = data, PluginDataDir = data,
                Pet = new ExtensionPet(), UI = ui, Capability = new CapabilityApiImpl { Storage = storage, Scheduler = scheduler, System = system, Events = bus } });
            try
            {
                Require(scheduler.Handles.Count == 0, "idle plugin started timer");
                var request = new FocusTaskRequest("fixture task"); bus.Emit(CompanionEvents.FocusTaskRequested, request);
                Require(request.Accepted && last?.TaskTitle == "fixture task", "todo request not handled");
                var active = scheduler.Handles.Single();
                now = 30000; bus.Emit(PetEvents.AppSleep); now += 3600000; bus.Emit(PetEvents.AppWake); active.Callback();
                Require(last!.Paused && last.RemainingSeconds == 30 && !active.IsActive, "lock did not pause or wake resumed");
                typeof(PomodoroPlugin).GetMethod("Primary", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(plugin, null);
                now += 30000; var tick = scheduler.Handles.Last(); tick.Callback(); tick.Callback();
                Require(system.Notices == 1 && last!.Stage == FocusStage.RestReady && !tick.IsActive, "completion did not cancel timer or repeated notification");
                var panel = Field<PomodoroWindow>(plugin, "_window");
                var capture = Environment.GetEnvironmentVariable("NEKO_CAPTURE_FOCUS");
                if (!string.IsNullOrEmpty(capture)) { Directory.CreateDirectory(capture); CaptureUiWindow(panel, Path.Combine(capture, "focus-complete.png")); }
                request = new FocusTaskRequest("next task"); bus.Emit(CompanionEvents.FocusTaskRequested, request);
                Require(request.Accepted && ReferenceEquals(panel, Field<PomodoroWindow>(plugin, "_window")), "opening task duplicated panel");
                var queued = scheduler.Handles.Last();
                Task.Run(queued.Callback).GetAwaiter().GetResult(); // queues a dispatcher callback from a scheduler worker
                plugin.OnUnload(); tick.Callback();
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                Require(last!.Stage == FocusStage.Idle && scheduler.Handles.All(h => !h.IsActive), "unload retained session");
                request = new FocusTaskRequest("after unload"); bus.Emit(CompanionEvents.FocusTaskRequested, request);
                Require(!request.Accepted, "unloaded plugin receives tasks");
            }
            finally { plugin.OnUnload(); remove(); }
        });
        Check("focus pose respects drag and restores preferences without showing keyboard", () =>
        {
            var config = new AppConfig { SafeMode = true, AppMode = "slack", KeyboardMirrorEnabled = false, SlackEdgeAttach = false };
            var pet = new PetWindow(config);
            try
            {
                pet.Show(); var bus = Field<EventBusImpl>(pet, "_eventBus"); var machine = Field<PetStateMachine>(pet, "_stateMachine");
                var state = new FocusSnapshot(FocusStage.Focusing, false, 60, 60, 0, "");
                bus.Emit(CompanionEvents.FocusChanged, state);
                Require(machine.Current == PetState.Work && config.AppMode == "slack" && !config.KeyboardMirrorEnabled, "focus persisted mode or did not work");
                machine.Play(PetState.Drag); bus.Emit(CompanionEvents.FocusChanged, state with { Paused = true });
                Require(machine.Current == PetState.Drag, "focus interrupted drag");
                machine.Play(PetState.Idle);
                typeof(PetWindow).GetMethod("SyncFocusPose", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(pet, null);
                Require(machine.Current == PetState.Sit, "deferred pause pose missing");
                bus.Emit(CompanionEvents.FocusChanged, state with { Stage = FocusStage.Idle });
                Require(machine.Current == PetState.Idle, "stop did not restore slack pose");
                int sleep = 0, wake = 0; bus.On(PetEvents.AppSleep, _ => sleep++); bus.On(PetEvents.AppWake, _ => wake++);
                void Session(string field, bool value) { typeof(PetWindow).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(pet, value); typeof(PetWindow).GetMethod("ApplySessionState", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(pet, null); }
                Session("_sessionLocked", true); Session("_systemSuspended", true); Session("_sessionLocked", false);
                Require(sleep == 1 && wake == 0, "combined lock/suspend emitted premature wake");
                Session("_systemSuspended", false); Require(wake == 1, "wake missing");
            }
            finally { pet.Dispose(); pet.Close(); }
        });
        Check("todo focus requires unfinished selection and does not mark it complete", () =>
        {
            var items = new List<TodoItem> { new() { Text = "unfinished" }, new() { Text = "done", Done = true } };
            int requests = 0;
            var window = new TodoWindow(items, _ => { }, new FocusSystem(), _ => { requests++; return "已开始"; });
            try
            {
                window.Show(); var button = UiChildren<Button>(window).Single(b => Equals(b.Content, "专注选中待办"));
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Require(requests == 0, "missing selection accepted");
                var list = Field<ListBox>(window, "_listBox"); list.SelectedIndex = 1;
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Require(requests == 0, "completed task accepted");
                list.SelectedIndex = 0; button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Require(requests == 1 && !items[0].Done, "task completed automatically");
            }
            finally { window.Close(); }
        });
    }
    private sealed class FocusScheduler : IScheduler
    {
        public List<FocusHandle> Handles { get; } = [];
        public IScheduleHandle Every(int intervalMs, Action callback) { var handle = new FocusHandle(callback); Handles.Add(handle); return handle; }
        public IScheduleHandle At(DateTime time, Action callback) => throw new NotSupportedException();
    }
    private sealed class FocusHandle(Action callback) : IScheduleHandle
    {
        public Action Callback { get; } = callback;
        public bool IsActive { get; private set; } = true;
        public void Dispose() => IsActive = false;
    }
    private sealed class FocusSystem : ISystem
    {
        public int Notices;
        public void Notify(string title, string body) => Notices++;
        public string? ReadClipboard() => null;
        public void WriteClipboard(string text) { }
        public void OpenUrl(string url) { }
        public string? PickFile(string filter = "") => null;
        public void Log(PluginLogLevel level, string message, Exception? exception = null) { }
    }
}
