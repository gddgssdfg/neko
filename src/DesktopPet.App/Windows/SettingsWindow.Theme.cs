// v0.1.84：从 SettingsWindow.cs 按功能区域拆出（审计 P2-11）——主题与页面外壳
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DesktopPet.App.Core;
using DesktopPet.App.Services;
using DesktopPet.Core;

namespace DesktopPet.App.Windows;

public sealed partial class SettingsWindow : Window
{
    // Map only the SDK's shared palette. Custom plugin artwork/colors remain under plugin control.
    private void ApplyPluginTheme(DependencyObject element)
    {
        Brush? Map(Brush? brush)
        {
            if (ReferenceEquals(brush, DesktopPet.Sdk.PluginUi.Bg)) return new SolidColorBrush(BgRoot);
            if (ReferenceEquals(brush, DesktopPet.Sdk.PluginUi.CardBg)) return new SolidColorBrush(BgCardHover);
            if (ReferenceEquals(brush, DesktopPet.Sdk.PluginUi.TextMain)) return new SolidColorBrush(TextMain);
            if (ReferenceEquals(brush, DesktopPet.Sdk.PluginUi.TextSub)) return new SolidColorBrush(TextSub);
            if (ReferenceEquals(brush, DesktopPet.Sdk.PluginUi.Border)) return new SolidColorBrush(BorderC);
            return brush;
        }
        void MapProperty(DependencyObject target, DependencyProperty property)
        {
            if (target.GetValue(property) is not Brush brush) return;
            var mapped = Map(brush);
            if (!ReferenceEquals(mapped, brush)) target.SetCurrentValue(property, mapped);
        }
        if (element is Control)
        {
            MapProperty(element, Control.ForegroundProperty);
            MapProperty(element, Control.BackgroundProperty);
            MapProperty(element, Control.BorderBrushProperty);
        }
        if (element is TextBlock) MapProperty(element, TextBlock.ForegroundProperty);
        if (element is Panel) MapProperty(element, Panel.BackgroundProperty);
        if (element is Border)
        {
            MapProperty(element, Border.BackgroundProperty);
            MapProperty(element, Border.BorderBrushProperty);
        }
        foreach (var child in LogicalTreeHelper.GetChildren(element))
            if (child is DependencyObject dependency) ApplyPluginTheme(dependency);
    }

    private UIElement WrapPage(FrameworkElement content, string title)
    {
        var sp = new StackPanel { Margin = new Thickness(28, 24, 28, 28) };
        sp.Children.Add(MakeHeader(title));
        sp.Children.Add(content);
        var sv = new ScrollViewer
        {
            Content = sp,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(BgRoot),
        };
        // The window's SDK resources provide the same scrollbars as standalone plugin tools.
        return sv;
    }

    private string PageDescription(string section) => section switch
    {
        "general" => T("安排小伙伴融入桌面的方式。", "Make Neko fit your day."),
        "pet" => T("选外观、调大小，找到舒服的陪伴距离。", "Choose a look and make it yours."),
        "input" => T("每一次敲击，都有一只小猫回应。", "A little response to every keystroke."),
        "radial" => T("把常用功能放到猫咪身边。", "Keep favorite actions close."),
        "plugin" => T("管理桌面工具，为小猫添一点本领。", "Useful little tools for your companion."),
        _ => T("一只像素橘猫，和你的每一天。", "A pixel cat for your everyday."),
    };

    private FrameworkElement MakeHeader(string title)
    {
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new TextBlock { Text = title, FontSize = 28,
            FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(TextMain) });
        header.Children.Add(new TextBlock { Text = PageDescription(_currentSection),
            FontSize = 12, Margin = new Thickness(0, 8, 0, 0), Foreground = new SolidColorBrush(TextSub) });
        return header;
    }

    /// <summary>v0.1.38：分组小节标题（如"快捷键"），与卡片之间拉开距离</summary>
    private FrameworkElement MakeSectionHeader(string title) => new TextBlock
    {
        Text = title,
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        Foreground = new SolidColorBrush(TextSub),
        Margin = new Thickness(0, 20, 0, 10),
    };
}
