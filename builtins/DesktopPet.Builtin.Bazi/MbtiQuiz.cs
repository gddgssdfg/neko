using System.Text;

namespace DesktopPet.Builtin.Bazi;

/// <summary>一道人格测试题（二选一）。<see cref="OptionA"/> 指向该维度的第一个极，<see cref="OptionB"/> 指向第二个极。</summary>
public sealed record MbtiQuestion(string Id, string Dimension, string Text, string OptionA, string OptionB);

/// <summary>
/// 16 型人格问卷（本地硬编码题库，<b>不联网</b>，定位「生日性格分析」）。
/// <para>v0.1.73：从 24 题精简版升级为 <b>完整 93 题</b>（E/I 23、S/N 26、T/F 24、J/P 20），
/// 按官方 MBTI 量表的四维度行为结构用自己的中文表述重写（非逐字照抄，规避版权）。</para>
/// <para>计分是<b>纯函数</b>：按维度统计 A/B 多数 → 拼 4 字母类型。平局（含整维未答）取该维度第一个极，保证确定性。</para>
/// </summary>
public static class MbtiQuiz
{
    /// <summary>四个维度，顺序 = 类型字母顺序（E/I、S/N、T/F、J/P）。</summary>
    public static string[] Dimensions { get; } = { "EI", "SN", "TF", "JP" };

    /// <summary>最近一次 <see cref="SelfCheck"/> 的结果（"OK" 或 "FAIL: ..."）。供加载器回归读取。</summary>
    public static string LastSelfTest { get; private set; } = "未运行";

