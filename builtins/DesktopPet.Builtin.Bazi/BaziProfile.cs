using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopPet.Core;

namespace DesktopPet.Builtin.Bazi;

/// <summary>
/// 用户命理档案（P0 scope：生日 / 性别 / 历法 / 播报设置 / API Key）。
/// 以单个 JSON 字符串键 <c>profile</c> 经 <see cref="IPluginStorage"/> 持久化，
/// 落到 <c>data/plugins/com.lantern.bazi/</c>，不写插件目录、不写宿主目录（手册铁律 3）。
/// </summary>
public sealed class BaziProfile
{
    /// <summary>Storage 键名（整档存一条 JSON，避免多键碎片化）。</summary>
    public const string StorageKey = "profile";

    /// <summary>出生年（公历或农历，取决于 <see cref="IsLunar"/>）；未填为 0。</summary>
    public int Year { get; set; }

    /// <summary>出生月 1-12；未填为 0。</summary>
    public int Month { get; set; }

    /// <summary>出生日 1-31；未填为 0。</summary>
    public int Day { get; set; }

    /// <summary>出生「时」0-23；未填为 null（不计时柱）。</summary>
    public int? Hour { get; set; }

    /// <summary>出生「分」0-59；未填为 null。</summary>
    public int? Minute { get; set; }

    /// <summary>true = 农历录入，false = 公历录入（默认）。</summary>
    public bool IsLunar { get; set; }

    /// <summary>农历闰月（仅 <see cref="IsLunar"/> 为 true 时有意义）。</summary>
    public bool LeapMonth { get; set; }

    /// <summary>性别：0 = 女，1 = 男（与 lunar-csharp <c>GetYun(gender, sect)</c> 约定一致）。</summary>
    public int Gender { get; set; } = 1;

    /// <summary>MBTI 类型（可空；P4 阶段改为问卷生成）。</summary>
    public string? Mbti { get; set; }

    /// <summary>DeepSeek API Key（可空；P3 使用）。<b>落盘时经 DPAPI 加密</b>（见 <see cref="EncryptedApiKeyConverter"/>），内存中为明文。</summary>
    [JsonConverter(typeof(EncryptedApiKeyConverter))]
    public string? ApiKey { get; set; }

    /// <summary>是否开启每日定时播报。</summary>
    public bool DailyBroadcastEnabled { get; set; }

    /// <summary>播报时刻，格式 "HH:mm"（默认 08:00）。</summary>
    public string BroadcastTime { get; set; } = "08:00";

    /// <summary>是否已录入完整出生日期。</summary>
    [JsonIgnore]
    public bool HasBirth => Year > 0 && Month > 0 && Day > 0;

    /// <summary>是否已录入出生时间（时柱需要）。</summary>
    [JsonIgnore]
    public bool HasTime => HasBirth && Hour is not null;

    /// <summary>从 Storage 读取档案；缺失 / 损坏时返回默认档案，绝不抛异常。</summary>
    public static BaziProfile Load(IPluginStorage storage)
    {
        try
        {
            var json = storage.Get<string>(StorageKey);
            if (string.IsNullOrWhiteSpace(json)) return new BaziProfile();
            return JsonSerializer.Deserialize<BaziProfile>(json) ?? new BaziProfile();
        }
        catch
        {
            return new BaziProfile();
        }
    }

    /// <summary>把当前档案写回 Storage（写失败静默，不影响宿主）。</summary>
    public void Save(IPluginStorage storage)
    {
        try
        {
            storage.Set(StorageKey, JsonSerializer.Serialize(this));
        }
        catch
        {
            // 持久化失败不拖垮宿主（手册铁律 7）；P5 打磨阶段接入统一日志。
        }
    }
}
