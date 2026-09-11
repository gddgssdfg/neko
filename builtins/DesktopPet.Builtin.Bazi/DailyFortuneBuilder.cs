using System.Text;

namespace DesktopPet.Builtin.Bazi;

/// <summary>每日运势（两个形态：<see cref="Short"/> 走气泡，<see cref="Full"/> 走系统通知）。</summary>
public sealed record DailyFortune
{
    public string Short { get; init; } = "";
    public string Full { get; init; } = "";
}

/// <summary>
/// 每日运势本地拼装器：四段（能量基调 / 宜忌 / 人格建议 / 方位）。
/// <para><b>纯本地规则，绝不调用 AI</b>（历法与推演皆本地算）。无 IO、无网络。</para>
/// </summary>
public static class DailyFortuneBuilder
{
    /// <summary>最近一次 <see cref="SelfCheck"/> 的结果（"OK" 或 "FAIL: ..."）。供加载器回归读取。</summary>
    public static string LastSelfTest { get; private set; } = "未运行";

    /// <summary>拼装当日运势。<paramref name="userChart"/> 为用户命盘（复用 <see cref="BaziEngine.ComputeProfile"/>）。</summary>
    public static DailyFortune Build(BaziProfile profile, BaziChart userChart, BaziToday today)
    {
        var userElement = BaziEngine.ElementOfGan(userChart.DayMaster);
        var todayElement = today.DayWuXing;
        var kind = Relation(todayElement, userElement);

        var theme = ThemeSentence(kind, todayElement, userElement);
        var yi = Pick(today.Yi, 3);
        var ji = Pick(today.Ji, 3);
        var advice = Advice(profile, userElement, kind);
        var direction = Direction(today);

        var yiText = yi.Count > 0 ? string.Join("、", yi) : "诸事平常";
        var jiText = ji.Count > 0 ? string.Join("、", ji) : "无明显禁忌";

        // Short（气泡）把宜/忌各自整体成句，穷尽 Pick 出的全部 2–3 条。
        var yiPart = yi.Count > 0 ? $"宜{string.Join("、", yi)}" : "诸事平常";
        var jiPart = ji.Count > 0 ? $"忌{string.Join("、", ji)}" : "无忌";
        var shortText = $"今日{today.DayPillar}日：{theme}。{yiPart}，{jiPart}。";

        var full = new StringBuilder();
        full.AppendLine($"【{today.DateText} 今日运势】");
        if (!string.IsNullOrEmpty(today.JieQi)) full.AppendLine($"节气：{today.JieQi}");
        full.AppendLine($"① 能量基调：{theme}。");
        full.AppendLine($"② 宜：{yiText}");
        full.AppendLine($"　 忌：{jiText}");
        full.AppendLine($"③ 建议：{advice}。");
        full.AppendLine($"④ 方位：{direction}。");
        if (today.JiShen.Length > 0)
            full.AppendLine($"吉神宜趋：{JoinFiltered(today.JiShen, 4)}");

        return new DailyFortune { Short = shortText, Full = full.ToString().TrimEnd() };
    }

    // ---- 第一段：能量基调（今日五行 与 用户日主 的生克） ----

    /// <summary>今日五行 对 用户日主 的关系：同气 / 生(得助) / 泄(付出) / 克(受压) / 耗(主动)。</summary>
    internal static string Relation(string todayElement, string userElement)
    {
        if (string.IsNullOrEmpty(todayElement) || string.IsNullOrEmpty(userElement)) return "平";
        if (todayElement == userElement) return "同气";
        if (Generates(todayElement, userElement)) return "生";
        if (Generates(userElement, todayElement)) return "泄";
        if (Overcomes(todayElement, userElement)) return "克";
        if (Overcomes(userElement, todayElement)) return "耗";
        return "平";
    }

    private static string ThemeSentence(string kind, string todayEl, string userEl) => kind switch
    {
        "同气" => $"今日{todayEl}气与你的{userEl}日主同气，气场相合，可稳中求进",
        "生"   => $"今日{todayEl}旺，生助你的{userEl}日主，是顺势得助的一天",
        "泄"   => $"今日{todayEl}当令，你的{userEl}日主需付出，量力而行",
        "克"   => $"今日{todayEl}旺，与你{userEl}日主相克，需谨慎应对",
        "耗"   => $"今日{todayEl}当前，你的{userEl}日主克之有余，适合主动出击",
        _      => $"今日{todayEl}气平和，顺其自然即可",
    };

