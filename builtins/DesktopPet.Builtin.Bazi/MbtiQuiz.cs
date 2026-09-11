using System.Text;

namespace DesktopPet.Builtin.Bazi;

/// <summary>一道简化版人格测试题（二选一）。<see cref="OptionA"/> 指向该维度的第一个极，<see cref="OptionB"/> 指向第二个极。</summary>
public sealed record MbtiQuestion(string Id, string Dimension, string Text, string OptionA, string OptionB);

/// <summary>
/// 简化版 16 型人格问卷（本地硬编码题库，<b>不联网</b>，定位「生日性格分析」）。
/// <para>四维度各 6 题、共 24 题；每题二选一，A 指向该维度第一个极、B 指向第二个极。</para>
/// <para>计分是<b>纯函数</b>：按维度统计 A/B 多数 → 拼 4 字母类型。平局（含整维未答）取该维度第一个极，保证确定性。</para>
/// </summary>
public static class MbtiQuiz
{
    /// <summary>四个维度，顺序 = 类型字母顺序（E/I、S/N、T/F、J/P）。</summary>
    public static string[] Dimensions { get; } = { "EI", "SN", "TF", "JP" };

    /// <summary>最近一次 <see cref="SelfCheck"/> 的结果（"OK" 或 "FAIL: ..."）。供加载器回归读取。</summary>
    public static string LastSelfTest { get; private set; } = "未运行";

    /// <summary>题库（24 题；EI/SN/TF/JP 各 6 题，按维度分块顺序排列）。</summary>
    public static IReadOnlyList<MbtiQuestion> Questions { get; } = new List<MbtiQuestion>
    {
        // ---- E / I ----
        new("EI1", "EI", "周末更想怎么过？", "约朋友出去走走", "一个人待着充电"),
        new("EI2", "EI", "在聚会上，你通常：", "主动和陌生人搭话", "和熟悉的人小范围聊"),
        new("EI3", "EI", "遇到烦心事，你更倾向：", "找人说出来", "自己先消化"),
        new("EI4", "EI", "长时间社交之后，你会：", "觉得更有精神", "需要独处恢复"),
        new("EI5", "EI", "表达想法时，你习惯：", "边说边想", "想清楚了再说"),
        new("EI6", "EI", "到了新环境，你一般：", "很快融入进去", "先观察再行动"),

        // ---- S / N ----
        new("SN1", "SN", "你更相信：", "亲眼所见的事实", "直觉和联想"),
        new("SN2", "SN", "学新东西时，你偏好：", "具体的步骤和例子", "先理解整体框架"),
        new("SN3", "SN", "你更关注：", "当下的细节", "未来的可能性"),
        new("SN4", "SN", "描述一件事，你倾向：", "如实讲清经过", "讲它意味着什么"),
        new("SN5", "SN", "你更容易记住：", "具体的信息", "整体的印象"),
        new("SN6", "SN", "做判断时，你更依赖：", "过往的经验", "当下的灵感"),

        // ---- T / F ----
        new("TF1", "TF", "做决定时你更看重：", "逻辑与公平", "感受与和谐"),
        new("TF2", "TF", "朋友向你倾诉，你会先：", "帮他分析问题", "先安慰他的情绪"),
        new("TF3", "TF", "评价一个方案，你更在意：", "是否合理", "是否照顾到人"),
        new("TF4", "TF", "与人意见不合时，你：", "就事论事把理讲清", "尽量避免伤了和气"),
        new("TF5", "TF", "你更希望别人说你：", "讲道理", "有同理心"),
        new("TF6", "TF", "面对规则，你倾向：", "规则优先", "人情优先"),

        // ---- J / P ----
        new("JP1", "JP", "出行之前，你通常：", "提前把行程做好", "到了再随机应变"),
        new("JP2", "JP", "面对截止日期，你：", "早早完成", "临近了才发力"),
        new("JP3", "JP", "你的桌面或房间通常：", "井井有条", "随性自在"),
        new("JP4", "JP", "计划临时有变时，你：", "会有点不自在", "觉得也没关系"),
        new("JP5", "JP", "你更喜欢：", "事情早点定下来", "保留更多选择"),
        new("JP6", "JP", "一天结束时，你倾向：", "按计划把清单做完", "顺其自然做到哪算哪"),
    };