    /// <summary>题库（93 题；EI=23、SN=26、TF=24、JP=20，按维度分块顺序排列）。</summary>
    public static IReadOnlyList<MbtiQuestion> Questions { get; } = new List<MbtiQuestion>
    {
        // ================= E / I（23 题）—— 能量来源：向外社交 vs 向内独处 =================
        new("EI1",  "EI", "忙碌一周后，你更想怎么恢复精力？", "约朋友聚一聚", "自己安静待着"),
        new("EI2",  "EI", "在热闹的聚会里，你通常：", "主动和陌生人搭话", "和熟悉的人小范围聊"),
        new("EI3",  "EI", "遇到烦心事，你更倾向：", "找人说出来", "自己先消化"),
        new("EI4",  "EI", "长时间社交之后，你会：", "觉得更有精神", "需要独处恢复"),
        new("EI5",  "EI", "表达想法时，你习惯：", "边说边想", "想清楚了再说"),
        new("EI6",  "EI", "到了新环境，你一般：", "很快融入进去", "先观察再行动"),
        new("EI7",  "EI", "电话和短信，你更喜欢：", "直接打电话聊", "发文字慢慢说"),
        new("EI8",  "EI", "你的朋友圈子：", "广而多，认识很多人", "小而精，深交几个"),
        new("EI9",  "EI", "开会讨论时，你：", "抢着先发言", "听别人说完再开口"),
        new("EI10", "EI", "一个人独处久了，你会：", "有点闷，想找人", "挺享受，不觉得无聊"),
        new("EI11", "EI", "认识新朋友时，你：", "很快热络起来", "需要时间慢慢熟"),
        new("EI12", "EI", "你的精力更像：", "充电来自与人相处", "充电来自独处"),
        new("EI13", "EI", "被突然拉进一个陌生饭局，你：", "很快就放开了", "大部分时间安静坐着"),
        new("EI14", "EI", "做决定前，你更想：", "找人聊聊听听意见", "自己反复想清楚"),
        new("EI15", "EI", "你说话的语速和音量：", "偏快偏响", "偏慢偏轻"),
        new("EI16", "EI", "团队活动里，你更愿意：", "当活跃气氛的那个", "当幕后支持的那个"),
        new("EI17", "EI", "放假时，你更想：", "安排满行程出去玩", "宅着休息或做自己的事"),
        new("EI18", "EI", "遇到难题，你的第一反应：", "找人讨论", "自己研究"),
        new("EI19", "EI", "你更害怕：", "长时间没人说话", "长时间被迫社交"),
        new("EI20", "EI", "在多人场合，你的存在感：", "比较强，容易成为焦点", "比较低调，不想被注意"),
        new("EI21", "EI", "聊天时你更喜欢：", "你来我往聊不停", "有来有回但会留白"),
        new("EI22", "EI", "你的周末更常是：", "约了朋友出门", "一个人待着或只约一两人"),
        new("EI23", "EI", "总的来说，你更像：", "外向的人", "内向的人"),

        // ================= S / N（26 题）—— 信息获取：具体现实 vs 抽象联想 =================
        new("SN1",  "SN", "你更相信：", "亲眼所见的事实", "直觉和联想"),
        new("SN2",  "SN", "学新东西时，你偏好：", "具体的步骤和例子", "先理解整体框架"),
        new("SN3",  "SN", "你更关注：", "当下的细节", "未来的可能性"),
        new("SN4",  "SN", "描述一件事，你倾向：", "如实讲清经过", "讲它意味着什么"),
        new("SN5",  "SN", "你更容易记住：", "具体的信息", "整体的印象"),
        new("SN6",  "SN", "做判断时，你更依赖：", "过往的经验", "当下的灵感"),
        new("SN7",  "SN", "读书时你更喜欢：", "情节扎实、有细节的", "有隐喻、能联想的"),
        new("SN8",  "SN", "你更擅长：", "处理眼前的实际问题", "想象未来的各种可能"),
        new("SN9",  "SN", "别人觉得你：", "踏实、注重实际", "有点天马行空"),
        new("SN10", "SN", "看说明书时，你：", "一步一步照着做", "扫一眼就上手试"),
        new("SN11", "SN", "你更欣赏的人：", "把事做扎实的人", "有新点子的人"),
        new("SN12", "SN", "旅行时你更在意：", "行程、路线、预算这些具体安排", "新鲜感和随性的探索"),
        new("SN13", "SN", "一个故事里，你更被吸引的是：", "情节和细节", "主题和寓意"),
        new("SN14", "SN", "你思考问题时：", "从事实出发", "从可能性出发"),
        new("SN15", "SN", "你更相信自己的：", "眼睛看到的", "心里感觉到的"),
        new("SN16", "SN", "学习时你更想先知道：", "这个怎么用", "这个为什么"),
        new("SN17", "SN", "你对「脑洞大开」的想法：", "觉得太飘，不太实际", "很感兴趣，越想越兴奋"),
        new("SN18", "SN", "回忆过去，你记得更清楚的是：", "当时发生了什么", "当时的感受和意义"),
        new("SN19", "SN", "你更常想到：", "现在手头的事", "未来想做的事"),
        new("SN20", "SN", "解一道题，你：", "套用学过的方法", "找新的思路"),
        new("SN21", "SN", "你更喜欢的工作：", "流程清晰、有据可依", "有创意空间、能发挥想象"),
        new("SN22", "SN", "别人给你建议，你更听得进：", "具体可行的步骤", "方向性的启发"),
        new("SN23", "SN", "你的思维更像：", "一条线，聚焦", "一张网，发散"),
        new("SN24", "SN", "面对一堆信息，你更擅长：", "抓取和整理细节", "提炼出背后的规律"),
        new("SN25", "SN", "你更认同：", "眼见为实", "事出有因，未必眼见"),
        new("SN26", "SN", "总的来说，你更偏向：", "务实", "务虚"),

        // ================= T / F（24 题）—— 决策依据：逻辑客观 vs 感受价值 =================
        new("TF1",  "TF", "做决定时你更看重：", "逻辑与公平", "感受与和谐"),
        new("TF2",  "TF", "朋友向你倾诉，你会先：", "帮他分析问题", "先安慰他的情绪"),
        new("TF3",  "TF", "评价一个方案，你更在意：", "是否合理", "是否照顾到人"),
        new("TF4",  "TF", "与人意见不合时，你：", "就事论事把理讲清", "尽量避免伤了和气"),
        new("TF5",  "TF", "你更希望别人说你：", "讲道理", "有同理心"),
        new("TF6",  "TF", "面对规则，你倾向：", "规则优先", "人情优先"),
        new("TF7",  "TF", "批评别人时，你：", "直接指出问题", "委婉、顾虑对方感受"),
        new("TF8",  "TF", "你更看重一个人的：", "能力", "人品和善意"),
        new("TF9",  "TF", "做选择时，你更依赖：", "利弊分析", "内心喜不喜欢"),
        new("TF10", "TF", "听到一个坏消息，你第一反应：", "想该怎么解决", "先关注受影响的人"),
        new("TF11", "TF", "争论时你更在意：", "谁更有道理", "别伤了感情"),
        new("TF12", "TF", "你更擅长：", "冷静客观地分析", "设身处地地理解"),
        new("TF13", "TF", "做决定后，你更可能因为什么后悔：", "结果不够好", "伤害了某个人"),
        new("TF14", "TF", "别人评价你：", "理性、果断", "温和、体贴"),
        new("TF15", "TF", "你更认同：", "对就是对，错就是错", "凡事有灰色地带"),
        new("TF16", "TF", "团队里你更常扮演：", "指出问题的那个", "调和气氛的那个"),
        new("TF17", "TF", "买东西时你更看：", "性价比和参数", "喜不喜欢、感觉对不对"),
        new("TF18", "TF", "安慰朋友时，你更会：", "给建议帮他想办法", "陪着、理解他的情绪"),
        new("TF19", "TF", "你更尊重：", "讲原则的人", "重情义的人"),
        new("TF20", "TF", "做判断时，你会：", "先冷静，再下结论", "容易被情绪带动"),
        new("TF21", "TF", "你更在意自己的：", "观点是否站得住", "行为是否让人舒服"),
        new("TF22", "TF", "处理矛盾，你倾向：", "当面说清楚", "先缓缓，别激化"),
        new("TF23", "TF", "你更欣赏的领导：", "能力强、对事不对人", "关心人、有人情味"),
        new("TF24", "TF", "总的来说，你更偏向：", "理性", "感性"),

        // ================= J / P（20 题）—— 生活组织：计划有序 vs 灵活随性 =================
        new("JP1",  "JP", "出行之前，你通常：", "提前把行程做好", "到了再随机应变"),
        new("JP2",  "JP", "面对截止日期，你：", "早早完成", "临近了才发力"),
        new("JP3",  "JP", "你的桌面或房间通常：", "井井有条", "随性自在"),
        new("JP4",  "JP", "计划临时有变时，你：", "会有点不自在", "觉得也没关系"),
        new("JP5",  "JP", "你更喜欢：", "事情早点定下来", "保留更多选择"),
        new("JP6",  "JP", "一天结束时，你倾向：", "按计划把清单做完", "顺其自然做到哪算哪"),
        new("JP7",  "JP", "做项目时，你：", "先定计划再动手", "边做边调整"),
        new("JP8",  "JP", "你的时间观念：", "很守时，喜欢提前", "比较灵活，差不多就行"),
        new("JP9",  "JP", "面对多个任务，你：", "排好优先级逐个清", "想到哪个做哪个"),
        new("JP10", "JP", "你更喜欢：", "确定性", "可能性"),
        new("JP11", "JP", "购物时你：", "提前列好清单", "逛到啥买啥"),
        new("JP12", "JP", "你的日程本/备忘录：", "记得清清楚楚", "基本不用或很随意"),
        new("JP13", "JP", "旅行回来，你的行李：", "当天就收拾归位", "可能放几天才收拾"),
        new("JP14", "JP", "你更喜欢的生活：", "有规律、可预期", "随性、有惊喜"),
        new("JP15", "JP", "面对一个决定，你：", "尽快定下来好安心", "能拖就拖，想留余地"),
        new("JP16", "JP", "你更擅长：", "把事收尾做完", "不断地开新头"),
        new("JP17", "JP", "突然空出半天，你：", "会想找点计划内的事做", "随意打发，干什么都行"),
        new("JP18", "JP", "你更讨厌：", "事情悬而未决", "被日程绑得太死"),
        new("JP19", "JP", "工作方式上，你更接近：", "先规划，后执行", "边摸索，边推进"),
        new("JP20", "JP", "总的来说，你更偏向：", "有条理、按计划", "随性、顺其自然"),
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
            sb.Append(aCount[dim] >= bCount[dim] ? poleA : poleB);   // 平局取首极
        }
        return sb.ToString();
    }

    /// <summary>某维度题数。</summary>
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

    /// <summary>16 型类型列表（按 Describe 顺序），供下拉直接填写。</summary>
    public static IReadOnlyList<string> AllTypes { get; } = new[]
    {
        "INTJ", "INTP", "ENTJ", "ENTP",
        "INFJ", "INFP", "ENFJ", "ENFP",
        "ISTJ", "ISFJ", "ESTJ", "ESFJ",
        "ISTP", "ISFP", "ESTP", "ESFP",
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
    /// 自检：总题数 93、四维 EI=23/SN=26/TF=24/JP=20；全选 A → ESTJ、全选 B → INFP；
    /// 维度间独立（部分选 B 只影响对应字母）；平局取第一个极。
    /// </summary>
    public static string SelfCheck()
    {
        var failures = new List<string>();

        if (Questions.Count != 93)
            failures.Add($"总题数 {Questions.Count} 应为 93");
        var expectedCount = new Dictionary<string, int> { ["EI"] = 23, ["SN"] = 26, ["TF"] = 24, ["JP"] = 20 };
        foreach (var dim in Dimensions)
        {
            var n = CountOf(dim);
            if (n != expectedCount[dim]) failures.Add($"维度 {dim} 题数 {n} 应为 {expectedCount[dim]}");
        }

        // 全选 A / 全选 B
        Expect("全选A", "ESTJ", Score(Enumerable.Repeat(0, Questions.Count).ToList()), failures);
        Expect("全选B", "INFP", Score(Enumerable.Repeat(1, Questions.Count).ToList()), failures);

        // 维度独立：SN 与 JP 全选 B（→N/P），EI 与 TF 全选 A（→E/T） ⇒ ENTP
        var mixed = Questions.Select(q => q.Dimension is "SN" or "JP" ? 1 : 0).ToList();
        Expect("维度独立", "ENTP", Score(mixed), failures);

        // 平局：EI 前 11 题选 A、后 11 题选 B、最后 1 题未答（11:11）→ 取第一个极 E；其余全 A ⇒ ESTJ
        var tie = new int[Questions.Count];
        var eiIdx = 0;
        for (var i = 0; i < Questions.Count; i++)
        {
            if (Questions[i].Dimension != "EI") { tie[i] = 0; continue; }
            tie[i] = eiIdx < 11 ? 0 : (eiIdx < 22 ? 1 : -1);
            eiIdx++;
        }
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
