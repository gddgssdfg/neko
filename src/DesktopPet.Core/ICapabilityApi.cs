namespace DesktopPet.Core;

/// <summary>插件私有 KV 存储（按 plugin.Id 隔离命名空间）</summary>
public interface IPluginStorage
{
    T? Get<T>(string key);
    void Set<T>(string key, T value);
    void Remove(string key);
    bool Contains(string key);
}

/// <summary>定时任务句柄</summary>
public interface IScheduleHandle : IDisposable
{
    bool IsActive { get; }
}

/// <summary>定时任务</summary>
public interface IScheduler
{
    IScheduleHandle Every(int intervalMs, Action callback);
    IScheduleHandle At(DateTime time, Action callback);
    IScheduleHandle Cron(string expression, Action callback);
}

/// <summary>系统能力</summary>
public interface ISystem
{
    void Notify(string title, string body);
    string? ReadClipboard();
    void WriteClipboard(string text);
    void OpenUrl(string url);
    string? PickFile(string filter = "");
}

/// <summary>事件总线：宿主 → 插件 单向广播为主</summary>
public interface IEventBus
{
    /// <summary>订阅宿主事件，返回取消订阅委托</summary>
    Action On(string evt, Action<object?> handler);
    /// <summary>向其他插件广播（需 network/UI 权限场景）</summary>
    void Emit(string evt, object? payload = null);
}

/// <summary>预定义事件名常量</summary>
public static class PetEvents
{
    public const string AppReady     = "app:ready";
    public const string AppWake      = "app:wake";
    public const string AppSleep     = "app:sleep";
    public const string PetPetted    = "pet:petted";
    public const string PetDragged   = "pet:dragged";
    public const string PetLanded    = "pet:landed";
    public const string PetEdge      = "pet:edge";
    public const string MouseMoved   = "mouse:moved";
    public const string ScreenChange = "screen:change";
    public const string Tick         = "tick";
}

/// <summary>事件 payload 强类型（v0.1.0+ 替代匿名类型，可跨程序集直接 cast）
/// 旧订阅代码（反射读 anonymous 字段）仍兼容；新代码建议直接 cast 到这些 record。</summary>
public sealed record MouseMovedArgs(double X, double Y);
public sealed record PetDraggedArgs(double X, double Y);
public sealed record PetEdgeArgs(int Side, bool Attached); // Side: 0左 1右 2上 3下

/// <summary>能力 API 聚合</summary>
public interface ICapabilityApi
{
    IPluginStorage Storage { get; }
    IScheduler Scheduler { get; }
    ISystem System { get; }
    IEventBus Events { get; }
}
