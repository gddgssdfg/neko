using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopPet.App.Core;
using DesktopPet.App.Services;
using DesktopPet.App.Windows;
using DesktopPet.Core;
using DesktopPet.Sdk;
using DesktopPet.Builtin.Notes;
using DesktopPet.Builtin.TodoList;
using DesktopPet.Builtin.ClipboardHistory;
using DesktopPet.Builtin.Bazi;

internal static partial class Program
{
    private static IEnumerable<T> UiChildren<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) yield return item;
        foreach (var child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject dependency)
                foreach (var descendant in UiChildren<T>(dependency)) yield return descendant;
    }

    private static void RunUiDesignChecks()
    {
        Check("heart bursts stay visibly spread even with clustered random samples", () =>
        {
            var hearts = new HeartParticleWindow { Left = 100, Top = 100 };
            try
            {
                typeof(HeartParticleWindow).GetField("_rng", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(hearts, new Random(9));
                hearts.Burst(new Point(160,140), 3,3);
                var particles = (System.Collections.IEnumerable)typeof(HeartParticleWindow).GetField("_particles", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(hearts)!;
                var xs = particles.Cast<object>().Select(p => (double)p.GetType().GetProperty("StartX")!.GetValue(p)!).ToArray();
                Require(xs.Max()-xs.Min() > 4, "several hearts collapsed into one visual position");
            }
            finally { hearts.Dismiss(); hearts.Close(); }
        });
        Check("themed dropdown retains editable text and a usable popup width", () =>
        {
            var combo = new ComboBox { IsEditable = true, Width = 220, Items = { "第一项", "第二项" } };
            var window = new Window { Content = combo, Width = 360, Height = 180 };
            try
            {
                NekoDesign.Apply(window); window.Show(); window.UpdateLayout();
                combo.SelectedIndex = 1; combo.Text = "自定义"; combo.ApplyTemplate();
                var editor = (TextBox)combo.Template.FindName("PART_EditableTextBox", combo);
                Require(editor.Text == "自定义", "editable combo text no longer reaches the template part");
                combo.IsDropDownOpen = true; window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var popup = (System.Windows.Controls.Primitives.Popup)combo.Template.FindName("PART_Popup", combo);
                Require(popup.IsOpen && ((FrameworkElement)popup.Child).ActualWidth >= combo.ActualWidth - 2, "dropdown is missing or narrower than its field");
                combo.IsDropDownOpen = false;
            }
            finally { window.Close(); }
        });
        Check("settings theme changes immediately and preserves the selected section", () =>
        {
            var pet = new PetWindow(new AppConfig { Theme = "dark" });
            var settings = new SettingsWindow(pet);
            try
            {
                settings.Show(); settings.UpdateLayout();
                UiChildren<Button>(settings).Single(b => (b.Content as string) == "浅色").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                settings.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Require(pet.Config.Theme == "light" && ((SolidColorBrush)settings.Background).Color == NekoPalette.Light.Background,
                    "theme still requires reopening the window");
                Require(UiChildren<Button>(settings).Any(b => (b.Content as string) == "浅色"), "theme change lost the general page");
            }
            finally { settings.Close(); pet.Dispose(); pet.Close(); }
        });
        Check("cached plugin form remains editable after unloading and returning", () =>
        {
            var storage = new JsonPluginStorage(Path.Combine(Paths.Data, "ui-bazi-cache"));
            var page = (FrameworkElement)new BaziSettingsPage(storage).BuildContent();
            var window = new Window { Content = page, Width = 600, Height = 600 };
            try
            {
                NekoDesign.Apply(window); window.Show(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.Content = null; window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.Content = page; window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                UiChildren<TextBox>(page).First().Text = "2000";
            }
            finally { window.Close(); }
        });
        Check("keyboard digit legends map to their VK and bottom row responds without overlaps", () =>
        {
            var config = new AppConfig();
            var mirror = new InputMirrorWindow(new GlobalInput(), config, () => { });
            try
            {
                var keys = (Dictionary<uint, Border>)typeof(InputMirrorWindow).GetField("_keyByVk", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(mirror)!;
                for (uint digit = 0; digit < 10; digit++)
                    Require(UiChildren<TextBlock>(keys[0x30+digit]).Single().Text == digit.ToString(), "digit VK/legend mismatch: " + digit);
                foreach (uint vk in new uint[] { 0x20, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C, 0x5D })
                    Require(keys.ContainsKey(vk), "missing bottom-row key: " + vk);
                Require(!mirror.LayoutHasOverlaps(), "keys overlap");
                config.Theme = "light"; mirror.RefreshTheme();
                Require(mirror.RegisteredKeys == 74 && !mirror.LayoutHasOverlaps(), "theme refresh duplicated keys");
            }
            finally { mirror.Close(); }
        });
        Check("switch exposes UI Automation TogglePattern and emits a single user change", () =>
        {
            var toggle = new NekoSwitch(NekoPalette.Dark.Line, NekoPalette.Dark.Accent);
            var window = new Window { Content = toggle, Width = 160, Height = 100 };
            try
            {
                NekoDesign.Apply(window); window.Show(); window.UpdateLayout(); int changes = 0;
                toggle.Toggled += _ => changes++;
                var peer = UIElementAutomationPeer.CreatePeerForElement(toggle)!;
                var provider = (IToggleProvider)peer.GetPattern(PatternInterface.Toggle)!;
                provider.Toggle();
                Require(toggle.IsOn && changes == 1, "automation did not toggle exactly once");
            }
            finally { window.Close(); }
        });
        Check("theme refresh recolors shared tokens while preserving custom plugin artwork", () =>
        {
            NekoDesign.LightTheme = false;
            var standard = PluginUi.Text("standard");
            var custom = new TextBlock { Foreground = Brushes.Magenta };
            var window = new Window { Content = new StackPanel { Children = { standard, custom } } };
            try
            {
                NekoDesign.Apply(window, false); NekoDesign.Apply(window, true);
                Require(((SolidColorBrush)standard.Foreground).Color == NekoPalette.Light.Text, "shared foreground stayed dark-theme");
                Require(ReferenceEquals(custom.Foreground, Brushes.Magenta), "custom artwork changed");
            }
            finally { window.Close(); }
        });
        Check("plugin search filters cards and settings build once on expansion", () =>
        {
            var pet = new PetWindow(new AppConfig { Theme = "light" });
            var page = new CountingUiPage();
            using (pet.UIApi.PushScope(page.Id)) pet.UIApi.RegisterSettingsPage(page);
            var settings = new SettingsWindow(pet);
            try
            {
                settings.Show(); settings.ShowSection("plugin"); settings.UpdateLayout();
                Require(page.Builds == 0, "collapsed settings built eagerly");
                var search = UiChildren<TextBox>(settings).Single(t => AutomationProperties.GetName(t) == "搜索插件");
                search.Text = "com.lantern.weather";
                var matches = UiChildren<Expander>(settings).Where(e => e.IsVisible).ToArray();
                Require(matches.Length == 1, "search did not isolate the weather plugin");
                matches[0].IsExpanded = true; matches[0].IsExpanded = false; matches[0].IsExpanded = true;
                Require(page.Builds == 1, "settings were missing or rebuilt on every expansion");
                search.Text = "no-such-plugin-012345";
                Require(!UiChildren<Expander>(settings).Any(e => e.IsVisible), "unmatched cards remain visible");
                search.Text = "";
                Require(UiChildren<Expander>(settings).Any(e => e.IsVisible), "clearing search did not restore cards");
            }
            finally { settings.Close(); pet.Dispose(); pet.Close(); }
        });
        Check("todo input stretches and add/check/delete preserve data at minimum width", () =>
        {
            var items = new List<TodoItem>(); int saves = 0;
            var window = new TodoWindow(items, _ => saves++, new SystemImpl());
            try
            {
                window.Width = window.MinWidth; window.Show(); window.UpdateLayout();
                var input = UiChildren<TextBox>(window).Single();
                Require(input.ActualWidth > 80, "input collapsed in a horizontal StackPanel");
                input.Text = "给小猫添水";
                UiChildren<Button>(window).Single(b => (b.Content as string) == "添加").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(items.Count == 1 && items[0].Text == "给小猫添水" && saves == 1, "add lost data");
                var list = UiChildren<ListBox>(window).Single(); list.SelectedIndex = 0;
                UiChildren<Button>(window).Single(b => (b.Content as string) == "删除选中").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(items.Count == 0 && saves == 2, "delete did not save");
            }
            finally { window.Close(); }
        });
        Check("clipboard filtering does not copy until the explicit action", () =>
        {
            string? copied = null;
            var window = new ClipboardWindow(new List<string> { "橘猫", "今天的计划" }, text => copied = text, new SystemImpl());
            try
            {
                window.Show(); window.UpdateLayout();
                var search = UiChildren<TextBox>(window).Single(); search.Text = "橘";
                var list = UiChildren<ListBox>(window).Single();
                Require(list.Items.Count == 1 && copied is null, "search copied or failed to filter");
                list.SelectedIndex = 0;
                UiChildren<Button>(window).Single(b => (b.Content as string) == "复制选中").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(copied == "橘猫", "copy action did not preserve full content");
            }
            finally { window.Close(); }
        });
    }

    private sealed class CountingUiPage : ISettingsPage
    {
        public string Id => "com.lantern.weather";
        public string Title => Id;
        public int Builds { get; private set; }
        public object BuildContent() { Builds++; return PluginUi.Text("天气设置测试"); }
    }

    private static void CaptureUiDesign(string output)
    {
        Capture(output, "pixel");
        var sys = new SystemImpl();
        foreach (var light in new[] { false, true })
        {
            NekoDesign.LightTheme = light;
            var theme = light ? "light" : "dark";
            var config = new AppConfig { Skin = "pixel", Theme = theme };
            var tools = new (string Name, Window Window)[] {
                ("notes", new NotesWindow("今天的小事\n\n给橘猫想一个名字。\n留一点时间散步。\n把好点子记在这里。", _ => { }, sys)),
                ("todo", new TodoWindow(new List<TodoItem> { new() { Text = "整理今天的计划" }, new() { Text = "喝一杯水", Done = true }, new() { Text = "陪橘猫休息一会儿" } }, _ => { }, sys)),
                ("clipboard", new ClipboardWindow(new List<string> { "今天也要保持好心情。", "Neko — 你的小小桌面伙伴", "橘猫的新动作已经准备好了。" }, _ => { }, sys)),
                ("chat", new ChatWindow(Array.Empty<ChatMessage>(), _ => Task.FromResult(ChatResult.Success("预览回答")))),
                ("quiz", new MbtiQuizWindow(_ => { })),
                ("keyboard", new InputMirrorWindow(new GlobalInput(), config, () => { })),
                ("welcome", new FirstRunWindow(config))
            };
            foreach (var (name, window) in tools)
            { try { window.Show(); CaptureUiWindow(window, Path.Combine(output, $"{name}-{theme}.png")); } finally { window.Close(); } }
            var pet = new PetWindow(config);
            var settings = new SettingsWindow(pet);
            try
            {
                pet.Show(); pet.LoadPlugins();
                pet.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                settings.Width = settings.MinWidth; settings.Height = settings.MinHeight; settings.Show();
                foreach (var section in new[] { "general", "pet", "input", "plugin" })
                { settings.ShowSection(section); CaptureUiWindow(settings, Path.Combine(output, $"narrow-{theme}-{section}.png")); }
                settings.Width = 1040; settings.Height = 780; settings.ShowSection("plugin");
                UiChildren<TextBox>(settings).Single(t => AutomationProperties.GetName(t) == "搜索插件").Text = "com.lantern.weather";
                foreach (var expander in UiChildren<Expander>(settings).Where(e => e.IsVisible)) expander.IsExpanded = true;
                CaptureUiWindow(settings, Path.Combine(output, $"plugin-settings-{theme}.png"));
            }
            finally { settings.Close(); pet.Dispose(); pet.Close(); }
        }
    }

    private static void CaptureUiWindow(Window window, string path)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_,_) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
        window.UpdateLayout(); FrameworkElement visual = window;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96,96,PixelFormats.Pbgra32);
        bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
