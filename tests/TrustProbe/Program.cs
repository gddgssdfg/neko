using System.IO;
using System.Security.Cryptography;
using DesktopPet.Runtime;

namespace TrustProbe;

/// <summary>
/// QA 独立证伪夹具：内置插件信任清单（P0-4 T02 / v0.1.82 接线）。
/// <para>
/// 与 <c>tests/LoaderTest</c> 的覆盖互补，重点补测<b>缺失/损坏/未知 schema/缺字段/空文件/摘要前缀/大小写/重复 id</b>
/// 等此前未覆盖的状态，以及「清单不匹配时高危插件是否**一定**被拒载」这条安全底线。
/// 用合成高危夹具（manifest 声明 <c>other=["process"]</c>）+ 真实 SamplePlugin.dll。
/// </para>
/// </summary>
internal static class Program
{
    private static int _fail, _pass;
    private static void Ok(string m) { _pass++; Console.WriteLine("PASS: " + m); }
    private static void Bad(string m) { _fail++; Console.WriteLine("FAIL: " + m); }
    private static void Check(bool ok, string okMsg, string failMsg) { if (ok) Ok(okMsg); else Bad(failMsg); }

    private const string FxId = "com.qa.highrisk";
    private static string _fixtureDir = "";
    private static string _pluginsDir = "";
    private static string _manifestPath = "";
    private static string _entryPath = "";

