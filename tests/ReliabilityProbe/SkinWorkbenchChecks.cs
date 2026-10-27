using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopPet.App.Core;
using DesktopPet.App.Renderer;
using DesktopPet.App.Services;
using DesktopPet.App.Windows;

internal static partial class Program
{
    private static void RunSkinWorkbenchChecks(string root)
    {
        var source = Path.Combine(Paths.Assets, "pixel");
        string Fixture(string name)
        {
            var path = Path.Combine(root, name, "pixel"); Directory.CreateDirectory(path);
            foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(path, Path.GetFileName(file)));
            return path;
        }
        Check("skin review accepts bundled skins and reports missing eye layers without modifying files", () =>
        {
            foreach (var skin in SkinRegistry.Scan(Paths.Assets))
                Require(SkinReview.Open(skin.Directory).Atlas.States.ContainsKey("work"), "bundled skin rejected: " + skin.Id);
            var fixture = Fixture("skin-review");
            File.Delete(Path.Combine(fixture, "thumb.png")); File.Delete(Path.Combine(fixture, "neko_eyes_open.png"));
            var idle = SkinReview.Open(source).Atlas.States["idle"];
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
                for (int i = 0; i < idle.Frames.Length; i++) dc.DrawImage(idle.Frames[0].Bitmap, new Rect(i * 32, 0, 32, 32));
            var sheet = new RenderTargetBitmap(32 * idle.Frames.Length, 32, 96, 96, PixelFormats.Pbgra32); sheet.Render(visual);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(sheet));
            using (var output = File.Create(Path.Combine(fixture, "neko_idle.png"))) png.Save(output);
            var before = File.ReadAllBytes(Path.Combine(fixture, "skin.json"));
            var report = SkinReview.Open(fixture);
            Require(report.Warnings.Any(w => w.Contains("thumb.png")) && report.Warnings.Any(w => w.Contains("eyes_open")), "missing art hints absent");
            Require(report.Warnings.Any(w => w.Contains("idle") && w.Contains("所有身体帧相同")), "identical animated frames not reported");
            Require(before.SequenceEqual(File.ReadAllBytes(Path.Combine(fixture, "skin.json"))), "review modified manifest");
        });
        Check("skin export round trips both layouts and refuses overwrite, stale ids and broken frames", () =>
        {
            void RoundTrip(string folder, string name)
            {
                var zip = Path.Combine(root, name + ".zip");
                ExtensionInstaller.ExportSkin(folder, zip, "pixel");
                using (var archive = ZipFile.OpenRead(zip))
                    Require(archive.Entries.Any(e => e.FullName == "pixel/skin.json"), "archive lacks single id directory");
                var unpacked = Path.Combine(root, name + "-unpacked"); ZipFile.ExtractToDirectory(zip, unpacked);
                var installed = ExtensionInstaller.Install(Path.Combine(unpacked, "pixel"), Path.Combine(root, name + "-installed"), false, "pixel");
                Require(installed == "pixel", "archive cannot be reimported");
                var before = File.ReadAllBytes(zip);
                try { ExtensionInstaller.ExportSkin(folder, zip, "pixel"); throw new Exception("existing package overwritten"); } catch (IOException) { }
                Require(before.SequenceEqual(File.ReadAllBytes(zip)), "existing package changed");
            }
            RoundTrip(source, "skin-sheet");
            var files = Fixture("skin-files"); var review = SkinReview.Open(files);
            var json = JsonNode.Parse(File.ReadAllText(Path.Combine(files, "skin.json")))!; json["layout"] = "files";
            File.WriteAllText(Path.Combine(files, "skin.json"), json.ToJsonString());
            foreach (var state in review.Atlas.States.Values)
            {
                for (int frame = 0; frame < state.Frames.Length; frame++)
                {
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(state.Frames[frame].Bitmap));
                    using var output = File.Create(Path.Combine(files, $"neko_{state.Name}_{frame}.png")); png.Save(output);
                }
                File.Delete(Path.Combine(files, $"neko_{state.Name}.png"));
            }
            RoundTrip(files, "skin-files");
            var broken = Fixture("skin-broken"); File.Delete(Path.Combine(broken, "neko_idle.png"));
            var badZip = Path.Combine(root, "broken.zip");
            try { ExtensionInstaller.ExportSkin(broken, badZip, "pixel"); throw new Exception("broken frames exported"); } catch (FormatException) { }
            Require(!File.Exists(badZip), "failed export left a partial archive");
            try { ExtensionInstaller.ExportSkin(source, badZip, "changed-id"); throw new Exception("stale preview id accepted"); } catch (IOException) { }
            try { ExtensionInstaller.ExportSkin(files, Path.Combine(files, "self.zip"), "pixel"); throw new Exception("archive written into source"); } catch (IOException) { }
            Require(!Directory.EnumerateDirectories(root, ".neko-skin-*").Any(), "private staging directory leaked");
        });
        Check("skin preview steps static frames and resumes from the selected frame without old fractional time", () =>
        {
            var atlas = SkinReview.Open(source).Atlas;
            var surface = new SkinPreviewSurface(atlas); surface.Tick(TimeSpan.FromSeconds(.2));
            surface.StepFrame(1); Require(surface.FrameIndex == 1 && !surface.Playing, "step did not pause");
            surface.Playing = true; surface.Tick(TimeSpan.FromSeconds(.1));
            Require(surface.FrameIndex == 1, "seek retained fractional time");
            surface.Tick(TimeSpan.FromSeconds(.15)); Require(surface.FrameIndex == 2, "play did not resume selected frame");
            surface.SelectState("sleep"); surface.StepFrame(-1); var selected = surface.FrameIndex;
            surface.Playing = true; surface.Tick(TimeSpan.FromDays(1));
            Require(surface.FrameIndex == selected, "fps=0 advanced automatically");
            surface.SelectState("wake_work"); surface.StepFrame(1);
            Require(surface.FrameIndex == 1, "extra transition state unavailable");
        });
        Check("skin workbench preserves loaded preview on invalid reload and stops its timer when closed", () =>
        {
            var window = new SkinWorkbenchWindow(source);
            try
            {
                window.Show(); window.UpdateLayout();
                var surface = Field<SkinPreviewSurface>(window, "_surface");
                var timer = Field<DispatcherTimer>(window, "_timer"); Require(timer.IsEnabled, "visible preview timer inactive");
                surface.StepFrame(1); Require(!timer.IsEnabled, "paused preview kept ticking");
                UiChildren<Button>(window).Single(b => Equals(b.Content, "播放")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(timer.IsEnabled, "resumed preview timer inactive");
                var states = Field<ComboBox>(window, "_states"); states.SelectedItem = "sleep";
                Require(!timer.IsEnabled, "static preview kept ticking");
                states.SelectedItem = "idle"; Require(timer.IsEnabled, "animated state did not restart timer");
                surface.StepFrame(1);
                typeof(SkinWorkbenchWindow).GetMethod("Reload", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(window, [Path.Combine(root, "missing-skin")]);
                Require(ReferenceEquals(surface, Field<SkinPreviewSurface>(window, "_surface")), "invalid reload discarded valid preview");
                typeof(SkinWorkbenchWindow).GetMethod("Reload", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(window, [source]);
                var capture = Environment.GetEnvironmentVariable("NEKO_CAPTURE_SKIN_WORKBENCH");
                if (!string.IsNullOrEmpty(capture)) { Directory.CreateDirectory(capture); CaptureUiWindow(window, Path.Combine(capture, "workbench.png")); }
                window.Close(); Require(!timer.IsEnabled, "closed preview timer still active");
            }
            finally { window.Close(); }
        });
    }
}
