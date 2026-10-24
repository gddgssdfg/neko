using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopPet.App.Core;
using DesktopPet.App.Renderer;
using DesktopPet.App.Windows;
using DesktopPet.Core;

internal static partial class Program
{
    private static void RunEyeTrackingChecks()
    {
        Check("gaze returns to neutral near the face and ramps up with distance", () =>
        {
            var compute = typeof(PetWindow).GetMethod("ComputeGazeDirection", BindingFlags.NonPublic | BindingFlags.Static)!;
            Vector Direction(Vector delta) => (Vector)compute.Invoke(null, new object[] { delta, 100d })!;
            Require(Direction(default).Length == 0 && Direction(new Vector(3,3)).Length == 0, "gaze did not recenter at the face");
            Require(Direction(new Vector(20,0)).Length < Direction(new Vector(80,0)).Length, "nearby cursor immediately forced a full-strength stare");
            Require(Direction(new Vector(10000,10000)).Length <= 1.00001, "far-away cursor exceeded unit direction");
        });
        Check("invalid gaze inputs cannot create non-finite drawing coordinates", () =>
        {
            var renderer = new PetRenderer(SpriteAtlas.LoadSkin(Paths.Assets, "pixel")) { Scale = 4 };
            renderer.Play(PetState.Idle);
            renderer.SetEyeOffset(new Vector(double.NaN, double.PositiveInfinity), double.PositiveInfinity);
            RenderAnimationFrame(renderer);
            var offset = Field<Vector>(renderer, "_eyeOffset");
            Require(double.IsFinite(offset.X) && double.IsFinite(offset.Y), "invalid gaze escaped into renderer state");
        });
        Check("B01 gaze stays below one source pixel even during curious tracking and typing", () =>
        {
            var renderer = new PetRenderer(SpriteAtlas.LoadSkin(Paths.Assets, "pixel")) { Scale = 4 };
            renderer.Play(PetState.Idle);
            var offsetMethod = typeof(PetRenderer).GetMethod("ScreenEyeOffset", BindingFlags.NonPublic | BindingFlags.Instance)!;
            foreach (var direction in new[] { new Vector(1,0), new Vector(-1,0), new Vector(0,1), new Vector(0,-1), new Vector(1,1) })
            {
                renderer.SetEyeOffset(direction, 1.6);
                var offset = (Vector)offsetMethod.Invoke(renderer, new object[] { "eyes_open", 1.5d })!;
                Require(Math.Abs(offset.X / renderer.Scale) <= 0.7 && Math.Abs(offset.Y / renderer.Scale) <= 0.4,
                    $"eye layer escaped its facial anchor: {offset.X/renderer.Scale:F2}, {offset.Y/renderer.Scale:F2} source px");
            }
        });
        Check("closed, half-closed and petted eyes stay fixed while the cursor moves", () =>
        {
            var renderer = new PetRenderer(SpriteAtlas.LoadSkin(Paths.Assets, "pixel")) { Scale = 4 };
            renderer.Play(PetState.Idle);
            var blink = typeof(PetRenderer).GetField("_blinkPhase", BindingFlags.NonPublic | BindingFlags.Instance)!;
            foreach (var phase in new[] { "Half", "Closed" })
            {
                blink.SetValue(renderer, Enum.Parse(blink.FieldType, phase));
                renderer.SetEyeOffset(default); var neutral = Pixels(RenderAnimationFrame(renderer));
                renderer.SetEyeOffset(new Vector(1,1), 1.6);
                Require(neutral.SequenceEqual(Pixels(RenderAnimationFrame(renderer))), phase + " eyelids slid across the face");
            }
            renderer.Play(PetState.Pet); blink.SetValue(renderer, Enum.Parse(blink.FieldType, "Open"));
            renderer.SetEyeOffset(default); var pet = Pixels(RenderAnimationFrame(renderer));
            renderer.SetEyeOffset(new Vector(-1,-1), 1.6);
            Require(pet.SequenceEqual(Pixels(RenderAnimationFrame(renderer))), "smiling eyes drifted while petting");
        });
        Check("open eyes still follow the mouse within the smaller range", () =>
        {
            var renderer = new PetRenderer(SpriteAtlas.LoadSkin(Paths.Assets, "pixel")) { Scale = 4 };
            renderer.Play(PetState.Idle); var neutral = Pixels(RenderAnimationFrame(renderer));
            renderer.SetEyeOffset(new Vector(1,0));
            Require(!neutral.SequenceEqual(Pixels(RenderAnimationFrame(renderer))), "tracking was disabled instead of bounded");
        });
    }

    private static void CaptureEyeTracking(string output)
    {
        Directory.CreateDirectory(output);
        var directions = new[] { new Vector(-1,-1), new Vector(0,-1), new Vector(1,-1), new Vector(-1,0), default(Vector),
            new Vector(1,0), new Vector(-1,1), new Vector(0,1), new Vector(1,1) };
        foreach (var state in new[] { PetState.Idle, PetState.Work, PetState.Pet, PetState.Drag })
        {
            var renderer = new PetRenderer(SpriteAtlas.LoadSkin(Paths.Assets, "pixel")) { Scale = 4 };
            renderer.Play(state);
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(247,242,233)), null, new Rect(0,0,384,384));
                for (int i=0; i<directions.Length; i++)
                {
                    renderer.SetEyeOffset(directions[i], 1.6);
                    dc.DrawImage(RenderAnimationFrame(renderer), new Rect(i%3*128, i/3*128,128,128));
                }
            }
            var bitmap = new RenderTargetBitmap(384,384,96,96,PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, state.ToString().ToLowerInvariant()+".png")); encoder.Save(stream);
        }
    }
}
