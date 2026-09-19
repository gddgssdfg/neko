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
    private UIElement WrapPage(FrameworkElement content, string title)
    {
        var sp = new StackPanel { Margin = new Thickness(24, 0, 24, 24) };   // v0.1.38：内容区左右留白，不再贴边
        sp.Children.Add(MakeHeader(title));
        sp.Children.Add(content);
        var sv = new ScrollViewer
        {
            Content = sp,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(BgRoot),
        };
        // v0.1.82：WPF 默认滚动条是浅色带箭头的，在深色设置页里非常突兀 → 换成深色细条
        sv.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), DarkScrollBarStyle());
        return sv;
    }

    /// <summary>
    /// v0.1.82：深色细滚动条（10px、圆角滑块、无箭头按钮、悬停变亮）。
    /// <para>用 XAML 字符串 + XamlReader 构造，比在 C# 里手搓 ControlTemplate 短得多也好读。
    /// 模板假设**竖直方向**（设置页只出竖直条）；横向时直接隐藏，避免出现错位的条。</para>
    /// </summary>
    private static Style DarkScrollBarStyle() =>
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
                    <Border x:Name='bar' Background='#3A4457' CornerRadius='5' Margin='2,0'/>
                    <ControlTemplate.Triggers>
                      <Trigger Property='IsMouseOver' Value='True'>
                        <Setter TargetName='bar' Property='Background' Value='#55657D'/>
                      </Trigger>
                      <Trigger Property='IsDragging' Value='True'>
                        <Setter TargetName='bar' Property='Background' Value='#5B7FB0'/>
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

    private FrameworkElement MakeHeader(string title) => new TextBlock
    {
        Text = title,
        FontSize = 20,   // v0.1.38：标题更大更醒目
        FontWeight = FontWeights.Bold,
        Foreground = new SolidColorBrush(TextMain),
        Margin = new Thickness(0, 0, 0, 22),
    };

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