    /// <summary>五行相生：木→火→土→金→水→木。</summary>
    private static bool Generates(string a, string b) => (a, b) switch
    {
        ("木", "火") or ("火", "土") or ("土", "金") or ("金", "水") or ("水", "木") => true,
        _ => false,
    };

    /// <summary>五行相克：木克土 / 土克水 / 水克火 / 火克金 / 金克木。</summary>
    private static bool Overcomes(string a, string b) => (a, b) switch
    {
        ("木", "土") or ("土", "水") or ("水", "火") or ("火", "金") or ("金", "木") => true,
        _ => false,
    };

    // ---- 第三段：人格定制建议（MBTI 为 P4；未填时按日主五行给通用建议） ----

    private static string Advice(BaziProfile profile, string userElement, string kind)
    {
        var baseAdvice = ElementAdvice(userElement);
        var tip = MbtiTip(profile.Mbti, kind);
        return tip is null ? baseAdvice : $"{tip}（{baseAdvice}）";
    }

    private static string ElementAdvice(string element) => element switch
    {
        "木" => "宜舒展身心、多走动接触绿意，忌憋闷急躁",
        "火" => "宜热情社交、把握机会，忌熬夜与情绪上头",
        "土" => "宜踏实推进既定计划，忌思虑过度钻牛角尖",
        "金" => "宜整理归纳、做决断，忌太刚硬与人硬碰",
        "水" => "宜灵活变通、多听少说，忌优柔寡断",
        _    => "顺其自然，保持平常心",
    };

    /// <summary>
    /// MBTI 定制建议（P4：16 型全覆盖）。按五行生克关系 <paramref name="kind"/> 分「受压(克)」与「其余」两支。
    /// 未填 / 未命中返回 null → 回退按日主五行的通用建议。
    /// </summary>
    private static string? MbtiTip(string? mbti, string kind)
    {
        if (string.IsNullOrWhiteSpace(mbti)) return null;
        var t = mbti.Trim().ToUpperInvariant();
        var pressed = kind == "克";
        return t switch
        {
            "INTJ" => pressed ? "遇冲突先按暂停键，理性复盘再回应" : "用你的分析力把机会拆成可执行步骤",
            "INTP" => pressed ? "别急着反驳，先把自己的逻辑理清" : "顺着好奇心深挖一件感兴趣的事",
            "ENTJ" => pressed ? "先退一步再出手，避免硬碰硬" : "主动定目标、抓落地",
            "ENTP" => pressed ? "把争辩的能量转成一次头脑风暴" : "用新点子打开一个突破口",
            "INFJ" => pressed ? "给自己独处空间，别硬撑情绪" : "按内心价值推进一件长期想做的事",
            "INFP" => pressed ? "写下来梳理情绪，别内耗" : "跟随热爱做一件小事，滋养自己",
            "ENFJ" => pressed ? "先照顾好自己，再去照顾别人" : "主动联结他人，一起推进目标",
            "ENFP" => pressed ? "转移注意力，用社交能量对冲负面情绪" : "把热情投向你最想做的那件事",
            "ISTJ" => pressed ? "守住既定节奏，别被外界带偏" : "按计划稳扎稳打，成果自然显现",
            "ISFJ" => pressed ? "别过度揽责，给自己留点余地" : "照顾好身边人，也别忘了自己",
            "ESTJ" => pressed ? "少一点强势，多听一句再决定" : "把秩序感用上，高效推进",
            "ESFJ" => pressed ? "别太在意他人评价，先稳住自己" : "用你的热心把一件事串联起来",
            "ISTP" => pressed ? "先动手解决一个小问题，别空想" : "用实操把想法快速验证",
            "ISFP" => pressed ? "去接触自然或音乐，舒缓情绪" : "跟随审美做一件让自己开心的事",
            "ESTP" => pressed ? "踩个刹车，评估风险再上" : "抓住眼前机会，果断行动",
            "ESFP" => pressed ? "找人聊聊，用当下的快乐冲淡烦恼" : "把活力放在你最想体验的事上",
            _ => null,
        };
    }

    // ---- 第四段：方位提醒 ----

