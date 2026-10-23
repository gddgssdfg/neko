using System.IO;
using System.Reflection;
using System.Net;
using System.Net.Http;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DesktopPet.App.Core;
using DesktopPet.App.Renderer;
using DesktopPet.App.Services;
using DesktopPet.App.Windows;
using DesktopPet.Core;
using DesktopPet.Runtime;

internal static partial class Program
{
    private static void RunExtensionChecks(string root)
    {
        const string id = "com.neko.lifecycle-test";
        var pluginRoot = Path.Combine(root, "extensions");
        var pluginDir = Path.Combine(pluginRoot, id);
        Directory.CreateDirectory(pluginDir);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "PluginLifecycleFixture.dll"), Path.Combine(pluginDir, "PluginLifecycleFixture.dll"));
        var manifest = Path.Combine(pluginDir, "plugin.toml");
        var validManifest = $"[plugin]\nid = \"{id}\"\nname = \"Lifecycle fixture\"\nentry = \"PluginLifecycleFixture.dll\"\napi = 1\n";
        File.WriteAllText(manifest, validManifest);
        Check("unsupported plugin API is rejected before activation", () =>
        {
            File.WriteAllText(manifest, validManifest.Replace("api = 1", "api = 99"));
            using var loaded = new PluginLoader(pluginRoot).LoadOne(pluginDir, out var error);
            Require(loaded is null && error?.Contains("API", StringComparison.OrdinalIgnoreCase) == true, "incompatible API accepted or no actionable diagnostic");
        });
        Check("plugin entry cannot point outside its package", () =>
        {
            File.Copy(Path.Combine(pluginDir, "PluginLifecycleFixture.dll"), Path.Combine(pluginRoot, "outside.dll"));
            File.WriteAllText(manifest, validManifest.Replace("PluginLifecycleFixture.dll", "../outside.dll"));
            using var loaded = new PluginLoader(pluginRoot).LoadOne(pluginDir, out var error);
            Require(loaded is null && error is not null, "outside entry accepted");
        });
        File.WriteAllText(manifest, validManifest);
        Check("malformed API fields and misspelled permissions have actionable errors", () =>
        {
            foreach (var badManifest in new[]
            {
                validManifest.Replace("api = 1", "api = 4294967297"),
                validManifest.Replace("api = 1", "api = \"1\""),
                validManifest + "\n[permissions]\npet = [\"notify\"]\n",
                validManifest + "\n[permissions]\nother = \"scheduler\"\n",
            })
            {
                File.WriteAllText(manifest, badManifest);
                using var invalid = new PluginLoader(pluginRoot).LoadOne(pluginDir, out var error);
                Require(invalid is null && !string.IsNullOrWhiteSpace(error), "invalid declaration silently accepted");
            }
            File.WriteAllText(manifest, validManifest);
        });
        var dataRoot = Path.Combine(root, "host-specific-data");
        var dataDir = Path.Combine(dataRoot, "plugins", id);
        var storage = new JsonPluginStorage(dataDir);
        PluginHost NewHost(EventBusImpl bus) => new(pluginRoot, new HashSet<string>(), new Version(1, 0), dataRoot,
            new ExtensionPet(), new UIApiImpl(_ => null), new CapabilityApiImpl
            {
                Storage = storage, Events = bus, Scheduler = new SchedulerImpl(), System = new SystemImpl(id),
            });
        Check("repeated loading does not duplicate plugins and host data directory is respected", () =>
        {
            using var host = NewHost(new EventBusImpl());
            host.LoadAll();
            host.LoadAll();
            Require(host.Plugins.Count == 1, "duplicate instance loaded");
            Require(storage.Get<string>("data-dir") == dataDir, "plugin storage ignored host data root");
            host.DisablePlugin(id.ToUpperInvariant());
            Require(!host.IsEnabled(id), "disabled IDs are case sensitive");
        });
        Check("failed OnLoad releases scheduler and event subscriptions and can be retried", () =>
        {
            var bus = new EventBusImpl();
            using var host = NewHost(bus);
            storage.Set("fail", true);
            host.LoadAll();
            var before = storage.Get<int>("events");
            bus.Emit("probe:lifecycle");
            Require(storage.Get<int>("events") == before, "failed plugin still receives events");
            var schedulers = (System.Collections.IDictionary)typeof(PluginHost).GetField("_schedulers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(host)!;
            Require(schedulers.Count == 0, "failed OnLoad leaked a scheduler");
            storage.Set("fail", false);
            Require(host.EnablePlugin(id), "retry did not load");
            Require(host.Errors.Count == 0, "successful retry kept stale errors");
            host.DisablePlugin(id);
            before = storage.Get<int>("events");
            bus.Emit("probe:lifecycle");
            Require(storage.Get<int>("events") == before, "disabled plugin still receives events");
        });
        Check("loaded plugin disposal is idempotent", () =>
        {
            using var scheduler = new SchedulerImpl();
            storage.Set("fail", false);
            var loader = new PluginLoader(pluginRoot);
            var loaded = loader.LoadOne(pluginDir, out var error) ?? throw new Exception(error);
            loaded.Instance.OnLoad(new PluginContextImpl
            {
                HostVersion = new Version(1, 0), DataRoot = dataRoot, PluginDataDir = dataDir, Pet = new ExtensionPet(),
                UI = new UIApiImpl(_ => null), Capability = new CapabilityApiImpl
                { Storage = storage, Events = new EventBusImpl(), Scheduler = scheduler, System = new SystemImpl(id) },
            });
            var before = storage.Get<int>("disposes");
            loaded.Dispose();
            loaded.Dispose();
            Require(storage.Get<int>("disposes") == before + 1, "Dispose ran twice");
        });
        Check("throwing enable observers cannot corrupt plugin state or block other observers", () =>
        {
            using var host = NewHost(new EventBusImpl());
            var observed = 0;
            host.EnabledChanged += (_, _) => throw new InvalidOperationException("intentional observer fault");
            host.EnabledChanged += (_, _) => observed++;
            Require(host.EnablePlugin(id) && host.Plugins.Count == 1, "observer error rolled back a loaded plugin");
            Require(host.DisablePlugin(id) && host.Plugins.Count == 0 && !host.IsEnabled(id), "observer error interrupted disabling");
            Require(observed == 2, "a failing observer blocked later observers");
        });
        Check("disabled plugins remain visible in settings after startup", () =>
        {
            var installedDir = Path.Combine(Paths.Plugins, id);
            Directory.CreateDirectory(installedDir);
            File.Copy(Path.Combine(pluginDir, "PluginLifecycleFixture.dll"), Path.Combine(installedDir, "PluginLifecycleFixture.dll"), true);
            File.Copy(manifest, Path.Combine(installedDir, "plugin.toml"), true);
            var pet = new PetWindow(new AppConfig { DisabledPlugins = new List<string> { id } });
            var settings = new SettingsWindow(pet);
            try
            {
                pet.LoadPlugins();
                var page = (DependencyObject)typeof(SettingsWindow).GetMethod("BuildPluginPage", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(settings, null)!;
                var name = "Lifecycle fixture";
                bool HasText(DependencyObject node) => node is TextBlock text && text.Text == name
                    || LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>().Any(HasText);
                Require(HasText(page), "disabled plugin missing from settings");
            }
            finally
            {
                settings.Close(); pet.Dispose(); pet.Close();
                File.Delete(Path.Combine(installedDir, "PluginLifecycleFixture.dll"));
                File.Delete(Path.Combine(installedDir, "plugin.toml"));
                Directory.Delete(installedDir);
            }
        });

        var assets = Path.Combine(root, "skin-fixtures");
        Directory.CreateDirectory(Path.Combine(assets, "pixel"));
        foreach (var path in Directory.GetFiles(Path.Combine(Paths.Assets, "pixel")))
            File.Copy(path, Path.Combine(assets, "pixel", Path.GetFileName(path)));
        string MakeSkin(string name, string layout = "sheet")
        {
            var dir = Path.Combine(assets, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "skin.json"), JsonSerializer.Serialize(new
            {
                id = name, layout, frameWidth = 32, frameHeight = 32, frames = 4,
                eyeLayers = new[] { "eyes_open" }, hasShadow = true,
                states = new[] { new { name = "idle", fps = 4 }, new { name = "walk", fps = 8 } },
            }));
            File.Copy(Path.Combine(assets, "pixel", "neko_idle.png"), Path.Combine(dir, "neko_idle.png"), true);
            return dir;
        }
        Check("bad optional sprite images do not discard a usable skin", () =>
        {
            var dir = MakeSkin("damaged-optional");
            foreach (var file in new[] { "neko_walk.png", "neko_eyes_open.png", "neko_shadow.png" })
                File.WriteAllText(Path.Combine(dir, file), "not a PNG");
            var atlas = SpriteAtlas.LoadSkin(assets, "damaged-optional");
            Require(atlas.Skin == "damaged-optional" && atlas.States.ContainsKey("idle"), "valid idle discarded");
            Require(!atlas.States.ContainsKey("walk") && atlas.GetEye("eyes_open") is null && atlas.Shadow is null, "bad optional image retained");
        });
        Check("undersized sheet and corrupt idle fall back to a usable default", () =>
        {
            var dir = MakeSkin("damaged-idle");
            File.Copy(Path.Combine(assets, "pixel", "neko_eyes_open.png"), Path.Combine(dir, "neko_idle.png"), true);
            var atlas = SpriteAtlas.LoadSkin(assets, "damaged-idle");
            Require(atlas.Skin == "pixel" && atlas.States.ContainsKey("idle"), "invalid idle did not fall back");
            File.WriteAllText(Path.Combine(dir, "neko_idle.png"), "not a PNG");
            Require(SpriteAtlas.LoadSkin(assets, "damaged-idle").Skin == "pixel", "corrupt idle did not fall back");
        });
        Check("skin IDs and names cannot alias another package or escape it", () =>
        {
            var dir = MakeSkin("bad-identifiers");
            var path = Path.Combine(dir, "skin.json");
            var text = File.ReadAllText(path);
            File.WriteAllText(path, text.Replace("bad-identifiers", "pixel"));
            Require(SkinRegistry.Scan(assets).All(s => s.Directory != dir), "ID mismatch accepted");
            File.WriteAllText(path, text.Replace("\"idle\"", "\"../escape\""));
            Require(SkinRegistry.Scan(assets).All(s => s.Directory != dir), "unsafe state name accepted");
        });
        Check("switching skins cannot reuse another skin's eye mask", () =>
        {
            var dir = MakeSkin("masked");
            var path = Path.Combine(dir, "skin.json");
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"eyes_open\"]", "\"eyes_open\",\"eyes_open_closed\"]"));
            File.Copy(Path.Combine(assets, "pixel", "neko_eyes_blink.png"), Path.Combine(dir, "neko_eyes_open_closed.png"));
            var renderer = new PetRenderer(SpriteAtlas.LoadSkin(assets, "pixel"));
            var method = typeof(PetRenderer).GetMethod("SocketMaskBrush", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Require(method.Invoke(renderer, new object[] { "eyes_open", "eyes_open" }) is null, "legacy skin unexpectedly masked");
            renderer.SetAtlas(SpriteAtlas.LoadSkin(assets, "masked"));
            Require(method.Invoke(renderer, new object[] { "eyes_open", "eyes_open" }) is not null, "cached null mask survived skin change");
        });
        Check("CPU usage does not count idle time twice", () =>
        {
            var type = typeof(DesktopPet.Builtin.SystemInfo.SystemInfoPlugin);
            // FILETIME includes idle inside kernel time: idle=80, kernel=90, user=10 => 20% busy.
            // Exercise the pure calculation independently from live CPU load.
            var calculate = type.GetMethod("CalculateCpuUsage", BindingFlags.NonPublic | BindingFlags.Static);
            Require(calculate is not null, "CPU calculation is coupled to live sampling");
            var usage = (double)calculate!.Invoke(null, new object[] { 80UL, 90UL, 10UL })!;
            Require(Math.Abs(usage - 20) < 0.001, "expected 20% busy, got " + usage);
            Require((double)calculate.Invoke(null, new object[] { 0UL, 0UL, 0UL })! == 0, "zero interval invalid");
        });
        Check("weather shows all three days and uses locale-independent coordinates", () =>
        {
            var weather = new DesktopPet.Builtin.Weather.WeatherPlugin();
            var pet = new ExtensionPet();
            var ui = new UIApiImpl(_ => null);
            var weatherStorage = new JsonPluginStorage(Path.Combine(root, "weather-fixture"));
            using var scheduler = new SchedulerImpl();
            using var scope = ui.PushScope("weather-test");
            weather.OnLoad(new PluginContextImpl
            {
                HostVersion = new Version(1, 0), DataRoot = root, PluginDataDir = root, Pet = pet, UI = ui,
                Capability = new CapabilityApiImpl { Storage = weatherStorage, Scheduler = scheduler, Events = new EventBusImpl(), System = new SystemImpl("weather-test") },
            });
            var handler = new WeatherHandler();
            typeof(DesktopPet.Builtin.Weather.WeatherPlugin).GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(weather, new HttpClient(handler));
            var previousCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                ui.AllRadialSlots.Single(s => s.Id == "weather-forecast").OnInvoke();
                PumpUntil(() => pet.Spoken.Count > 0);
                Require(pet.Spoken.Last().Contains("后天"), "three-day forecast only showed two days");
                Require(handler.Urls.Single().Contains("latitude=23.02&longitude=113.11"), "coordinates used locale commas");
                weatherStorage.Set("city", "missing-city-fixture");
                var before = pet.Spoken.Count;
                ui.AllRadialSlots.Single(s => s.Id == "weather-now").OnInvoke();
                PumpUntil(() => pet.Spoken.Count > before);
                Require(pet.Spoken.Last().Contains("找不到城市"), "unknown city silently used default coordinates");
                Require(handler.Urls.Count == 2, "unknown city requested default weather");
                weather.OnUnload();
                before = pet.Spoken.Count;
                ui.AllRadialSlots.Single(s => s.Id == "weather-now").OnInvoke();
                Require(handler.Urls.Count == 2 && pet.Spoken.Count == before, "unloaded weather recreated a client or spoke");
            }
            finally { CultureInfo.CurrentCulture = previousCulture; weather.OnUnload(); }
        });
        Check("closing the pet cancels queued deferred loading and repeated startup is harmless", () =>
        {
            var pet = new PetWindow(new AppConfig { GameModeEnabled = true, ChatEnabled = true, AppMode = "work" });
            try
            {
                pet.Show();
                pet.LoadPlugins();
                pet.LoadPlugins();
                pet.InitializeAfterFirstFrame();
                pet.Dispose();
                var completed = false;
                pet.Dispatcher.BeginInvoke(new Action(() => completed = true), System.Windows.Threading.DispatcherPriority.ContextIdle);
                PumpUntil(() => completed);
                Require(pet.PluginHost.Plugins.Count == 0, "queued load revived disposed plugins");
                var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                var gameTimer = (System.Windows.Threading.DispatcherTimer)typeof(PetWindow).GetField("_gameTimer", flags)!.GetValue(pet)!;
                Require(!gameTimer.IsEnabled, "game-mode polling survived disposal");
                Require(typeof(PetWindow).GetField("_mirrorWindow", flags)!.GetValue(pet) is null, "queued startup recreated keyboard window");
                Require(typeof(PetWindow).GetField("_chatScheduler", flags)!.GetValue(pet) is null, "queued startup recreated chat scheduler");
                Require(typeof(PetWindow).GetField("_radialMenu", flags)!.GetValue(pet) is null, "queued startup recreated radial window");
            }
            finally { pet.Dispose(); pet.Close(); }
        });
        Check("current skin can be reloaded and a broken reload persists the actual fallback", () =>
        {
            const string skinId = "reload-fixture";
            var dir = Path.Combine(Paths.Assets, skinId);
            Directory.CreateDirectory(dir);
            var manifestPath = Path.Combine(dir, "skin.json");
            File.WriteAllText(manifestPath, """{"id":"reload-fixture","frames":4,"states":[{"name":"idle","fps":2}]}""");
            File.Copy(Path.Combine(Paths.Assets, "pixel", "neko_idle.png"), Path.Combine(dir, "neko_idle.png"), true);
            var pet = new PetWindow(new AppConfig { Skin = skinId });
            var renderer = (PetRenderer)typeof(PetWindow).GetField("_renderer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pet)!;
            try
            {
                File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace("\"fps\":2", "\"fps\":11"));
                pet.SetSkin(skinId, forceReload: true);
                Require(renderer.Atlas.States["idle"].Fps == 11, "same-skin refresh reused cached data");
                File.WriteAllText(Path.Combine(dir, "neko_idle.png"), "broken replacement");
                pet.SetSkin(skinId, forceReload: true);
                Require(renderer.Atlas.Skin == "pixel" && pet.Config.Skin == "pixel", "broken skin remained configured");
            }
            finally
            {
                pet.Dispose(); pet.Close();
                File.Delete(manifestPath);
                File.Delete(Path.Combine(dir, "neko_idle.png"));
                Directory.Delete(dir);
            }
        });
    }

    private sealed class ExtensionPet : IPetApi
    {
        public List<string> Spoken { get; } = new();
        public Task PlayAnimationAsync(PetState state, AnimationOptions? opts = null) => Task.CompletedTask;
        public void SetMood(PetMood mood) { }
        public void Say(string text, int durationMs = 3000) => Spoken.Add(text);
        public Point2 GetPosition() => Point2.Zero;
        public Task MoveToAsync(Point2 target, int durationMs = 500) => Task.CompletedTask;
        public Rect2 GetWorkArea() => new(0, 0, 1920, 1080);
    }

    private static void PumpUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (done() || DateTime.UtcNow >= deadline) { timer.Stop(); frame.Continue = false; } };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        Require(done(), "async test timed out");
    }

    private sealed class WeatherHandler : HttpMessageHandler
    {
        public List<string> Urls { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.AbsoluteUri);
            var json = request.RequestUri.Host.StartsWith("geocoding-") ? """{"results":[]}"""
                : """{"daily":{"temperature_2m_max":[22,23,24],"temperature_2m_min":[12,13,14],"weather_code":[0,1,3]}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
