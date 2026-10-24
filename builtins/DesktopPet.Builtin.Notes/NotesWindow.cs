using DesktopPet.Sdk;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DesktopPet.Core;

namespace DesktopPet.Builtin.Notes;

/// <summary>桌面便签窗口：多行输入，停顿 800ms 自动保存 + 手动保存按钮 + 关窗保存。</summary>
public sealed class NotesWindow : Window
{
    private static Brush Bg => NekoPalette.Brush(NekoDesign.Current.Background);
    private static Brush TextMain => NekoPalette.Brush(NekoDesign.Current.Text);
    private static Brush TextSub => NekoPalette.Brush(NekoDesign.Current.Muted);
    private static Brush CardBg => NekoPalette.Brush(NekoDesign.Current.Surface);
    private static Brush CardBorder => NekoPalette.Brush(NekoDesign.Current.Line);

    private readonly Action<string> _onSave;
    private readonly ISystem _sys;
    private readonly TextBox _editor;
    private readonly DispatcherTimer _autoSave;

    public NotesWindow(string current, Action<string> onSave, ISystem system)
    {
        _onSave = onSave;
        _sys = system;
        Title = "Neko 备忘录";
        Width = 480;
        Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        MinWidth = 260;
        MinHeight = 200;
        Background = Bg;
        NekoDesign.Apply(this);
        Opacity = 0;   // 淡入动画起点

        var dock = new DockPanel { Margin = new Thickness(24) };
        var header = NekoDesign.ToolHeader("随手记", "灵感、提醒，还有今天的小事。");
        DockPanel.SetDock(header, Dock.Top); dock.Children.Add(header);
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var status = PluginUi.Text("停顿后自动保存 · Ctrl+S 保存", sub: true);
        var save = new Button
        {
            Content = "保存并关闭",
            Padding = new Thickness(16, 6, 16, 6),
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = TextMain,
            Background = CardBg,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
        };
        DockPanel.SetDock(save, Dock.Right); footer.Children.Add(save); footer.Children.Add(status);
        DockPanel.SetDock(footer, Dock.Bottom); dock.Children.Add(footer);

        _editor = new TextBox
        {
            Text = current,
            AcceptsReturn = true,
            AcceptsTab = true,
            VerticalContentAlignment = VerticalAlignment.Top,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = 15,
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = TextMain,
            Background = CardBg,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            CaretBrush = TextMain,
        };

        // 自动保存：停顿 800ms 无输入即保存（不用点按钮）
        _autoSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _autoSave.Tick += (_, _) => { _autoSave.Stop(); Save(); };
        _editor.TextChanged += (_, _) => { status.Text = $"{_editor.Text.Length} 字 · 自动保存"; _autoSave.Stop(); _autoSave.Start(); };
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.S && System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control)) { _autoSave.Stop(); Save(); e.Handled = true; } };

        save.Click += (_, _) => { Save(); Close(); };
        Closed += (_, _) => { _autoSave.Stop(); Save(); };

        dock.Children.Add(_editor);
        Content = dock;

        Loaded += (_, _) =>
        {
            _editor.Focus();
            _editor.CaretIndex = _editor.Text.Length;
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
        };
    }

    private void Save()
    {
        // v0.1.83：落盘失败是真故障——经插件日志通道上报（宿主会加 [插件:<id>] 前缀）；绝不外抛（铁律 7）
        try { _onSave(_editor.Text); } catch (Exception ex) { _sys.Log(PluginLogLevel.Warn, $"备忘录落盘失败：{ex.Message}"); }
    }
}
