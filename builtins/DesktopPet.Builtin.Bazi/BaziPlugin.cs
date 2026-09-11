using DesktopPet.Core;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.Bazi;

/// <summary>
/// 命理助手插件（P0：骨架 + 生日录入设置页 + lunar 依赖加载验证）。
/// <para>P0 只打通「加载 → OnLoad → 计算 → 卸载」链路，AI 咨询 / 定时播报 / MBTI 属后续阶段。</para>
/// </summary>
public sealed class BaziPlugin : PluginBase
{
    private BaziProfile _profile = new();

    public override string Id => "com.lantern.bazi";
    public override string Name => "命理助手";

    /// <summary>与 plugin.toml 的 permissions 段保持一致（两处都要写，手册 4.1）。</summary>
    public override PluginPermissions RequiredPermissions =>
        PluginPermissions.PetAnimate | PluginPermissions.PetRead
        | PluginPermissions.UIRegister | PluginPermissions.Storage
        | PluginPermissions.Scheduler | PluginPermissions.Notify | PluginPermissions.Network;

    public override void OnLoad(IPluginContext ctx)
    {
        base.OnLoad(ctx);   // 铁律 1：第一行，否则 Pet / UI / Capability 全为 null

        _profile = BaziProfile.Load(Capability.Storage);

        // P0 核心验证动作：无条件跑一次 lunar 自检。
        //  ① 若 lunar.dll 未被插件 ALC 正确解析，这里会抛异常 —— 宿主捕获后会禁用本插件，
        //     加载器测试据此判定第三方依赖加载链是否可用；
        //  ② 顺带完成 lunar-csharp 的已知用例回归校验。
        var selfTest = BaziEngine.VerifyKnownCases();

        // 已录入完整生日时，OnLoad 再算一次真实命盘（预热 + 便于排查）。
        var preview = BaziEngine.ComputeProfile(_profile);
        var fourPillars = preview.HasBirth ? preview.FourPillars : "（未录入生日）";
        _ = selfTest;   // 结果由加载器测试通过反射读取，运行时不额外提示

        // 径向槽位「问问猫」：AI 深度咨询入口（P3 开放，P0 先占位）
        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "bazi-ask",
            Label = "问问猫",
            Icon = "star",
            Order = 30,
            OnInvoke = () => Pet.Say($"命盘：{fourPillars}（AI 咨询后续开放）", 4000),
        });

        // 设置页：生日录入 + 实时四柱预览
        UI.RegisterSettingsPage(new BaziSettingsPage(Capability.Storage));
    }

    public override void OnUnload()
    {
        // P0 未订阅事件、未持有定时器句柄；如后续阶段新增订阅，务必在此退订（铁律 2）。
    }
}
