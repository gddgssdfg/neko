using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopPet.App.Core;
using DesktopPet.App.Services;
using DesktopPet.App.Renderer;
using DesktopPet.Sdk;

namespace DesktopPet.App.Windows;

public sealed partial class SettingsWindow
{
    private void ImportExtension(bool plugin)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = plugin ? "选择包含 plugin.toml 的插件目录" : "选择包含 skin.json 的皮肤目录" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var source = picker.FolderName;
            string title, details, expectedId; ImageSource? picture = null;
            if (plugin)
            {
                var manifest = ExtensionInstaller.InspectPlugin(source);
                title = manifest.Name; expectedId = manifest.Id;
                details = $"{manifest.Id} · v{manifest.Version}\n作者：{manifest.Author}\n\n{manifest.Description}\n\n声明能力：{ExtensionInstaller.PermissionSummary(manifest)}\n\n插件可执行本机代码，不受安全沙箱隔离。安装后保持停用，确认来源可信后再手动开启。";
            }
            else
            {
                var atlas = ExtensionInstaller.InspectSkin(source);
                var info = SkinRegistry.InspectFolder(source);
                title = info.DisplayName; expectedId = info.Id;
                details = $"{info.Id} · {info.FrameWidth}×{info.FrameHeight} · 每动作 {info.Frames} 帧\n已校验 {info.States.Count} 组动作与帧尺寸。\n\n安装不会覆盖已有皮肤；安装后可在外观列表选择。";
                picture = ExtensionInstaller.SkinPreview(atlas);
            }
            var preview = new ExtensionPreviewWindow(title, details, picture, plugin ? "安装并保持停用" : "安装皮肤") { Owner = this };
            if (preview.ShowDialog() != true) return;
            if (plugin)
            {
                var inspected = ExtensionInstaller.InspectPlugin(source);
                if (inspected.Id != expectedId) throw new IOException("预览后插件 id 已改变，请重新预览。");
                if (Directory.Exists(Path.Combine(Paths.Plugins, inspected.Id))) throw new IOException("同名插件已存在，未覆盖或停用原插件。");
                if (!_config.DisabledPlugins.Contains(inspected.Id, StringComparer.OrdinalIgnoreCase)) _config.DisabledPlugins.Add(inspected.Id);
                Save();
                using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Paths.ConfigFile));
                if (!saved.RootElement.TryGetProperty("DisabledPlugins", out var disabled) ||
                    !disabled.EnumerateArray().Any(x => string.Equals(x.GetString(), inspected.Id, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("无法持久保存停用状态，因此取消安装。请检查数据目录权限。");
                _pet.PluginHost.DisablePlugin(inspected.Id);
            }
            ExtensionInstaller.Install(source, plugin ? Paths.Plugins : Paths.Assets, plugin, expectedId);
            Save(); RebuildSection(plugin ? "plugin" : "pet");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"未能导入：{ex.Message}\n\n原有扩展和当前外观未被覆盖。", "导入未完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}

public sealed class ExtensionPreviewWindow : Window
{
    public ExtensionPreviewWindow(string title, string details, ImageSource? picture, string confirm)
    {
        Title = "预览与安装"; Width = 520; Height = 580; MinWidth = 380; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; NekoDesign.Apply(this);
        var root = new DockPanel { Margin = new Thickness(24) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = PluginUi.Button("取消"); cancel.Click += (_,_) => DialogResult = false;
        var install = PluginUi.Button(confirm); install.Margin = new Thickness(12,0,0,0); install.Click += (_,_) => DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(install); DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var content = new StackPanel();
        content.Children.Add(NekoDesign.ToolHeader(title, "确认内容后再安装"));
        if (picture is not null)
        {
            var image = new Image { Source = picture, Width = 160, Height = 160, Margin = new Thickness(0,12,0,18) };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor); content.Children.Add(image);
        }
        content.Children.Add(new TextBlock { Text = details, TextWrapping = TextWrapping.Wrap, Foreground = NekoPalette.Brush(NekoDesign.Current.Text), LineHeight = 23, Margin = new Thickness(0,12,0,20) });
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Content = root;
    }
}
