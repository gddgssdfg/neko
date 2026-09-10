namespace DesktopPet.Core;

/// <summary>宠物控制 API</summary>
public interface IPetApi
{
    /// <summary>播放某个动画（对应雪碧图状态）</summary>
    Task PlayAnimationAsync(PetState state, AnimationOptions? opts = null);

    /// <summary>设置宠物情绪</summary>
    void SetMood(PetMood mood);

    /// <summary>头顶说话气泡</summary>
    void Say(string text, int durationMs = 3000);

    /// <summary>获取当前像素位置（左上角）</summary>
    Point2 GetPosition();

    /// <summary>平滑移动到目标位置</summary>
    Task MoveToAsync(Point2 target, int durationMs = 500);

    /// <summary>可用桌面区域（扣除任务栏）</summary>
    Rect2 GetWorkArea();
}

/// <summary>动画播放选项</summary>
public sealed record AnimationOptions
{
    public bool Loop { get; init; } = true;
    public bool Interrupt { get; init; } = true;
    public double FrameRateOverride { get; init; } = -1;
}
