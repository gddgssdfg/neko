using System.Diagnostics;
using System.Text.Json;
using DesktopPet.App.Core;

namespace DesktopPet.Tests.ConfigSaveProbe;

/// <summary>
/// 配置保存的**故障注入**验证（v0.1.86）。
///
/// 背景（真机试运行实测得到的证据）：
///   errors.log 出现 `[ERROR] 配置保存失败` + `IOException: 操作成功完成`
///   来自 `FileSystem.ReplaceFile` —— Win32 `ReplaceFile` 的经典怪象，通常是有进程/杀毒软件
///   正**短暂**持有目标文件；同一台机器 46 秒后的下一次保存就成功了（说明是**瞬时**失败）。
///   危害：失败一次 = 用户这次改的设置丢了，而旧实现还会删掉 tmp → 连恢复机会都没有。
///
/// 本探针要证明的事：
///   ① 目标文件被独占持有时，`ConfigManager.Save` **不会丢数据**（重试后最终写成功）；
///   ② 重试路径**确实被执行过**（日志里能看到"配置原子替换失败（第 N/3 次）"）——
///      否则这个测试就只是"碰巧没失败"，属于假绿。
///
/// 跑法：
///   bash /c/Users/Lantern/neko-dotnet-env.sh build tests/ConfigSaveProbe/ConfigSaveProbe.csproj -c Debug
///   tests/ConfigSaveProbe/bin/Debug/net8.0-windows/win-x64/ConfigSaveProbe.exe
/// 退出码 0 = 全部通过。
/// </summary>
internal static class Program
{
    private static int _fail;

    private static void Check(string name, Action body)
    {
        try
        {
            body();
            Console.WriteLine($"PASS: {name}");
        }
        catch (Exception ex)
        {
            _fail++;
            Console.WriteLine($"FAIL: {name} -> {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "neko-cfgsave-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var cfgPath = Path.Combine(root, "config.json");
        var logDir = Path.Combine(root, "logs");
        Log.Initialize("debug", logDir);          // debug：保证重试的 Warn 一定落盘

        Check("原子替换被独占持有阻挡时，Save 仍不丢数据（重试救回）", () =>
        {
            File.WriteAllText(cfgPath, JsonSerializer.Serialize(new AppConfig { Skin = "pixel" }));

            // 后台线程独占持有目标文件 100ms —— 模拟"杀软/其它进程正短暂持有"，
            // 让第 1 次 File.Replace 必然失败；随后放开，第 2 次重试应成功。
            using var held = new ManualResetEventSlim(false);
            var holder = new Thread(() =>
            {
                using (File.Open(cfgPath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    held.Set();
                    Thread.Sleep(100);
                }
            });
            holder.Start();
            held.Wait(2000);

            var sw = Stopwatch.StartNew();
            ConfigManager.Save(cfgPath, new AppConfig { Skin = "cat_pixel" });
            sw.Stop();

            holder.Join(2000);

            // ① 数据没丢：内容必须是新值
            var text = File.ReadAllText(cfgPath);
            if (!text.Contains("cat_pixel"))
                throw new Exception($"保存后内容不是新值（丢了这次修改）：{text[..Math.Min(120, text.Length)]}");

            // ② 不能留 tmp 残留（成功路径应当把 tmp 消费掉）
            if (File.Exists(cfgPath + ".tmp"))
                throw new Exception("保存成功后仍残留 config.json.tmp");

            // ③ 重试路径确实跑过 —— 否则本测试是假绿
            var logs = Directory.Exists(logDir)
                ? string.Join("\n", Directory.GetFiles(logDir, "*.log").Select(File.ReadAllText))
                : "";
            if (!logs.Contains("配置原子替换失败"))
                throw new Exception(
                    "日志里没有『配置原子替换失败』—— 说明第 1 次替换并未失败，本用例没真正测到重试路径（假绿）。" +
                    $"耗时 {sw.ElapsedMilliseconds}ms。日志片段：{logs[..Math.Min(300, logs.Length)]}");

            Console.WriteLine($"   耗时 {sw.ElapsedMilliseconds}ms；已命中重试路径 ✓");
        });

        Check("正常路径（无竞争）不产生任何 WARN，且原子替换生效", () =>
        {
            File.WriteAllText(cfgPath, JsonSerializer.Serialize(new AppConfig { Skin = "pixel" }));
            var before = File.ReadAllText(cfgPath);
            ConfigManager.Save(cfgPath, new AppConfig { Skin = "phoebe_hd", Scale = 3.0 });
            var after = File.ReadAllText(cfgPath);
            if (after == before) throw new Exception("内容未变化，保存没生效");
            if (!after.Contains("phoebe_hd")) throw new Exception("内容不是新值");
            if (File.Exists(cfgPath + ".tmp")) throw new Exception("残留 tmp");

            var logs = string.Join("\n", Directory.GetFiles(logDir, "*.log").Select(File.ReadAllText));
            // 注意：日志是累加的，这里只看"本次调用之后是否新增了 Warn"——用计数比较
            var warnBefore = CountOccurrences(logs, "[WARN]");
            ConfigManager.Save(cfgPath, new AppConfig { Skin = "pixel", Scale = 2.0 });
            var logs2 = string.Join("\n", Directory.GetFiles(logDir, "*.log").Select(File.ReadAllText));
            var warnAfter = CountOccurrences(logs2, "[WARN]");
            if (warnAfter != warnBefore)
                throw new Exception($"无竞争的正常保存不应新增 WARN（{warnBefore} → {warnAfter}）");
        });

        try { Directory.Delete(root, true); } catch { }

        Console.WriteLine(_fail == 0 ? "\n=== 全部通过 ===" : $"\n=== {_fail} 项失败 ===");
        Console.Out.Flush();
        Environment.Exit(_fail == 0 ? 0 : 1);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }
}
