using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace DesktopPet.Sdk;

/// <summary>A real toggle control: tab focus, Space key, and UI Automation TogglePattern.</summary>
public class NekoSwitch : ToggleButton
{
    private readonly Brush _off;
    private readonly Brush _on;
    private readonly Ellipse _knob;
    private bool _setting;
    public event Action<bool>? Toggled;
    public bool IsOn { get => IsChecked == true; set { _setting = true; try { IsChecked = value; } finally { _setting = false; } } }

    public NekoSwitch(Color off, Color on)
    {
        _off = NekoPalette.Brush(off); _on = NekoPalette.Brush(on);
        Width = 44; Height = 26; MinHeight = 0; Padding = new Thickness(3);
        BorderThickness = new Thickness(1); BorderBrush = Brushes.Transparent;
        Cursor = System.Windows.Input.Cursors.Hand;
        Template = (ControlTemplate)XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ToggleButton">
 <Border x:Name="plate" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
   BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="13" Padding="{TemplateBinding Padding}">
  <ContentPresenter HorizontalAlignment="Stretch" VerticalAlignment="Center"/>
 </Border><ControlTemplate.Triggers>
 <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="plate" Property="BorderBrush" Value="{DynamicResource Neko.Text}"/></Trigger>
 <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger>
 </ControlTemplate.Triggers>
</ControlTemplate>
""");
        _knob = new Ellipse { Width = 18, Height = 18, Fill = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = new TranslateTransform() };
        Content = new Grid { Children = { _knob } };
        Background = _off;
        Checked += (_, _) => Changed(); Unchecked += (_, _) => Changed();
    }

    private void Changed() { UpdateVisual(); if (!_setting) Toggled?.Invoke(IsOn); }
    private void UpdateVisual()
    {
        Background = IsOn ? _on : _off;
        ((TranslateTransform)_knob.RenderTransform).BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(IsOn ? 18 : 0, TimeSpan.FromMilliseconds(120)));
    }
}
