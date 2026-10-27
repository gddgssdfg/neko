using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using DesktopPet.App.Behavior;
using DesktopPet.App.Core;
using DesktopPet.App.Windows;
using DesktopPet.App.Renderer;
using System.IO;
using System.Text.Json.Nodes;
using DesktopPet.Core;

internal static partial class Program
{
    private static void RunCharacterChecks(string root)
    {
        Check("all bundled skins keep valid optional motion and compatible transition declarations", () =>
        {
            foreach (var file in Directory.EnumerateFiles(Paths.Assets, "skin.json", SearchOption.AllDirectories))
            {
                var skin = SkinRegistry.InspectFolder(Path.GetDirectoryName(file)!);
                Require(!skin.IsBuiltinFallback, "bundled skin fell back: " + file);
            }
        });
        Check("skin transitions finish once, accept interruption and reset when switching skins", () =>
        {
            var atlas = SpriteAtlas.LoadSkin(Paths.Assets, "pixel");
            var renderer = new PetRenderer(atlas);
            var animator = Field<FrameAnimator>(renderer, "_animator");
            renderer.Play(PetState.Sleep); renderer.Play(PetState.Work);
            Require(renderer.IsTransitioning && animator.State!.Name == "wake_work" && !animator.Loop, "dedicated wake bridge not selected");
            renderer.Play(PetState.Work); renderer.Tick(TimeSpan.FromSeconds(1.1));
            Require(!renderer.IsTransitioning && animator.State!.Name == "work" && animator.Loop, "transition did not arrive at work");
            renderer.Play(PetState.Sleep); renderer.Play(PetState.Idle); renderer.Play(PetState.Drag);
            renderer.Tick(TimeSpan.FromSeconds(2));
            Require(!renderer.IsTransitioning && animator.State!.Name == "drag", "old bridge replaced drag");
            renderer.Play(PetState.Sleep); renderer.Play(PetState.Idle);
            renderer.SetTypingActive(TimeSpan.FromSeconds(10)); renderer.SetAtlas(atlas); renderer.Play(PetState.Pet);
            renderer.Tick(TimeSpan.FromMilliseconds(100));
            Require(!renderer.IsTransitioning && animator.State!.Name == "pet" && Field<double>(renderer, "_breathOffset") == 0, "skin reload retained old motion");
            renderer.Play(PetState.Walk, loop: false); renderer.Tick(TimeSpan.FromSeconds(2));
            Require(animator.IsComplete && animator.State!.Name == "walk", "one shot was overridden by transition logic");
        });
        Check("dedicated pose endings match their neutral destination and remain interruptible", () =>
        {
            var atlas = SpriteAtlas.LoadSkin(Paths.Assets, "pixel");
            foreach (var item in new[] { ("sleep_enter", PetState.Sleep), ("wake_idle", PetState.Idle), ("wake_work", PetState.Work), ("pet_end_idle", PetState.Idle), ("pet_end_work", PetState.Work) })
            {
                var end = new PetRenderer(atlas) { Scale = 1 };
                var animation = Field<FrameAnimator>(end, "_animator");
                animation.Loop = false; animation.SetState(atlas.States[item.Item1]);
                end.Tick(TimeSpan.FromSeconds(.29)); // keep blink timers out of endpoint comparison
                animation.Tick(TimeSpan.FromSeconds(2));
                var target = new PetRenderer(atlas) { Scale = 1 }; target.Play(item.Item2);
                Require(Pixels(RenderAnimationFrame(end)).SequenceEqual(Pixels(RenderAnimationFrame(target))), "endpoint changed appearance: " + item.Item1);
            }
            var renderer = new PetRenderer(atlas); var animator = Field<FrameAnimator>(renderer, "_animator");
            renderer.Play(PetState.Sit); renderer.Play(PetState.Sleep);
            Require(renderer.IsTransitioning && animator.State!.Name == "sleep_enter", "sleep did not fold down");
            renderer.Play(PetState.Pet); renderer.Play(PetState.Work);
            Require(renderer.IsTransitioning && animator.State!.Name == "pet_end_work", "pet did not ease back to work");
            renderer.Play(PetState.Drag); renderer.Tick(TimeSpan.FromSeconds(1));
            Require(!renderer.IsTransitioning && animator.State!.Name == "drag", "pet ending blocked a drag");
            renderer.Play(PetState.Pet); renderer.Play(PetState.Idle); renderer.Play(PetState.Pet);
            Require(!renderer.IsTransitioning && animator.State!.Name == "pet", "a new pet could not interrupt the old ending");
        });
        Check("skin stride follows frame rate, scale and mood; old skins keep legacy motion", () =>
        {
            var atlas = SpriteAtlas.LoadSkin(Paths.Assets, "pixel");
            var renderer = new PetRenderer(atlas) { Scale = 4 };
            var expected = 10d * 4 * 8 / 4;
            Require(Math.Abs(renderer.WalkSpeed - expected) < .001, "declared stride speed wrong");
            renderer.SetMood(PetMood.Happy);
            Require(Math.Abs(renderer.WalkSpeed - expected * 1.3) < .001, "stride did not follow playback multiplier");
            var directory = Path.Combine(root, "legacy-motion"); Directory.CreateDirectory(directory);
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(Paths.Assets, "pixel", "skin.json")))!.AsObject();
            manifest["id"] = "legacy-motion"; manifest.Remove("motion"); manifest.Remove("transitions");
            foreach (var file in Directory.EnumerateFiles(Path.Combine(Paths.Assets, "pixel"), "*.png")) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
            var jsonFile = Path.Combine(directory, "skin.json"); File.WriteAllText(jsonFile, manifest.ToJsonString());
            var legacy = SpriteAtlas.InspectFolder(directory);
            Require(legacy.Motion is null && legacy.Transitions.Count == 0 && new PetRenderer(legacy).WalkSpeed == 72, "legacy skin changed semantics");
            manifest["motion"] = new JsonObject { ["gazeMaxX"] = 9 };
            File.WriteAllText(jsonFile, manifest.ToJsonString());
            try { SkinRegistry.InspectFolder(directory); throw new Exception("unsafe gaze accepted"); } catch (FormatException) { }
            manifest["motion"] = new JsonObject { ["blinkMinSeconds"] = 9, ["blinkMaxSeconds"] = 2 };
            File.WriteAllText(jsonFile, manifest.ToJsonString());
            try { SkinRegistry.InspectFolder(directory); throw new Exception("reversed blink range accepted"); } catch (FormatException) { }
            manifest.Remove("motion");
            manifest["transitions"] = new JsonArray(new JsonObject { ["from"] = "idle", ["to"] = "drag", ["via"] = "sit" });
            File.WriteAllText(jsonFile, manifest.ToJsonString());
            try { SkinRegistry.InspectFolder(directory); throw new Exception("transition intercepted direct drag"); } catch (FormatException) { }
        });
        Check("typing motion stays subtle, settles and stops during direct interactions", () =>
        {
            var renderer = new PetRenderer(SpriteAtlas.LoadSkin(Paths.Assets, "pixel")) { Scale = 4 };
            var nextBlink = Field<double>(renderer, "_nextBlinkAtMs");
            Require(nextBlink >= 2500 && nextBlink <= 6500, "skin blink range ignored");
            renderer.Play(PetState.Work); renderer.SetTypingActive(TimeSpan.FromSeconds(.4)); renderer.Tick(TimeSpan.FromSeconds(.1));
            var breath = Field<double>(renderer, "_breathOffset");
            Require(Math.Abs(breath) > 0 && Math.Abs(breath) <= .18 * renderer.Scale, "typing bob out of bounds");
            renderer.Play(PetState.Drag); renderer.Tick(TimeSpan.FromSeconds(.1));
            Require(Field<double>(renderer, "_breathOffset") == 0, "typing bob interfered with dragging");
            renderer.Play(PetState.Work); renderer.Tick(TimeSpan.FromSeconds(2));
            Require(Field<double>(renderer, "_breathOffset") == 0, "expired typing motion did not settle");
        });
        Check("repeated petting extends the latest reaction and restores the work pose", () =>
        {
            var config = new AppConfig { SafeMode = true, KeyboardMirrorEnabled = false, AppMode = "work" };
            var pet = new PetWindow(config);
            try
            {
                pet.Show();
                var touch = typeof(PetWindow).GetMethod("Pet", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var advance = typeof(PetWindow).GetMethod("UpdatePetReaction", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var machine = Field<PetStateMachine>(pet, "_stateMachine");
                void Touch() => touch.Invoke(pet, [new Point(pet.Width / 2, pet.Height * .9)]);
                void Tick(double ms) => advance.Invoke(pet, [TimeSpan.FromMilliseconds(ms)]);
                Touch(); Tick(500); Touch(); Tick(200);
                Require(machine.Current == PetState.Pet, "earlier pet ended the repeated interaction");
                Tick(450); Require(machine.Current == PetState.Work, "pet did not restore work pose");
                Touch(); machine.Play(PetState.Pet); Tick(1000);
                Require(machine.Current == PetState.Pet, "old reaction overrode a newer same-state request");
                machine.Play(PetState.Sleep);
                typeof(PetWindow).GetMethod("OnInputReaction", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(pet, null);
                Require(machine.Current == PetState.Work, "typing did not wake work companion");
            }
            finally { pet.Dispose(); pet.Close(); }
        });
        Check("a completed pet reaction never overrides a subsequent drag", () =>
        {
            var pet = new PetWindow(new AppConfig { SafeMode = true, KeyboardMirrorEnabled = false });
            try
            {
                pet.Show();
                typeof(PetWindow).GetMethod("Pet", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(pet, [new Point(pet.Width / 2, pet.Height * .9)]);
                var machine = Field<PetStateMachine>(pet, "_stateMachine");
                machine.Play(PetState.Drag);
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start(); Dispatcher.PushFrame(frame);
                Require(machine.Current == PetState.Drag, "old pet timer replaced drag with " + machine.Current);
            }
            finally { pet.Dispose(); pet.Close(); }
        });
        var capture = Environment.GetEnvironmentVariable("NEKO_CAPTURE_CHARACTER");
        if (!string.IsNullOrEmpty(capture)) CaptureCharacterPreview(capture);
    }

    private static void CaptureCharacterPreview(string output)
    {
        Directory.CreateDirectory(output);
        foreach (var skin in new[] { "pixel", "pixel_redraw" })
        {
            var renderer = new PetRenderer(SpriteAtlas.LoadSkin(Paths.Assets, skin)) { Scale = 6 };
            var encoder = new System.Windows.Media.Imaging.GifBitmapEncoder();
            renderer.Play(PetState.Sit); renderer.Play(PetState.Sleep);
            for (int i = 0; i < 80; i++)
            {
                if (i == 10) renderer.Play(PetState.Work);
                if (i >= 25 && i < 45) renderer.SetTypingActive(TimeSpan.FromSeconds(.3));
                if (i == 50) renderer.Play(PetState.Pet);
                if (i == 57) renderer.Play(PetState.Work);
                var visual = new System.Windows.Media.DrawingVisual();
                System.Windows.Media.RenderOptions.SetBitmapScalingMode(visual, System.Windows.Media.BitmapScalingMode.NearestNeighbor);
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(35, 32, 29)), null, new Rect(0, 0, 280, 256));
                    var label = i < 10 ? (renderer.IsTransitioning ? "Settle > Sleep" : "Sleep") : i < 25 ? "Wake > Work" : i < 50 ? "Typing > Settle" : i < 57 ? "Pet" : "Back to Work";
                    dc.DrawText(new System.Windows.Media.FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, new System.Windows.Media.Typeface("Segoe UI"), 16, System.Windows.Media.Brushes.Wheat, 1), new Point(16, 12));
                    dc.PushTransform(new System.Windows.Media.TranslateTransform(44, 48)); renderer.Draw(dc); dc.Pop();
                }
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(280, 256, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var metadata = new System.Windows.Media.Imaging.BitmapMetadata("gif");
                metadata.SetQuery("/grctlext/Delay", (ushort)10);
                var palette = new System.Windows.Media.Imaging.BitmapPalette(bitmap, 256);
                var indexed = new System.Windows.Media.Imaging.FormatConvertedBitmap(bitmap, System.Windows.Media.PixelFormats.Indexed8, palette, 0);
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(indexed, null, metadata, null));
                if (i is 0 or 10 or 15 or 25 or 50 or 57)
                {
                    var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var still = File.Create(Path.Combine(output, $"{skin}-{i:D2}.png")); png.Save(still);
                }
                renderer.Tick(TimeSpan.FromMilliseconds(100));
            }
            using var stream = new MemoryStream(); encoder.Save(stream);
            var bytes = stream.ToArray();
            // WIC's GIF encoder drops frame-delay metadata. Fix the container fields without changing pixels.
            int offset = 13 + ((bytes[10] & 128) != 0 ? 3 * (1 << ((bytes[10] & 7) + 1)) : 0);
            int images = 0;
            using var timed = new MemoryStream();
            timed.Write(bytes, 0, offset);
            timed.Write(new byte[] { 0x21, 0xff, 11, 78, 69, 84, 83, 67, 65, 80, 69, 50, 46, 48, 3, 1, 0, 0, 0 }); // NETSCAPE2.0, loop
            void SkipBlocks() { while (bytes[offset] != 0) offset += 1 + bytes[offset]; offset++; }
            while (offset < bytes.Length && bytes[offset] != 0x3b)
            {
                int start = offset;
                if (bytes[offset] == 0x21)
                {
                    bool control = bytes[offset + 1] == 0xf9;
                    offset += 2; SkipBlocks();
                    if (!control) timed.Write(bytes, start, offset - start);
                }
                else if (bytes[offset] == 0x2c)
                {
                    timed.Write(new byte[] { 0x21, 0xf9, 4, 4, 10, 0, 0, 0 }); // full opaque frame, 100ms
                    var packed = bytes[offset + 9]; offset += 10;
                    if ((packed & 128) != 0) offset += 3 * (1 << ((packed & 7) + 1));
                    offset++; SkipBlocks();
                    timed.Write(bytes, start, offset - start); images++;
                }
                else throw new InvalidDataException("Invalid preview GIF structure");
            }
            Require(images == 80, "preview images missing");
            timed.WriteByte(0x3b);
            File.WriteAllBytes(Path.Combine(output, skin + "-character.gif"), timed.ToArray());
        }
    }
}
