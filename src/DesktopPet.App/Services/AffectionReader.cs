using System.IO;

namespace DesktopPet.App.Services;

/// <summary>
/// v0.1.59(⑨)：好感度等级读取（从 ChatScheduler.ReadAffectionLevel 提取为可复用静态方法）。
/// 读 数据根/plugins/com.lantern.affection/score.json（纯数字文本）→ 4 档：
///   &lt;10 陌生(0) / &lt;50 熟悉(1) / &lt;100 亲密(2) / ≥100 挚友(3)。
/// 文件缺失 / 解析失败 → 0 陌生（静默回退，不抛异常）。
/// 单一数据源：ChatScheduler 与 PetWindow(心情驱动动画) 共用，避免出现两份读取逻辑。
/// </summary>
public static class AffectionReader
{
    /// <summary>读好感度等级 0..3。文件缺失/解析失败 → 0 陌生。对齐 AffectionPlugin 的分档阈值。</summary>
    public static int ReadLevel(string affectionFile)
    {
        try
        {
            if (!File.Exists(affectionFile)) return 0;
            var score = int.Parse(File.ReadAllText(affectionFile).Trim());
            return score switch
            {
                < 10 => 0,      // 陌生
                < 50 => 1,      // 熟悉
                < 100 => 2,     // 亲密
                _ => 3,         // 挚友
            };
        }
        catch { return 0; }
    }
}
