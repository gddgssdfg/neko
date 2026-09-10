using DesktopPet.Core;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.WordCard;

/// <summary>
/// 单词打卡插件：每日一个英语单词（按日期确定性选），
/// 「✅ 打卡」记录连续天数（Storage 持久化，跨会话保留）。
/// 展示 Storage 的日期逻辑用法。
/// </summary>
public sealed class WordCardPlugin : PluginBase
{
    private const string LastDayKey = "lastCheckDay"; // 上次打卡日期 yyyyMMdd
    private const string StreakKey = "streak";        // 连续打卡天数

    private static readonly (string Word, string Cn)[] Words =
    {
        ("apple", "苹果"), ("banana", "香蕉"), ("cat", "猫"), ("dog", "狗"),
        ("book", "书"), ("water", "水"), ("happy", "开心的"), ("light", "光"),
        ("star", "星星"), ("dream", "梦想"), ("coffee", "咖啡"), ("music", "音乐"),
        ("friend", "朋友"), ("family", "家庭"), ("smile", "微笑"), ("courage", "勇气"),
        ("patience", "耐心"), ("freedom", "自由"), ("journey", "旅程"), ("ocean", "海洋"),
        ("mountain", "山"), ("rainbow", "彩虹"), ("sunrise", "日出"), ("sunset", "日落"),
        ("garden", "花园"), ("forest", "森林"), ("river", "河流"), ("wind", "风"),
        ("snow", "雪"), ("flower", "花"),
    };

    public override string Id => "com.lantern.wordcard";
    public override string Name => "单词打卡";

    public override PluginPermissions RequiredPermissions =>
        PluginPermissions.PetAnimate | PluginPermissions.UIRegister | PluginPermissions.Storage;

    public override void OnLoad(IPluginContext ctx)
    {
        base.OnLoad(ctx);

        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "word-show",
            Label = "今日单词",
            Icon = "book",
            Order = 10,
            OnInvoke = () => Pet.Say(WordOfToday(), 5000),
        });
        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "word-check",
            Label = "✅ 打卡",
            Icon = "heart",
            Order = 11,
            OnInvoke = CheckIn,
        });
    }

    /// <summary>按日期确定性选词（同天同词、跨天刷新），返回 "word 中文"</summary>
    private static string WordOfToday()
    {
        var days = (int)(DateTime.Today - new DateTime(1900, 1, 1)).TotalDays;
        var idx = (int)((uint)(days * 2654435761u % 1000000007u) % Words.Length);
        var w = Words[idx];
        return $"{w.Word} · {w.Cn}";
    }

    private void CheckIn()
    {
        var today = DateTime.Today.ToString("yyyyMMdd");
        var last = Capability.Storage.Get<string>(LastDayKey) ?? "";
        if (last == today)
        {
            Pet.Say($"今天已打卡，连续 {Capability.Storage.Get<int>(StreakKey)} 天", 2500);
            return;
        }

        var yesterday = DateTime.Today.AddDays(-1).ToString("yyyyMMdd");
        var streak = last == yesterday ? Capability.Storage.Get<int>(StreakKey) + 1 : 1;
        Capability.Storage.Set(LastDayKey, today);
        Capability.Storage.Set(StreakKey, streak);
        Pet.Say($"✅ 打卡成功！连续 {streak} 天", 3000);
    }
}
