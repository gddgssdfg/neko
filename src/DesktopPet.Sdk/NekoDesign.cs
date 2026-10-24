using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace DesktopPet.Sdk;

/// <summary>Shared host/plugin design tokens and accessible WPF controls. No dependency on the host.</summary>
public sealed record NekoPalette(Color Background, Color Surface, Color Raised, Color Line,
    Color Text, Color Muted, Color Accent, Color Selected)
{
    public static NekoPalette Dark { get; } = new(C("#211F1D"), C("#2B2825"), C("#35312D"), C("#51473E"),
        C("#FFF4E6"), C("#C3B4A5"), C("#B45B2E"), C("#493326"));
    public static NekoPalette Light { get; } = new(C("#F7F2E9"), C("#FFFCF7"), C("#EEE5D8"), C("#D8C9B7"),
        C("#34271F"), C("#786455"), C("#A45128"), C("#F2DDC5"));
    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Color, SolidColorBrush> Brushes = new();
    public static SolidColorBrush Brush(Color color) => Brushes.GetOrAdd(color, c => { var b = new SolidColorBrush(c); b.Freeze(); return b; });
}

public static class NekoDesign
{
    private static readonly DependencyProperty PaletteProperty = DependencyProperty.RegisterAttached(
        "Palette", typeof(NekoPalette), typeof(NekoDesign));
    public static bool LightTheme { get; set; }
    public static NekoPalette Current => LightTheme ? NekoPalette.Light : NekoPalette.Dark;

