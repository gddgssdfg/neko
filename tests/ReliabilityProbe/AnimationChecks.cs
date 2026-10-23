using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopPet.App.Behavior;
using DesktopPet.App.Core;
using DesktopPet.App.Renderer;
using DesktopPet.App.Services;
using DesktopPet.App.Windows;
using DesktopPet.Core;

internal static partial class Program
{
    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;

    private static void RunAnimationChecks()
    {
        Check("non-looping animation holds its last frame", () =>
        {
            var renderer = new PetRenderer(SpriteAtlas.LoadSkin(Paths.Assets, "pixel"));
            renderer.Play(PetState.Walk, loop: false);
            renderer.Tick(TimeSpan.FromSeconds(0.625));
            var animator = Field<FrameAnimator>(renderer, "_animator");
            Require(animator.FrameIndex == animator.State!.Frames.Length - 1, "loop:false wrapped to an earlier frame");
        });
        Check("fall starts with the falling pose and announces landing only after settling", () =>
        {
            var pet = new PetWindow(new AppConfig { Skin = "pixel", WalkEnabled = false });
            var bus = Field<EventBusImpl>(pet, "_eventBus");
            var landed = 0;
            var unsubscribe = bus.On(PetEvents.PetLanded, _ => landed++);
            try
            {
                pet.Top = SystemParameters.WorkArea.Bottom - pet.Height - 100;
                Field<PetStateMachine>(pet, "_stateMachine").Play(PetState.Drag);
                typeof(PetWindow).GetMethod("StartFall", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(pet, new object[] { new Vector() });
                Require(Field<PetStateMachine>(pet, "_stateMachine").Current == PetState.Fall, "fall retained drag pose");
                Require(landed == 0, "landed was emitted while airborne");
                PumpUntil(() => Field<DispatcherTimer?>(pet, "_fallTimer") is null);
                Require(landed == 1, "landing event was missing or repeated for bounces");
                Require(Field<PetStateMachine>(pet, "_stateMachine").Current == PetState.Idle, "settled pet never returned to idle");
                var renderer = Field<PetRenderer>(pet, "_renderer");
                Require(renderer.IsLanding, "landing animation was not started");
                typeof(PetWindow).GetMethod("UpdateEdgeSnap", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(pet, null);
                Require(renderer.IsLanding && Field<FrameAnimator>(renderer, "_animator").State!.Name == "land", "edge snap interrupted landing");
            }
            finally { unsubscribe(); Field<DispatcherTimer?>(pet, "_fallTimer")?.Stop(); pet.Dispose(); pet.Close(); }
        });
        Check("disposing during a fall stops the physics timer", () =>
        {
            var pet = new PetWindow(new AppConfig());
            DispatcherTimer? timer = null;
            try
            {
                typeof(PetWindow).GetMethod("StartFall", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(pet, new object[] { new Vector() });
                timer = Field<DispatcherTimer>(pet, "_fallTimer");
                pet.Dispose();
                Require(!timer.IsEnabled, "fall physics continued after disposal");
            }
            finally { timer?.Stop(); pet.Dispose(); pet.Close(); }
        });
        Check("landing plays once, returns to idle and respects a new interaction", () =>
        {
            var renderer = new PetRenderer(SpriteAtlas.LoadSkin(Paths.Assets, "pixel"));
            var animator = Field<FrameAnimator>(renderer, "_animator");
            renderer.PlayLanding();
            Require(renderer.IsLanding && renderer.HasTransientVisual, "landing was not considered active");
            renderer.Tick(TimeSpan.FromSeconds(1));
            Require(!renderer.IsLanding && animator.State!.Name == "idle", "landing did not finish at idle");
            renderer.PlayLanding();
            renderer.Play(PetState.Drag);
            renderer.Tick(TimeSpan.FromSeconds(1));
            Require(animator.State!.Name == "drag", "finished landing overwrote the new interaction");
            renderer.Play(PetState.Walk, loop: false);
            renderer.Tick(TimeSpan.FromSeconds(1));
            renderer.Play(PetState.Walk, loop: true);
            renderer.Tick(TimeSpan.FromSeconds(0.125));
            Require(animator.FrameIndex == 1 && !animator.IsComplete, "restarting a completed one-shot stayed frozen");
        });
        Check("eye anchors move pupils and blink layers with frame motion and typing breath", () =>
        {
            var atlas = SpriteAtlas.LoadSkin(Paths.Assets, "pixel");
            var renderer = new PetRenderer(atlas) { Scale = 1 };
            var animator = Field<FrameAnimator>(renderer, "_animator");
            animator.SetState(new SpriteState
            {
                Name = "anchor-test", Fps = 8, Frames = Enumerable.Repeat(atlas.States["idle"].Frames[0], 4).ToArray(),
                EyeLayer = "eyes_open", EyeOffsets = new[] { default(SkinEyeOffset), new SkinEyeOffset(2, 1), default, default },
            });
            bool WhiteAt(BitmapSource bitmap, int x, int y)
            {
                var pixel = new byte[4];
                bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
                return pixel[0] == 255 && pixel[1] == 255 && pixel[2] == 255 && pixel[3] == 255;
            }
            Require(WhiteAt(RenderAnimationFrame(renderer), 9, 10), "fixture eye highlight missing");
            renderer.Tick(TimeSpan.FromSeconds(0.125));
            var moved = RenderAnimationFrame(renderer);
            Require(WhiteAt(moved, 11, 11) && !WhiteAt(moved, 9, 10), "pupils did not follow frame anchor");
            typeof(PetRenderer).GetField("_breathOffset", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(renderer, 2d);
            Require(WhiteAt(RenderAnimationFrame(renderer), 11, 13), "typing breath moved the body without its eyes");
            var blink = typeof(PetRenderer).GetField("_blinkPhase", BindingFlags.NonPublic | BindingFlags.Instance)!;
            blink.SetValue(renderer, Enum.Parse(blink.FieldType, "Closed"));
            var withAnchor = RenderAnimationFrame(renderer);
            animator.SetState(new SpriteState { Name = "unanchored", Fps = 0, Frames = animator.State!.Frames, EyeLayer = "eyes_open" });
            Require(!Pixels(withAnchor).SequenceEqual(Pixels(RenderAnimationFrame(renderer))), "blink ignored frame anchors");
        });
        Check("skin eye anchors are optional, finite and one entry per frame", () =>
        {
            var root = Path.Combine(Paths.Data, "anchor-fixtures");
            var dir = Path.Combine(root, "anchored");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "skin.json");
            var valid = """{"id":"anchored","frames":4,"states":[{"name":"idle","fps":4,"eyeOffsets":[{"x":0,"y":0},{"x":1,"y":-1},{"x":0,"y":0},{"x":0,"y":0}]}]}""";
            File.WriteAllText(path, valid);
            Require(SkinRegistry.Scan(root).Single().States[0].EyeOffsets[1] == new SkinEyeOffset(1, -1), "valid anchor lost");
            foreach (var bad in new[] { valid.Replace("\"x\":1", "\"x\":1e999"), valid.Replace("\"x\":1", "\"x\":33"), valid.Replace(",{\"x\":1,\"y\":-1}", "") })
            {
                File.WriteAllText(path, bad);
                Require(SkinRegistry.Scan(root).Single().IsBuiltinFallback, "invalid anchor accepted");
            }
            Require(SkinRegistry.Resolve(Paths.Assets, "soft").States.All(s => s.EyeOffsets.Count == 0), "legacy skin acquired offsets");
            Require(SkinRegistry.Resolve(Paths.Assets, "soft").States.All(s => s.Facing == SkinFacing.None), "legacy skin acquired directional mirroring");
        });
        Check("walking direction mirrors the complete sprite and returns to the original resting orientation", () =>
        {
            var renderer = new PetRenderer(SpriteAtlas.LoadSkin(Paths.Assets, "pixel")) { Scale = 1 };
            renderer.Play(PetState.Walk);
            renderer.SetWalkDirection(-72);
            var left = Pixels(RenderAnimationFrame(renderer));
            renderer.SetWalkDirection(72);
            var right = Pixels(RenderAnimationFrame(renderer));
            for (var y = 0; y < 32; y++)
            for (var x = 0; x < 32; x++)
            for (var c = 0; c < 4; c++)
                Require(left[(y * 32 + x) * 4 + c] == right[(y * 32 + 31 - x) * 4 + c], "walking was not mirrored consistently");
            renderer.Play(PetState.Idle);
            var idle = Pixels(RenderAnimationFrame(renderer));
            renderer.SetWalkDirection(-72);
            Require(idle.SequenceEqual(Pixels(RenderAnimationFrame(renderer))), "walk direction flipped the resting design");
        });
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static RenderTargetBitmap RenderAnimationFrame(PetRenderer renderer)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        using (var context = visual.RenderOpen()) renderer.Draw(context);
        var bitmap = new RenderTargetBitmap((int)(32 * renderer.Scale), (int)(32 * renderer.Scale), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static void CaptureAnimations(string output)
    {
        Directory.CreateDirectory(output);
        var atlas = SpriteAtlas.LoadSkin(Paths.Assets, "pixel");
        foreach (var state in atlas.States.Values)
        {
            var renderer = new PetRenderer(atlas) { Scale = 4 };
            if (state.Name == "land") renderer.PlayLanding();
            else renderer.Play(Enum.Parse<PetState>(state.Name, ignoreCase: true));
            for (var frame = 0; frame < state.Frames.Length; frame++)
            {
                var bitmap = RenderAnimationFrame(renderer);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(Path.Combine(output, $"{state.Name}-{frame}.png"))) encoder.Save(stream);
                if (state.Fps > 0) renderer.Tick(TimeSpan.FromSeconds(1 / state.Fps + 0.00001));
            }
        }
    }
}
