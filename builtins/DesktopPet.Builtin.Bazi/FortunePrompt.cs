using System.Text;

namespace DesktopPet.Builtin.Bazi;

/// <summary>
/// AI 咨询的 Prompt 组装器（规格 4.4）。<b>抽成纯函数便于回归自检</b>。
/// <para>四部分：① 系统提示（含铁律）② 结构化命盘 + 今日历法 ③ 最近 N 轮历史 ④ 用户当前问题。</para>
/// <para>铁律：历法数据一律本地算好喂进去，只让 AI 做「解读」，绝不让 AI 重算干支（规格 2.2）。</para>
/// </summary>
public static class FortunePrompt
{
    /// <summary>系统提示必须原样体现的红线句（自检据此断言）。</summary>
    public const string IroncladRule = "只负责解读，不做任何历法和干支计算";

    /// <summary>历史条数上限（10 轮 = 20 条）；与 <see cref="ChatHistory.MaxMessages"/> 一致。</summary>
    public const int MaxHistoryMessages = ChatHistory.MaxMessages;

    /// <summary>最近一次 <see cref="SelfCheck"/> 的结果（"OK" 或 "FAIL: ..."）。供加载器回归读取。</summary>
    public static string LastSelfTest { get; private set; } = "未运行";

    /// <summary>组装完整的 messages 数组：system（含铁律 + 命盘 + 今日）+ 历史 + 当前问题。</summary>
    public static List<ChatMessage> BuildMessages(
        BaziProfile profile,
        BaziChart chart,
        BaziToday today,
        IReadOnlyList<ChatMessage>? history,
        string question)
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.OfSystem(BuildSystemPrompt(chart, today, profile.Mbti)),
        };

        var recent = (history ?? Array.Empty<ChatMessage>())
            .Where(m => m.Role == "user" || m.Role == "assistant")
            .Where(m => !string.IsNullOrEmpty(m.Content))
            .TakeLast(MaxHistoryMessages);
        messages.AddRange(recent);

        messages.Add(ChatMessage.OfUser(question));
        return messages;
    }

    /// <summary>系统提示：铁律 + 本地算好的结构化命盘与今日历法（AI 直接采信）。</summary>
    public static string BuildSystemPrompt(BaziChart chart, BaziToday today, string? mbti)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是传统文化解读助手，只负责解读，不做任何历法和干支计算；历法数据由系统提供，直接采信，不要重新推算。");
        sb.AppendLine("请用温和、务实的中文回答，结合下面的命盘给出可执行的生活建议；不要预测吉凶祸福，不做医疗/投资等专业建议，不引导付费。");
        sb.AppendLine();

        sb.AppendLine("【用户命盘（系统已算好，直接采用，无需校验）】");
        sb.AppendLine($"四柱：{chart.FourPillars}");
        sb.AppendLine($"日主：{chart.DayMaster}（{BaziEngine.ElementOfGan(chart.DayMaster)}）");
        sb.AppendLine($"五行分布：{WuXingDistribution(chart)}");
        sb.AppendLine($"十神（天干）：年 {Or(chart.YearShiShen)} / 月 {Or(chart.MonthShiShen)} / 日 {Or(chart.DayShiShen)} / 时 {(chart.HasTime ? Or(chart.TimeShiShen) : "—")}");
        sb.AppendLine($"日柱纳音：{Or(chart.DayNaYin)}");
        sb.AppendLine($"星座：{Or(chart.XingZuo)}");
        sb.AppendLine($"人格（MBTI）：{(string.IsNullOrWhiteSpace(mbti) ? "未提供" : mbti)}");
        sb.AppendLine();

        sb.AppendLine("【今日历法数据（系统已算好，直接采用，无需校验）】");
        sb.AppendLine($"公历日期：{today.DateText}");
        sb.AppendLine($"今日干支：{today.DayPillar}（日干五行 {Or(today.DayWuXing)}）");
        if (!string.IsNullOrEmpty(today.JieQi))
            sb.AppendLine($"节气：{today.JieQi}");
        sb.AppendLine($"宜：{Join(today.Yi, 6)}");
        sb.AppendLine($"忌：{Join(today.Ji, 6)}");
        sb.AppendLine($"喜神方位：{Or(today.XiPositionDesc)}　财神方位：{Or(today.CaiPositionDesc)}");
        sb.AppendLine($"冲煞：{(string.IsNullOrEmpty(today.ChongDesc) ? "—" : today.ChongDesc)}　煞{(string.IsNullOrEmpty(today.Sha) ? "—" : today.Sha)}");

        return sb.ToString().TrimEnd();
    }

    /// <summary>统计四柱 8 字（4 干 + 4 支）的五行分布，如「木1 火2 土3 金1 水1」。</summary>
    private static string WuXingDistribution(BaziChart chart)
    {
        var order = new[] { "木", "火", "土", "金", "水" };
        var count = order.ToDictionary(e => e, _ => 0);

        foreach (var pillar in new[] { chart.YearPillar, chart.MonthPillar, chart.DayPillar, chart.HasTime ? chart.TimePillar : "" })
        {
            if (string.IsNullOrEmpty(pillar)) continue;
            var gan = pillar[..1];
            var zhi = pillar.Length > 1 ? pillar[1..2] : "";
            var gEl = BaziEngine.ElementOfGan(gan);
            var zEl = BaziEngine.ElementOfZhi(zhi);
            if (count.ContainsKey(gEl)) count[gEl]++;
            if (count.ContainsKey(zEl)) count[zEl]++;
        }

        return string.Join(" ", order.Select(e => $"{e}{count[e]}"));
    }

    private static string Join(string[]? items, int n)
    {
        var picked = (items ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x) && x != "无")
            .Take(n)
            .ToList();
        return picked.Count > 0 ? string.Join("、", picked) : "无";
    }

    private static string Or(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    // ---- 自检（纯函数，无 IO / 无网络；供加载器回归读取） ----

    /// <summary>
    /// 自检：用固定档案 + 固定日期组装一次 messages，断言四部分齐全
    /// （system 含铁律与四柱/日主/五行/今日干支/宜/忌，历史被带入，末条为当前问题）。
    /// </summary>
    public static string SelfCheck()
    {
        var failures = new List<string>();
        try
        {
            var profile = new BaziProfile
            {
                Year = 1981, Month = 1, Day = 29, Hour = 23, Minute = 37, Gender = 0, Mbti = "INTJ",
            };
            var chart = BaziEngine.ComputeProfile(profile);
            var today = BaziEngine.ComputeToday(new DateTime(2024, 6, 15));
            var history = new List<ChatMessage>
            {
                ChatMessage.OfUser("上一问"),
                ChatMessage.OfAssistant("上一答"),
            };
            var messages = BuildMessages(profile, chart, today, history, "我今年适合换工作吗？");

            if (messages.Count < 3) failures.Add($"messages 过少（{messages.Count}）");
            if (messages.Count == 0 || messages[0].Role != "system") failures.Add("首条应为 system");
            var last = messages[^1];
            if (last.Role != "user" || !last.Content.Contains("换工作")) failures.Add("末条应为用户当前问题");
            if (!messages.Any(m => m.Role == "assistant" && m.Content == "上一答")) failures.Add("历史未被带入");

            var sys = messages[0].Content;
            if (!sys.Contains(IroncladRule)) failures.Add("system 缺铁律句");
            foreach (var keyword in new[] { "四柱", "日主", "五行", "今日干支", "宜", "忌" })
                if (!sys.Contains(keyword)) failures.Add($"system 缺「{keyword}」");
            if (!sys.Contains(chart.DayPillar)) failures.Add("system 缺日柱干支");
            if (!sys.Contains(today.DayPillar)) failures.Add("system 缺今日干支值");
        }
        catch (Exception ex)
        {
            failures.Add($"自检抛异常 {ex.GetType().Name}: {ex.Message}");
        }

        LastSelfTest = failures.Count == 0 ? "OK" : "FAIL: " + string.Join("; ", failures);
        return LastSelfTest;
    }
}
