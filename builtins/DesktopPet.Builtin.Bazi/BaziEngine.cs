using Lunar;
using LunarDate = Lunar.Lunar;

namespace DesktopPet.Builtin.Bazi;

/// <summary>
/// 一张命盘的计算结果（值对象，全部只读）。
/// P0 scope：四柱 / 日主 / 五行 / 纳音 / 十神 / 星座 / 大运起运。
/// </summary>
public sealed record BaziChart
{
    public bool HasBirth { get; init; }
    public bool HasTime { get; init; }

    public string YearPillar { get; init; } = "";
    public string MonthPillar { get; init; } = "";
    public string DayPillar { get; init; } = "";
    public string TimePillar { get; init; } = "";

    /// <summary>日主（日干，如「辛」）。</summary>
    public string DayMaster { get; init; } = "";

    public string YearWuXing { get; init; } = "";
    public string MonthWuXing { get; init; } = "";
    public string DayWuXing { get; init; } = "";
    public string TimeWuXing { get; init; } = "";

    public string YearNaYin { get; init; } = "";
    public string MonthNaYin { get; init; } = "";
    public string DayNaYin { get; init; } = "";
    public string TimeNaYin { get; init; } = "";

    public string YearShiShen { get; init; } = "";
    public string MonthShiShen { get; init; } = "";
    public string DayShiShen { get; init; } = "";
    public string TimeShiShen { get; init; } = "";

    /// <summary>星座（如「水瓶座」）。</summary>
    public string XingZuo { get; init; } = "";

    public int YunStartYear { get; init; }
    public int YunStartMonth { get; init; }
    public int YunStartDay { get; init; }

    /// <summary>四柱一行展示，无时辰时柱显示「—」。</summary>
    public string FourPillars => HasBirth
        ? $"{YearPillar} {MonthPillar} {DayPillar} {(HasTime ? TimePillar : "—")}"
        : "";

    /// <summary>起运公历日期，如 "1989-02-18"。</summary>
    public string YunStartText => HasBirth && YunStartYear > 0
        ? $"{YunStartYear}-{YunStartMonth:D2}-{YunStartDay:D2}"
        : "";
}

/// <summary>
/// 某一日的黄历信息（值对象，全部只读）。P2 scope：日柱 / 五行 / 纳音 / 节气 / 宜忌 / 吉神 / 方位 / 冲煞。
/// </summary>
public sealed record BaziToday
{
    /// <summary>公历日期 yyyy-MM-dd。</summary>
    public string DateText { get; init; } = "";

    /// <summary>日柱干支（如「己卯」）。</summary>
    public string DayPillar { get; init; } = "";

    /// <summary>日干（如「己」）。</summary>
    public string DayGan { get; init; } = "";

    /// <summary>日干五行。</summary>
    public string DayWuXing { get; init; } = "";

    /// <summary>日纳音（如「剑锋金」）。</summary>
    public string DayNaYin { get; init; } = "";

    /// <summary>节气名（无节气为空串）。</summary>
    public string JieQi { get; init; } = "";

    /// <summary>宜。</summary>
    public string[] Yi { get; init; } = [];

    /// <summary>忌。</summary>
    public string[] Ji { get; init; } = [];

    /// <summary>吉神宜趋。</summary>
    public string[] JiShen { get; init; } = [];

    /// <summary>凶煞宜忌。</summary>
    public string[] XiongSha { get; init; } = [];

    /// <summary>喜神方位（汉字，如「艮」）。</summary>
    public string XiPosition { get; init; } = "";

    /// <summary>喜神方位描述（如「东北」）。</summary>
    public string XiPositionDesc { get; init; } = "";

    /// <summary>财神方位描述（如「正南」）。</summary>
    public string CaiPositionDesc { get; init; } = "";

    /// <summary>日冲（地支，如「申」）。</summary>
    public string Chong { get; init; } = "";

    /// <summary>日冲描述（如「(壬申)猴」）。</summary>
    public string ChongDesc { get; init; } = "";

    /// <summary>日煞（如「北」）。</summary>
    public string Sha { get; init; } = "";

    /// <summary>值日天神。</summary>
    public string TianShen { get; init; } = "";