    /// <summary>Install per-window resources; templates remain shared by all settings and tools.</summary>
    public static void Apply(Window window, bool? light = null)
    {
        var p = (light ?? LightTheme) ? NekoPalette.Light : NekoPalette.Dark;
        if (window.GetValue(PaletteProperty) is NekoPalette previous && previous != p)
            Recolor(window, previous, p);
        window.SetValue(PaletteProperty, p);
        if (!window.Resources.Contains("Neko.DesignInstalled"))
        {
            window.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(Templates));
            window.Resources["Neko.DesignInstalled"] = true;
        }
        foreach (var pair in new[] { ("Neko.Bg", p.Background), ("Neko.Surface", p.Surface),
            ("Neko.Raised", p.Raised), ("Neko.Line", p.Line), ("Neko.Text", p.Text),
            ("Neko.Muted", p.Muted), ("Neko.Accent", p.Accent), ("Neko.Selected", p.Selected) })
            window.Resources[pair.Item1] = NekoPalette.Brush(pair.Item2);
        window.Background = NekoPalette.Brush(p.Background);
        window.Foreground = NekoPalette.Brush(p.Text);
        window.FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        window.FontSize = 13;
        window.UseLayoutRounding = true;
        window.SnapsToDevicePixels = true;
    }

    public static void RefreshWindows(bool light)
    {
        LightTheme = light;
        if (Application.Current is not { } app) return;
        foreach (Window window in app.Windows)
            if (window.GetValue(PaletteProperty) is NekoPalette) Apply(window, light);
    }

    // Only shared token brushes are recolored; plugin-specific artwork keeps its own colors.
    private static void Recolor(DependencyObject element, NekoPalette from, NekoPalette to)
    {
        var pairs = new[] { (from.Background, to.Background), (from.Surface, to.Surface),
            (from.Raised, to.Raised), (from.Line, to.Line), (from.Text, to.Text),
            (from.Muted, to.Muted), (from.Accent, to.Accent), (from.Selected, to.Selected) };
        foreach (var property in new[] { Control.BackgroundProperty, Control.ForegroundProperty, Control.BorderBrushProperty,
            TextBlock.ForegroundProperty, Panel.BackgroundProperty, Border.BackgroundProperty, Border.BorderBrushProperty,
            System.Windows.Controls.Primitives.TextBoxBase.CaretBrushProperty })
        {
            if (element.GetValue(property) is not Brush brush) continue;
            foreach (var pair in pairs)
                if (ReferenceEquals(brush, NekoPalette.Brush(pair.Item1)))
                { element.SetCurrentValue(property, NekoPalette.Brush(pair.Item2)); break; }
        }
        foreach (var child in LogicalTreeHelper.GetChildren(element))
            if (child is DependencyObject dependency) Recolor(dependency, from, to);
    }

    public static FrameworkElement ToolHeader(string title, string subtitle)
    {
        var p = Current;
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeights.Bold,
            Foreground = NekoPalette.Brush(p.Text) });
        stack.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Foreground = NekoPalette.Brush(p.Muted), Margin = new Thickness(0, 6, 0, 0) });
        return stack;
    }

    private const string Templates = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
 <Style TargetType="Button">
  <Setter Property="Foreground" Value="{DynamicResource Neko.Text}"/>
  <Setter Property="Background" Value="{DynamicResource Neko.Raised}"/>
  <Setter Property="BorderBrush" Value="{DynamicResource Neko.Line}"/>
  <Setter Property="BorderThickness" Value="1"/><Setter Property="Padding" Value="14,8"/>
  <Setter Property="MinHeight" Value="34"/><Setter Property="Cursor" Value="Hand"/>
  <Setter Property="HorizontalContentAlignment" Value="Center"/><Setter Property="VerticalContentAlignment" Value="Center"/>
  <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
   <Border x:Name="box" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
    BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="7" Padding="{TemplateBinding Padding}">
    <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}" RecognizesAccessKey="True"/>
   </Border><ControlTemplate.Triggers>
    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="box" Property="BorderBrush" Value="{DynamicResource Neko.Accent}"/></Trigger>
    <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="box" Property="BorderBrush" Value="{DynamicResource Neko.Accent}"/><Setter TargetName="box" Property="BorderThickness" Value="2"/></Trigger>
    <Trigger Property="IsPressed" Value="True"><Setter TargetName="box" Property="Opacity" Value="0.7"/></Trigger>
    <Trigger Property="IsEnabled" Value="False"><Setter TargetName="box" Property="Opacity" Value="0.4"/></Trigger>
   </ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType="TextBox">
  <Setter Property="Foreground" Value="{DynamicResource Neko.Text}"/><Setter Property="CaretBrush" Value="{DynamicResource Neko.Accent}"/>
  <Setter Property="Background" Value="{DynamicResource Neko.Surface}"/><Setter Property="BorderBrush" Value="{DynamicResource Neko.Line}"/>
  <Setter Property="BorderThickness" Value="1"/><Setter Property="Padding" Value="10,8"/>
  <Setter Property="VerticalContentAlignment" Value="Center"/>
  <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TextBox">
   <Border x:Name="box" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
    BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="7" Padding="{TemplateBinding Padding}">
    <ScrollViewer x:Name="PART_ContentHost"/>
   </Border><ControlTemplate.Triggers>
    <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="box" Property="BorderBrush" Value="{DynamicResource Neko.Accent}"/></Trigger>
    <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
   </ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType="CheckBox">
  <Setter Property="Foreground" Value="{DynamicResource Neko.Text}"/><Setter Property="Margin" Value="0,6"/>
  <Setter Property="VerticalContentAlignment" Value="Center"/>
 </Style>
 <Style TargetType="RadioButton"><Setter Property="Foreground" Value="{DynamicResource Neko.Text}"/><Setter Property="Margin" Value="0,6"/></Style>
 <Style TargetType="ComboBox">
  <Setter Property="Foreground" Value="{DynamicResource Neko.Text}"/><Setter Property="Background" Value="{DynamicResource Neko.Surface}"/>
  <Setter Property="BorderBrush" Value="{DynamicResource Neko.Line}"/><Setter Property="MinHeight" Value="34"/><Setter Property="Padding" Value="10,6"/>
  <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBox">
   <Grid><Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="7"/>
    <ToggleButton Focusable="False" IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
     <ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border Background="Transparent" Padding="10,6"><DockPanel LastChildFill="False">
      <TextBlock Text="⌄" DockPanel.Dock="Right" Foreground="{DynamicResource Neko.Muted}" Margin="14,0,0,0"/>
     </DockPanel></Border></ControlTemplate></ToggleButton.Template>
    </ToggleButton>
    <ContentPresenter x:Name="selection" Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"
      Margin="10,5,32,5" VerticalAlignment="Center" IsHitTestVisible="False"/>
    <TextBox x:Name="PART_EditableTextBox" Visibility="Collapsed" Margin="2,2,30,2" Background="{TemplateBinding Background}" Foreground="{TemplateBinding Foreground}"/>
    <Popup x:Name="PART_Popup" IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom" AllowsTransparency="True" Focusable="False" PopupAnimation="Fade">
     <Border Background="{DynamicResource Neko.Surface}" BorderBrush="{DynamicResource Neko.Line}" BorderThickness="1" CornerRadius="7"
       MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}" MaxHeight="{TemplateBinding MaxDropDownHeight}" Padding="4">
      <ScrollViewer><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/></ScrollViewer>
     </Border>
    </Popup>
   </Grid><ControlTemplate.Triggers>
    <Trigger Property="IsEditable" Value="True"><Setter TargetName="PART_EditableTextBox" Property="Visibility" Value="Visible"/><Setter TargetName="selection" Property="Visibility" Value="Collapsed"/></Trigger>
    <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
   </ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType="ComboBoxItem"><Setter Property="Foreground" Value="{DynamicResource Neko.Text}"/><Setter Property="Padding" Value="10,8"/>
  <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBoxItem"><Border x:Name="item" Padding="{TemplateBinding Padding}" Background="Transparent" CornerRadius="4"><ContentPresenter/></Border>
   <ControlTemplate.Triggers><Trigger Property="IsHighlighted" Value="True"><Setter TargetName="item" Property="Background" Value="{DynamicResource Neko.Selected}"/></Trigger></ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType="Expander"><Setter Property="Foreground" Value="{DynamicResource Neko.Text}"/>
  <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Expander"><StackPanel>
   <ToggleButton IsChecked="{Binding IsExpanded, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}" Content="{TemplateBinding Header}" HorizontalContentAlignment="Left">
    <ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border x:Name="header" Background="Transparent" Padding="0,8"><DockPanel>
     <TextBlock Text="⌄" Foreground="{DynamicResource Neko.Muted}" Margin="0,0,10,0"/>
     <ContentPresenter Content="{TemplateBinding Content}"/>
    </DockPanel></Border><ControlTemplate.Triggers><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="header" Property="Background" Value="{DynamicResource Neko.Selected}"/></Trigger></ControlTemplate.Triggers></ControlTemplate></ToggleButton.Template>
   </ToggleButton>
   <ContentPresenter x:Name="details" Visibility="Collapsed" Content="{TemplateBinding Content}"/>
  </StackPanel><ControlTemplate.Triggers><Trigger Property="IsExpanded" Value="True"><Setter TargetName="details" Property="Visibility" Value="Visible"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType="ListBox"><Setter Property="Foreground" Value="{DynamicResource Neko.Text}"/><Setter Property="Background" Value="{DynamicResource Neko.Surface}"/><Setter Property="BorderThickness" Value="0"/><Setter Property="ScrollViewer.HorizontalScrollBarVisibility" Value="Disabled"/></Style>
 <Style TargetType="ScrollBar">
  <Setter Property="Width" Value="10"/><Setter Property="Background" Value="Transparent"/>
  <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ScrollBar">
   <Track x:Name="PART_Track" Orientation="{TemplateBinding Orientation}" IsDirectionReversed="True">
    <Track.DecreaseRepeatButton><RepeatButton x:Name="less" Command="ScrollBar.PageUpCommand" Opacity="0" Focusable="False"/></Track.DecreaseRepeatButton>
    <Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType="Thumb">
     <Border x:Name="bar" Background="{DynamicResource Neko.Line}" CornerRadius="4" Margin="2,0"/>
     <ControlTemplate.Triggers>
      <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="bar" Property="Background" Value="{DynamicResource Neko.Muted}"/></Trigger>
      <Trigger Property="IsDragging" Value="True"><Setter TargetName="bar" Property="Background" Value="{DynamicResource Neko.Accent}"/></Trigger>
     </ControlTemplate.Triggers>
    </ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
    <Track.IncreaseRepeatButton><RepeatButton x:Name="more" Command="ScrollBar.PageDownCommand" Opacity="0" Focusable="False"/></Track.IncreaseRepeatButton>
   </Track><ControlTemplate.Triggers>
    <Trigger Property="Orientation" Value="Horizontal"><Setter TargetName="PART_Track" Property="IsDirectionReversed" Value="False"/>
     <Setter TargetName="less" Property="Command" Value="ScrollBar.PageLeftCommand"/><Setter TargetName="more" Property="Command" Value="ScrollBar.PageRightCommand"/>
    </Trigger>
   </ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
  <Style.Triggers><Trigger Property="Orientation" Value="Horizontal"><Setter Property="Width" Value="Auto"/><Setter Property="Height" Value="10"/></Trigger></Style.Triggers>
 </Style>
 <Style TargetType="ListBoxItem">
  <Setter Property="Padding" Value="10,10"/><Setter Property="HorizontalContentAlignment" Value="Stretch"/>
  <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ListBoxItem">
   <Border x:Name="row" CornerRadius="6" Padding="{TemplateBinding Padding}" Background="Transparent"><ContentPresenter/></Border>
   <ControlTemplate.Triggers><Trigger Property="IsSelected" Value="True"><Setter TargetName="row" Property="Background" Value="{DynamicResource Neko.Selected}"/></Trigger>
    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="row" Property="Background" Value="{DynamicResource Neko.Raised}"/></Trigger>
   </ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
</ResourceDictionary>
""";
}
