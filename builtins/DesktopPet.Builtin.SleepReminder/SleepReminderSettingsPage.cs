using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopPet.Core;

namespace DesktopPet.Builtin.SleepReminder;

/// <summary>
/// 睡眠提醒设置页：时/分步进（0-23 / 0-59），持久化到 Storage（remindHour / remindMinute）。
/// 深色配色，与宿主设置页（#1E262F 背景 / #2B3647 卡片）一致。
/// </summary>
public sealed class SleepReminderSettingsPage : ISettingsPage
{
    private readonly IPluginStorage _storage;
    private const string HourKey = "remindHour";
    private const string MinuteKey = "remindMinute";
    private const int DefaultHour = 23;
    private const int DefaultMinute = 0;

    private static readonly Brush TextMain = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB));
    private static readonly Brush TextSub = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));

    public SleepReminderSettingsPage(IPluginStorage storage) => _storage = storage;

    public string Id => "com.lantern.sleep";
    public string Title => "com.lantern.sleep";

    public object BuildContent()
    {
        var hour = _storage.Contains(HourKey) ? _storage.Get<int>(HourKey) : DefaultHour;
        var minute = _storage.Contains(MinuteKey) ? _storage.Get<int>(MinuteKey) : DefaultMinute;
        hour = Math.Clamp(hour, 0, 23);
        minute = Math.Clamp(minute, 0, 59);

        var stack = new StackPanel { Margin = new Thickness(8) };

        var label = new TextBlock
        {
            Text = $"提醒时间：{hour:D2}:{minute:D2}",
            FontSize = 13,
            Foreground = TextMain,
            Margin = new Thickness(0, 0, 0, 8),
        };

        var hourRow = MakeRow("时", hour, 0, 23, (v) => hour = v);
        var minuteRow = MakeRow("分", minute, 0, 59, (v) => minute = v);

        var saveBtn = new Button { Content = "保存", Padding = new Thickness(12, 4, 12, 4) };
        saveBtn.Click += (_, _) =>
        {
            _storage.Set(HourKey, hour);
            _storage.Set(MinuteKey, minute);
            label.Text = $"提醒时间：{hour:D2}:{minute:D2}";
            MessageBox.Show("已保存，下次启动径向菜单文案会同步", "睡眠提醒设置", MessageBoxButton.OK, MessageBoxImage.Information);
        };

        stack.Children.Add(label);
        stack.Children.Add(hourRow);
        stack.Children.Add(minuteRow);
        stack.Children.Add(saveBtn);
        return stack;
    }

    private FrameworkElement MakeRow(string caption, int value, int min, int max, Action<int> onChange)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        row.Children.Add(new TextBlock { Text = caption, Foreground = TextSub, Width = 32, VerticalAlignment = VerticalAlignment.Center });

        var minus = new Button { Content = "−", Width = 28, Height = 24, Padding = new Thickness(0) };
        var valueText = new TextBlock
        {
            Text = value.ToString("D2"),
            Foreground = TextMain,
            Width = 32,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var plus = new Button { Content = "+", Width = 28, Height = 24, Padding = new Thickness(0) };

        minus.Click += (_, _) => { value = Math.Max(min, value - 1); valueText.Text = value.ToString("D2"); onChange(value); };
        plus.Click += (_, _) => { value = Math.Min(max, value + 1); valueText.Text = value.ToString("D2"); onChange(value); };

        row.Children.Add(minus);
        row.Children.Add(valueText);
        row.Children.Add(plus);
        return row;
    }
}