    /// <summary>值日天神类型（黄道 / 黑道）。</summary>
    public string TianShenType { get; init; } = "";

    /// <summary>值日天神吉凶（吉 / 凶）。</summary>
    public string TianShenLuck { get; init; } = "";
}

/// <summary>
/// lunar-csharp 的薄封装：命盘计算 + 已知用例自检。
/// 历法一律本地算，绝不交给 AI（规格铁律）。全部为同步纯计算，无 IO / 无网络。
/// <para><b>严禁在 OnTick 里调用</b>（OnTick 约 10Hz，会拖慢宠物动画）。</para>
/// </summary>
public static class BaziEngine
{
    /// <summary>大运流派：1 = 按天数起运（lunar-csharp 常见默认）。</summary>
    public const int YunSect = 1;

    /// <summary>最近一次 <see cref="VerifyKnownCases"/> 的结果（"OK" 或 "FAIL: ..."）。供加载器回归校验读取。</summary>
    public static string LastSelfTest { get; private set; } = "未运行";

    /// <summary>按档案计算命盘。档案不完整时返回空盘（<see cref="BaziChart.HasBirth"/> == false），不抛异常。</summary>
    public static BaziChart ComputeProfile(BaziProfile p)
    {
        if (!p.HasBirth) return new BaziChart();

        var solar = ToSolar(p);
        var lunar = solar.Lunar;
        var ec = lunar.EightChar;
        var hasTime = p.HasTime;
        var yun = ec.GetYun(p.Gender, YunSect);
        // 注意：Yun.StartYear/Month/Day 是「起运岁数差」（如 8岁0月20天），不是公历日期；
        // 起运公历日期在 Yun.StartSolar（阳历）。
        var yunStart = yun.StartSolar;

        return new BaziChart
        {
            HasBirth = true,
            HasTime = hasTime,
            YearPillar = ec.Year,
            MonthPillar = ec.Month,
            DayPillar = ec.Day,
            TimePillar = hasTime ? ec.Time : "",
            DayMaster = ec.DayGan,
            YearWuXing = ec.YearWuXing,
            MonthWuXing = ec.MonthWuXing,
            DayWuXing = ec.DayWuXing,
            TimeWuXing = ec.TimeWuXing,
            YearNaYin = ec.YearNaYin,
            MonthNaYin = ec.MonthNaYin,
            DayNaYin = ec.DayNaYin,
            TimeNaYin = ec.TimeNaYin,
            YearShiShen = ec.YearShiShenGan,
            MonthShiShen = ec.MonthShiShenGan,
            DayShiShen = ec.DayShiShenGan,
            TimeShiShen = ec.TimeShiShenGan,
            XingZuo = solar.XingZuo,
            YunStartYear = yunStart.Year,
            YunStartMonth = yunStart.Month,
            YunStartDay = yunStart.Day,
        };
    }

    /// <summary>按某一日计算黄历信息（日柱 / 五行 / 宜忌 / 吉神方位 / 冲煞 / 节气）。纯计算，无 IO。</summary>
    public static BaziToday ComputeToday(DateTime date)
    {
        // 用正午，规避 23:00 日界对"日柱"的干扰。
        var solar = Solar.FromYmdHms(date.Year, date.Month, date.Day, 12, 0, 0);
        var lunar = solar.Lunar;

        return new BaziToday
        {
            DateText = date.ToString("yyyy-MM-dd"),
            DayPillar = lunar.DayInGanZhi,
            DayGan = lunar.DayGan,
            // 日干五行（单字）。注意：lunar 的 EightChar.*WuXing 返回「干+支」两字（如「土土」），
            // 直接拿来和"日主五行"比会有歧义，故这里显式取日干的五行。
            DayWuXing = ElementOfGan(lunar.DayGan),
            DayNaYin = lunar.DayNaYin,
            JieQi = lunar.JieQi ?? "",
            Yi = (lunar.DayYi ?? []).ToArray(),
            Ji = (lunar.DayJi ?? []).ToArray(),
            JiShen = (lunar.DayJiShen ?? []).ToArray(),
            XiongSha = (lunar.DayXiongSha ?? []).ToArray(),
            XiPosition = lunar.DayPositionXi ?? "",
            XiPositionDesc = lunar.DayPositionXiDesc ?? "",
            CaiPositionDesc = lunar.DayPositionCaiDesc ?? "",
            Chong = lunar.DayChong ?? "",
            ChongDesc = lunar.DayChongDesc ?? "",
            Sha = lunar.DaySha ?? "",
            TianShen = lunar.DayTianShen ?? "",
            TianShenType = lunar.DayTianShenType ?? "",
            TianShenLuck = lunar.DayTianShenLuck ?? "",
        };
    }

