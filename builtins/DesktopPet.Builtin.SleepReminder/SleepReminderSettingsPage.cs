using System.Windows;
using System.Windows.Controls;
using DesktopPet.Core;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.SleepReminder;

/// <summary>
/// 睡眠提醒设置页：时/分步进（0-23 / 0-59），持久化到 Storage（remindHour / remindMinute）。
/// v0.1.63：改用 PluginUi 统一深色风格；修复"步进后顶部提醒时间 label 不实时刷新"的问题。
/// </summary>
public sealed class SleepReminderSettingsPage : ISettingsPage
{
    private readonly IPluginStorage _storage;
    private const string HourKey = "remindHour";
    private const string MinuteKey = "remindMinute";
    private const int DefaultHour = 23;
    private const int DefaultMinute = 0;

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

        var label = PluginUi.Text($"提醒时间：{hour:D2}:{minute:D2}");
        label.FontSize = 13;
        label.Margin = new Thickness(0, 0, 0, 8);

        // v0.1.63 fix：步进时立即刷新顶部 label（原来只有保存时才更新）
        void RefreshLabel() => label.Text = $"提醒时间：{hour:D2}:{minute:D2}";

        var hourRow = MakeRow("时", hour, 0, 23, v => { hour = v; RefreshLabel(); });
        var minuteRow = MakeRow("分", minute, 0, 59, v => { minute = v; RefreshLabel(); });

        var saveBtn = PluginUi.Button("保存");
        saveBtn.Click += (_, _) =>
        {
            _storage.Set(HourKey, hour);
            _storage.Set(MinuteKey, minute);
            RefreshLabel();
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
        var captionText = PluginUi.Text(caption, sub: true);
        captionText.Width = 32;
        row.Children.Add(captionText);

        var minus = SubtleButton("−");
        var valueText = PluginUi.Text(value.ToString("D2"));
        valueText.Width = 32;
        valueText.TextAlignment = TextAlignment.Center;
        var plus = SubtleButton("+");

        minus.Click += (_, _) => { value = Math.Max(min, value - 1); valueText.Text = value.ToString("D2"); onChange(value); };
        plus.Click += (_, _) => { value = Math.Min(max, value + 1); valueText.Text = value.ToString("D2"); onChange(value); };

        row.Children.Add(minus);
        row.Children.Add(valueText);
        row.Children.Add(plus);
        return row;
    }

    private static Button SubtleButton(string text)
    {
        var b = PluginUi.Button(text);
        b.Width = 28;
        b.Height = 24;
        b.Padding = new Thickness(0);
        return b;
    }
}
