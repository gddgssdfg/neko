using System.Windows;
using System.Windows.Media;

namespace DesktopPet.App.Renderer;

/// <summary>
/// 承载 DrawingVisual 的 UIElement，作为窗口内容。
/// 每帧动画推进时触发 InvalidateVisual 重绘。
/// </summary>
public sealed class DrawingVisualHost : FrameworkElement
{
    private readonly PetRenderer _renderer;

    public DrawingVisualHost(PetRenderer renderer)
    {
        _renderer = renderer;
        _renderer.RenderInvalidated += InvalidateVisual;
    }

    protected override void OnRender(DrawingContext dc)
    {
        _renderer.Draw(dc);
    }

    // 自报绘制尺寸：宿主按精灵实际大小测量，配合固定尺寸窗口保证像素精确对齐
    protected override Size MeasureOverride(Size availableSize) =>
        new(SpriteAtlas.FrameW * _renderer.Scale, SpriteAtlas.FrameH * _renderer.Scale);
}