    /// <summary>
    /// 计分：<paramref name="answers"/> 与 <see cref="Questions"/> 同序，0 = 选 A、1 = 选 B，其它值视为未答（忽略）。
    /// 每个维度取多数极；平局或整维未答 → 取该维度第一个极（E/S/T/J），保证结果确定。
    /// </summary>
    public static string Score(IReadOnlyList<int>? answers)
    {
        var aCount = new Dictionary<string, int>();
        var bCount = new Dictionary<string, int>();
        foreach (var dim in Dimensions) { aCount[dim] = 0; bCount[dim] = 0; }

        for (var i = 0; i < Questions.Count; i++)
        {
            var ans = answers is not null && i < answers.Count ? answers[i] : -1;
            if (ans != 0 && ans != 1) continue;
            var dim = Questions[i].Dimension;
            if (ans == 0) aCount[dim]++; else bCount[dim]++;
        }

        var sb = new StringBuilder(4);
        foreach (var dim in Dimensions)
        {
            var (poleA, poleB) = Poles(dim);
            sb.Append(aCount[dim] >= bCount[dim] ? poleA : poleB);
        }
        return sb.ToString();
    }

    /// <summary>某维度题目数（用于自检题量均衡）。</summary>
    public static int CountOf(string dimension)
        => Questions.Count(q => q.Dimension == dimension);

    /// <summary>类型 → 中文概称（16 型全覆盖）。</summary>
    public static string Describe(string? type) => type switch
    {
        "INTJ" => "建筑师",
        "INTP" => "逻辑学家",
        "ENTJ" => "指挥官",
        "ENTP" => "辩论家",
        "INFJ" => "提倡者",
        "INFP" => "调停者",
        "ENFJ" => "主人公",
        "ENFP" => "竞选者",
        "ISTJ" => "物流师",
        "ISFJ" => "守卫者",
        "ESTJ" => "总经理",
        "ESFJ" => "执政官",
        "ISTP" => "鉴赏家",
        "ISFP" => "探险家",
        "ESTP" => "企业家",
        "ESFP" => "表演者",
        _ => "未知类型",
    };

    /// <summary>维度 → （第一个极, 第二个极）。</summary>
    private static (char A, char B) Poles(string dimension) => dimension switch
    {
        "EI" => ('E', 'I'),
        "SN" => ('S', 'N'),
        "TF" => ('T', 'F'),
        "JP" => ('J', 'P'),
        _ => ('?', '?'),
    };

    // ---- 自检（纯函数，无 IO / 无网络；供加载器回归读取） ----

    /// <summary>
    /// 自检：题量均衡（四维各 5–7 题、总数 20–28）；全选 A → ESTJ、全选 B → INFP；
    /// 维度间独立（部分选 B 只影响对应字母）；平局取第一个极。
    /// </summary>
    public static string SelfCheck()
    {
        var failures = new List<string>();

        if (Questions.Count is < 20 or > 28)
            failures.Add($"题量 {Questions.Count} 不在 20–28");
        foreach (var dim in Dimensions)
        {
            var n = CountOf(dim);
            if (n is < 5 or > 7) failures.Add($"维度 {dim} 题数 {n} 不在 5–7");
        }

        // 全选 A / 全选 B
        Expect("全选A", "ESTJ", Score(Enumerable.Repeat(0, Questions.Count).ToList()), failures);
        Expect("全选B", "INFP", Score(Enumerable.Repeat(1, Questions.Count).ToList()), failures);

        // 维度独立：SN 与 JP 全选 B（→N/P），EI 与 TF 全选 A（→E/T） ⇒ ENTP
        var mixed = Questions.Select(q => q.Dimension is "SN" or "JP" ? 1 : 0).ToList();
        Expect("维度独立", "ENTP", Score(mixed), failures);

        // 平局：EI 前 3 题选 A、后 3 题选 B（3:3）→ 取第一个极 E；其余全 A ⇒ ESTJ
        var tie = Questions.Select((q, i) =>
            q.Dimension == "EI" ? (i % 6 < 3 ? 0 : 1) : 0).ToList();
        Expect("EI 平局取首极", "ESTJ", Score(tie), failures);

        // 部分未答：仅答 EI（全 A）→ EI=E，其余取首极 ⇒ ESTJ（不抛异常）
        var partial = new int[Questions.Count];
        for (var i = 0; i < partial.Length; i++) partial[i] = -1;
        for (var i = 0; i < Questions.Count; i++) if (Questions[i].Dimension == "EI") partial[i] = 0;
        Expect("仅答一维", "ESTJ", Score(partial), failures);

        LastSelfTest = failures.Count == 0 ? "OK" : "FAIL: " + string.Join("; ", failures);
        return LastSelfTest;
    }

    private static void Expect(string name, string expected, string actual, List<string> failures)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            failures.Add($"{name} 期望 {expected} 实际 {actual}");
    }
}
