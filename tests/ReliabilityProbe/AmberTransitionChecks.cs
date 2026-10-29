using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopPet.App.Core;
using DesktopPet.App.Renderer;
using DesktopPet.Core;

internal static partial class Program
{
    private static readonly (PetState From, PetState To, string Bridge, int EndFrame)[] AmberRoutes =
    [
        (PetState.Sit, PetState.Sleep, "sleep_enter", 0),
        (PetState.Sleep, PetState.Idle, "wake_idle", 0),
        (PetState.Sleep, PetState.Work, "wake_work", 1),
        (PetState.Pet, PetState.Idle, "pet_end_idle", 0),
        (PetState.Pet, PetState.Work, "pet_end_work", 1),
    ];

    private static void RunAmberTransitionChecks()
    {
        Check("Amber dedicated bridges keep exact canonical endpoints and reach resting work frame", () =>
        {
            var atlas = SpriteAtlas.InspectFolder(Path.Combine(Paths.Assets, "amber"));
            foreach (var route in AmberRoutes)
            {
                var bridge = atlas.States[route.Bridge];
                var from = atlas.States[route.From.ToString().ToLowerInvariant()];
                var to = atlas.States[route.To.ToString().ToLowerInvariant()];
                Require(Pixels(bridge.Frames[0].Bitmap).SequenceEqual(Pixels(from.Frames[0].Bitmap)), "bridge changed source endpoint: " + route.Bridge);
                Require(Pixels(bridge.Frames[^1].Bitmap).SequenceEqual(Pixels(to.Frames[route.EndFrame].Bitmap)), "bridge changed destination endpoint: " + route.Bridge);
                var renderer = new PetRenderer(atlas); var animator = Field<FrameAnimator>(renderer, "_animator");
                renderer.Play(route.From); renderer.Play(route.To);
                Require(renderer.IsTransitioning && animator.State!.Name == route.Bridge && !animator.Loop, "dedicated route not selected: " + route.Bridge);
                renderer.Tick(TimeSpan.FromSeconds(.15)); var progress = animator.FrameIndex;
                renderer.Play(route.To);
                Require(animator.FrameIndex == progress, "same target restarted the bridge");
                renderer.Tick(TimeSpan.FromSeconds(1));
                Require(!renderer.IsTransitioning && animator.State!.Name == to.Name && animator.Loop && animator.FrameIndex == route.EndFrame,
                    "bridge did not arrive at neutral destination: " + route.Bridge);
            }
        });
        Check("Amber bridges yield to drag, repeated petting, retargeting and skin reload", () =>
        {
            var atlas = SpriteAtlas.LoadSkin(Paths.Assets, "amber");
            foreach (var route in AmberRoutes)
            {
                foreach (var interrupt in new[] { PetState.Drag, PetState.Pet })
                {
                    var renderer = new PetRenderer(atlas); var animator = Field<FrameAnimator>(renderer, "_animator");
                    renderer.Play(route.From); renderer.Play(route.To); renderer.Tick(TimeSpan.FromSeconds(.15));
                    renderer.Play(interrupt); renderer.Tick(TimeSpan.FromSeconds(1));
                    Require(!renderer.IsTransitioning && animator.State!.Name == interrupt.ToString().ToLowerInvariant(), "old bridge overrode interaction");
                }
            }
            var changed = new PetRenderer(atlas); var animation = Field<FrameAnimator>(changed, "_animator");
            changed.Play(PetState.Sleep); changed.Play(PetState.Idle); changed.Play(PetState.Work);
            changed.Tick(TimeSpan.FromSeconds(1));
            Require(!changed.IsTransitioning && animation.State!.Name == "work", "new destination retained old wake target");
            changed.Play(PetState.Sleep); changed.Play(PetState.Work);
            changed.SetAtlas(SpriteAtlas.LoadSkin(Paths.Assets, "pixel")); changed.Play(PetState.Idle);
            changed.Tick(TimeSpan.FromSeconds(1));
            Require(!changed.IsTransitioning && animation.State!.Name == "idle", "skin reload retained an Amber bridge");
            var once = new PetRenderer(atlas); once.Play(PetState.Sleep); once.Play(PetState.Work, loop: false);
            Require(!once.IsTransitioning, "single-shot plugin animation acquired a bridge");
        });
        var output = Environment.GetEnvironmentVariable("NEKO_CAPTURE_AMBER_TRANSITIONS");
        if (!string.IsNullOrWhiteSpace(output)) CaptureAmberTransitionPreview(output);
    }

    private static void CaptureAmberTransitionPreview(string output)
    {
        Directory.CreateDirectory(output);
        var atlas = SpriteAtlas.LoadSkin(Paths.Assets, "amber"); var encoder = new GifBitmapEncoder();
        var total = 0;
        foreach (var route in AmberRoutes)
        {
            var renderer = new PetRenderer(atlas) { Scale = 4 }; renderer.Play(route.From);
            var frames = route.To == PetState.Sleep ? 28 : 24;
            for (var index = 0; index < frames; index++)
            {
                if (index == 7) renderer.Play(route.To);
                var visual = new DrawingVisual(); RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(35, 32, 29)), null, new Rect(0, 0, 280, 208));
                    dc.DrawText(new FormattedText($"{route.From} > {route.To}", System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, new Typeface("Segoe UI"), 16, Brushes.Wheat, 1), new Point(16, 12));
                    dc.PushTransform(new TranslateTransform(76, 48)); renderer.Draw(dc); dc.Pop();
                }
                var bitmap = new RenderTargetBitmap(280, 208, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
                var palette = new BitmapPalette(bitmap, 256);
                encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(bitmap, PixelFormats.Indexed8, palette, 0))); total++;
                if (index is 7 or 10 or 12 or 15 or 20)
                {
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                    using var still = File.Create(Path.Combine(output, $"{route.Bridge}-{index:D2}.png")); png.Save(still);
                }
                renderer.Tick(TimeSpan.FromSeconds(.05));
            }
        }
        SaveTimedCharacterGif(encoder, Path.Combine(output, "amber-transitions.gif"), total, 5);
    }
}
