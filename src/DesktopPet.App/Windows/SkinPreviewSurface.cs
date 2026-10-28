using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using DesktopPet.App.Core;
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
    public Func<string, int, SkinEyeOffset>? OffsetProvider { get; set; }
    public bool AnchorEditingEnabled { get; set; }
    public bool IsAnchorDragging => _anchorDragging;
    private bool _anchorDragging;
    private Point _dragStart;
    private SkinEyeOffset _dragInitial, _dragPreview;
    public event Action<SkinEyeOffset>? AnchorDragged;
    public SkinEyeOffset CurrentEyeOffset => _anchorDragging ? _dragPreview
        : OffsetProvider?.Invoke(State.Name, FrameIndex)
            ?? (State.EyeOffsets.Count > FrameIndex ? State.EyeOffsets[FrameIndex] : default);
    private bool _gazePreviewEnabled, _showUnclippedGaze;
    private Vector _gazeDirection;
    public bool GazePreviewEnabled { get => _gazePreviewEnabled; set { if (_gazePreviewEnabled == value) return; _gazePreviewEnabled = value; InvalidateVisual(); } }
    public Vector GazeDirection { get => _gazeDirection; set { if (_gazeDirection == value) return; _gazeDirection = value; InvalidateVisual(); } }
    public bool ShowUnclippedGaze { get => _showUnclippedGaze; set { if (_showUnclippedGaze == value) return; _showUnclippedGaze = value; InvalidateVisual(); } }
    private readonly Dictionary<string, ImageBrush?> _socketMasks = new();
    private BitmapSource? _gazeRaster;
    private (string State, string Key, SkinEyeOffset Anchor, Vector Gaze, bool Ghost)? _gazeRasterKey;
    private int _gazeRasterPadding;
    public bool CanPreviewGaze => State.EyeLayer is { } key && _atlas.GetEye(key) is { } eye
        && eye.PixelWidth == State.Frames[0].Bitmap.PixelWidth && eye.PixelHeight == State.Frames[0].Bitmap.PixelHeight;
    public Vector GazeLimits => CanPreviewGaze ? ToSourceFrame(EyeGazeMath.SourceLimits(_atlas, State.Name, State.EyeLayer!)) : default;
    public Vector GazeOffset => GazePreviewEnabled && CanPreviewGaze
        ? ToSourceFrame(EyeGazeMath.SourceOffset(_atlas, State.Name, State.EyeLayer!, EyeGazeMath.InputOffset(GazeDirection))) : default;
    private Vector ToSourceFrame(Vector value)
    {
        // The runtime has a square 32-DIP canvas, even for non-square source frames.
        var frame = State.Frames[0].Bitmap;
        return new Vector(value.X, value.Y * frame.PixelHeight / frame.PixelWidth);
    }
    public bool HasSocketMask => CanPreviewGaze && SocketMask() is not null;
    public bool GazeOutsideFrame
    {
        get
        {
            if (!CanPreviewGaze || !_eyeBounds.TryGetValue(State.EyeLayer!, out var box) || box.IsEmpty) return false;
            var anchor = CurrentEyeOffset; var gaze = GazeOffset; var bitmap = State.Frames[0].Bitmap;
            return box.X + anchor.X + gaze.X < 0 || box.Y + anchor.Y + gaze.Y < 0
                || box.X + box.Width + anchor.X + gaze.X > bitmap.PixelWidth
                || box.Y + box.Height + anchor.Y + gaze.Y > bitmap.PixelHeight;
        }
    }
    private ImageBrush? SocketMask()
    {
        var key = State.EyeLayer!;
        if (!_socketMasks.TryGetValue(key, out var mask))
        {
            var bitmap = State.Frames[0].Bitmap;
            mask = EyeGazeMath.SocketBrush(_atlas, key, key, new Size(bitmap.PixelWidth, bitmap.PixelHeight));
            _socketMasks[key] = mask;
        }
        return mask;
    }
    private static readonly Brush Checker = MakeChecker();

    public SkinPreviewSurface(SpriteAtlas atlas)
    {
        _atlas = atlas;
        foreach (var key in atlas.States.Values.Select(s => s.EyeLayer).OfType<string>().Distinct())
            if (atlas.GetEye(key) is { } eye) _eyeBounds[key] = SpriteAtlas.AlphaBBox(eye);
        MinHeight = 240; Focusable = true;
        SelectState("idle");
        RenderOptions.SetBitmapScalingMode(this, atlas.PixelArt ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
    }

    public void SelectState(string name)
    {
        CancelAnchorDrag();
        _animator.SetState(_atlas.States[name], restart: true);
        InvalidateVisual(); FrameChanged?.Invoke();
    }

    public void StepFrame(int delta) => SelectFrame((FrameIndex + delta % State.Frames.Length + State.Frames.Length) % State.Frames.Length);
    public void SelectFrame(int frame)
    {
        CancelAnchorDrag(); Playing = false;
        _animator.SeekFrame(frame);
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
        var (origin, zoom) = PreviewTransform();
        if (zoom <= 0) return;
        dc.PushClip(new RectangleGeometry(viewport));
        dc.PushTransform(new TranslateTransform(origin.X, origin.Y));
        dc.PushTransform(new ScaleTransform(zoom, zoom));
        dc.DrawImage(bitmap, new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
        var offset = CurrentEyeOffset;
        if (State.EyeLayer is { } key && _atlas.GetEye(key) is { } eye)
        {
            if (GazePreviewEnabled && CanPreviewGaze)
            {
                var image = GazeRaster(key, eye, offset);
                if (image is not null)
                    dc.DrawImage(image, new Rect(-_gazeRasterPadding, -_gazeRasterPadding, image.PixelWidth, image.PixelHeight));
                else
                {
                    var layer = new DrawingGroup();
                    RenderOptions.SetBitmapScalingMode(layer, BitmapScalingMode.LowQuality);
                    using (var drawing = layer.Open()) DrawEyePixels(drawing, key, eye, offset);
                    dc.DrawDrawing(layer);
                }
                DrawEyeGuides(dc, key, eye, offset, zoom);
            }
            else DrawEyeLayer(dc, key, eye, offset, zoom);
        }
        if (ShowGuides) dc.DrawRectangle(null, new Pen(Brushes.OrangeRed, 1 / zoom), new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
        dc.Pop(); dc.Pop(); dc.Pop();
    }

    private void DrawEyeLayer(DrawingContext dc, string key, BitmapSource eye, SkinEyeOffset offset, double zoom)
    {
        DrawEyePixels(dc, key, eye, offset);
        DrawEyeGuides(dc, key, eye, offset, zoom);
    }

    private BitmapSource? GazeRaster(string key, BitmapSource eye, SkinEyeOffset anchor)
    {
        var gaze = GazeOffset;
        var cacheKey = (State.Name, key, anchor, gaze, ShowUnclippedGaze);
        if (_gazeRasterKey == cacheKey) return _gazeRaster;
        _gazeRasterKey = cacheKey; _gazeRaster = null;
        var padding = (int)Math.Ceiling(Math.Max(Math.Abs(anchor.X + gaze.X), Math.Abs(anchor.Y + gaze.Y))) + 3;
        var width = (long)eye.PixelWidth + 2L * padding; var height = (long)eye.PixelHeight + 2L * padding;
        // Bound the additional raster allocation; large assets retain the direct drawing path.
        if (width > 16384 || height > 16384 || width * height > 4_000_000) return null;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.LowQuality);
        using (var drawing = visual.RenderOpen())
        {
            drawing.PushTransform(new TranslateTransform(padding, padding));
            DrawEyePixels(drawing, key, eye, anchor); drawing.Pop();
        }
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); _gazeRasterPadding = padding; _gazeRaster = bitmap;
        return bitmap;
    }

    private void DrawEyePixels(DrawingContext dc, string key, BitmapSource eye, SkinEyeOffset offset)
    {
        var gaze = GazeOffset;
        var mask = GazePreviewEnabled && CanPreviewGaze ? SocketMask() : null;
        dc.PushTransform(new TranslateTransform(offset.X, offset.Y));
        var eyeRect = new Rect(gaze.X, gaze.Y, eye.PixelWidth, eye.PixelHeight);
        if (mask is not null && ShowUnclippedGaze)
        { dc.PushOpacity(.3); dc.DrawImage(eye, eyeRect); dc.Pop(); }
        var clip = GazePreviewEnabled && CanPreviewGaze
            ? EyeGazeMath.ClipRect(_atlas, eye, key, gaze, new Size(eye.PixelWidth, eye.PixelHeight)) : null;
        if (clip is not null) dc.PushClip(new RectangleGeometry(clip.Value));
        if (mask is not null) dc.PushOpacityMask(mask);
        dc.DrawImage(eye, eyeRect);
        if (mask is not null) dc.Pop();
        if (clip is not null) dc.Pop();
        dc.Pop();
    }

    private void DrawEyeGuides(DrawingContext dc, string key, BitmapSource eye, SkinEyeOffset offset, double zoom)
    {
        var gaze = GazeOffset; var mask = GazePreviewEnabled && CanPreviewGaze ? SocketMask() : null;
        dc.PushTransform(new TranslateTransform(offset.X, offset.Y));
        if (ShowGuides && GazePreviewEnabled && CanPreviewGaze)
        {
            if (mask is not null)
            {
                dc.PushOpacityMask(mask);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(65, 25, 175, 105)), null,
                    new Rect(0, 0, eye.PixelWidth, eye.PixelHeight));
                dc.Pop();
            }
            if (_eyeBounds.TryGetValue(key, out var pupil) && !pupil.IsEmpty)
            {
                var limits = GazeLimits;
                dc.DrawEllipse(null, new Pen(Brushes.DarkOrange, 1 / zoom),
                    new Point(pupil.X + pupil.Width / 2d, pupil.Y + pupil.Height / 2d), limits.X, limits.Y);
            }
        }
        dc.Pop();
        if (ShowGuides)
        {
            var pen = new Pen(Brushes.DodgerBlue, 1 / zoom);
            if (_eyeBounds.TryGetValue(key, out var box) && !box.IsEmpty)
                dc.DrawRectangle(null, pen, new Rect(box.X + offset.X + gaze.X, box.Y + offset.Y + gaze.Y, box.Width, box.Height));
            dc.DrawLine(pen, new Point(offset.X - 3 / zoom, offset.Y), new Point(offset.X + 3 / zoom, offset.Y));
            dc.DrawLine(pen, new Point(offset.X, offset.Y - 3 / zoom), new Point(offset.X, offset.Y + 3 / zoom));
        }
    }

    private (Point Origin, double Zoom) PreviewTransform()
    {
        var bitmap = State.Frames[FrameIndex].Bitmap;
        var zoom = Math.Max(0, Math.Min((ActualWidth - 32) / bitmap.PixelWidth, (ActualHeight - 32) / bitmap.PixelHeight));
        return (new Point((ActualWidth - bitmap.PixelWidth * zoom) / 2, (ActualHeight - bitmap.PixelHeight * zoom) / 2), zoom);
    }

    // Public gesture methods also let regression probes exercise source-pixel/DPI mapping without native input injection.
    public bool BeginAnchorDrag(Point position)
    {
        if (_anchorDragging || GazePreviewEnabled || !AnchorEditingEnabled || State.EyeLayer is not { } key || !_eyeBounds.TryGetValue(key, out var box) || box.IsEmpty) return false;
        var (origin, zoom) = PreviewTransform();
        if (zoom <= 0) return false;
        var offset = CurrentEyeOffset;
        var hit = new Rect(origin.X + (box.X + offset.X) * zoom, origin.Y + (box.Y + offset.Y) * zoom, box.Width * zoom, box.Height * zoom);
        hit.Inflate(4, 4);
        if (!hit.Contains(position)) return false;
        Playing = false; _dragStart = position; _dragInitial = _dragPreview = offset; _anchorDragging = true;
        FrameChanged?.Invoke(); return true;
    }
    public void MoveAnchorDrag(Point position)
    {
        if (!_anchorDragging) return;
        var (_, zoom) = PreviewTransform(); if (zoom <= 0) return;
        var bitmap = State.Frames[FrameIndex].Bitmap;
        _dragPreview = new SkinEyeOffset(
            Math.Clamp(_dragInitial.X + Math.Round((position.X - _dragStart.X) / zoom), -bitmap.PixelWidth, bitmap.PixelWidth),
            Math.Clamp(_dragInitial.Y + Math.Round((position.Y - _dragStart.Y) / zoom), -bitmap.PixelHeight, bitmap.PixelHeight));
        InvalidateVisual(); FrameChanged?.Invoke();
    }
    public void EndAnchorDrag()
    {
        if (!_anchorDragging) return;
        var offset = _dragPreview; _anchorDragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        AnchorDragged?.Invoke(offset); InvalidateVisual(); FrameChanged?.Invoke();
    }
    public void CancelAnchorDrag()
    {
        var wasDragging = _anchorDragging;
        _anchorDragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
        if (wasDragging) FrameChanged?.Invoke();
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!BeginAnchorDrag(e.GetPosition(this))) return;
        Focus();
        if (!CaptureMouse()) CancelAnchorDrag();
        e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_anchorDragging) { MoveAnchorDrag(e.GetPosition(this)); e.Handled = true; }
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_anchorDragging) { EndAnchorDrag(); e.Handled = true; }
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && _anchorDragging) { CancelAnchorDrag(); e.Handled = true; }
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        CancelAnchorDrag(); FrameChanged?.Invoke();
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
