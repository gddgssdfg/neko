using DesktopPet.Core;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.Affection;

/// <summary>
/// 内置插件：好感度（dogfooding 示范）。
/// 消费 pet:petted 事件，用 Storage 持久化好感度分数，
/// 随等级提升触发不同的宠物反应（说话气泡 + 情绪 + 径向槽位展示）。
/// 完全走第三方插件的加载路径（ALC 隔离 + 权限校验 + 事件订阅）。
/// </summary>
public sealed class AffectionPlugin : PluginBase
{
    private const string ScoreKey = "score";
    private const int LevelFamiliar = 10;   // 熟悉
    private const int LevelIntimate = 50;   // 亲密
    private const int LevelBestFriend = 100;// 挚友

    private readonly Random _rng = new();
    private int _score;
    private Action? _unsubscribePetted;

    public override string Id => "com.lantern.affection";
    public override string Name => "好感度";

    public override PluginPermissions RequiredPermissions =>
        PluginPermissions.PetAnimate | PluginPermissions.PetRead
        | PluginPermissions.UIRegister | PluginPermissions.Storage;

    public override void OnLoad(IPluginContext ctx)
    {
        base.OnLoad(ctx);

        // 读取持久化的好感度（跨会话保留）
        _score = Capability.Storage.Get<int>(ScoreKey);

        // 订阅抚摸事件：好感度增长入口
        _unsubscribePetted = Capability.Events.On(PetEvents.PetPetted, _ => OnPetted());

        // 注册径向槽位：点击可查询当前好感度（标签为初始等级，v1 不做动态刷新）
        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "affection-status",
            Label = LevelName(GetLevel(_score)),
            Icon = "heart",
            Order = 1,
            OnInvoke = () => Pet.Say($"好感度：{_score}（{LevelName(GetLevel(_score))}）"),
        });

        // 注册自定义设置页（v0.1.5 ISettingsPage dogfooding 示范）—— 嵌入宿主设置窗口的「插件」tab
        UI.RegisterSettingsPage(new AffectionSettingsPage(Capability.Storage, () => _score));

        System.Diagnostics.Debug.WriteLine($"[好感度] 加载完成，当前分数={_score}");
    }

    public override void OnUnload()
    {
        _unsubscribePetted?.Invoke();
        _unsubscribePetted = null;
    }

    private void OnPetted()
    {
        var oldLevel = GetLevel(_score);
        _score++;
        Capability.Storage.Set(ScoreKey, _score);

        // 升到新等级：特殊庆祝；否则普通友好反应
        if (GetLevel(_score) > oldLevel) OnLevelUp(GetLevel(_score));
        else OnNormalPet();
    }

    private void OnNormalPet()
    {
        switch (_rng.Next(4))
        {
            case 0: Pet.Say("喵~", 1500); break;
            case 1: Pet.SetMood(PetMood.Happy); break;
            case 2: Pet.Say("咕噜咕噜~", 1500); break;
            default: Pet.Say("（蹭蹭）", 1200); break;
        }
    }

    private void OnLevelUp(int level)
    {
        var msg = level switch
        {
            1 => "好感度提升！我们熟起来了~",
            2 => "好感度提升！你是我最喜欢的人类！",
            3 => "好感度提升！我们是挚友了！",
            _ => "好感度满级！一辈子都要在一起！",
        };
        Pet.Say(msg, 2600);
        Pet.SetMood(PetMood.Happy);
    }

    private static int GetLevel(int score) => score switch
    {
        < LevelFamiliar => 0,    // 陌生
        < LevelIntimate => 1,    // 熟悉
        < LevelBestFriend => 2,  // 亲密
        _ => 3,                  // 挚友
    };

    private static string LevelName(int level) => level switch
    {
        0 => "陌生",
        1 => "熟悉",
        2 => "亲密",
        _ => "挚友",
    };
}