    private static int Main(string[] args)
    {
        var repo = args.Length > 0 ? args[0] : "G:/lantetn-li/neko";
        Console.WriteLine("=== 内置插件信任清单 独立证伪（TrustProbe）===");

        var root = Path.Combine(Path.GetTempPath(), "qa-trust-" + Guid.NewGuid().ToString("N"));
        _pluginsDir = Path.Combine(root, "plugins");
        _fixtureDir = Path.Combine(_pluginsDir, FxId);
        Directory.CreateDirectory(_fixtureDir);

        try
        {
            if (!BuildFixture(repo)) return 2;

            // 前置：夹具确实被判为高危
            var declared = PermissionChecker.FromManifest(ManifestParser.Parse(_manifestPath));
            Check(PermissionChecker.IsHighRisk(declared),
                $"夹具被正确判为高危（{declared}）",
                $"夹具未被判高危（{declared}）→ 后续断言无意义");
            if (!PermissionChecker.IsHighRisk(declared)) return 2;

            // ---- 各种清单状态 ----
            Case("清单文件不存在", null,
                expectLoad: false, expectWarn: true);
            Case("清单内容非法 JSON", "not a json ][ {",
                expectLoad: false, expectWarn: true);
            Case("空文件（0 字节）", "",
                expectLoad: false, expectWarn: true);
            Case("schema=99（未来版本，应拒）", $"{{ \"schema\": 99, \"builtins\": [ {{ \"id\": \"{FxId}\" }} ] }}",
                expectLoad: false, expectWarn: true);
            Case("schema 缺失（=0，应拒）", $"{{ \"builtins\": [ {{ \"id\": \"{FxId}\" }} ] }}",
                expectLoad: false, expectWarn: true);
            Case("builtins=null（应不崩、拒载）", "{ \"schema\": 1, \"builtins\": null }",
                expectLoad: false, expectWarn: true);
            Case("builtins 非数组", "{ \"schema\": 1, \"builtins\": 123 }",
                expectLoad: false, expectWarn: true);
            Case("id 不在清单", $"{{ \"schema\": 1, \"builtins\": [ {{ \"id\": \"com.other\" }} ] }}",
                expectLoad: false, expectWarn: false);
            Case("id 空字符串", $"{{ \"schema\": 1, \"builtins\": [ {{ \"id\": \"   \" }} ] }}",
                expectLoad: false, expectWarn: false);

            // ---- 摘要绑定 ----
            Case("仅 id（未登记摘要）→ 设计允许按 id 信任", LockJson(idOnly: true),
                expectLoad: true, expectWarn: false);
            Case("manifest 摘要不匹配 → 拒", LockJson(manifestSha: new string('0', 64)),
                expectLoad: false, expectWarn: false);
            Case("manifest 摘要匹配 → 放行", LockJson(manifestSha: Sha256Hex(_manifestPath)),
                expectLoad: true, expectWarn: false);
            Case("entry 摘要不匹配（manifest 匹配）→ 拒",
                LockJson(manifestSha: Sha256Hex(_manifestPath), entrySha: new string('0', 64)),
                expectLoad: false, expectWarn: false);
            Case("manifest+entry 摘要均匹配 → 放行",
                LockJson(manifestSha: Sha256Hex(_manifestPath), entrySha: Sha256Hex(_entryPath)),
                expectLoad: true, expectWarn: false);
            Case("sha256: 前缀 + 摘要匹配 → 放行（Normalize 容忍前缀）",
                LockJson(manifestSha: "sha256:" + Sha256Hex(_manifestPath), entrySha: "SHA256:" + Sha256Hex(_entryPath).ToUpperInvariant()),
                expectLoad: true, expectWarn: false);
            Case("id 大小写不同（OrdinalIgnoreCase）→ 放行",
                LockJson(idOverride: FxId.ToUpperInvariant(), manifestSha: Sha256Hex(_manifestPath)),
                expectLoad: true, expectWarn: false);
            Case("重复 id：后者摘要错 → 拒（记录 last-wins 语义）",
                $"{{ \"schema\": 1, \"builtins\": [ {{ \"id\": \"{FxId}\", \"manifestSha256\": \"{Sha256Hex(_manifestPath)}\" }}, {{ \"id\": \"{FxId}\", \"manifestSha256\": \"{new string('0', 64)}\" }} ] }}",
                expectLoad: false, expectWarn: false);
            Case("未知额外字段（前向兼容）→ 放行",
                LockJson(manifestSha: Sha256Hex(_manifestPath), extra: "\"future\":\"x\""),
                expectLoad: true, expectWarn: false);

            // ---- 关键：Empty 信任集恒拒（安全兜底的可达性）----
            var empty = PluginTrustSet.Empty;
            Check(!empty.IsTrusted(FxId, _manifestPath, _entryPath, out var why),
                $"PluginTrustSet.Empty.IsTrusted 恒 false（{why}）",
                "PluginTrustSet.Empty 竟然信任了插件");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(_fail == 0 ? $"=== 全部通过（{_pass} 项）===" : $"=== {_fail} 项失败（通过 {_pass}）===");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>写一份清单到临时文件 → Load → 用返回的信任集驱动真实 PluginLoader.LoadOne。</summary>
    private static void Case(string name, string? lockContent, bool expectLoad, bool expectWarn)
    {
        var lockPath = Path.Combine(Path.GetTempPath(), "qa-lock-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            if (lockContent is not null)
                File.WriteAllText(lockPath, lockContent, new System.Text.UTF8Encoding(false));
            else
                lockPath = Path.Combine(Path.GetTempPath(), "qa-missing-" + Guid.NewGuid().ToString("N") + ".json");

            PluginTrustSet? trust = null;
            string? warn = null;
            Exception? ex = null;
            try { trust = PluginTrustSet.Load(lockPath, out warn); }
            catch (Exception e) { ex = e; }

            if (ex is not null)
            {
                Bad($"{name}: PluginTrustSet.Load 抛异常 {ex.GetType().Name}: {ex.Message}");
                return;
            }

            var warnOk = expectWarn ? !string.IsNullOrEmpty(warn) : string.IsNullOrEmpty(warn);

            bool loaded;
            string? err;
            var loader = new PluginLoader(_pluginsDir, trust);
            var lp = loader.LoadOne(_fixtureDir, out err);
            loaded = lp is not null;
            lp?.Dispose();

            if (loaded == expectLoad && warnOk)
            {
                Ok($"{name}: loaded={loaded}（符合期望），warn={(string.IsNullOrEmpty(warn) ? "无" : "有")}");
            }
            else
            {
                Bad($"{name}: loaded={loaded}（期望 {expectLoad}），warn={(string.IsNullOrEmpty(warn) ? "无" : "有")}（期望 {(expectWarn ? "有" : "无")}）；err={err}");
            }
        }
        finally
        {
            try { if (File.Exists(lockPath)) File.Delete(lockPath); } catch { }
        }
    }

    private static string LockJson(
        bool idOnly = false,
        string? manifestSha = null,
        string? entrySha = null,
        string? idOverride = null,
        string? extra = null)
    {
        var id = idOverride ?? FxId;
        var parts = new List<string> { $"\"id\": \"{id}\"" };
        if (!idOnly)
        {
            if (manifestSha is not null) parts.Add($"\"manifestSha256\": \"{manifestSha}\"");
            if (entrySha is not null) parts.Add($"\"entrySha256\": \"{entrySha}\"");
        }
        if (extra is not null) parts.Add(extra);
        return $"{{ \"schema\": 1, \"builtins\": [ {{ {string.Join(", ", parts)} }} ] }}";
    }

    private static bool BuildFixture(string repo)
    {
        var sampleOut = Path.Combine(repo, "samples", "SamplePlugin", "bin", "Debug", "net8.0-windows");
        if (!Directory.Exists(sampleOut))
        {
            Console.WriteLine($"SKIP: 缺少 SamplePlugin 产物 {sampleOut}（先 build samples/SamplePlugin）");
            return false;
        }
        foreach (var f in new[] { "SamplePlugin.dll", "SamplePlugin.deps.json", "DesktopPet.Sdk.dll", "DesktopPet.Core.dll" })
            if (File.Exists(Path.Combine(sampleOut, f)))
                File.Copy(Path.Combine(sampleOut, f), Path.Combine(_fixtureDir, f), true);

        _manifestPath = Path.Combine(_fixtureDir, "plugin.toml");
        File.WriteAllText(_manifestPath,
            $"[plugin]\nid = \"{FxId}\"\nname = \"信任通道 QA 夹具\"\nversion = \"0.1.0\"\napi = 1\n" +
            "entry = \"SamplePlugin.dll\"\nauthor = \"qa\"\n\n" +
            "[permissions]\nui = [\"register\"]\nother = [\"process\"]\n");
        _entryPath = Path.Combine(_fixtureDir, "SamplePlugin.dll");
        return true;
    }

    private static string Sha256Hex(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }
}
