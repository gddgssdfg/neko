using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DesktopPet.Sdk;

/// <summary>
/// 插件设置页深色控件工厂（v0.1.64）。配色与宿主设置页一致，
/// 让所有插件设置页风格统一，避免"各写各的控件样式"。
///
/// 用法示例：
/// <code>
/// var stack = new StackPanel { Margin = new Thickness(8) };
/// stack.Children.Add(PluginUi.SectionHeader("时间设置"));
/// stack.Children.Add(PluginUi.Text("提醒时间", sub: true));
/// stack.Children.Add(PluginUi.Input("23:00"));
/// stack.Children.Add(PluginUi.Button("保存"));
/// </code>
/// </summary>
public static class PluginUi
{
    /// <summary>窗口/页面背景 #1E262F</summary>
    public static readonly Brush Bg = Frozen(0x1E, 0x26, 0x2F);

    /// <summary>卡片 / 输入框底 #2B3647</summary>
    public static readonly Brush CardBg = Frozen(0x2B, 0x36, 0x47);

    /// <summary>主文字 #E5E7EB</summary>
    public static readonly Brush TextMain = Frozen(0xE5, 0xE7, 0xEB);

    /// <summary>次文字 #9CA3AF</summary>
    public static readonly Brush TextSub = Frozen(0x9C, 0xA3, 0xAF);

    /// <summary>边框 #3A4755</summary>
    public static readonly Brush Border = Frozen(0x3A, 0x47, 0x55);

    /// <summary>深色主题文本（sub=true 用次文字色）。</summary>
    public static System.Windows.Controls.TextBlock Text(string content, bool sub = false) => new()
    {
        Text = content,
        Foreground = sub ? TextSub : TextMain,
        FontSize = 12.5,
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>深色主题按钮：深底 + 浅字 + 边框。</summary>
    public static System.Windows.Controls.Button Button(string text) => new()
    {
        Content = text,
        Foreground = TextMain,
        Background = CardBg,
        BorderBrush = Border,
        BorderThickness = new Thickness(1),
        Padding = new Thickness(12, 4, 12, 4),
    };

    /// <summary>深色主题单行输入框：深底 + 浅字 + Caret 浅色。</summary>
    public static System.Windows.Controls.TextBox Input(string text = "") => new()
    {
        Text = text,
        Foreground = TextMain,
        Background = CardBg,
        BorderBrush = Border,
        BorderThickness = new Thickness(1),
        CaretBrush = TextMain,
        Padding = new Thickness(6, 3, 6, 3),
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    /// <summary>分组小节标题（加粗主文字）。</summary>
    public static System.Windows.Controls.TextBlock SectionHeader(string title) => new()
    {
        Text = title,
        Foreground = TextMain,
        FontSize = 14,
        FontWeight = FontWeights.Bold,
        Margin = new Thickness(0, 0, 0, 8),
    };

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();   // 冻结后可跨线程共享、省渲染开销（宿主常驻内存红线）
        return brush;
    }
}
