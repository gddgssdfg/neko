using System.Reflection;
using System.Text;
using System.Text.Json;
using DesktopPet.Builtin.Bazi;
using DesktopPet.Core;

namespace BaziJsonDiff;

/// <summary>
/// QA 独立证伪夹具（software-qa-engineer-2 / v0.1.82）。
/// <para>
/// 目标：打破工程师「手写 <c>Utf8JsonWriter</c> 与旧版 STJ 反射默认<b>逐字节一致</b>」的强断言，
/// 并独立验证 <c>ChatHistory</c> 向后兼容 / 畸形输入健壮性。
/// </para>
/// <para>
/// 只做「读」——通过反射调用插件内部 <c>DeepSeekClient.BuildRequestBody</c>（internal）与
/// <c>ChatHistory.Serialize</c>（private）；旧版行为在本夹具内按 git 历史<b>逐字重建</b>，不修改插件源码。
/// </para>
/// </summary>
internal static class Program
{
    private static int _fail;
    private static int _pass;

    private static void Ok(string m) { _pass++; Console.WriteLine("PASS: " + m); }
    private static void Bad(string m) { _fail++; Console.WriteLine("FAIL: " + m); }
    private static void Check(bool ok, string okMsg, string failMsg) { if (ok) Ok(okMsg); else Bad(failMsg); }

    /// <summary>最小内存版 <see cref="IPluginStorage"/>（模拟插件存储）。</summary>
    private sealed class MemoryStorage : IPluginStorage
    {
        private readonly Dictionary<string, object?> _d = new();
        public T? Get<T>(string key) => _d.TryGetValue(key, out var v) ? (T?)v : default;
        public void Set<T>(string key, T value) => _d[key] = value;
        public void Remove(string key) => _d.Remove(key);
        public bool Contains(string key) => _d.ContainsKey(key);
    }

