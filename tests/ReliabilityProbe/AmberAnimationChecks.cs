using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using DesktopPet.App.Core;
using DesktopPet.App.Renderer;
using DesktopPet.Core;

internal static partial class Program
{
    private static void RunAmberAnimationChecks(string root)
    {
        Check("uneven frame timing gives a short blink, preserves seeking and catches up after suspend", () =>
        {
            var state = SpriteAtlas.LoadSkin(Paths.Assets, "amber").States["idle"];
            Require(state.FrameDurations.Count == 4, "idle timing was lost in atlas loading");
            var animator = new FrameAnimator(); animator.SetState(state);
            animator.Tick(TimeSpan.FromSeconds(1.79)); Require(animator.FrameIndex == 0, "neutral hold ended early");
            animator.Tick(TimeSpan.FromSeconds(1.22)); Require(animator.FrameIndex == 2, "blink frame not reached");
            animator.Tick(TimeSpan.FromSeconds(.14)); Require(animator.FrameIndex == 3, "blink held too long");
            animator.SeekFrame(2); animator.Tick(TimeSpan.FromSeconds(.13));
            Require(animator.FrameIndex == 2, "seek retained old elapsed time");
            animator.Tick(TimeSpan.FromSeconds(.02)); Require(animator.FrameIndex == 3, "seek did not resume timing");
            animator.SeekFrame(0); animator.Playback = 2;
            animator.Tick(TimeSpan.FromSeconds(.91)); Require(animator.FrameIndex == 1, "playback multiplier ignored");
            animator.Playback = 1; animator.SeekFrame(0);
            animator.Tick(TimeSpan.FromSeconds(454000003.01));
            Require(animator.FrameIndex == 2, "suspend catch-up corrupted uneven timing");
            animator.Loop = false; animator.SeekFrame(0);
            animator.Tick(TimeSpan.FromSeconds(100));
            Require(animator.IsComplete && animator.FrameIndex == 3, "one-shot uneven animation did not finish");
            var pixel = SpriteAtlas.LoadSkin(Paths.Assets, "pixel");
            var walk = pixel.States["walk"];
            Field<Dictionary<string, SpriteState>>(pixel, "_states")["walk"] = new SpriteState
            { Name = "walk", Fps = 8, Frames = walk.Frames, FrameDurations = new double[] { 1, 1, 1, 1 } };
            Require(Math.Abs(new PetRenderer(pixel) { Scale = 4 }.WalkSpeed - 10) < .001, "stride ignored uneven cycle duration");
        });
        Check("work paws follow input, rest with embedded blinking and never override a drag", () =>
        {
            var renderer = new PetRenderer(SpriteAtlas.LoadSkin(Paths.Assets, "amber"));
            var animator = Field<FrameAnimator>(renderer, "_animator");
            renderer.Play(PetState.Work);
            Require(animator.FrameIndex == 1, "work started with a raised paw without input");
            renderer.Tick(TimeSpan.FromSeconds(1)); Require(animator.FrameIndex == 1, "resting cat kept typing");
            var neutral = Pixels(RenderAnimationFrame(renderer));
            renderer.SetTypingActive(TimeSpan.FromSeconds(.6));
            renderer.Tick(TimeSpan.FromSeconds(.3)); Require(animator.FrameIndex == 3, "typing did not animate paws");
            typeof(PetRenderer).GetField("_breathOffset", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(renderer, 0d);
            Require(neutral.SequenceEqual(Pixels(RenderAnimationFrame(renderer))), "fast typing repeated the embedded blink every cycle");
            renderer.Tick(TimeSpan.FromSeconds(.4)); Require(animator.FrameIndex == 1, "paws did not return to keyboard");
            typeof(PetRenderer).GetField("_nextBlinkAtMs", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(renderer, 0d);
            renderer.Tick(TimeSpan.FromMilliseconds(1)); Require(animator.FrameIndex == 3, "embedded rest blink missing");
            renderer.Tick(TimeSpan.FromMilliseconds(150)); Require(animator.FrameIndex == 1, "rest blink stuck");
            renderer.Play(PetState.Drag); renderer.Tick(TimeSpan.FromSeconds(.4));
            Require(animator.State!.Name == "drag" && animator.FrameIndex != 1, "work rest logic intercepted drag");
            renderer.SetAtlas(SpriteAtlas.LoadSkin(Paths.Assets, "pixel")); renderer.Play(PetState.Work);
            renderer.Tick(TimeSpan.FromSeconds(.5)); Require(animator.FrameIndex == 1, "legacy work loop changed");
        });
        Check("invalid frame timing and input-frame declarations are rejected", () =>
        {
            var directory = Path.Combine(root, "timing-fixtures", "timing"); Directory.CreateDirectory(directory);
            var source = JsonNode.Parse(File.ReadAllText(Path.Combine(Paths.Assets, "amber", "skin.json")))!.AsObject();
            source["id"] = "timing";
            var path = Path.Combine(directory, "skin.json");
            void Reject(Action<JsonObject> mutate)
            {
                var invalid = source.DeepClone().AsObject(); mutate(invalid); File.WriteAllText(path, invalid.ToJsonString());
                Require(SkinRegistry.Scan(Path.GetDirectoryName(directory)!).Single().IsBuiltinFallback, "invalid timing manifest accepted");
            }
            Reject(m => m["states"]![0]!["frameDurations"] = new JsonArray(1, 1, 0, 1));
            Reject(m => m["states"]![0]!["frameDurations"] = new JsonArray(1, 1));
            Reject(m => m["states"]![0]!["frameDurations"] = new JsonArray(1, 1, "fast", 1));
            Reject(m => m["states"]![0]!["frameDurations"] = new JsonArray(1, 1, 61, 1));
            Reject(m => m["states"]![8]!["typingRestFrame"] = 4);
            Reject(m => m["states"]![8]!["typingRestFrame"] = .5);
            Reject(m => m["states"]![8]!.AsObject().Remove("typingRestFrame"));
            Reject(m => m["states"]![0]!["typingRestFrame"] = 0);
            File.WriteAllText(path, source.ToJsonString());
            Require(SkinRegistry.InspectFolder(directory).States[8].BlinkFrame == 3, "valid embedded blink rejected");
        });
    }
}
