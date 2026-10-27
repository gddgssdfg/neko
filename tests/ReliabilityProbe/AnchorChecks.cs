using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using DesktopPet.App.Core;
using DesktopPet.App.Services;
using DesktopPet.App.Windows;

internal static partial class Program
{
    private static void RunAnchorChecks(string root)
    {
        string Fixture(string name)
        {
            var path = Path.Combine(root, name, "pixel"); Directory.CreateDirectory(path);
            foreach (var file in Directory.GetFiles(Path.Combine(Paths.Assets, "pixel"))) File.Copy(file, Path.Combine(path, Path.GetFileName(file)));
            return path;
        }
        Check("anchor drafts isolate frames, undo gestures and reject invalid or unavailable offsets", () =>
        {
            var source = Fixture("anchor-draft"); var original = File.ReadAllBytes(Path.Combine(source, "skin.json"));
            var editor = new SkinAnchorEditor(source); var initial = editor.GetOffset("idle", 0); var second = editor.GetOffset("idle", 1);
            editor.SetOffset("idle", 0, new(2, -1));
            Require(editor.IsDirty && editor.GetOffset("idle", 1) == second, "editing leaked to another frame");
            editor.Undo(); Require(!editor.IsDirty && editor.GetOffset("idle", 0) == initial, "undo did not restore baseline");
            editor.Redo(); Require(editor.IsDirty && editor.GetOffset("idle", 0) == new SkinEyeOffset(2, -1), "redo lost edit");
            editor.RestoreFrame("idle", 0); Require(!editor.IsDirty, "restore did not restore saved frame");
            foreach (var bad in new[] { new SkinEyeOffset(double.NaN, 0), new SkinEyeOffset(0, double.PositiveInfinity), new SkinEyeOffset(33, 0) })
            { try { editor.SetOffset("idle", 0, bad); throw new Exception("invalid offset accepted"); } catch (ArgumentOutOfRangeException) { } }
            try { editor.SetOffset("sleep", 0, new(1, 0)); throw new Exception("embedded eyes editable"); } catch (InvalidOperationException) { }
            Require(original.SequenceEqual(File.ReadAllBytes(Path.Combine(source, "skin.json"))), "draft wrote to disk");
        });
        Check("anchor save backs up exact bytes, preserves unknown metadata and remains importable", () =>
        {
            var source = Fixture("anchor-save"); var manifest = Path.Combine(source, "skin.json");
            var document = JsonNode.Parse(File.ReadAllText(manifest))!;
            document["authorMetadata"] = new JsonObject { ["note"] = "作者自定义内容" };
            document["states"]![0]!["eyeOffsets"]![0]!["extra"] = "keep me";
            File.WriteAllText(manifest, document.ToJsonString(), new UTF8Encoding(true));
            var original = File.ReadAllBytes(manifest);
            var editor = new SkinAnchorEditor(source); editor.SetOffset("idle", 0, new(2, -1));
            var backup = editor.Save(Path.Combine(root, "anchor-backups"));
            Require(backup is not null && original.SequenceEqual(File.ReadAllBytes(backup)), "backup is not original bytes");
            var saved = JsonNode.Parse(File.ReadAllText(manifest))!;
            Require(saved["authorMetadata"]!["note"]!.GetValue<string>() == "作者自定义内容"
                && saved["states"]![0]!["eyeOffsets"]![0]!["extra"]!.GetValue<string>() == "keep me", "unknown metadata lost");
            Require(!editor.IsDirty && !editor.CanUndo && editor.Save(Path.Combine(root, "anchor-backups")) is null, "save did not establish new baseline");
            var reloaded = new SkinAnchorEditor(source);
            Require(reloaded.GetOffset("idle", 0) == new SkinEyeOffset(2, -1), "saved offset not reloaded");
            ExtensionInstaller.ExportSkin(source, Path.Combine(root, "edited-skin.zip"), "pixel");
            Require(!Directory.EnumerateFiles(source, "*.tmp").Any(), "temporary manifest leaked");
        });
        Check("anchor save rejects stale documents and backup failure preserves original and draft", () =>
        {
            var source = Fixture("anchor-conflict"); var manifest = Path.Combine(source, "skin.json");
            var first = new SkinAnchorEditor(source); var stale = new SkinAnchorEditor(source);
            first.SetOffset("idle", 0, new(1, 0)); first.Save(Path.Combine(root, "anchor-conflict-backups"));
            var foreign = File.ReadAllBytes(manifest); stale.SetOffset("idle", 0, new(2, 0));
            try { stale.Save(Path.Combine(root, "anchor-conflict-backups")); throw new Exception("stale manifest overwritten"); } catch (IOException) { }
            Require(stale.IsDirty && foreign.SequenceEqual(File.ReadAllBytes(manifest)), "external edit or draft lost");
            var retry = new SkinAnchorEditor(source); retry.SetOffset("idle", 1, new(3, 0));
            var blocked = Path.Combine(root, "not-a-backup-directory"); File.WriteAllText(blocked, "sentinel");
            try { retry.Save(blocked); throw new Exception("backup failure ignored"); } catch (IOException) { }
            Require(retry.IsDirty && foreign.SequenceEqual(File.ReadAllBytes(manifest)), "failed save modified original or cleared draft");
            retry.Save(Path.Combine(root, "anchor-retry-backups"));
            Require(!retry.IsDirty && !Directory.EnumerateFiles(source, "*.tmp").Any(), "retry failed or temp leaked");
        });
        Check("anchor drag maps viewport pixels to source pixels and commits once or cancels", () =>
        {
            var editor = new SkinAnchorEditor(Fixture("anchor-drag"));
            foreach (var size in new[] { new Size(320, 320), new Size(640, 480) })
            {
                var surface = new SkinPreviewSurface(editor.Review.Atlas) { OffsetProvider = editor.GetOffset, AnchorEditingEnabled = true };
                surface.Measure(size); surface.Arrange(new Rect(size));
                var zoom = (size.Height - 32) / 32;
                var origin = new Point((size.Width - 32 * zoom) / 2, 16);
                var start = new Point(origin.X + 15 * zoom, origin.Y + 11 * zoom);
                int commits = 0;
                surface.AnchorDragged += offset => { commits++; editor.SetOffset("idle", 0, offset); };
                Require(!surface.BeginAnchorDrag(new Point(0, 0)), "background starts a drag");
                Require(surface.BeginAnchorDrag(start) && !surface.Playing, "eye drag not started or did not pause");
                surface.MoveAnchorDrag(start + new Vector(2 * zoom, -zoom));
                Require(!editor.IsDirty && surface.CurrentEyeOffset == new SkinEyeOffset(2, -1), "drag wrote before release or mapping wrong");
                surface.EndAnchorDrag(); Require(commits == 1 && editor.IsDirty, "drag not one committed edit");
                editor.Undo(); Require(!editor.IsDirty, "one undo did not undo entire drag");
                Require(surface.BeginAnchorDrag(start), "second drag rejected"); surface.MoveAnchorDrag(start + new Vector(zoom, zoom));
                surface.StepFrame(1); Require(!surface.IsAnchorDragging && !editor.IsDirty && commits == 1, "frame switch committed interrupted gesture");
            }
        });
        Check("anchor UI gates export, saves edits and keeps uneditable states disabled", () =>
        {
            var source = Fixture("anchor-ui"); var window = new SkinWorkbenchWindow(source);
            try
            {
                window.Show(); window.UpdateLayout();
                var editor = Field<SkinAnchorEditor>(window, "_editor");
                var surface = Field<SkinPreviewSurface>(window, "_surface");
                Field<CheckBox>(window, "_editAnchors").IsChecked = true;
                var x = Field<TextBox>(window, "_anchorX"); var y = Field<TextBox>(window, "_anchorY");
                x.Text = "2"; y.Text = "-1";
                Field<Button>(window, "_applyAnchor").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(editor.IsDirty && !Field<Button>(window, "_export").IsEnabled && surface.CurrentEyeOffset == new SkinEyeOffset(2, -1), "draft UI out of sync");
                Field<ComboBox>(window, "_states").SelectedItem = "work";
                Field<Button>(window, "_undo").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(!editor.IsDirty && surface.State.Name == "idle" && surface.FrameIndex == 0, "UI undo did not reveal edited frame");
                Field<Button>(window, "_redo").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Require(editor.IsDirty, "UI redo failed");
                var capture = Environment.GetEnvironmentVariable("NEKO_CAPTURE_ANCHOR_EDITOR");
                if (!string.IsNullOrEmpty(capture)) { Directory.CreateDirectory(capture); CaptureUiWindow(window, Path.Combine(capture, "anchor-editor.png")); }
                Field<Button>(window, "_save").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(!editor.IsDirty && Field<Button>(window, "_export").IsEnabled && new SkinAnchorEditor(source).GetOffset("idle", 0).X == 2, "UI save failed");
                Field<ComboBox>(window, "_states").SelectedItem = "sleep";
                Require(!x.IsEnabled && !Field<Button>(window, "_applyAnchor").IsEnabled && !surface.AnchorEditingEnabled, "embedded-eye state editable");
            }
            finally
            {
                // Never leave an unattended unsaved-changes dialog if a preceding assertion fails.
                var editor = Field<SkinAnchorEditor>(window, "_editor"); while (editor.CanUndo) editor.Undo();
                window.Close();
            }
        });
    }
}
