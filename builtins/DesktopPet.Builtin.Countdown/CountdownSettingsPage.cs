using System.Windows;
using System.Windows.Controls;
using DesktopPet.Core;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.Countdown;

/// <summary>
/// 倒计时设置页：自定义默认倒计时时长（分钟，1-180），持久化到 Storage（customMinutes）。
/// 保留 5/10 快捷槽位不变，本页只改「倒计时 N分」这个自定义槽位。
/// UI 统一走 SDK 的 <see cref="PluginUi"/> 深色控件工厂（v0.1.63）。
/// </summary>
public sealed class CountdownSettingsPage : ISettingsPage
{
    private readonly IPluginStorage _storage;
    private const string CustomKey = "customMinutes";
    private const int DefaultCustomMinutes = 15;

    public CountdownSettingsPage(IPluginStorage storage) => _storage = storage;

    public string Id => "com.lantern.countdown";
    public string Title => "com.lantern.countdown";

    public object BuildContent()
    {
        var current = _storage.Contains(CustomKey) ? _storage.Get<int>(CustomKey) : DefaultCustomMinutes;

        var stack = new StackPanel { Margin = new Thickness(8) };
        stack.Children.Add(new TextBlock
        {
            Text = "自定义倒计时时长（分钟）",
            FontSize = 12,
            Foreground = PluginUi.TextSub,
            Margin = new Thickness(0, 0, 0, 8),
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var minus = StepButton("−");
        var valueText = new TextBlock
        {
            Text = current.ToString(),
            Foreground = PluginUi.TextMain,
            Width = 40,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var plus = StepButton("+");

        int value = current;
        minus.Click += (_, _) => { value = Math.Max(1, value - 1); valueText.Text = value.ToString(); };
        plus.Click += (_, _) => { value = Math.Min(180, value + 1); valueText.Text = value.ToString(); };

        row.Children.Add(minus);
        row.Children.Add(valueText);
        row.Children.Add(plus);

        var hint = new TextBlock
        {
            Text = "5/10 分快捷槽位不变；这里改的是第三个「倒计时 N分」槽位",
            FontSize = 11,
            Foreground = PluginUi.TextSub,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };

        var saveBtn = PluginUi.Button("保存");
        saveBtn.HorizontalAlignment = HorizontalAlignment.Left;
        saveBtn.Click += (_, _) =>
        {
            _storage.Set(CustomKey, value);
            MessageBox.Show($"已保存：自定义倒计时 {value} 分钟", "倒计时设置", MessageBoxButton.OK, MessageBoxImage.Information);
        };

        stack.Children.Add(row);
        stack.Children.Add(hint);
        stack.Children.Add(saveBtn);
        return stack;
    }

    /// <summary>步进按钮：复用深色样式，但压小尺寸适配 +/- 行内布局。</summary>
    private static Button StepButton(string glyph)
    {
        var b = PluginUi.Button(glyph);
        b.Width = 28;
        b.Height = 24;
        b.Padding = new Thickness(0);
        return b;
    }
}
