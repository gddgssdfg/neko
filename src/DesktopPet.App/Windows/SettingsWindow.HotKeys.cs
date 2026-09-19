// v0.1.84：从 SettingsWindow.cs 按功能区域拆出（审计 P2-11）——热键编辑卡片
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
    private Border BuildHotKeySet(string title, HotKeyConfig current, string id)
    {
        // v0.1.33：附加热键直接调 _pet.SetExtraHotKey(id, ...)，热更新不用重启
        return BuildHotKeyCard(title, current, (c, a, s, k) => _pet.SetExtraHotKey(id, c, a, s, k));
    }

    private Border BuildHotKeyCard(string title, HotKeyConfig current, Action<bool, bool, bool, string> onSave)
    {
        // 三 Toggle + TextBox + 保存
        var ctrlSw = new ToggleSwitch(ToggleOff) { IsOn = current.Ctrl };   // v0.1.50：主题色
        var altSw = new ToggleSwitch(ToggleOff) { IsOn = current.Alt };
        var shiftSw = new ToggleSwitch(ToggleOff) { IsOn = current.Shift };
        var keyBox = new TextBox
        {
            Text = current.Key,
            MaxLength = 1,
            Width = 36, FontSize = 14,
            Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x22, 0x2A)),
            Foreground = new SolidColorBrush(TextMain),
            BorderBrush = new SolidColorBrush(BorderC),
            CaretBrush = new SolidColorBrush(Accent),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),   // v0.1.38：6→10
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(WrapToggleWithLabel("Ctrl", ctrlSw));
        sp.Children.Add(WrapToggleWithLabel("Alt", altSw, 12, 0, 0, 0));     // v0.1.38：6→12
        sp.Children.Add(WrapToggleWithLabel("Shift", shiftSw, 12, 0, 0, 0));
        sp.Children.Add(keyBox);
        var saveBtn = MakeStepperButton("✓", () =>
        {
            var k = string.IsNullOrWhiteSpace(keyBox.Text) ? current.Key : keyBox.Text[..1].ToUpperInvariant();
            onSave(ctrlSw.IsOn, altSw.IsOn, shiftSw.IsOn, k);
        });
        saveBtn.Width = 36; saveBtn.Margin = new Thickness(10, 0, 0, 0);
        sp.Children.Add(saveBtn);
        return BuildCard(title, "输入一个字母主键，保存后全局立即生效", sp);
    }

    private FrameworkElement WrapToggleWithLabel(string label, ToggleSwitch sw, double l = 0, double t = 0, double r = 0, double b = 0)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(l, t, r, b), VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(sw);
        sp.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = new SolidColorBrush(TextSub), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });  // v0.1.38：4→6
        return sp;
    }
}
