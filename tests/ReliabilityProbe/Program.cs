using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopPet.App.Core;
using DesktopPet.App.Renderer;
using DesktopPet.App.Services;
using DesktopPet.App.Windows;

internal static partial class Program
{
    private static int _failed;
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--dpi96")) SetThreadDpiAwarenessContext(new IntPtr(-1));
        var root = Path.Combine(Path.GetTempPath(), "neko-reliability-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("NEKO_DATA_DIR", root);
        Paths.EnsureCreated();
        Log.Initialize("debug", Path.Combine(root, "logs"));
        RunExtensionChecks(root);
        RunAnimationChecks();
        RunEyeTrackingChecks();
        RunUiDesignChecks();
        RunCompanionChecks();
        RunReleaseReadyChecks(root);
        RunResidencyChecks(root);
        RunCharacterChecks(root);
        RunFocusChecks();
        RunSkinWorkbenchChecks(root);
        RunAnchorChecks(root);
        Check("scheduler does not run a task scheduled forty days in the future", () =>
        {
            using var scheduler = new SchedulerImpl();
            using var fired = new ManualResetEventSlim();
            using var handle = scheduler.At(DateTime.Now.AddDays(40), () => fired.Set());
            Require(!fired.Wait(150), "future task ran immediately (millisecond overflow)");
        });
        Check("disposed scheduler rejects new tasks", () =>
        {
            using var scheduler = new SchedulerImpl();
            scheduler.Dispose();
            try { using var handle = scheduler.Every(1000, () => { }); throw new Exception("disposed scheduler accepted a timer"); }
            catch (ObjectDisposedException) { }
        });
        Check("scheduler cancellation and throwing one-shot callbacks release handles", () =>
        {
            using var scheduler = new SchedulerImpl();
            using var cancelled = new ManualResetEventSlim();
            var handle = scheduler.At(DateTime.UtcNow.AddDays(40), () => cancelled.Set());
            handle.Dispose();
            Require(!cancelled.Wait(100), "cancelled future task ran");
            using var fired = new ManualResetEventSlim();
            using var failed = scheduler.At(DateTime.Now, () => { fired.Set(); throw new InvalidOperationException("intentional regression fault"); });
            Require(fired.Wait(2000), "throwing task never started");
            Require(SpinWait.SpinUntil(() => !failed.IsActive, 2000), "throwing one-shot stayed active");
        });
        Check("registration racing with scheduler shutdown never leaves active timers", () =>
        {
            for (var i = 0; i < 24; i++)
            {
                using var scheduler = new SchedulerImpl();
                using var start = new ManualResetEventSlim();
                var handles = new System.Collections.Concurrent.ConcurrentBag<DesktopPet.Core.IScheduleHandle>();
                var add = Task.Run(() =>
                {
                    start.Wait();
                    for (var n = 0; n < 16; n++)
                    {
                        try { handles.Add(scheduler.Every(60000, () => { })); }
                        catch (ObjectDisposedException) { }
                    }
                });
                var stop = Task.Run(() => { start.Wait(); scheduler.Dispose(); });
                start.Set();
                Require(Task.WaitAll(new[] { add, stop }, 5000), "shutdown race deadlocked");
                Require(handles.All(h => !h.IsActive), "a timer escaped the shutdown snapshot");
            }
        });
        Check("completed one-shot and cancelled handles leave the scheduler registry", () =>
        {
            using var scheduler = new SchedulerImpl();
            using var fired = new ManualResetEventSlim();
            using var handle = scheduler.At(DateTime.Now, () => fired.Set());
            Require(fired.Wait(2000), "immediate task did not run");
            Require(SpinWait.SpinUntil(() => !handle.IsActive, 2000), "completed one-shot stayed active");
            for (var i = 0; i < 50; i++) scheduler.Every(60000, () => { }).Dispose();
            var registry = typeof(SchedulerImpl).GetField("_handles", System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance)!.GetValue(scheduler)!;
            Require(SpinWait.SpinUntil(() =>
            {
                lock (registry) return (int)registry.GetType().GetProperty("Count")!.GetValue(registry)! == 0;
            }, 2000), "completed/cancelled handles accumulate");
        });
        Check("null config source is preserved", () =>
        {
            var path = Path.Combine(root, "null-config.json");
            File.WriteAllText(path, "null");
            ConfigManager.Load(path);
            Require(Directory.GetFiles(root, "null-config.json.rejected-*").Length == 1, "null source was discarded without backup");
        });
        Check("null config members and invalid dimensions cannot crash startup", () =>
        {
            var path = Path.Combine(root, "members-config.json");
            File.WriteAllText(path, """{"MenuHotKey":null,"TogglePetHotKey":null,"RadialMenu":{"Order":null,"Visible":null},"DisabledPlugins":null,"Scale":-1,"Opacity":3}""");
            var loaded = ConfigManager.Load(path);
            Require(loaded.MenuHotKey is not null && loaded.TogglePetHotKey is not null, "null hotkey survived");
            Require(loaded.RadialMenu.Order is not null && loaded.RadialMenu.Visible is not null, "null radial map survived");
            Require(loaded.DisabledPlugins is not null && loaded.Scale >= 1 && loaded.Opacity <= 1, "invalid values survived");
        });
        Check("concurrent config saves cannot steal each other's temporary file", () =>
        {
            var path = Path.Combine(root, "concurrent-config.json");
            string Errors() => string.Join("\n", Directory.GetFiles(Path.Combine(root, "logs"), "*.log")
                .Select(File.ReadAllText).SelectMany(text => text.Split('\n')).Where(line => line.Contains("[ERROR]")));
            var before = Errors();
            Parallel.For(0, 24, i => ConfigManager.Save(path, new AppConfig { CatName = "saved-" + i }));
            Require(Errors() == before, "concurrent saves logged write failures");
            Require(ConfigManager.Load(path).CatName.StartsWith("saved-"), "final config was damaged");
            Require(!File.Exists(path + ".tmp"), "successful saves left a temporary file");
        });
        var storage = new JsonPluginStorage(Path.Combine(root, "storage"));
        Check("failed serialization preserves the previous value", () =>
        {
            storage.Set("value", 42);
            var cycle = new List<object>();
            cycle.Add(cycle);
            try { storage.Set("value", cycle); throw new Exception("serialization unexpectedly succeeded"); }
            catch (JsonException) { }
            Require(storage.Get<int>("value") == 42, "previous value was lost");
        });
        Check("failed replacement preserves old JSON and cleans temporary files", () =>
        {
            storage.Set("value", 42);
            using (File.Open(Path.Combine(root, "storage", "value.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { storage.Set("value", 99); throw new Exception("locked replacement unexpectedly succeeded"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            Require(storage.Get<int>("value") == 42, "locked value was damaged");
            Require(Directory.GetFiles(Path.Combine(root, "storage"), "*.tmp").Length == 0, "temporary file leaked");
        });
        Check("concurrent read/write never exposes partial JSON", () =>
        {
            storage.Set("concurrent", "initial");
            Parallel.For(0, 80, i =>
            {
                storage.Set("concurrent", new string('x', 4096) + i);
                Require(storage.Get<string>("concurrent") is not null, "reader saw incomplete JSON");
            });
        });
        Check("all storage operations reject path traversal and alternate streams", () =>
        {
            foreach (var key in new[] { "../escape", "..\\escape", "C:\\escape", "value:stream", "", ".." })
            foreach (var operation in new Action[] { () => storage.Set(key, 1), () => storage.Get<int>(key),
                         () => storage.Remove(key), () => storage.Contains(key) })
            {
                try { operation(); throw new Exception("unsafe key was accepted: " + key); }
                catch (ArgumentException) { }
            }
        });
        Check("animator recovers from a delay larger than Int32 frame count", () =>
        {
            var frame = new SpriteFrame { Bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4) };
            var animator = new FrameAnimator();
            animator.SetState(new SpriteState { Name = "test", Fps = 8, Frames = Enumerable.Repeat(frame, 4).ToArray() });
            var seconds = (double)int.MaxValue + 123;
            animator.Tick(TimeSpan.FromSeconds(seconds));
            animator.Tick(TimeSpan.FromSeconds(0.125));
            Require(animator.FrameIndex == 1, "animation stayed frozen after resume");
            animator.Playback = double.PositiveInfinity;
            Require(!animator.Tick(TimeSpan.FromSeconds(1)), "non-finite playback was accepted");
            animator.Playback = 1;
            animator.Tick(TimeSpan.FromSeconds(0.125));
            Require(animator.FrameIndex == 2, "invalid playback poisoned subsequent frames");
        });
        Check("amber deploys nine complete transparent animated states", () =>
        {
            var info = SkinRegistry.Scan(Paths.Assets).Single(s => s.Id == "amber");
            var atlas = SpriteAtlas.Load(Paths.Assets, "amber");
            Require(info.States.Count == 9, "missing poses");
            Require(!info.PixelArt && !atlas.PixelArt, "illustrated skin must opt into smooth rendering");
            Require(SkinRegistry.Resolve(Paths.Assets, "pixel").PixelArt, "legacy pixel skin lost nearest-neighbor rendering");
            foreach (var state in info.States)
            {
                atlas.States.TryGetValue(state.Name, out var loaded);
                Require(loaded is not null && loaded.Frames.Length == info.Frames, "missing frames: " + state.Name);
            }
        });
        Check("plugin SDK palette follows host theme while custom colors survive", () =>
        {
            var pet = new PetWindow(new AppConfig { Skin = "amber", Theme = "light" });
            var settings = new SettingsWindow(pet);
            try
            {
                var rootPanel = new System.Windows.Controls.StackPanel();
                var standard = DesktopPet.Sdk.PluginUi.Text("standard");
                var custom = new System.Windows.Controls.TextBlock { Text = "custom", Foreground = Brushes.Red };
                rootPanel.Children.Add(standard);
                rootPanel.Children.Add(custom);
                typeof(SettingsWindow).GetMethod("ApplyPluginTheme", System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic)!.Invoke(settings, new object[] { rootPanel });
                Require(!ReferenceEquals(standard.Foreground, DesktopPet.Sdk.PluginUi.TextMain), "SDK foreground stayed light on light background");
                Require(ReferenceEquals(custom.Foreground, Brushes.Red), "custom plugin artwork was recolored");
            }
            finally { settings.Close(); pet.Dispose(); pet.Close(); }
        });
        Check("redrawn pixel skin deploys, selects and retains nearest-neighbor scaling", () =>
        {
            var atlas = SpriteAtlas.Load(Paths.Assets, "pixel_redraw");
            Require(atlas.Skin == "pixel_redraw" && atlas.PixelArt && atlas.States.Count == 9, "new skin missing from deployment");
            var pet = new PetWindow(new AppConfig { Skin = "pixel", Scale = 2 });
            try
            {
                pet.SetSkin("pixel_redraw");
                Require(pet.Config.Skin == "pixel_redraw" && pet.Config.Scale == 4, "selection or recommended scale failed");
                Require(RenderOptions.GetBitmapScalingMode((DependencyObject)pet.Content) == BitmapScalingMode.NearestNeighbor,
                    "pixel skin was smoothed");
            }
            finally { pet.Dispose(); pet.Close(); }
        });
        if (args.Length >= 2 && args[0] == "--capture")
            Check("render settings pages in both themes", () => Capture(Path.GetFullPath(args[1]), args.Length >= 3 ? args[2] : "amber"));
        if (args.Length >= 2 && args[0] == "--capture-animation")
            Check("render all native action frames through the production renderer", () => CaptureAnimations(Path.GetFullPath(args[1])));
        if (args.Length >= 2 && args[0] == "--capture-ui")
            Check("render all companion UI in both themes and narrow layouts", () => CaptureUiDesign(Path.GetFullPath(args[1])));
        if (args.Length >= 2 && args[0] == "--capture-gaze")
            Check("render B01 gaze directions", () => CaptureEyeTracking(Path.GetFullPath(args[1])));
        Console.WriteLine(_failed == 0 ? "=== ALL PASSED ===" : $"=== FAILED: {_failed} ===");
        return _failed == 0 ? 0 : 1;
    }

    private static void Capture(string output, string skin)
    {
        Directory.CreateDirectory(output);
        var pet = new PetWindow(new AppConfig { Skin = skin, Scale = 4 });
        try
        {
            pet.Show();
            pet.LoadPlugins(); // Match App startup: this also starts the initial idle animation.
            pet.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            var petVisual = (FrameworkElement)pet.Content;
            petVisual.UpdateLayout();
            var petBitmap = new RenderTargetBitmap((int)Math.Ceiling(petVisual.ActualWidth),
                (int)Math.Ceiling(petVisual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            petBitmap.Render(petVisual);
            var petPixels = new byte[petBitmap.PixelWidth * petBitmap.PixelHeight * 4];
            petBitmap.CopyPixels(petPixels, petBitmap.PixelWidth * 4, 0);
            Require(Enumerable.Range(0, petPixels.Length / 4).Any(i => petPixels[i * 4 + 3] != 0), "captured pet is blank");
            var petEncoder = new PngBitmapEncoder();
            petEncoder.Frames.Add(BitmapFrame.Create(petBitmap));
            using (var stream = File.Create(Path.Combine(output, "pet.png"))) petEncoder.Save(stream);
            foreach (var theme in new[] { "light", "dark" })
            {
                pet.Config.Theme = theme;
                var settings = new SettingsWindow(pet);
                try
                {
                    settings.Show();
                    foreach (var section in new[] { "general", "pet", "input", "radial", "plugin", "about" })
                    {
                        settings.ShowSection(section);
                        var pump = new DispatcherFrame();
                        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
                        timer.Tick += (_, _) => { timer.Stop(); pump.Continue = false; };
                        timer.Start();
                        Dispatcher.PushFrame(pump);
                        settings.UpdateLayout();
                        var visual = (FrameworkElement)settings.Content;
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth),
                            (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(visual);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var stream = File.Create(Path.Combine(output, $"settings-{theme}-{section}.png"));
                        encoder.Save(stream);
                    }
                }
                finally { settings.Close(); }
            }
        }
        finally { pet.Dispose(); pet.Close(); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Check(string name, Action action)
    {
        try { action(); Console.WriteLine("PASS: " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL: " + name + ": " + ex); }
    }
}
