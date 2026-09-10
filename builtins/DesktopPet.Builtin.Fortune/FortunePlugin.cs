using DesktopPet.Core;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.Fortune;

/// <summary>
/// 今日运势插件：按日期确定性选运势等级（同天同运、跨天刷新），建议随机。
/// 猫气泡播报「今日运势：大吉 🌟 适合努力」。
/// </summary>
public sealed class FortunePlugin : PluginBase
{
    private static readonly (string Level, string Star)[] Fortunes =
    {
        ("大吉", "🌟"), ("中吉", "✨"), ("小吉", "🌤"),
        ("平", "☁️"), ("小凶", "🌧"), ("大凶", "⛈"),
    };

    private static readonly string[] Tips =
    {
        "适合努力 / 适合表白 / 适合摸鱼半小时后认真工作",
        "宜早起 / 宜喝奶茶 / 宜整理桌面 / 宜发个朋友圈",
        "忌拖延 / 忌熬夜 / 忌冲动消费 / 忌吃太辣",
        "宜静心 / 宜散步 / 宜给老朋友发条消息 / 宜早睡",
        "小心丢东西 / 注意说话分寸 / 记得带伞 / 别点外卖",
        "稳住，明天会好的 / 先休息一下 / 别做重大决定 / 猫猫陪你",
    };

    private readonly Random _rng = new();

    public override string Id => "com.lantern.fortune";
    public override string Name => "今日运势";

    public override PluginPermissions RequiredPermissions =>
        PluginPermissions.PetAnimate | PluginPermissions.UIRegister;

    public override void OnLoad(IPluginContext ctx)
    {
        base.OnLoad(ctx);

        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "fortune-today",
            Label = "今日运势",
            Icon = "star",
            Order = 22,
            OnInvoke = () => Pet.Say(Text(), 5000),
        });
    }

    private string Text()
    {
        var days = (int)(DateTime.Today - new DateTime(1900, 1, 1)).TotalDays;
        var idx = (int)((uint)(days * 2654435761u % 1000000007u) % Fortunes.Length);
        var f = Fortunes[idx];
        var tip = Tips[idx][_rng.Next(Tips[idx].Split('/').Length)];
        return $"今日运势：{f.Level} {f.Star}（{tip}）";
    }
}
