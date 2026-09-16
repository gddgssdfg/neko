using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DesktopPet.Builtin.Notes;

/// <summary>桌面便签窗口：多行输入，停顿 800ms 自动保存 + 手动保存按钮 + 关窗保存。</summary>
public sealed class NotesWindow : Window
{
    /// <summary>v0.1.82：冻结画刷 —— 冻结后可跨线程共享、省渲染开销；未冻结的 Freezable 在跨线程或高频渲染时是纯损耗。</summary>
    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static readonly Brush Bg = Frozen(0x1E, 0x26, 0x2F);
    private static readonly Brush TextMain = Frozen(0xE5, 0xE7, 0xEB);
    private static readonly Brush TextSub = Frozen(0x9C, 0xA3, 0xAF);
    private static readonly Brush CardBg = Frozen(0x2B, 0x36, 0x47);
    private static readonly Brush CardBorder = Frozen(0x3A, 0x47, 0x55);

    private readonly Action<string> _onSave;
    private readonly TextBox _editor;
    private readonly DispatcherTimer _autoSave;

    public NotesWindow(string current, Action<string> onSave)
    {
        _onSave = onSave;
        Title = "Neko 备忘录";
        Width = 380;
        Height = 340;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        MinWidth = 260;
        MinHeight = 200;
        Background = Bg;
        Opacity = 0;   // 淡入动画起点

        var dock = new DockPanel { Margin = new Thickness(12) };
        var save = new Button
        {
            Content = "保存",
            Padding = new Thickness(16, 6, 16, 6),
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = TextMain,
            Background = CardBg,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
        };
        DockPanel.SetDock(save, Dock.Bottom);

        _editor = new TextBox
        {
            Text = current,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = 14,
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
        _editor.TextChanged += (_, _) => { _autoSave.Stop(); _autoSave.Start(); };

        save.Click += (_, _) => { Save(); Close(); };
        Closed += (_, _) => { _autoSave.Stop(); Save(); };

        dock.Children.Add(save);
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
        try { _onSave(_editor.Text); } catch { }
    }
}
