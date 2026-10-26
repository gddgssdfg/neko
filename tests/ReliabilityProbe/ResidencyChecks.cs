using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using DesktopPet.App.Core;
using DesktopPet.App.Services;
using DesktopPet.App.Windows;

internal static partial class Program
{
    private static void RunResidencyChecks(string root)
    {
        Check("preferences export excludes extension secrets and import preserves machine choices", () =>
        {
            var current = new AppConfig { StartWithWindows = true, DisabledPlugins = ["test.disabled"], Scale = 2, CatName = "Before",
                Extra = new() { ["secret"] = JsonSerializer.SerializeToElement("private-token") } };
            var json = SettingsBackup.Export(current);
            Require(!json.Contains("private-token") && !json.Contains("StartWithWindows") && !json.Contains("DisabledPlugins"), "private or machine-specific state leaked");
            var next = SettingsBackup.Preview(SettingsBackup.Export(new AppConfig { CatName = "After", Scale = 4 }), current);
            Require(next.CatName == "After" && next.Scale == 4 && next.StartWithWindows && next.DisabledPlugins.SequenceEqual(current.DisabledPlugins), "merge changed protected settings");
            Require(next.Extra!["secret"].GetString() == "private-token", "existing unknown config lost");
            var configPath = Path.Combine(root, "restore.json"); var pending = configPath + ".pending";
            ConfigManager.Save(configPath, current);
            SettingsBackup.Stage(SettingsBackup.Export(next), current, pending);
            Require(ConfigManager.Load(configPath).CatName == "Before", "staging changed active config");
            var backup = SettingsBackup.ApplyPending(pending, configPath);
            Require(backup is not null && ConfigManager.Load(backup).CatName == "Before", "original backup missing");
            Require(ConfigManager.Load(configPath).CatName == "After" && !File.Exists(pending), "pending import not consumed");
            File.WriteAllText(pending, "{broken"); var before = File.ReadAllText(configPath);
            try { SettingsBackup.ApplyPending(pending, configPath); throw new Exception("invalid import accepted"); } catch (JsonException) { }
            Require(File.ReadAllText(configPath) == before && File.Exists(pending), "failed import modified current config");
            foreach (var invalid in new[] { "{\"format\":\"neko-settings\",\"version\":2,\"settings\":{}}", "{\"format\":\"neko-settings\",\"version\":1,\"settings\":{\"StartWithWindows\":true}}" })
            {
                try { SettingsBackup.Preview(invalid, current); throw new Exception("unsupported settings accepted"); } catch (InvalidDataException) { }
            }
        });
        Check("desktop clamping supports negative monitors and screens smaller than the pet", () =>
        {
            var p = PetWindow.ClampDesktopPosition(new Point(-5000, 2000), new Size(100, 100), new Rect(-1920, 0, 1920, 1080));
            Require(p == new Point(-1920, 980), "negative monitor clamp wrong");
            Require(PetWindow.ClampDesktopPosition(new Point(double.NaN, 100), new Size(500, 500), new Rect(0, 0, 200, 200)) == new Point(), "small screen clamp failed");
        });
        Check("safe session skips plugins without changing preferences; lock, quiet and recovery compose", () =>
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var config = new AppConfig { SafeMode = true, Skin = "unavailable-original", KeyboardMirrorEnabled = true, AppMode = "work" };
            config.RadialMenu.Order.Add("preserve.slot", 1);
            var pet = new PetWindow(config);
            try
            {
                pet.Show(); pet.LoadPlugins(); pet.SetAppMode("work");
                pet.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Require(pet.PluginHost.Plugins.Count == 0 && !pet.PluginHost.EnablePlugin("com.lantern.notes"), "safe mode loaded plugin");
                Require(config.Skin == "unavailable-original" && config.DisabledPlugins.Count == 0, "safe mode rewrote preferences");
                Require(config.RadialMenu.Order.ContainsKey("preserve.slot"), "safe mode pruned menu preferences");
                Require(pet.TryBeginNotification() && !pet.TryBeginNotification(), "notification flood not throttled");
                pet.SetQuietMode(true); Require(pet.NotificationsSuppressed, "quiet mode did not gate notifications");
                pet.SetQuietMode(false);
                var message = typeof(PetWindow).GetMethod("ResidencyMessage", flags)!;
                var initialArea = typeof(PetWindow).GetMethod("GetWorkArea", flags)!.Invoke(pet, null);
                message.Invoke(pet, [IntPtr.Zero, 0x007E, IntPtr.Zero, IntPtr.Zero, false]);
                Require(typeof(PetWindow).GetField("_cachedWorkArea", flags)!.GetValue(pet) is null, "display change kept stale work area");
                message.Invoke(pet, [IntPtr.Zero, 0x02B1, new IntPtr(7), IntPtr.Zero, false]);
                var mirror = (InputMirrorWindow)typeof(PetWindow).GetField("_mirrorWindow", flags)!.GetValue(pet)!;
                Require(!mirror.IsVisible && pet.NotificationsSuppressed, "lock left companion visible");
                pet.SetKeyboardMirror(true); Require(!mirror.IsVisible, "settings reopened locked companion");
                message.Invoke(pet, [IntPtr.Zero, 0x02B1, new IntPtr(8), IntPtr.Zero, false]);
                Require(mirror.IsVisible, "unlock failed to restore keyboard");
                message.Invoke(pet, [IntPtr.Zero, 0x0218, new IntPtr(4), IntPtr.Zero, false]);
                Require(!mirror.IsVisible && pet.NotificationsSuppressed, "suspend left keyboard or notices active");
                message.Invoke(pet, [IntPtr.Zero, 0x02B1, new IntPtr(7), IntPtr.Zero, false]);
                message.Invoke(pet, [IntPtr.Zero, 0x0218, new IntPtr(18), IntPtr.Zero, false]);
                Require(!mirror.IsVisible, "power resume bypassed session lock");
                message.Invoke(pet, [IntPtr.Zero, 0x02B1, new IntPtr(8), IntPtr.Zero, false]);
                pet.TogglePetVisible(); pet.RecoverDesktop(); Require(!pet.IsVisible, "recovery revealed manually hidden cat");
                pet.FindCat(); Require(pet.IsVisible, "find cat did not reveal cat");
                typeof(PetWindow).GetField("_onBattery", flags)!.SetValue(pet, true);
                typeof(PetWindow).GetMethod("UpdateLoopFrequency", flags)!.Invoke(pet, null);
                var timer = (DispatcherTimer)typeof(PetWindow).GetField("_loopTimer", flags)!.GetValue(pet)!;
                Require(timer.Interval.TotalMilliseconds == 150, "battery budget not applied");
                pet.SetPaused(true); Require(timer.Interval.TotalMilliseconds == 1000, "paused loop still busy");
                var output = Environment.GetEnvironmentVariable("NEKO_CAPTURE_RESIDENCY");
                if (!string.IsNullOrEmpty(output))
                {
                    Directory.CreateDirectory(output);
                    var settings = new SettingsWindow(pet); settings.Show(); settings.ShowSection("general");
                    CaptureUiWindow(settings, Path.Combine(output, "general.png"));
                    System.Windows.Controls.ScrollViewer? FindScroll(System.Windows.DependencyObject node)
                    {
                        if (node is System.Windows.Controls.ScrollViewer scroll && scroll.ScrollableHeight > 0) return scroll;
                        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
                            if (FindScroll(System.Windows.Media.VisualTreeHelper.GetChild(node, i)) is { } child) return child;
                        return null;
                    }
                    var scroll = FindScroll(settings);
                    Require(scroll is not null, "settings scroll missing");
                    scroll!.ScrollToVerticalOffset(scroll.ScrollableHeight / 2); settings.UpdateLayout();
                    CaptureUiWindow(settings, Path.Combine(output, "recovery-middle.png"));
                    scroll.ScrollToEnd(); settings.UpdateLayout();
                    CaptureUiWindow(settings, Path.Combine(output, "recovery-bottom.png"));
                    settings.Close();
                }
            }
            finally { pet.Dispose(); pet.Close(); }
        });
    }
}
