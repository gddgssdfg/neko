using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopPet.App.Core;
using DesktopPet.App.Renderer;
using DesktopPet.App.Services;
using DesktopPet.App.Windows;
using DesktopPet.Core;

internal static partial class Program
{
    private static void RunGazePreviewChecks(string root)
    {
        string Synthetic(string name, int width = 32, int height = 32)
        {
            var directory = Path.Combine(root, name, "gaze-test"); Directory.CreateDirectory(directory);
            var states = new JsonArray();
            foreach (var state in new[] { "idle", "walk", "sit", "sleep", "pet", "drag", "fall", "climb", "work" })
            {
                states.Add(new JsonObject { ["name"] = state, ["fps"] = 0, ["eyeLayer"] = state == "sleep" ? null : "eyes_open" });
                Write(Path.Combine(directory, $"neko_{state}.png"), width, height, dc =>
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(245, 235, 220)), null, new Rect(0, 0, width, height)));
            }
            Write(Path.Combine(directory, "neko_eyes_open.png"), width, height, Eyes);
            Write(Path.Combine(directory, "neko_eyes_open_closed.png"), width, height, Eyes);
            var manifest = new JsonObject { ["id"] = "gaze-test", ["frameWidth"] = width, ["frameHeight"] = height, ["frames"] = 1,
                ["eyeLayers"] = new JsonArray("eyes_open", "eyes_open_closed"), ["states"] = states };
            File.WriteAllText(Path.Combine(directory, "skin.json"), manifest.ToJsonString());
            return directory;
        }
        void Eyes(DrawingContext dc)
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(9, 10, 4, 4));
            dc.DrawRectangle(Brushes.Black, null, new Rect(19, 10, 4, 4));
        }
        void Write(string path, int width, int height, Action<DrawingContext> draw)
        {
            var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) draw(dc);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
        }
        RenderTargetBitmap RenderSurface(SkinPreviewSurface surface, int width = 64, int height = 64)
        {
            surface.MinHeight = 0; // allow the 64px off-screen comparison viewport; the UI keeps its 240-DIP minimum
            surface.Measure(new Size(width, height)); surface.Arrange(new Rect(0, 0, width, height)); surface.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface); return bitmap;
        }
        var directions = new[] { new Vector(-1,-1), new Vector(0,-1), new Vector(1,-1), new Vector(-1,0), default(Vector),
            new Vector(1,0), new Vector(-1,1), new Vector(0,1), new Vector(1,1) };
        Check("shared gaze math keeps legacy limits, diagonal ellipse and fixed expressions", () =>
        {
            var atlas = SpriteAtlas.LoadSkin(Paths.Assets, "pixel");
            var limits = EyeGazeMath.SourceLimits(atlas, "idle", "eyes_open");
            Require(Math.Abs(limits.X - .55) < 1e-10 && Math.Abs(limits.Y - .3) < 1e-10, "legacy travel changed");
            var diagonal = EyeGazeMath.SourceOffset(atlas, "idle", "eyes_open", EyeGazeMath.InputOffset(new Vector(1, 1)));
            Require(Math.Abs(diagonal.X - .55 / Math.Sqrt(2)) < 1e-10 && Math.Abs(diagonal.Y - .3 / Math.Sqrt(2)) < 1e-10, "diagonal is not elliptical");
            foreach (var key in new[] { "eyes_pet", "eyes_half", "eyes_blink", "eyes_open_closed" })
                Require(EyeGazeMath.SourceOffset(atlas, "idle", key, new Vector(2,2)).Length == 0, "expression moved: " + key);
            Require(EyeGazeMath.SourceLimits(atlas, "pet", "eyes_open").Length == 0, "petting moved pupils");
        });
        Check("preview matches runtime eye offset for bundled skins and non-square frames", () =>
        {
            foreach (var skin in SkinRegistry.Scan(Paths.Assets).Select(s => s.Directory).Append(Synthetic("gaze-rectangular", 64, 32)))
            {
                var atlas = ExtensionInstaller.InspectSkin(skin);
                var surface = new SkinPreviewSurface(atlas) { GazePreviewEnabled = true };
                if (!surface.CanPreviewGaze) continue;
                var renderer = new PetRenderer(atlas) { Scale = 3 }; renderer.Play(PetState.Idle);
                var method = typeof(PetRenderer).GetMethod("ScreenEyeOffset", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                var eye = atlas.GetEye(surface.State.EyeLayer!)!;
                foreach (var direction in directions)
                {
                    surface.GazeDirection = direction; renderer.SetEyeOffset(direction);
                    var screen = (Vector)method.Invoke(renderer, [surface.State.EyeLayer!, 0d])!;
                    var expected = new Vector(screen.X * eye.PixelWidth / 96, screen.Y * eye.PixelHeight / 96);
                    Require((expected - surface.GazeOffset).Length < 1e-9, "preview differs from runtime: " + atlas.Skin);
                }
            }
        });
        Check("gaze preview composites the same mask as runtime and shows unclipped comparison only on request", () =>
        {
            foreach (var masked in new[] { true, false })
            {
                var source = Synthetic(masked ? "gaze-mask" : "gaze-unmasked");
                if (!masked)
                {
                    var path = Path.Combine(source, "skin.json"); var document = JsonNode.Parse(File.ReadAllText(path))!;
                    document["eyeLayers"] = new JsonArray("eyes_open"); File.WriteAllText(path, document.ToJsonString());
                }
                var atlas = ExtensionInstaller.InspectSkin(source);
                var surface = new SkinPreviewSurface(atlas) { ShowGuides = false, GazePreviewEnabled = true };
                var renderer = new PetRenderer(atlas) { Scale = 1 }; renderer.Play(PetState.Idle);
                Require(surface.HasSocketMask == masked, "mask availability incorrect");
                foreach (var direction in directions)
                {
                    surface.GazeDirection = direction; renderer.SetEyeOffset(direction);
                    var preview = RenderSurface(surface);
                    var cached = Field<BitmapSource>(surface, "_gazeRaster");
                    surface.InvalidateVisual(); RenderSurface(surface);
                    Require(ReferenceEquals(cached, Field<BitmapSource>(surface, "_gazeRaster")), "unchanged gaze rerasterized the layer");
                    var cropped = new CroppedBitmap(preview, new Int32Rect(16, 16, 32, 32));
                    Require(Pixels(cropped).SequenceEqual(Pixels(RenderAnimationFrame(renderer))), "preview differs from runtime, mask=" + masked + ", direction=" + direction);
                }
                surface.GazeDirection = new Vector(1, 0);
                var clipped = Pixels(RenderSurface(surface)); surface.ShowUnclippedGaze = true;
                Require(clipped.SequenceEqual(Pixels(RenderSurface(surface))) != masked,
                    "unclipped comparison should change masked output only");
            }
        });
        Check("gaze inspection warns on missing masks and frame overflow without changing anchor drafts", () =>
        {
            var source = Path.Combine(root, "gaze-legacy", "pixel"); Directory.CreateDirectory(source);
            foreach (var file in Directory.GetFiles(Path.Combine(Paths.Assets, "pixel"))) File.Copy(file, Path.Combine(source, Path.GetFileName(file)));
            var original = File.ReadAllBytes(Path.Combine(source, "skin.json"));
            var window = new SkinWorkbenchWindow(source);
            try
            {
                window.Show(); window.UpdateLayout();
                var editor = Field<SkinAnchorEditor>(window, "_editor"); var surface = Field<SkinPreviewSurface>(window, "_surface");
                Field<CheckBox>(window, "_editAnchors").IsChecked = true;
                Field<Expander>(window, "_gazePanel").IsExpanded = true;
                Field<CheckBox>(window, "_gazePreview").IsChecked = true;
                Require(!surface.Playing && !surface.AnchorEditingEnabled && Field<CheckBox>(window, "_editAnchors").IsChecked == false, "inspection did not isolate editing");
                var button = UiChildren<Button>(window).Single(b => Equals(b.Tag, new Vector(1, -1)));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(surface.GazeOffset.X > 0 && surface.GazeOffset.Y < 0 && !surface.HasSocketMask
                    && Field<TextBlock>(window, "_gazeInfo").Text.Contains("无法自动确认眼眶安全"), "legacy safety notice missing");
                Require(!surface.BeginAnchorDrag(new Point(150, 110)) && !editor.IsDirty
                    && original.SequenceEqual(File.ReadAllBytes(Path.Combine(source, "skin.json"))), "preview mutated anchors");
                foreach (var width in new[] { 480d, 760d })
                {
                    window.Width = width; window.UpdateLayout();
                    var layout = Field<Grid>(window, "_previewLayout");
                    var directionsPanel = Field<System.Windows.Controls.Primitives.UniformGrid>(window, "_gazeDirections");
                    var catBounds = new Rect(surface.TranslatePoint(new Point(), layout), new Size(surface.ActualWidth, surface.ActualHeight));
                    var buttonBounds = new Rect(directionsPanel.TranslatePoint(new Point(), layout), new Size(directionsPanel.ActualWidth, directionsPanel.ActualHeight));
                    Require(!catBounds.IntersectsWith(buttonBounds), "direction controls cover the preview");
                }
                var capture = Environment.GetEnvironmentVariable("NEKO_CAPTURE_GAZE_PREVIEW");
                if (!string.IsNullOrEmpty(capture)) { Directory.CreateDirectory(capture); CaptureUiWindow(window, Path.Combine(capture, "gaze-preview.png")); }
                surface.OffsetProvider = (_, _) => new SkinEyeOffset(32, 0);
                Require(surface.GazeOutsideFrame, "overflowing draft not detected");
                Field<ComboBox>(window, "_states").SelectedItem = "sleep";
                Require(!surface.CanPreviewGaze && Field<TextBlock>(window, "_gazeInfo").Text.Contains("无法模拟"), "embedded eyes got a false check");
            }
            finally { window.Close(); }
        });
    }
}
