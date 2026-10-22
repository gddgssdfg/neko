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
        var sp = new StackPanel { Margin = new Thickness(30, 24, 30, 28) };
        sp.Children.Add(MakeHeader(title));
        sp.Children.Add(content);
        var sv = new ScrollViewer
        {
            Content = sp,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(BgRoot),
        };
        // v0.1.82：WPF 默认滚动条是浅色带箭头的，在深色设置页里非常突兀 → 换成深色细条
        // v0.1.88：改用静态缓存的样式实例（原来每次 WrapPage 都 XamlReader.Parse → ~0.8ms/次切页）
        sv.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), DarkScrollBar);
        sv.Resources["Neko.ScrollThumb"] = new SolidColorBrush(ToggleOff);
        sv.Resources["Neko.ScrollHover"] = new SolidColorBrush(TextSub);
        sv.Resources["Neko.ScrollActive"] = new SolidColorBrush(Accent);
        return sv;
    }

    /// <summary>
    /// v0.1.88：深色细滚动条样式——<b>静态只解析一次</b>并复用（原每次 <see cref="WrapPage"/> 都重建，~0.8ms/次）。
    /// <para>Style / ControlTemplate 是纯静态资源、无实例状态，作为共享资源被多个 <see cref="ScrollViewer"/> 引用是 WPF 的常规用法
    /// （同一 UI 线程，且构建后不再修改）。深浅主题沿用同一套样式，与改动前行为一致。</para>
    /// </summary>
    private static readonly Style DarkScrollBar = BuildDarkScrollBarStyle();

    /// <summary>
    /// v0.1.82：深色细滚动条（10px、圆角滑块、无箭头按钮、悬停变亮）。
    /// <para>用 XAML 字符串 + XamlReader 构造，比在 C# 里手搓 ControlTemplate 短得多也好读。
    /// 模板假设**竖直方向**（设置页只出竖直条）；横向时直接隐藏，避免出现错位的条。</para>
    /// </summary>
    private static Style BuildDarkScrollBarStyle() =>
        (Style)System.Windows.Markup.XamlReader.Parse(@"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
       TargetType='ScrollBar'>
  <Setter Property='Width' Value='10'/>
  <Setter Property='Margin' Value='2,0,0,0'/>
  <Setter Property='Background' Value='Transparent'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='ScrollBar'>
        <Grid Background='Transparent'>
          <Track x:Name='PART_Track' IsDirectionReversed='True'>
            <Track.DecreaseRepeatButton>
              <RepeatButton Command='ScrollBar.PageUpCommand' Opacity='0' Focusable='False'/>
            </Track.DecreaseRepeatButton>
            <Track.Thumb>
              <Thumb>
                <Thumb.Template>
                  <ControlTemplate TargetType='Thumb'>
                    <Border x:Name='bar' Background='{DynamicResource Neko.ScrollThumb}' CornerRadius='5' Margin='2,0'/>
                    <ControlTemplate.Triggers>
                      <Trigger Property='IsMouseOver' Value='True'>
                        <Setter TargetName='bar' Property='Background' Value='{DynamicResource Neko.ScrollHover}'/>
                      </Trigger>
                      <Trigger Property='IsDragging' Value='True'>
                        <Setter TargetName='bar' Property='Background' Value='{DynamicResource Neko.ScrollActive}'/>
                      </Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Thumb.Template>
              </Thumb>
            </Track.Thumb>
            <Track.IncreaseRepeatButton>
              <RepeatButton Command='ScrollBar.PageDownCommand' Opacity='0' Focusable='False'/>
            </Track.IncreaseRepeatButton>
          </Track>
        </Grid>
        <ControlTemplate.Triggers>
          <Trigger Property='Orientation' Value='Horizontal'>
            <Setter Property='Visibility' Value='Collapsed'/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>");

    private FrameworkElement MakeHeader(string title)
    {
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new TextBlock { Text = title, FontSize = 28,
            FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(TextMain) });
        header.Children.Add(new TextBlock { Text = T("让陪伴，刚刚好。", "Make yourself at home."),
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
