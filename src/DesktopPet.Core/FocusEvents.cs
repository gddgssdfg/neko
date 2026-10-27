namespace DesktopPet.Core;

public enum FocusStage { Idle, Focusing, RestReady, Resting, FocusReady }

/// <summary>只在当前进程广播，不持久化待办标题或计时进度。</summary>
public sealed record FocusSnapshot(FocusStage Stage, bool Paused, int RemainingSeconds,
    int TotalSeconds, int CompletedSessions, string TaskTitle);

public static class CompanionEvents
{
    public const string FocusChanged = "companion:focus-changed";
    public const string FocusTaskRequested = "companion:focus-task-requested";
}

/// <summary>UI 线程同步请求；接收方在 Emit 返回前填写结果，不保留请求引用。</summary>
public sealed class FocusTaskRequest(string taskTitle)
{
    public string TaskTitle { get; } = taskTitle;
    public bool Accepted { get; set; }
    public string Message { get; set; } = "请先在插件中心启用番茄钟。";
}
