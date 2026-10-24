using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DesktopPet.Sdk;

/// <summary>
/// 插件控件工厂：使用 NekoDesign 的当前主题和统一控件模板，
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
    /// <summary>Inline save feedback; reuse one label and keep focus in the form.</summary>
    public static void ShowStatus(Panel panel, string message, bool error = false)
    {
        var label = panel.Children.OfType<TextBlock>().FirstOrDefault(t => Equals(t.Tag, "Neko.Status"));
        if (label is null)
        {
            label = Text(""); label.Tag = "Neko.Status"; label.Margin = new Thickness(0,12,0,0);
            System.Windows.Automation.AutomationProperties.SetLiveSetting(label, System.Windows.Automation.AutomationLiveSetting.Polite);
            panel.Children.Add(label);
        }
        label.Text = message;
        label.Foreground = NekoPalette.Brush(error ? NekoDesign.Current.Accent : NekoDesign.Current.Muted);
        System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(label)?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }
    /// <summary>窗口/页面背景 #1E262F</summary>
    public static readonly Brush Bg = NekoPalette.Brush(NekoPalette.Dark.Background);

    /// <summary>卡片 / 输入框底 #2B3647</summary>
    public static readonly Brush CardBg = NekoPalette.Brush(NekoPalette.Dark.Surface);

    /// <summary>主文字 #E5E7EB</summary>
    public static readonly Brush TextMain = NekoPalette.Brush(NekoPalette.Dark.Text);

    /// <summary>次文字 #9CA3AF</summary>
    public static readonly Brush TextSub = NekoPalette.Brush(NekoPalette.Dark.Muted);

    /// <summary>边框 #3A4755</summary>
    public static readonly Brush Border = NekoPalette.Brush(NekoPalette.Dark.Line);

    /// <summary>当前主题文本（sub=true 用次文字色）。</summary>
    public static System.Windows.Controls.TextBlock Text(string content, bool sub = false) => new()
    {
        Text = content,
        Foreground = sub ? NekoPalette.Brush(NekoDesign.Current.Muted) : NekoPalette.Brush(NekoDesign.Current.Text),
        FontSize = 12.5,
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>当前主题按钮：深底 + 浅字 + 边框。</summary>
    public static System.Windows.Controls.Button Button(string text) => new()
    {
        Content = text,
        Foreground = NekoPalette.Brush(NekoDesign.Current.Text),
        Background = NekoPalette.Brush(NekoDesign.Current.Surface),
        BorderBrush = NekoPalette.Brush(NekoDesign.Current.Line),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(12, 4, 12, 4),
    };

    /// <summary>当前主题单行输入框：深底 + 浅字 + Caret 浅色。</summary>
    public static System.Windows.Controls.TextBox Input(string text = "") => new()
    {
        Text = text,
        Foreground = NekoPalette.Brush(NekoDesign.Current.Text),
        Background = NekoPalette.Brush(NekoDesign.Current.Surface),
        BorderBrush = NekoPalette.Brush(NekoDesign.Current.Line),
        BorderThickness = new Thickness(1),
        CaretBrush = NekoPalette.Brush(NekoDesign.Current.Text),
        Padding = new Thickness(6, 3, 6, 3),
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    /// <summary>分组小节标题（加粗主文字）。</summary>
    public static System.Windows.Controls.TextBlock SectionHeader(string title) => new()
    {
        Text = title,
        Foreground = NekoPalette.Brush(NekoDesign.Current.Text),
        FontSize = 14,
        FontWeight = FontWeights.Bold,
        Margin = new Thickness(0, 0, 0, 8),
    };

}
