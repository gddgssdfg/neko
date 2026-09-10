using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopPet.Core;

namespace DesktopPet.Builtin.HourlyChime;

/// <summary>
/// 整点报时设置页：开关 + 提醒时段（开始/结束小时 0-23），持久化到 Storage。
/// 开关通过回调同步到插件实例（_enabled），并触发计时器启停。
/// </summary>
public sealed class HourlyChimeSettingsPage : ISettingsPage
{
    private readonly IPluginStorage _storage;
    private readonly Func<bool> _getEnabled;
    private readonly Action<bool> _setEnabled;

    private const string EnabledKey = "enabled";
    private const string StartHourKey = "startHour";
    private const string EndHourKey = "endHour";
    private const int DefaultStartHour = 9;
    private const int DefaultEndHour = 18;

    private static readonly Brush TextMain = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB));
    private static readonly Brush TextSub = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));

    public HourlyChimeSettingsPage(IPluginStorage storage, Func<bool> getEnabled, Action<bool> setEnabled)
    {
        _storage = storage;
        _getEnabled = getEnabled;
        _setEnabled = setEnabled;
    }

    public string Id => "com.lantern.hourly";
    public string Title => "com.lantern.hourly";

    public object BuildContent()
    {
        var enabled = _getEnabled();
        var start = _storage.Contains(StartHourKey) ? _storage.Get<int>(StartHourKey) : DefaultStartHour;
        var end = _storage.Contains(EndHourKey) ? _storage.Get<int>(EndHourKey) : DefaultEndHour;
        start = Math.Clamp(start, 0, 23);
        end = Math.Clamp(end, 0, 23);

        var stack = new StackPanel { Margin = new Thickness(8) };

        var toggle = new CheckBox
        {
            Content = $"整点报时已{(_getEnabled() ? "开启" : "关闭")}",
            IsChecked = enabled,
            Foreground = TextMain,
            Margin = new Thickness(0, 0, 0, 12),
        };

        var startRow = MakeHourRow("开始", start, (v) => start = v);
        var endRow = MakeHourRow("结束", end, (v) => end = v);

        var hint = new TextBlock
        {
            Text = "仅在该时段内播报（开始 > 结束视为跨天，如 22-6 点）",
            FontSize = 11,
            Foreground = TextSub,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };

        var saveBtn = new Button { Content = "保存", Padding = new Thickness(12, 4, 12, 4) };
        saveBtn.Click += (_, _) =>
        {
            var newEnabled = toggle.IsChecked == true;
            _storage.Set(EnabledKey, newEnabled);
            _storage.Set(StartHourKey, start);
            _storage.Set(EndHourKey, end);
            _setEnabled(newEnabled);
            toggle.Content = $"整点报时已{(newEnabled ? "开启" : "关闭")}";
            MessageBox.Show("已保存", "整点报时设置", MessageBoxButton.OK, MessageBoxImage.Information);
        };

        stack.Children.Add(toggle);
        stack.Children.Add(startRow);
        stack.Children.Add(endRow);
        stack.Children.Add(hint);
        stack.Children.Add(saveBtn);
        return stack;
    }

    private FrameworkElement MakeHourRow(string caption, int value, Action<int> onChange)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        row.Children.Add(new TextBlock { Text = caption, Foreground = TextSub, Width = 40, VerticalAlignment = VerticalAlignment.Center });

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

        minus.Click += (_, _) => { value = Math.Max(0, value - 1); valueText.Text = value.ToString("D2"); onChange(value); };
        plus.Click += (_, _) => { value = Math.Min(23, value + 1); valueText.Text = value.ToString("D2"); onChange(value); };

        row.Children.Add(minus);
        row.Children.Add(valueText);
        row.Children.Add(plus);
        return row;
    }
}
