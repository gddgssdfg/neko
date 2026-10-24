using System.Windows;
using System.Windows.Controls;
using DesktopPet.Core;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.Weather;

/// <summary>
/// 天气插件的自定义设置页（v0.1.63：改用 PluginUi 统一深色风格）：城市名输入 + 保存（Storage 持久化）。
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

        var label = PluginUi.Text("天气城市（支持常用城市名，未知城市走地理编码）", sub: true);
        label.Margin = new Thickness(0, 0, 0, 4);

        var input = PluginUi.Input(_storage.Get<string>(WeatherPlugin.CityKey) ?? WeatherPlugin.DefaultCity);
        input.FontSize = 14;
        input.Margin = new Thickness(0, 0, 0, 8);

        var save = PluginUi.Button("保存城市");
        save.Padding = new Thickness(10, 4, 10, 4);
        save.Click += (_, _) =>
        {
            var city = string.IsNullOrWhiteSpace(input.Text) ? WeatherPlugin.DefaultCity : input.Text.Trim();
            _storage.Set(WeatherPlugin.CityKey, city);
            PluginUi.ShowStatus(stack, $"天气城市已设为「{city}」");
        };

        stack.Children.Add(label);
        stack.Children.Add(input);
        stack.Children.Add(save);
        return stack;
    }
}
