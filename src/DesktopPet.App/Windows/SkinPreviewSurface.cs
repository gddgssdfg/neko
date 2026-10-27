using System.Windows;
using System.Windows.Media;
using DesktopPet.App.Renderer;

namespace DesktopPet.App.Windows;

/// <summary>源素材检查，不叠加随机眨眼、呼吸或自动过渡，便于确定逐帧锚点。</summary>
public sealed class SkinPreviewSurface : FrameworkElement
{
    private readonly SpriteAtlas _atlas;
    private readonly Dictionary<string, Int32Rect> _eyeBounds = new();
    private readonly FrameAnimator _animator = new();
    public bool Playing { get; set; } = true;
    public bool ShowGuides { get; set; } = true;
    public int FrameIndex => _animator.FrameIndex;
    public SpriteState State => _animator.State!;
    public double Playback { get => _animator.Playback; set => _animator.Playback = value; }
    public event Action? FrameChanged;
    private static readonly Brush Checker = MakeChecker();

    public SkinPreviewSurface(SpriteAtlas atlas)
    {
        _atlas = atlas;
        foreach (var key in atlas.States.Values.Select(s => s.EyeLayer).OfType<string>().Distinct())
            if (atlas.GetEye(key) is { } eye) _eyeBounds[key] = SpriteAtlas.AlphaBBox(eye);
        MinHeight = 240;
        SelectState("idle");
        RenderOptions.SetBitmapScalingMode(this, atlas.PixelArt ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
    }

    public void SelectState(string name)
    {
        _animator.SetState(_atlas.States[name], restart: true);
        InvalidateVisual(); FrameChanged?.Invoke();
    }

    public void StepFrame(int delta)
    {
        Playing = false;
        _animator.SeekFrame((FrameIndex + delta % State.Frames.Length + State.Frames.Length) % State.Frames.Length);
        InvalidateVisual(); FrameChanged?.Invoke();
    }

    public void Tick(TimeSpan elapsed)
    {
        if (Playing && _animator.Tick(elapsed)) { InvalidateVisual(); FrameChanged?.Invoke(); }
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var viewport = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Checker, null, viewport);
        var bitmap = State.Frames[FrameIndex].Bitmap;
        var zoom = Math.Max(0, Math.Min((ActualWidth - 32) / bitmap.PixelWidth, (ActualHeight - 32) / bitmap.PixelHeight));
        if (zoom <= 0) return;
        var origin = new Point((ActualWidth - bitmap.PixelWidth * zoom) / 2, (ActualHeight - bitmap.PixelHeight * zoom) / 2);
        dc.PushClip(new RectangleGeometry(viewport));
        dc.PushTransform(new TranslateTransform(origin.X, origin.Y));
        dc.PushTransform(new ScaleTransform(zoom, zoom));
        dc.DrawImage(bitmap, new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
        var offset = State.EyeOffsets.Count > FrameIndex ? State.EyeOffsets[FrameIndex] : default;
        if (State.EyeLayer is { } key && _atlas.GetEye(key) is { } eye)
        {
            dc.DrawImage(eye, new Rect(offset.X, offset.Y, eye.PixelWidth, eye.PixelHeight));
            if (ShowGuides)
            {
                var pen = new Pen(Brushes.DodgerBlue, 1 / zoom);
                if (_eyeBounds.TryGetValue(key, out var box) && !box.IsEmpty)
                    dc.DrawRectangle(null, pen, new Rect(box.X + offset.X, box.Y + offset.Y, box.Width, box.Height));
                dc.DrawLine(pen, new Point(offset.X - 3 / zoom, offset.Y), new Point(offset.X + 3 / zoom, offset.Y));
                dc.DrawLine(pen, new Point(offset.X, offset.Y - 3 / zoom), new Point(offset.X, offset.Y + 3 / zoom));
            }
        }
        if (ShowGuides) dc.DrawRectangle(null, new Pen(Brushes.OrangeRed, 1 / zoom), new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
        dc.Pop(); dc.Pop(); dc.Pop();
    }

    private static Brush MakeChecker()
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(230, 228, 223)), null, new Rect(0, 0, 16, 16));
            var shade = new SolidColorBrush(Color.FromRgb(209, 207, 202));
            dc.DrawRectangle(shade, null, new Rect(0, 0, 8, 8));
            dc.DrawRectangle(shade, null, new Rect(8, 8, 8, 8));
        }
        var brush = new DrawingBrush(drawing) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 16, 16), Stretch = Stretch.None };
        brush.Freeze(); return brush;
    }
}
