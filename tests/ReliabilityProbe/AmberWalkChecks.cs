using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopPet.App.Core;
using DesktopPet.App.Renderer;
using DesktopPet.Core;

internal static partial class Program
{
    private static void RunAmberWalkChecks()
    {
        Check("Amber right-facing gait mirrors every frame and keeps idle orientation", () =>
        {
            var atlas = SpriteAtlas.LoadSkin(Paths.Assets, "amber");
            var walk = atlas.States["walk"];
            Require(walk.Facing == SkinFacing.Right, "right-facing artwork has no direction declaration");
            var alphaBounds = typeof(SpriteAtlas).GetMethod("AlphaBBox", BindingFlags.Static | BindingFlags.NonPublic)!;
            var ground = walk.Frames.Select(frame => (Int32Rect)alphaBounds.Invoke(null, [frame.Bitmap])!).ToArray();
            Require(ground.All(box => !box.IsEmpty && box.X > 0 && box.Y > 0 && box.X + box.Width < 256 && box.Y + box.Height < 247), "gait lost transparent margins");
            Require(ground.Max(box => box.Y + box.Height) - ground.Min(box => box.Y + box.Height) <= 1, "contact baseline shifts between frames");
            var renderer = new PetRenderer(atlas) { Scale = 4 }; renderer.Play(PetState.Walk);
            var animator = Field<FrameAnimator>(renderer, "_animator");
            for (var frame = 0; frame < walk.Frames.Length; frame++)
            {
                animator.SeekFrame(frame); renderer.SetWalkDirection(75);
                var right = Pixels(RenderAnimationFrame(renderer));
                renderer.SetWalkDirection(-75); var left = Pixels(RenderAnimationFrame(renderer));
                Require(!left.SequenceEqual(right), "heading change did not turn the cat");
                for (var y = 0; y < 128; y++)
                for (var x = 0; x < 128; x++)
                for (var channel = 0; channel < 4; channel++)
                    Require(left[(y * 128 + x) * 4 + channel] == right[(y * 128 + 127 - x) * 4 + channel], "gait frame mirrored incorrectly");
            }
            renderer.Play(PetState.Idle); var idle = Pixels(RenderAnimationFrame(renderer));
            renderer.SetWalkDirection(75);
            Require(idle.SequenceEqual(Pixels(RenderAnimationFrame(renderer))), "walking heading leaked into idle pose");
            Require(Math.Abs(renderer.WalkSpeed - 75) < .001, "default gait speed changed");
            renderer.Scale = 2; Require(Math.Abs(renderer.WalkSpeed - 37.5) < .001, "gait speed ignored size");
            renderer.SetMood(PetMood.Happy); Require(Math.Abs(renderer.WalkSpeed - 48.75) < .001, "gait speed ignored mood");
        });
        var output = Environment.GetEnvironmentVariable("NEKO_CAPTURE_AMBER_WALK");
        if (!string.IsNullOrWhiteSpace(output)) CaptureAmberWalkPreview(output);
    }

    private static void CaptureAmberWalkPreview(string output)
    {
        Directory.CreateDirectory(output);
        var renderer = new PetRenderer(SpriteAtlas.LoadSkin(Paths.Assets, "amber")) { Scale = 4 };
        renderer.Play(PetState.Walk);
        var encoder = new GifBitmapEncoder();
        for (var index = 0; index < 48; index++)
        {
            var right = index < 24;
            renderer.SetWalkDirection(right ? renderer.WalkSpeed : -renderer.WalkSpeed);
            var x = 50 + renderer.WalkSpeed * (right ? index : 48 - index) * .1;
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(35, 32, 29)), null, new Rect(0, 0, 400, 200));
                dc.DrawText(new FormattedText(right ? "Walk right" : "Walk left", System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 16, Brushes.Wheat, 1), new Point(16, 12));
                var floor = new Pen(new SolidColorBrush(Color.FromRgb(100, 89, 75)), 1);
                dc.DrawLine(floor, new Point(8, 175), new Point(392, 175));
                for (var mark = 16; mark < 400; mark += 32) dc.DrawLine(floor, new Point(mark, 175), new Point(mark, 180));
                dc.PushTransform(new TranslateTransform(x, 50)); renderer.Draw(dc); dc.Pop();
            }
            var bitmap = new RenderTargetBitmap(400, 200, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var palette = new BitmapPalette(bitmap, 256);
            encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(bitmap, PixelFormats.Indexed8, palette, 0)));
            if (index is 0 or 2 or 4 or 5 or 24)
            {
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using var still = File.Create(Path.Combine(output, $"amber-walk-{index:D2}.png")); png.Save(still);
            }
            renderer.Tick(TimeSpan.FromSeconds(.1));
        }
        SaveTimedCharacterGif(encoder, Path.Combine(output, "amber-walk.gif"), 48, 10);
    }
}
