using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopPet.Core;

namespace DesktopPet.Builtin.Weather;

/// <summary>
/// 天气插件的自定义设置页：城市名输入 + 保存（Storage 持久化）。
/// Title 与 WeatherPlugin.Id 匹配，宿主设置窗口「插件」tab 嵌入。
/// </summary>
public sealed class WeatherSettingsPage : ISettingsPage
{
    private readonly IPluginStorage _storage;

    public WeatherSettingsPage(IPluginStorage storage) => _storage = storage;

    public string Id => "com.lantern.weather";
    public string Title => "com.lantern.weather";

    public object BuildContent()
    {
        var stack = new StackPanel { Margin = new Thickness(8) };

        var label = new TextBlock
        {
            Text = "天气城市（支持常用城市名，未知城市走地理编码）",
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 4),
        };
        var input = new TextBox
        {
            Text = _storage.Get<string>(WeatherPlugin.CityKey) ?? WeatherPlugin.DefaultCity,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 8),
        };
        var save = new Button { Content = "保存城市", Padding = new Thickness(10, 4, 10, 4) };
        save.Click += (_, _) =>
        {
            var city = string.IsNullOrWhiteSpace(input.Text) ? WeatherPlugin.DefaultCity : input.Text.Trim();
            _storage.Set(WeatherPlugin.CityKey, city);
            MessageBox.Show($"天气城市已设为「{city}」", "天气设置", MessageBoxButton.OK, MessageBoxImage.Information);
        };

        stack.Children.Add(label);
        stack.Children.Add(input);
        stack.Children.Add(save);
        return stack;
    }
}
