using DesktopPet.Core;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.Quote;

/// <summary>
/// 每日一句插件：右键菜单「每日一句」槽位，按日期确定性选一句中文句子，宠物气泡播报。
/// 日期变化自动换句（同一天内多次点击显示同一句，跨天刷新）。
/// </summary>
public sealed class QuotePlugin : PluginBase
{
    private static readonly string[] Quotes =
    {
        "今天也要加油鸭！",
        "愿你今天心情像猫一样轻松～",
        "慢慢来，比较快。",
        "生活明朗，万物可爱。",
        "每天进步一点点。",
        "别着急，你的努力不会白费。",
        "保持热爱，奔赴山海。",
        "开心最重要！",
        "世界很大，一起去看看。",
        "今天也要好好吃饭。",
        "焦虑的时候，深呼吸三次。",
        "你比想象中更强大。",
        "专注当下，未来自有答案。",
        "微风不燥，岁月静好。",
        "给自己一个大大的拥抱。",
        "行动是治愈恐惧的良药。",
        "今天也要元气满满！",
        "最好的时光就是现在。",
        "放轻松，你已经做得很好了。",
        "心有猛虎，细嗅蔷薇。",
    };

    public override string Id => "com.lantern.quote";
    public override string Name => "每日一句";

    public override PluginPermissions RequiredPermissions =>
        PluginPermissions.PetAnimate | PluginPermissions.UIRegister;

    public override void OnLoad(IPluginContext ctx)
    {
        base.OnLoad(ctx);

        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "quote-daily",
            Label = "每日一句",
            Icon = "info",
            Order = 9,
            OnInvoke = () => Pet.Say(QuoteOfToday(), 5000),
        });
    }

    /// <summary>按日期确定性选句：同一天同一句，跨天变化（用日序号的稳定哈希取模）</summary>
    private static string QuoteOfToday()
    {
        // 用 1900-01-01 以来的天数做种子，同一天稳定、跨天变化
        var days = (int)(DateTime.Today - new DateTime(1900, 1, 1)).TotalDays;
        var idx = (int)((uint)(days * 2654435761u % 1000000007u) % Quotes.Length);
        return Quotes[idx];
    }
}
