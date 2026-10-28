using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopPet.App.Core;

namespace DesktopPet.App.Renderer;

/// <summary>主程序与创作工作台共用的眼神限幅和遮罩坐标约定。</summary>
public static class EyeGazeMath
{
    public static bool IsFixedExpression(string? state, string key) => key is "eyes_blink" or "eyes_pet"
        || key.EndsWith("_closed", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("_half", StringComparison.OrdinalIgnoreCase) || state == "pet";

    public static Vector InputOffset(Vector direction, double gain = 1)
    {
        if (!double.IsFinite(direction.X) || !double.IsFinite(direction.Y)) direction = default;
        if (direction.LengthSquared > 1) direction.Normalize();
        gain = double.IsFinite(gain) ? Math.Clamp(gain, 0, 2) : 1;
        return direction * 2 * gain;
    }
    public static Vector SourceLimits(SpriteAtlas atlas, string? state, string key)
    {
        if (IsFixedExpression(state, key)) return default;
        var k = atlas.SourcePixelsPerLogical;
        var safe = atlas.GetEyeSafeShift(key);
        double x = (atlas.Motion?.GazeMaxX ?? .65) * k, y = (atlas.Motion?.GazeMaxY ?? .35) * k;
        if (safe.X > 0 || safe.Y > 0) { x = Math.Min(x, safe.X); y = Math.Min(y, safe.Y); }
        return new(x, y);
    }
    public static Vector SourceOffset(SpriteAtlas atlas, string? state, string key, Vector logicalOffset, double eyeDownLogical = 0)
    {
        var limits = SourceLimits(atlas, state, key);
        var k = atlas.SourcePixelsPerLogical;
        double x = Math.Clamp(logicalOffset.X * k, -limits.X, limits.X);
        double y = Math.Clamp((logicalOffset.Y + eyeDownLogical) * k, -limits.Y, limits.Y);
        var radius = Math.Sqrt((limits.X > 0 ? x * x / (limits.X * limits.X) : 0) + (limits.Y > 0 ? y * y / (limits.Y * limits.Y) : 0));
        if (radius > 1) { x /= radius; y /= radius; }
        return new(x, y);
    }
    public static Rect? ClipRect(SpriteAtlas atlas, BitmapSource eye, string key, Vector offset, Size target)
    {
        if (!atlas.TryGetEyeBBox(key, out var box) || box.Width <= 0 || box.Height <= 0) return null;
        var sx = target.Width / eye.PixelWidth; var sy = target.Height / eye.PixelHeight;
        var rect = new Rect(offset.X + box.X * sx, offset.Y + box.Y * sy, box.Width * sx, box.Height * sy);
        rect.Inflate(2, 2);
        return rect;
    }
    public static ImageBrush? SocketBrush(SpriteAtlas atlas, string? stateEyeLayer, string key, Size target)
    {
        if (string.IsNullOrEmpty(stateEyeLayer) || key == stateEyeLayer + "_closed"
            || atlas.GetEye(stateEyeLayer + "_closed") is not { } socket) return null;
        var brush = new ImageBrush(socket)
        {
            Stretch = Stretch.Fill, ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(new Point(), target), AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
        };
        brush.Freeze(); return brush;
    }
}
