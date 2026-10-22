using System;

namespace DesktopPet.App.Behavior;

/// <summary>抚摸命中区域（v0.2.4「分区摸头」）。</summary>
public enum PetRegion
{
    /// <summary>头部（上段）—— 猫咪最受用的位置。</summary>
    Head,

    /// <summary>躯干（中段）。</summary>
    Body,

    /// <summary>下半身（爪 / 尾 / 后腿）—— 猫一般不太愿意被摸这里。</summary>
    Lower,
}

/// <summary>
/// 抚摸区域判定（v0.2.4，**纯函数，可离线单测**）。
///
/// <para><b>为什么按"归一化高度"分层，而不是去找真正的身体部位</b>：</para>
/// <list type="bullet">
///   <item>9 套皮肤的体型、朝向、尾巴位置各不相同（有的尾巴甩在左侧、有的盘在身前），
///         按像素找部位要给每套皮肤标注包围盒 —— 成本高，且一改素材就失效；</item>
///   <item>宠物窗口的尺寸**就等于**精灵尺寸（如 32px × scale 倍率），
///         所以"窗口内归一化 y"本身就是"在猫身上的相对位置"，**跨皮肤语义一致**。</item>
/// </list>
///
/// <para><b>分层阈值</b>（对齐 32×32 像素橘猫的体型：头约占上 38%，躯干到 76%，其余是爪/尾）：</para>
/// <list type="bullet">
///   <item><c>y &lt; 0.38</c> → <see cref="PetRegion.Head"/></item>
///   <item><c>0.38 ≤ y &lt; 0.76</c> → <see cref="PetRegion.Body"/></item>
///   <item><c>y ≥ 0.76</c> → <see cref="PetRegion.Lower"/></item>
/// </list>
/// </summary>
public static class PetRegionMath
{
    /// <summary>头 / 躯干分界（归一化高度）。</summary>
    public const double HeadBodyBoundary = 0.38;

    /// <summary>躯干 / 下半身分界（归一化高度）。</summary>
    public const double BodyLowerBoundary = 0.76;

    /// <summary>
    /// 按归一化高度判定区域。**越界值先夹到 [0,1]、NaN 视为 0** ——
    /// 这样调用方不必自己防御（窗口尚未布局时高度可能为 0/异常）。
    /// </summary>
    public static PetRegion Classify(double normalizedY)
    {
        if (double.IsNaN(normalizedY)) normalizedY = 0.0;
        normalizedY = Math.Clamp(normalizedY, 0.0, 1.0);

        if (normalizedY < HeadBodyBoundary) return PetRegion.Head;
        if (normalizedY < BodyLowerBoundary) return PetRegion.Body;
        return PetRegion.Lower;
    }
}
