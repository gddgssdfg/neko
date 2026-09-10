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

    public PetApiImpl(
        PetRenderer renderer,
        Func<System.Windows.Point> getWindowPos,
        Action<System.Windows.Point> setWindowPos,
        Func<DesktopPet.Core.Rect2> getWorkArea)
    {
        _renderer = renderer;
        _getWindowPos = getWindowPos;
        _setWindowPos = setWindowPos;
        _getWorkArea = getWorkArea;
    }

    public Task PlayAnimationAsync(PetState state, AnimationOptions? opts = null)
    {
        _renderer.Play(state, opts?.Loop ?? true);
        return Task.CompletedTask;
    }

    public void SetMood(PetMood mood)
    {
        _renderer.SetMood(mood);
    }

    public void Say(string text, int durationMs = 3000)
    {
        _renderer.Say(text, durationMs);
    }

    public Point2 GetPosition()
    {
        var p = _getWindowPos();
        return new Point2(p.X, p.Y);
    }

    public Task MoveToAsync(Point2 target, int durationMs = 500)
    {
        _setWindowPos(new System.Windows.Point(target.X, target.Y));
        return Task.CompletedTask;
    }

    public Rect2 GetWorkArea()
    {
        return _getWorkArea();
    }
}
