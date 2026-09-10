using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopPet.Core;

namespace DesktopPet.Builtin.Countdown;

/// <summary>
/// 倒计时设置页：自定义默认倒计时时长（分钟，1-180），持久化到 Storage（customMinutes）。
/// 保留 5/10 快捷槽位不变，本页只改「倒计时 N分」这个自定义槽位。
/// </summary>
public sealed class CountdownSettingsPage : ISettingsPage
{
    private readonly IPluginStorage _storage;
    private const string CustomKey = "customMinutes";
    private const int DefaultCustomMinutes = 15;

    private static readonly Brush TextMain = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB));
    private static readonly Brush TextSub = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));

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
            Foreground = TextSub,
            Margin = new Thickness(0, 0, 0, 8),
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var minus = new Button { Content = "−", Width = 28, Height = 24, Padding = new Thickness(0) };
        var valueText = new TextBlock
        {
            Text = current.ToString(),
            Foreground = TextMain,
            Width = 40,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var plus = new Button { Content = "+", Width = 28, Height = 24, Padding = new Thickness(0) };

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
            Foreground = TextSub,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };

        var saveBtn = new Button { Content = "保存", Padding = new Thickness(12, 4, 12, 4) };
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
}