    private static string Direction(BaziToday t)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(t.XiPositionDesc))
            parts.Add($"喜神在{t.XiPositionDesc}{(string.IsNullOrEmpty(t.XiPosition) ? "" : $"（{t.XiPosition}）")}");
        if (!string.IsNullOrEmpty(t.CaiPositionDesc))
            parts.Add($"财神在{t.CaiPositionDesc}");
        var main = parts.Count > 0 ? string.Join("，", parts) : "方位平平";

        var chong = string.IsNullOrEmpty(t.ChongDesc) ? "" : $"，今日冲{t.ChongDesc}";
        var sha = string.IsNullOrEmpty(t.Sha) ? "" : $"，煞{t.Sha}";
        return main + chong + sha;
    }

    // ---- 工具 ----

    private static List<string> Pick(string[] items, int n)
        => items is null
            ? []
            : items.Where(x => !string.IsNullOrWhiteSpace(x) && x != "无").Take(n).ToList();

    private static string JoinFiltered(string[] items, int n)
    {
        var picked = Pick(items, n);
        return picked.Count > 0 ? string.Join("、", picked) : "";
    }

    // ---- 自检（纯函数，无生产副作用；供加载器回归读取） ----

    /// <summary>
    /// 自检：五行生克 5 例 + 一次 Build 冒烟（固定日期，不依赖系统时间）。
    /// 结果写入 <see cref="LastSelfTest"/>。刻意不 try/catch 掉 lunar 相关异常（同 <see cref="BaziEngine.VerifyKnownCases"/>）。
    /// </summary>
    public static string SelfCheck()
    {
        var failures = new List<string>();

        Check("同气", "同气", Relation("木", "木"), failures);
        Check("今日生你", "生", Relation("木", "火"), failures);
        Check("你生今日", "泄", Relation("火", "木"), failures);
        Check("今日克你", "克", Relation("木", "土"), failures);
        Check("你克今日", "耗", Relation("木", "金"), failures);

        try
        {
            var profile = new BaziProfile { Year = 1981, Month = 1, Day = 29, Hour = 23, Minute = 37, Gender = 0 };
            var chart = BaziEngine.ComputeProfile(profile);
            var today = BaziEngine.ComputeToday(new DateTime(2024, 6, 15));
            var fortune = Build(profile, chart, today);

            if (string.IsNullOrWhiteSpace(fortune.Short)) failures.Add("Build.Short 为空");
            if (string.IsNullOrWhiteSpace(fortune.Full)) failures.Add("Build.Full 为空");
            if (!fortune.Short.Contains("宜")) failures.Add("Short 缺「宜」");
            if (!fortune.Short.Contains("忌")) failures.Add("Short 缺「忌」");
            if (!fortune.Full.Contains("宜")) failures.Add("Full 缺「宜」");
            if (!fortune.Full.Contains("忌")) failures.Add("Full 缺「忌」");
            if (!fortune.Full.Contains("方位")) failures.Add("Full 缺「方位」");

            // P4：MBTI 16 型全覆盖 + 同一 kind 下各不相同（证明真按类型分支，而非恒返回 null）
            var types16 = new[]
            {
                "INTJ", "INTP", "ENTJ", "ENTP", "INFJ", "INFP", "ENFJ", "ENFP",
                "ISTJ", "ISFJ", "ESTJ", "ESFJ", "ISTP", "ISFP", "ESTP", "ESFP",
            };
            foreach (var k in new[] { "同气", "生", "泄", "克", "耗" })
            {
                var tips = types16.Select(t => MbtiTip(t, k)).ToList();
                if (tips.Any(string.IsNullOrWhiteSpace)) failures.Add($"kind={k} 有未覆盖类型");
                if (tips.Distinct().Count() != types16.Length) failures.Add($"kind={k} 存在重复建议（未按类型分支）");
            }
            if (MbtiTip("XXXX", "克") is not null) failures.Add("未知类型应返回 null");
            if (MbtiTip(null, "克") is not null) failures.Add("null 类型应返回 null");

            // P4：同一命盘下，填 MBTI 与不填 → 播报内容应不同（类型真的影响输出）
            var withMbti = new BaziProfile { Year = 1981, Month = 1, Day = 29, Hour = 23, Minute = 37, Gender = 0, Mbti = "INTJ" };
            if (Build(withMbti, chart, today).Full == fortune.Full)
                failures.Add("填 MBTI 后播报内容未变化");
        }
        catch (Exception ex)
        {
            failures.Add($"Build 抛异常 {ex.GetType().Name}: {ex.Message}");
        }

        LastSelfTest = failures.Count == 0 ? "OK" : "FAIL: " + string.Join("; ", failures);
        return LastSelfTest;
    }

    private static void Check(string name, string expected, string actual, List<string> failures)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            failures.Add($"{name} 期望 {expected} 实际 {actual}");
    }
}
