using System.Net.Http;
using System.Text.Json;
using DesktopPet.Core;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.Weather;

/// <summary>
/// 天气插件：右键菜单「天气」槽位，调 Open-Meteo 免费 API 查询当前天气。
/// 城市可配置（Storage 存 city，设置页 WeatherSettingsPage 修改）；
/// 内置常用城市坐标表，未命中用 Open-Meteo 地理编码 API 解析。
/// </summary>
public sealed class WeatherPlugin : PluginBase
{
    public const string CityKey = "city";
    public const string DefaultCity = "佛山";

    // 常用城市坐标（免地理编码请求；未命中走 API）
    private static readonly Dictionary<string, (double Lat, double Lon)> KnownCities = new()
    {
        ["佛山"] = (23.02, 113.11),
        ["广州"] = (23.13, 113.26),
        ["深圳"] = (22.54, 114.06),
        ["东莞"] = (23.02, 113.75),
        ["珠海"] = (22.27, 113.57),
        ["北京"] = (39.90, 116.40),
        ["上海"] = (31.23, 121.47),
        ["杭州"] = (30.27, 120.16),
        ["南京"] = (32.06, 118.80),
        ["成都"] = (30.57, 104.07),
        ["重庆"] = (29.56, 106.55),
        ["武汉"] = (30.59, 114.31),
        ["长沙"] = (28.23, 112.94),
        ["西安"] = (34.34, 108.94),
    };

    /// <summary>
    /// v0.1.82：共享 HttpClient。原先三处各自 `using var http = new HttpClient{...}` ——
    /// 每次调用都新建+销毁 → 频繁查询会耗尽本地端口（TIME_WAIT 堆积）。
    /// <para>⚠️ 但**不能**写成 `static readonly`：插件跑在**可回收 ALC** 里，
    /// SocketsHttpHandler 的连接池定时器会从进程级根钉住类型 → ALC 收不掉（Bazi 就是这么泄漏 8.5MB 的）。
    /// 折中：实例级延迟创建 + <see cref="OnUnload"/> 里 Dispose —— 既复用连接池，又能让 ALC 回收。</para>
    /// </summary>
    private HttpClient? _http;
    private HttpClient Http => _http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

    public override void OnUnload()
    {
        // v0.1.82：释放共享 HttpClient（见字段注释），否则 ALC 无法回收
        try { _http?.Dispose(); } catch { }
        _http = null;
    }

    public override string Id => "com.lantern.weather";
    public override string Name => "天气";

    public override PluginPermissions RequiredPermissions =>
        PluginPermissions.PetAnimate | PluginPermissions.UIRegister | PluginPermissions.Storage | PluginPermissions.Network;

    public override void OnLoad(IPluginContext ctx)
    {
        base.OnLoad(ctx);

        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "weather-now",
            Label = "天气",
            Icon = "info",
            Order = 6,
            OnInvoke = () => ShowWeather(),
        });
        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "weather-forecast",
            Label = "3日预报",
            Icon = "clock",
            Order = 12,
            OnInvoke = () => ShowForecast(),
        });

        // 设置页：改城市（dogfooding ISettingsPage + Storage 组合）
        UI.RegisterSettingsPage(new WeatherSettingsPage(Capability.Storage));
    }

    private async void ShowWeather()
    {
        try
        {
            var city = Capability.Storage.Get<string>(CityKey) ?? DefaultCity;
            var (lat, lon) = await ResolveCityAsync(city);

            var http = Http;
            var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current=temperature_2m,weather_code";
            var json = await http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var cur = doc.RootElement.GetProperty("current");
            var temp = cur.GetProperty("temperature_2m").GetDouble();
            var code = cur.GetProperty("weather_code").GetInt32();
            Pet.Say($"{city} {temp:F0}°C {Describe(code)}", 4000);
        }
        catch
        {
            Pet.Say("天气获取失败，检查网络", 3000);
        }
    }

    /// <summary>城市名 → 坐标：先查内置表，未命中走 Open-Meteo 地理编码 API，失败回退佛山
    /// <para>v0.1.82：由 static 改为实例方法 —— 共享 HttpClient 是实例字段（见 <c>_http</c> 注释）。</para></summary>
    private async Task<(double Lat, double Lon)> ResolveCityAsync(string city)
    {
        if (KnownCities.TryGetValue(city.Trim(), out var known)) return known;
        try
        {
            var http = Http;
            var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city)}&count=1&language=zh";
            var json = await http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
            {
                var r = results[0];
                return (r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble());
            }
        }
        catch { }
        return KnownCities[DefaultCity];
    }

    /// <summary>未来 2 天预报（Open-Meteo daily API，forecast_days=3）</summary>
    private async void ShowForecast()
    {
        try
        {
            var city = Capability.Storage.Get<string>(CityKey) ?? DefaultCity;
            var (lat, lon) = await ResolveCityAsync(city);

            var http = Http;
            var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}" +
                      $"&daily=temperature_2m_max,temperature_2m_min,weather_code&forecast_days=3&timezone=auto";
            var json = await http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var daily = doc.RootElement.GetProperty("daily");
            var maxs = daily.GetProperty("temperature_2m_max").EnumerateArray().Select(x => x.GetDouble()).ToArray();
            var mins = daily.GetProperty("temperature_2m_min").EnumerateArray().Select(x => x.GetDouble()).ToArray();
            var codes = daily.GetProperty("weather_code").EnumerateArray().Select(x => x.GetInt32()).ToArray();

            string[] names = { "今天", "明天" };
            var msg = $"{city} ";
            for (int i = 0; i < Math.Min(2, names.Length); i++)
                msg += $"{names[i]} {maxs[i]:F0}/{mins[i]:F0}°C {ShortDesc(codes[i])}  ";
            Pet.Say(msg, 6000);
        }
        catch
        {
            Pet.Say("天气预报获取失败，检查网络", 3000);
        }
    }

    /// <summary>简短天气描述（预报用，比 Describe 更短）</summary>
    private static string ShortDesc(int code) => code switch
    {
        0 => "晴",
        1 or 2 => "少云",
        3 => "多云",
        45 or 48 => "雾",
        >= 51 and <= 67 => "雨",
        >= 71 and <= 77 => "雪",
        >= 80 and <= 82 => "阵雨",
        >= 95 => "雷暴",
        _ => "未知",
    };

    /// <summary>WMO 天气代码 → 中文描述</summary>
    private static string Describe(int code) => code switch
    {
        0 => "☀️ 晴",
        1 or 2 => "🌤 少云",
        3 => "☁️ 多云",
        45 or 48 => "🌫 雾",
        >= 51 and <= 57 => "🌦 毛毛雨",
        >= 61 and <= 67 => "🌧 雨",
        >= 71 and <= 77 => "❄️ 雪",
        >= 80 and <= 82 => "🌧 阵雨",
        >= 85 and <= 86 => "🌨 阵雪",
        >= 95 => "⛈ 雷暴",
        _ => "🌡 未知",
    };
}
