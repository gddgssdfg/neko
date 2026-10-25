using System.IO;
using System.Reflection;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DesktopPet.Core;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using DesktopPet.App.Core;
using DesktopPet.App.Renderer;
using DesktopPet.App.Services;
using DesktopPet.App.Windows;
using DesktopPet.Builtin.ClipboardHistory;

internal static partial class Program
{
    private static void RunReleaseReadyChecks(string root)
    {
        Check("updater uses public GitHub and distinguishes absent releases, failures and cancellation", () =>
        {
            using var handler = new ReleaseHandler(_ => (HttpStatusCode.OK, """{"tag_name":"v9.0.0","html_url":"https://github.com/gddgssdfg/neko/releases/tag/v9.0.0"}"""));
            using var client = new HttpClient(handler);
            var found = UpdateService.CheckLatestAsync(default, client).GetAwaiter().GetResult();
            Require(found.Status == UpdateCheckStatus.Success && found.Release?.Version == "9.0.0", "valid release missing");
            Require(handler.Hosts.SequenceEqual(new[] { "api.github.com" }), "private mirror blocked primary source");
            handler.Response = _ => (HttpStatusCode.NotFound, "");
            Require(UpdateService.CheckLatestAsync(default, client).GetAwaiter().GetResult().Status == UpdateCheckStatus.NoRelease, "no release presented as network failure");
            handler.Response = _ => (HttpStatusCode.Forbidden, "");
            Require(UpdateService.CheckLatestAsync(default, client).GetAwaiter().GetResult().Status == UpdateCheckStatus.Failed, "rate limit presented as latest version");
            handler.Response = _ => (HttpStatusCode.OK, "{invalid");
            Require(UpdateService.CheckLatestAsync(default, client).GetAwaiter().GetResult().Status == UpdateCheckStatus.Failed, "bad JSON presented as latest");
            handler.Response = host => host == "api.github.com" ? (HttpStatusCode.ServiceUnavailable, "") : (HttpStatusCode.OK, """{"tag_name":"v1.0.0"}""");
            var fallback = UpdateService.CheckLatestAsync(default, client).GetAwaiter().GetResult();
            Require(fallback.Release?.ReleaseUrl == "https://gitee.com/gddgssdfg/neko/releases/tag/v1.0.0", "mirror release points at wrong host");
            var unsafeLink = UpdateService.ParseLatest("""{"tag_name":"v1.0.0","html_url":"file:///C:/unsafe.exe"}""");
            Require(unsafeLink?.ReleaseUrl == "https://github.com/gddgssdfg/neko/releases/tag/v1.0.0", "unsafe update link accepted");
            handler.Response = _ => (HttpStatusCode.OK, """{"tag_name":"v9.0.0-beta","prerelease":true}""");
            Require(UpdateService.CheckLatestAsync(default, client).GetAwaiter().GetResult().Status == UpdateCheckStatus.NoRelease, "preview release advertised as stable");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Require(UpdateService.CheckLatestAsync(cancelled.Token, client).GetAwaiter().GetResult().Status == UpdateCheckStatus.Cancelled, "cancel became failure");
        });
        Check("clipboard opt-in, pause, retention, migration and clear persist correctly", () =>
        {
            var storage = new JsonPluginStorage(Path.Combine(root, "clipboard-policy"));
            var now = DateTimeOffset.UtcNow;
            var store = new ClipboardStore(storage, () => now);
            store.Record("not consented"); Require(store.Read().Count == 0, "history collected without consent");
            store.SetRecording(true); store.Record("one"); store.Record("one");
            Require(store.Read().Count == 1, "duplicate history");
            store.SetRecording(false); store.Record("paused");
            Require(store.Read().Count == 1, "pause still records");
            now = now.AddDays(8); Require(store.Read().Count == 0, "expired entries retained");
            store.SetRecording(true);
            for (int i = 0; i < 30; i++) store.Record("entry " + i);
            Require(store.Read().Count == 20 && store.Read()[0].Text == "entry 29", "history not bounded or wrong order");
            store.Clear(); Require(new ClipboardStore(storage).Read().Count == 0 && !storage.Contains("entries"), "clear left history on disk");
            var legacy = new JsonPluginStorage(Path.Combine(root, "clipboard-legacy"));
            legacy.Set(ClipboardHistoryPlugin.HistoryKey, new List<string> { "legacy" });
            var migrated = new ClipboardStore(legacy);
            Require(!migrated.Recording && migrated.Read().Single().Text == "legacy" && !legacy.Contains(ClipboardHistoryPlugin.HistoryKey), "migration opted in or lost history");
        });
        Check("clipboard plugin does not read before consent and pause detaches listeners", () =>
        {
            var storage = new JsonPluginStorage(Path.Combine(root, "clipboard-runtime"));
            var system = new ConsentSystem(); var ui = new UIApiImpl(_ => null);
            using var scope = ui.PushScope("consent-test"); using var scheduler = new SchedulerImpl();
            var plugin = new ClipboardHistoryPlugin();
            plugin.OnLoad(new PluginContextImpl { HostVersion = new Version(1,0), DataRoot = root, PluginDataDir = root,
                Pet = new ExtensionPet(), UI = ui, Capability = new CapabilityApiImpl {
                    Storage = storage, Scheduler = scheduler, System = system, Events = new EventBusImpl() } });
            try
            {
                Require(system.Reads == 0, "plugin reads clipboard before consent");
                var page = (FrameworkElement)plugin.BuildContent();
                var toggle = UiChildren<CheckBox>(page).Single();
                toggle.IsChecked = true; toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Require(system.Reads == 1, "consent did not establish clipboard baseline");
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                system.Text = "consented fixture";
                typeof(ClipboardHistoryPlugin).GetMethod("PollOnce", flags)!.Invoke(plugin, null);
                Require(new ClipboardStore(storage).Read().Single().Text == system.Text, "enabled listener did not persist text");
                toggle.IsChecked = false; toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                int reads = system.Reads;
                typeof(ClipboardHistoryPlugin).GetMethod("PollOnce", flags)!.Invoke(plugin, null);
                Require(system.Reads == reads, "paused plugin still reads clipboard");
                Require(typeof(ClipboardHistoryPlugin).GetField("_messageWindow", flags)!.GetValue(plugin) is null &&
                    typeof(ClipboardHistoryPlugin).GetField("_fallbackPoller", flags)!.GetValue(plugin) is null, "pause left listener alive");
                UiChildren<Button>(page).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(new ClipboardStore(storage).Read().Count == 0, "clear button left persisted history");
                string? output = Environment.GetEnvironmentVariable("NEKO_CAPTURE_RELEASE_UI");
                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output);
                    foreach (bool light in new[] { false, true })
                    {
                        DesktopPet.Sdk.NekoDesign.LightTheme = light;
                        string theme = light ? "light" : "dark";
                        var controls = (FrameworkElement)plugin.BuildContent();
                        var clipboard = new ClipboardWindow(new(), _ => { }, system, controls, recordingState: () => false);
                        var welcome = new FirstRunWindow(new AppConfig { Theme = theme });
                        var atlas = SpriteAtlas.LoadSkin(Paths.Assets, "pixel");
                        var preview = new ExtensionPreviewWindow("圆头橘猫", "已校验 10 组动作与帧尺寸。安装不会覆盖已有皮肤。", ExtensionInstaller.SkinPreview(atlas), "安装皮肤");
                        try
                        {
                            clipboard.Show(); CaptureUiWindow(clipboard, Path.Combine(output, "clipboard-"+theme+".png"));
                            welcome.Show(); CaptureUiWindow(welcome, Path.Combine(output, "welcome-"+theme+".png"));
                            preview.Show(); CaptureUiWindow(preview, Path.Combine(output, "import-"+theme+".png"));
                        }
                        finally { clipboard.Close(); welcome.Close(); preview.Close(); }
                    }
                }
            }
            finally { plugin.OnUnload(); ui.UnregisterAll("consent-test"); }
        });
        Check("first-run default action accepts blank naming and leaves recording/network opt-in", () =>
        {
            var config = new AppConfig();
            var welcome = new FirstRunWindow(config);
            welcome.Loaded += (_, _) => welcome.Dispatcher.BeginInvoke(new Action(() =>
            {
                foreach (var input in UiChildren<TextBox>(welcome)) input.Text = "";
                UiChildren<Button>(welcome).Single(b => b.Content?.ToString() == "使用默认设置").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            try
            {
                Require(welcome.ShowDialog() == true, "default start rejected");
                Require(config.CatName == "小橘" && config.OwnerTitle == "你", "default names missing");
                Require(!config.UpdateCheckEnabled && config.KeyboardMirrorEnabled && config.AppMode == "work", "first-run preferences not applied");
                var storage = new JsonPluginStorage(Path.Combine(Paths.Data, "plugins", "com.lantern.clipboard"));
                Require(!storage.Get<bool>("recordingEnabled"), "default start consented to clipboard");
            }
            finally { welcome.Close(); }
        });
        Check("skin import previews and installs atomically without overwriting existing packages", () =>
        {
            var source = Path.Combine(root, "import-source", "pixel"); Directory.CreateDirectory(source);
            foreach (var path in Directory.GetFiles(Path.Combine(Paths.Assets, "pixel"))) File.Copy(path, Path.Combine(source, Path.GetFileName(path)));
            var atlas = ExtensionInstaller.InspectSkin(source);
            Require(atlas.States.Count >= 9, "preview failed to validate animation");
            var target = Path.Combine(root, "import-target");
            Require(ExtensionInstaller.Install(source, target, false) == "pixel", "skin not installed");
            var original = File.ReadAllBytes(Path.Combine(target, "pixel", "neko_idle.png"));
            try { ExtensionInstaller.Install(source, target, false); throw new Exception("duplicate overwrote package"); }
            catch (IOException) { }
            File.Delete(Path.Combine(source, "neko_walk.png"));
            try { ExtensionInstaller.Install(source, Path.Combine(root, "broken-target"), false); throw new Exception("broken animation imported"); }
            catch (FormatException) { }
            Require(original.SequenceEqual(File.ReadAllBytes(Path.Combine(target, "pixel", "neko_idle.png"))), "failed import changed installed skin");
            Require(!Directory.Exists(Path.Combine(root, "broken-target", "pixel")), "failed import left a partial skin");
        });
        Check("plugin inspection rejects unsafe entry and imports metadata without executing code", () =>
        {
            var source = Path.Combine(root, "preview-plugin"); Directory.CreateDirectory(source);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "PluginLifecycleFixture.dll"), Path.Combine(source, "PluginLifecycleFixture.dll"));
            var manifest = "[plugin]\nid = \"com.neko.preview\"\nname = \"Preview\"\nentry = \"PluginLifecycleFixture.dll\"\napi = 1\n";
            File.WriteAllText(Path.Combine(source, "plugin.toml"), manifest);
            var inspected = ExtensionInstaller.InspectPlugin(source);
            Require(inspected.Id == "com.neko.preview", "metadata preview failed");
            var target = Path.Combine(root, "plugin-import-target");
            Require(ExtensionInstaller.Install(source, target, true) == inspected.Id, "valid package not installed");
            File.WriteAllText(Path.Combine(source, "plugin.toml"), manifest.Replace("PluginLifecycleFixture.dll", "../PluginLifecycleFixture.dll"));
            try { ExtensionInstaller.InspectPlugin(source); throw new Exception("outside plugin entry accepted"); }
            catch (FormatException) { }
        });
    }

    private sealed class ConsentSystem : ISystem
    {
        public int Reads; public string Text = "baseline";
        public void Notify(string title, string body) { }
        public string? ReadClipboard() { Reads++; return Text; }
        public void WriteClipboard(string text) { }
        public void OpenUrl(string url) { }
        public string? PickFile(string filter = "") => null;
        public void Log(PluginLogLevel level, string message, Exception? exception = null) { }
    }

    private sealed class ReleaseHandler(Func<string, (HttpStatusCode Status, string Json)> response) : HttpMessageHandler
    {
        public Func<string, (HttpStatusCode Status, string Json)> Response = response;
        public List<string> Hosts { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Hosts.Add(request.RequestUri!.Host);
            var result = Response(request.RequestUri.Host);
            return Task.FromResult(new HttpResponseMessage(result.Status) { Content = new StringContent(result.Json) });
        }
    }
}