    private static readonly MethodInfo BuildRequestBodyMi =
        typeof(DeepSeekClient).GetMethod("BuildRequestBody",
            BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo ChatSerializeMi =
        typeof(ChatHistory).GetMethod("Serialize",
            BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>重建旧版 BuildRequestBody：匿名类型 + JsonSerializer.Serialize（默认 options）。</summary>
    private static string OldBuildRequestBody(IEnumerable<ChatMessage> msgs)
    {
        var payload = new
        {
            model = "deepseek-chat",
            messages = msgs.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            stream = false,
        };
        return JsonSerializer.Serialize(payload);
    }

    private static string ManualBuildRequestBody(IReadOnlyList<ChatMessage> msgs)
        => (string)BuildRequestBodyMi.Invoke(null, new object[] { msgs })!;

    private static string ManualChatSerialize(List<ChatMessage> list)
        => (string)ChatSerializeMi.Invoke(null, new object[] { list })!;

    private static void Main()
    {
        Console.WriteLine("=== Bazi 手写 JSON vs 旧版 STJ 逐字节比对（QA 独立夹具）===");

        BuildRequestBody_BadChars();
        BuildRequestBody_Fuzz();
        ChatHistory_Serialize_BadChars();
        ChatHistory_Serialize_Fuzz();
        ChatHistory_Append_RawByteIdentical();
        ChatHistory_BackwardCompat();
        ChatHistory_Malformed();
        ChatHistory_Append_Boundary();

        BaziProfile_Save_ByteIdentical();
        BaziProfile_Save_Fuzz();
        BaziProfile_ApiKey_Semantics();
        BaziProfile_BackwardCompat();
        BaziProfile_Malformed();

        Console.WriteLine();
        Console.WriteLine(_fail == 0
            ? $"=== 全部通过（{_pass} 项）==="
            : $"=== {_fail} 项失败（通过 {_pass}）===");
        Environment.Exit(_fail == 0 ? 0 : 1);
    }

    // ---------------- 1. BuildRequestBody：需要转义的字符 ----------------

    private static void BuildRequestBody_BadChars()
    {
        Console.WriteLine();
        Console.WriteLine("--- 1. BuildRequestBody 边界字符逐字节比对 ---");

        var cases = new (string role, string content)[]
        {
            ("user", "你好，世界"),
            ("user", "he said \"hi\" and \\ escaped"),
            ("user", "C:\\Users\\a\\b.txt"),
            ("user", "line1\nline2\r\n\ttabbed"),
            ("user", "<script>alert('x'+1)</script>"),
            ("user", "emoji 🐱😺🎉"),
            ("user", "ctl:\u0000\u0001\u001F\u007F:"),
            ("user", "slash/and&amp'plus+back`tick"),
            ("user", "\u2028\u2029 line/paragraph sep"),
            ("user", "non-BMP: \U0001F600 \U0001F4A9"),
            ("user", "lone-surrogate:\uD800:"),
            ("user", ""),
            ("user", new string('x', 20000)),   // 超长
            ("system", "你是命理助手\"><x>"),
            ("assistant", "换行\n与引号\"结束"),
        };

        int diffs = 0;
        foreach (var (role, content) in cases)
        {
            var msgs = new List<ChatMessage> { new(role, content) };
            var manual = ManualBuildRequestBody(msgs);
            var old = OldBuildRequestBody(msgs);
            if (manual != old)
            {
                diffs++;
                Bad($"BuildRequestBody 差异 role={role} content={Show(content)}\n" +
                    $"      手写 = {Show(manual)}\n" +
                    $"      旧版 = {Show(old)}");
            }
        }
        Check(diffs == 0,
            $"BuildRequestBody 边界字符 {cases.Length} 例全部逐字节一致",
            $"BuildRequestBody 有 {diffs}/{cases.Length} 例不一致");
    }

    // ---------------- 2. BuildRequestBody：随机差分模糊 ----------------

    private static void BuildRequestBody_Fuzz()
    {
        Console.WriteLine();
        Console.WriteLine("--- 2. BuildRequestBody 随机差分模糊（20000 例）---");

        var rnd = new Random(20260918);
        int diffs = 0;
        string? firstDiff = null;

        for (int i = 0; i < 20000; i++)
        {
            int n = rnd.Next(0, 3);
            var msgs = new List<ChatMessage>();
            for (int k = 0; k < n; k++)
                msgs.Add(new ChatMessage(RandomRole(rnd), RandomString(rnd, 12)));

            var manual = ManualBuildRequestBody(msgs);
            var old = OldBuildRequestBody(msgs);
            if (manual != old)
            {
                diffs++;
                if (firstDiff is null)
                    firstDiff = $"手写 = {Show(manual)}\n      旧版 = {Show(old)}";
            }
        }
        Check(diffs == 0,
            "BuildRequestBody 随机模糊 20000 例全部逐字节一致",
            $"BuildRequestBody 随机模糊有 {diffs} 例不一致；首个：\n      {firstDiff}");
    }

    // ---------------- 3. ChatHistory.Serialize：边界字符 ----------------

    private static void ChatHistory_Serialize_BadChars()
    {
        Console.WriteLine();
        Console.WriteLine("--- 3. ChatHistory.Serialize 边界字符逐字节比对 ---");

        var list = new List<ChatMessage>
        {
            new("user", "你好 \"q\" \\n"),
            new("assistant", "<b>&amp;</b>\n\ttail"),
            new("user", "emoji 🐱 \u0000 \u2028 \U0001F4A9"),
            new("assistant", ""),
        };

        var manual = ManualChatSerialize(list);
        var old = JsonSerializer.Serialize(list);
        Check(manual == old,
            "ChatHistory.Serialize 边界字符逐字节一致",
            $"ChatHistory.Serialize 不一致：\n      手写 = {Show(manual)}\n      旧版 = {Show(old)}");
    }

    private static void ChatHistory_Serialize_Fuzz()
    {
        Console.WriteLine();
        Console.WriteLine("--- 4. ChatHistory.Serialize 随机差分模糊（20000 例）---");

        var rnd = new Random(777);
        int diffs = 0;
        string? firstDiff = null;
        for (int i = 0; i < 20000; i++)
        {
            int n = rnd.Next(0, 4);
            var list = new List<ChatMessage>();
            for (int k = 0; k < n; k++)
                list.Add(new ChatMessage(RandomRole(rnd), RandomString(rnd, 16)));

            var manual = ManualChatSerialize(list);
            var old = JsonSerializer.Serialize(list);
            if (manual != old)
            {
                diffs++;
                if (firstDiff is null)
                    firstDiff = $"手写 = {Show(manual)}\n      旧版 = {Show(old)}";
            }
        }
        Check(diffs == 0,
            "ChatHistory.Serialize 随机模糊 20000 例全部逐字节一致",
            $"ChatHistory.Serialize 有 {diffs} 例不一致；首个：\n      {firstDiff}");
    }

    // ---------------- 5. Append 落盘 = 旧版 STJ 默认输出 ----------------

    private static void ChatHistory_Append_RawByteIdentical()
    {
        Console.WriteLine();
        Console.WriteLine("--- 5. Append 落盘字节 = 旧版 JsonSerializer.Serialize(List<ChatMessage>) ---");

        var st = new MemoryStorage();
        ChatHistory.Append(st, "q1", "a1");
        var raw = st.Get<string>(ChatHistory.StorageKey);
        var expected = JsonSerializer.Serialize(new List<ChatMessage>
        {
            ChatMessage.OfUser("q1"),
            ChatMessage.OfAssistant("a1"),
        });
        Check(raw == expected,
            $"Append 落盘字节 === 旧版 STJ 默认（{Show(raw)}）",
            $"Append 落盘字节 ≠ 旧版：\n      实际 = {Show(raw)}\n      期望 = {Show(expected)}");
    }

    // ---------------- 6. 向后兼容：旧版存档可读 ----------------

    private static void ChatHistory_BackwardCompat()
    {
        Console.WriteLine();
        Console.WriteLine("--- 6. 向后兼容：旧版 STJ 存档（含转义）读回 ---");

        // 用「旧版写法」生成存档：JsonSerializer.Serialize(List<ChatMessage>)，内容含需转义字符。
        var oldList = new List<ChatMessage>
        {
            new("user", "引号\"反斜杠\\换行\n中英🐱"),
            new("assistant", "<x>&amp;\ttab"),
            new("system", "应被过滤"),           // 非 user/assistant → 旧/新都应丢
            new("user", ""),                     // 空内容 → 丢弃
        };
        var oldJson = JsonSerializer.Serialize(oldList);   // 完全等同旧版落盘

        var st = new MemoryStorage();
        st.Set(ChatHistory.StorageKey, oldJson);

        var loaded = ChatHistory.Load(st);
        Check(loaded.Count == 2,
            $"旧版存档读回 {loaded.Count} 条（保留 2 条 user/assistant）",
            $"旧版存档读回 {loaded.Count} 条（期望 2）");
        if (loaded.Count >= 1)
            Check(loaded[0].Content == "引号\"反斜杠\\换行\n中英🐱",
                "旧版存档第 1 条内容逐字符还原（转义正确解析）",
                $"旧版存档第 1 条内容不符：{Show(loaded[0].Content)}");
        if (loaded.Count >= 2)
            Check(loaded[1].Content == "<x>&amp;\ttab",
                "旧版存档第 2 条内容逐字符还原",
                $"旧版存档第 2 条内容不符：{Show(loaded[1].Content)}");
    }

    // ---------------- 7. 畸形 / 缺字段 / 空 / 超大 输入不崩 ----------------

    private static void ChatHistory_Malformed()
    {
        Console.WriteLine();
        Console.WriteLine("--- 7. 畸形 / 缺字段 / 空 输入健壮性 ---");

        var cases = new (string name, string? json, int expectedCount)[]
        {
            ("null 值", null, 0),
            ("空串", "", 0),
            ("纯空白", "   \n\t ", 0),
            ("非 JSON", "not a json ][ {", 0),
            ("JSON object（非数组）", "{}", 0),
            ("JSON null 字面量", "null", 0),
            ("JSON number", "12345", 0),
            ("空数组", "[]", 0),
            ("数组含 null 元素", "[null]", 0),
            ("缺 Content", "[{\"Role\":\"user\"}]", 0),
            ("缺 Role", "[{\"Content\":\"x\"}]", 0),
            ("Content 非字符串", "[{\"Role\":\"user\",\"Content\":123}]", 0),
            ("content 小写键（旧版不会写）", "[{\"role\":\"user\",\"content\":\"x\"}]", 0),
            ("数组含非对象元素", "[1,\"x\",true]", 0),
            ("正常 1 条", "[{\"Role\":\"user\",\"Content\":\"hi\"}]", 1),
            ("scalar Role", "[{\"Role\":true,\"Content\":\"x\"}]", 0),
        };

        foreach (var (name, json, expected) in cases)
        {
            var st = new MemoryStorage();
            if (json is not null) st.Set(ChatHistory.StorageKey, json);
            try
            {
                var got = ChatHistory.Load(st);
                Check(got.Count == expected,
                    $"{name} → Load 返回 {got.Count} 条（不崩，符合期望 {expected}）",
                    $"{name} → Load 返回 {got.Count} 条（期望 {expected}）");
            }
            catch (Exception ex)
            {
                Bad($"{name} → Load 抛异常 {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Append 也必须在畸形既有存档上不崩
        try
        {
            var st = new MemoryStorage();
            st.Set(ChatHistory.StorageKey, "}\u0000bad{");
            ChatHistory.Append(st, "q", "a");
            var after = ChatHistory.Load(st);
            Check(after.Count == 2,
                $"畸形既有存档上 Append 后 Load 得 {after.Count} 条（自愈为 2）",
                $"畸形既有存档上 Append 后 Load 得 {after.Count} 条（期望 2）");
        }
        catch (Exception ex)
        {
            Bad($"畸形存档上 Append 抛异常 {ex.GetType().Name}: {ex.Message}");
        }

        // 超大存档（100000 条）——不得栈溢出 / OOM / 抛
        try
        {
            var sb = new StringBuilder(4_000_000);
            sb.Append('[');
            for (int i = 0; i < 100_000; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"Role\":\"user\",\"Content\":\"m").Append(i).Append("\"}");
            }
            sb.Append(']');
            var st = new MemoryStorage();
            st.Set(ChatHistory.StorageKey, sb.ToString());
            var got = ChatHistory.Load(st);
            Check(got.Count == ChatHistory.MaxMessages,
                $"超大存档（100000 条）→ 滚动截断到 {got.Count}（MaxMessages={ChatHistory.MaxMessages}），不崩",
                $"超大存档 → 得到 {got.Count} 条（期望 {ChatHistory.MaxMessages}）");
        }
        catch (Exception ex)
        {
            Bad($"超大存档 Load 抛异常 {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---------------- 8. Append 滚动边界 ----------------

    private static void ChatHistory_Append_Boundary()
    {
        Console.WriteLine();
        Console.WriteLine("--- 8. Append 滚动截断边界 ---");

        var st = new MemoryStorage();
        for (int i = 0; i < 15; i++) ChatHistory.Append(st, $"q{i}", $"a{i}");
        var loaded = ChatHistory.Load(st);
        Check(loaded.Count == ChatHistory.MaxMessages,
            $"15 轮 Append 后保留 {loaded.Count} 条（上限 {ChatHistory.MaxMessages}）",
            $"15 轮 Append 后保留 {loaded.Count} 条（期望 {ChatHistory.MaxMessages}）");

        if (loaded.Count >= 2)
        {
            Check(loaded[^2].Content == "q14" && loaded[^1].Content == "a14",
                $"滚动保留的是最近一轮（q14/a14）",
                $"滚动保留的不是最近一轮：{Show(loaded[^2].Content)} / {Show(loaded[^1].Content)}");
        }

        // Append 落盘字节应始终等于「旧版对同一 List 的 Serialize」
        var raw = st.Get<string>(ChatHistory.StorageKey);
        var expected = JsonSerializer.Serialize(loaded);
        Check(raw == expected,
            "滚动截断后落盘字节仍 === 旧版 STJ 默认",
            $"滚动截断后落盘字节 ≠ 旧版：\n      实际 = {Show(raw)}\n      期望 = {Show(expected)}");
    }

    // ================= v0.1.82 Round2：BaziProfile 源生成 vs 旧版 STJ 反射 =================

    /// <summary>走**插件真实路径** Save（源生成 <c>BaziJsonContext.Default.BaziProfile</c>），读取落盘字符串。</summary>
    private static string ManualProfileSave(BaziProfile p)
    {
        var st = new MemoryStorage();
        p.Save(st);
        return st.Get<string>(BaziProfile.StorageKey) ?? "";
    }

    /// <summary>旧版路径：<c>JsonSerializer.Serialize(profile)</c>（默认反射 options）。</summary>
    private static string OldProfileSerialize(BaziProfile p) => JsonSerializer.Serialize(p);

    /// <summary>9. BaziProfile 源生成落盘字节 = 旧版反射（ApiKey=null 时确定性）。</summary>
    private static void BaziProfile_Save_ByteIdentical()
    {
        Console.WriteLine();
        Console.WriteLine("--- 9. BaziProfile 源生成落盘字节 vs 旧版 STJ 反射（边界样例）---");

        var cases = new BaziProfile[]
        {
            new(),                                                       // 全默认
            new() { Year = 1981, Month = 1, Day = 29, Hour = 23, Minute = 37, Gender = 0, ApiKey = null },
            new() { Year = 2000, Month = 12, Day = 31, IsLunar = true, LeapMonth = true, Gender = 1, Mbti = "INFP", MbtiEnabled = true, DailyBroadcastEnabled = true, BroadcastTime = "23:59" },
            new() { Mbti = "引号\"反斜杠\\换行\n中英🐱<x>&amp;", BroadcastTime = "00:00" },  // Mbti 含需转义字符
            new() { Hour = 0, Minute = 0, Gender = 0 },                  // 0 值非 null
            new() { Mbti = new string('Z', 5000), BroadcastTime = "12:34" }, // 超长
        };

        int diffs = 0;
        foreach (var p in cases)
        {
            var manual = ManualProfileSave(p);
            var old = OldProfileSerialize(p);
            if (manual != old)
            {
                diffs++;
                Bad($"BaziProfile 落盘字节差异：\n      源生成 = {Show(manual)}\n      旧反射 = {Show(old)}");
            }
        }
        Check(diffs == 0,
            $"BaziProfile 边界样例 {cases.Length} 例源生成落盘字节全部 === 旧版 STJ 反射",
            $"BaziProfile 有 {diffs}/{cases.Length} 例字节不一致");
    }

    /// <summary>10. BaziProfile 随机差分模糊（ApiKey 恒 null，保证可确定性比对）。</summary>
    private static void BaziProfile_Save_Fuzz()
    {
        Console.WriteLine();
        Console.WriteLine("--- 10. BaziProfile 随机差分模糊（20000 例）---");

        var rnd = new Random(31415926);
        int diffs = 0;
        string? firstDiff = null;

        for (int i = 0; i < 20000; i++)
        {
            var p = RandomProfile(rnd);
            var manual = ManualProfileSave(p);
            var old = OldProfileSerialize(p);
            if (manual != old)
            {
                diffs++;
                if (firstDiff is null)
                    firstDiff = $"源生成 = {Show(manual)}\n      旧反射 = {Show(old)}";
            }
        }
        Check(diffs == 0,
            "BaziProfile 随机模糊 20000 例源生成落盘字节全部 === 旧版 STJ 反射",
            $"BaziProfile 随机模糊有 {diffs} 例不一致；首个：\n      {firstDiff}");
    }

    /// <summary>11. ApiKey 语义：内存明文 → 落盘密文（enc: 前缀、无明文）→ 读回明文。</summary>
    private static void BaziProfile_ApiKey_Semantics()
    {
        Console.WriteLine();
        Console.WriteLine("--- 11. ApiKey 加密往返语义（DPAPI，非确定性故只比语义不比字节）---");

        const string plain = "sk-test-1234567890-abcd";
        var p = new BaziProfile { Year = 1990, Month = 6, Day = 15, ApiKey = plain };
        var stored = ManualProfileSave(p);

        Check(stored.Contains("\"ApiKey\":", StringComparison.Ordinal) && stored.Contains("enc:", StringComparison.Ordinal),
            "ApiKey 落盘带 enc: 前缀（已加密）",
            $"ApiKey 未加密落盘：{Show(stored)}");
        Check(!stored.Contains(plain, StringComparison.Ordinal),
            "ApiKey 落盘不含明文",
            $"ApiKey 落盘疑似含明文：{Show(stored)}");

        var st = new MemoryStorage();
        st.Set(BaziProfile.StorageKey, stored);
        var back = BaziProfile.Load(st);
        Check(back.ApiKey == plain,
            $"ApiKey 读回明文逐字符还原（{Show(plain)}）",
            $"ApiKey 读回不符：{Show(back.ApiKey ?? "<null>")}");

        // 空串 / null → 一律写 null（与旧版 EncryptedApiKeyConverter 语义一致）
        var empty = new BaziProfile { ApiKey = "" };
        Check(ManualProfileSave(empty).Contains("\"ApiKey\":null", StringComparison.Ordinal),
            "ApiKey = 空串 → 落盘 null",
            $"ApiKey = 空串 未落 null：{Show(ManualProfileSave(empty))}");
        var nul = new BaziProfile { ApiKey = null };
        Check(ManualProfileSave(nul).Contains("\"ApiKey\":null", StringComparison.Ordinal),
            "ApiKey = null → 落盘 null",
            $"ApiKey = null 未落 null：{Show(ManualProfileSave(nul))}");
    }

    /// <summary>12. 向后兼容：旧版反射写出的存档可读（含旧版明文 ApiKey）。</summary>
    private static void BaziProfile_BackwardCompat()
    {
        Console.WriteLine();
        Console.WriteLine("--- 12. BaziProfile 向后兼容（旧版 STJ 反射存档可读）---");

        // 旧版反射存档（字段全设，ApiKey=null）
        var old = new BaziProfile
        {
            Year = 1977, Month = 3, Day = 8, Hour = 14, Minute = 5,
            IsLunar = false, LeapMonth = false, Gender = 0,
            Mbti = "ENFP", MbtiEnabled = true, DailyBroadcastEnabled = true, BroadcastTime = "07:05",
        };
        var oldJson = JsonSerializer.Serialize(old);   // 完全等同旧版落盘

        var st = new MemoryStorage();
        st.Set(BaziProfile.StorageKey, oldJson);
        var loaded = BaziProfile.Load(st);
        Check(loaded.Year == 1977 && loaded.Month == 3 && loaded.Day == 8 && loaded.Hour == 14 &&
              loaded.Minute == 5 && loaded.Gender == 0 && loaded.Mbti == "ENFP" &&
              loaded.MbtiEnabled && loaded.DailyBroadcastEnabled && loaded.BroadcastTime == "07:05",
            "旧版反射存档逐字段读回一致",
            $"旧版反射存档读回不一致：Year={loaded.Year} Month={loaded.Month} Day={loaded.Day} " +
            $"Hour={loaded.Hour} Mbti={loaded.Mbti} BroadcastTime={loaded.BroadcastTime}");

        // 旧版**明文** ApiKey（无 enc: 前缀）→ 原样兼容
        var legacyPlain = "{\"Year\":1990,\"Month\":5,\"Day\":5,\"Gender\":1,\"BroadcastTime\":\"07:30\",\"ApiKey\":\"sk-legacy-plain\"}";
        var st2 = new MemoryStorage();
        st2.Set(BaziProfile.StorageKey, legacyPlain);
        var loaded2 = BaziProfile.Load(st2);
        Check(loaded2.ApiKey == "sk-legacy-plain",
            "旧版明文 ApiKey 原样兼容（无 enc: 前缀）",
            $"旧版明文 ApiKey 未兼容：{Show(loaded2.ApiKey ?? "<null>")}");
        Check(loaded2.Year == 1990 && loaded2.BroadcastTime == "07:30",
            "旧版明文存档其余字段一并读回",
            $"旧版明文存档字段不符：Year={loaded2.Year} BroadcastTime={loaded2.BroadcastTime}");
    }

    /// <summary>13. 畸形 / 缺字段 / 类型错 / 截断 / 超大 输入不崩，返回默认档案。</summary>
    private static void BaziProfile_Malformed()
    {
        Console.WriteLine();
        Console.WriteLine("--- 13. BaziProfile 畸形输入健壮性 ---");

        var cases = new (string name, string? json)[]
        {
            ("null 值", null),
            ("空串", ""),
            ("纯空白", "   \n\t "),
            ("非 JSON", "not a json ][ {"),
            ("JSON 对象为空", "{}"),
            ("JSON null 字面量", "null"),
            ("JSON number", "12345"),
            ("JSON 数组", "[]"),
            ("未知字段", "{\"Foo\":1}"),
            ("Year 类型错（字符串）", "{\"Year\":\"x\"}"),
            ("截断 JSON", "{\"Year\":198"),
            ("嵌套类型错", "{\"Hour\":\"zz\"}"),
            ("正常 1 条", "{\"Year\":2001,\"Month\":2,\"Day\":3,\"Gender\":1,\"BroadcastTime\":\"09:00\"}"),
        };

        foreach (var (name, json) in cases)
        {
            var st = new MemoryStorage();
            if (json is not null) st.Set(BaziProfile.StorageKey, json);
            try
            {
                var got = BaziProfile.Load(st);
                Check(got is not null,
                    $"{name} → Load 返回非 null（不崩）",
                    $"{name} → Load 返回 null");
            }
            catch (Exception ex)
            {
                Bad($"{name} → Load 抛异常 {ex.GetType().Name}: {ex.Message}");
            }
        }

        // 超大 Mbti 字段（5,000,000 字符）——不得 OOM / 抛
        try
        {
            var big = "{\"Year\":2000,\"BroadcastTime\":\"08:00\",\"Mbti\":\"" + new string('M', 5_000_000) + "\"}";
            var st = new MemoryStorage();
            st.Set(BaziProfile.StorageKey, big);
            var got = BaziProfile.Load(st);
            Check(got.Mbti is { Length: 5_000_000 },
                "超大 Mbti（5,000,000 字符）读回完整，不崩",
                $"超大 Mbti 读回长度 = {got.Mbti?.Length}");
        }
        catch (Exception ex)
        {
            Bad($"超大 Mbti Load 抛异常 {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static BaziProfile RandomProfile(Random rnd) => new()
    {
        Year = rnd.Next(0, 2200),
        Month = rnd.Next(0, 13),
        Day = rnd.Next(0, 32),
        Hour = rnd.Next(0, 3) == 0 ? null : rnd.Next(0, 24),
        Minute = rnd.Next(0, 3) == 0 ? null : rnd.Next(0, 60),
        IsLunar = rnd.Next(2) == 0,
        LeapMonth = rnd.Next(2) == 0,
        Gender = rnd.Next(0, 2),
        Mbti = rnd.Next(3) == 0 ? null : RandomString(rnd, 8),
        MbtiEnabled = rnd.Next(2) == 0,
        ApiKey = null,   // 恒 null：DPAPI 加密非确定性，无法逐字节比对
        DailyBroadcastEnabled = rnd.Next(2) == 0,
        BroadcastTime = rnd.Next(3) switch { 0 => "08:00", 1 => "23:59", _ => "00:00" },
    };

    // ---------------- 工具 ----------------

    private static string RandomRole(Random rnd) => rnd.Next(4) switch
    {
        0 => "user",
        1 => "assistant",
        2 => "system",
        _ => "tool",
    };

    private static string RandomString(Random rnd, int maxLen)
    {
        int len = rnd.Next(0, maxLen + 1);
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++)
        {
            switch (rnd.Next(12))
            {
                case 0: sb.Append((char)rnd.Next(0x20, 0x7F)); break;            // 可见 ASCII
                case 1: sb.Append((char)rnd.Next(0x00, 0x20)); break;            // 控制字符
                case 2: sb.Append((char)(0x4E00 + rnd.Next(0x2000))); break;     // CJK
                case 3: sb.Append('"'); break;
                case 4: sb.Append('\\'); break;
                case 5: sb.Append('\n'); break;
                case 6: sb.Append('\t'); break;
                case 7: sb.Append('<').Append('>').Append('&').Append('\'').Append('+').Append('`'); break;
                case 8: sb.Append('/'); break;
                case 9: sb.Append('\u2028'); break;
                case 10: sb.Append('\uD800'); break;                             // 孤立代理项
                default: sb.Append(char.ConvertFromUtf32(0x1F400 + rnd.Next(0x200))); break; // emoji（代理对）
            }
        }
        return sb.ToString();
    }

    private static string Show(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        sb.Append('"');
        foreach (var ch in s)
        {
            if (ch == '\n') sb.Append("\\n");
            else if (ch == '\r') sb.Append("\\r");
            else if (ch == '\t') sb.Append("\\t");
            else if (char.IsControl(ch)) sb.Append($"\\u{(int)ch:X4}");
            else sb.Append(ch);
        }
        sb.Append('"');
        var text = sb.ToString();
        return text.Length > 400 ? text[..400] + "…(len=" + s.Length + ")" : text;
    }
}