    /// <summary>天干 → 五行（甲乙木 / 丙丁火 / 戊己土 / 庚辛金 / 壬癸水）。</summary>
    public static string ElementOfGan(string gan) => gan switch
    {
        "甲" or "乙" => "木",
        "丙" or "丁" => "火",
        "戊" or "己" => "土",
        "庚" or "辛" => "金",
        "壬" or "癸" => "水",
        _ => "",
    };

    /// <summary>地支 → 五行（子亥水 / 寅卯木 / 巳午火 / 申酉金 / 辰戌丑未土）。</summary>
    public static string ElementOfZhi(string zhi) => zhi switch
    {
        "子" or "亥" => "水",
        "寅" or "卯" => "木",
        "巳" or "午" => "火",
        "申" or "酉" => "金",
        "辰" or "戌" or "丑" or "未" => "土",
        _ => "",
    };

    /// <summary>档案 → 公历 Solar。农历录入时先转公历（闰月用负数月份表示）。</summary>
    private static Solar ToSolar(BaziProfile p)
    {
        // 未填时辰时用正午，规避 23:00 跨日边界对日柱的干扰。
        var hour = p.Hour ?? 12;
        var minute = p.Minute ?? 0;

        if (p.IsLunar)
        {
            var lunarMonth = p.LeapMonth ? -p.Month : p.Month;
            var lunarDate = LunarDate.FromYmdHms(p.Year, lunarMonth, p.Day, hour, minute, 0);
            return lunarDate.Solar;
        }

        return Solar.FromYmdHms(p.Year, p.Month, p.Day, hour, minute, 0);
    }

    /// <summary>
    /// 用 lunar-csharp 官方回归用例自检，结果写入 <see cref="LastSelfTest"/>。
    /// <para>刻意<b>不</b>捕获异常：若 lunar.dll 在插件 ALC 下无法解析，此处会直接抛出 ——
    /// 这正是加载器用来判定「第三方依赖是否可加载」的决定性信号。</para>
    /// </summary>
    public static string VerifyKnownCases()
    {
        var failures = new List<string>();

        // 用例 1：1981-01-29 23:37 性别 0 → 起运公历 1989-02-18（团队实测）
        var s1 = Solar.FromYmdHms(1981, 1, 29, 23, 37, 0).Lunar.EightChar.GetYun(0, YunSect).StartSolar;
        Check("用例1 起运", "1989-02-18", $"{s1.Year}-{s1.Month:D2}-{s1.Day:D2}", failures);

        // 用例 2：2020-01-06 11:22 性别 1 → 起运公历 2020-02-06（团队实测）
        var s2 = Solar.FromYmdHms(2020, 1, 6, 11, 22, 0).Lunar.EightChar.GetYun(1, YunSect).StartSolar;
        Check("用例2 起运", "2020-02-06", $"{s2.Year}-{s2.Month:D2}-{s2.Day:D2}", failures);

        // 用例 3/4：2021 年立春（2021-02-03 22:58）前后各一天 → 年柱切换 庚子 → 辛丑
        var before = Solar.FromYmdHms(2021, 2, 2, 12, 0, 0).Lunar.EightChar.Year;
        var after = Solar.FromYmdHms(2021, 2, 4, 12, 0, 0).Lunar.EightChar.Year;
        Check("用例3 立春前年柱", "庚子", before, failures);
        Check("用例4 立春后年柱", "辛丑", after, failures);

        LastSelfTest = failures.Count == 0 ? "OK" : "FAIL: " + string.Join("; ", failures);
        return LastSelfTest;
    }

    private static void Check(string name, string expected, string actual, List<string> failures)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            failures.Add($"{name} 期望 {expected} 实际 {actual}");
    }
}
