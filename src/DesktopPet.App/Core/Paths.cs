using System;
using System.IO;

namespace DesktopPet.App.Core;

/// <summary>数据根目录的来源（诊断 / 迁移判定用）。</summary>
public enum DataRootKind
{
    /// <summary>由环境变量 <c>NEKO_DATA_DIR</c> 指定（测试隔离、高级用户）。</summary>
    EnvOverride,

    /// <summary>便携模式：程序根存在 <c>portable.flag</c> → 数据留在程序根的 <c>data/</c>。</summary>
    Portable,

    /// <summary>默认：<c>%AppData%\Neko</c>。</summary>
    AppData,

    /// <summary>极端兜底：取不到 <c>%AppData%</c> 时退回程序根（等价 v0.1.97 及以前的行为）。</summary>
    ExeDirFallback,
}

/// <summary>
/// 运行时目录布局（与 docs/architecture.md 的「分层架构 / 现状速览」一致）。
///
/// <para><b>v0.1.98 起「程序根」与「数据根」分离</b>——它们是两个不同概念：</para>
/// <code>
///   程序根目录 = exe 所在目录（【只】放随包分发、只读的内容）
///   ├── plugins/            用户可自由增删
///   ├── neko-assets/        雪碧图
///   └── builtins.lock.json  内置信任清单
///
///   数据根目录（【唯一的可写用户数据位置】）
///   ├── config.json         应用配置（含皮肤、日志级别、禁用插件等）
///   ├── logs/               文件日志
///   └── plugins/&lt;id&gt;/       插件私有数据（好感度 score.json 等）
/// </code>
///
/// <para><b>数据根解析顺序</b>（见 <see cref="DataRootKind"/>）：</para>
/// <list type="number">
///   <item>环境变量 <c>NEKO_DATA_DIR</c>（绝对或相对路径）——测试隔离与高级用户覆盖；</item>
///   <item>便携模式：程序根存在 <c>portable.flag</c> → 程序根 <c>data/</c>（绿色版显式选择）；</item>
///   <item>默认：<c>%AppData%\Neko</c>。</item>
/// </list>
///
/// <para><b>为什么默认放 %AppData%</b>（v0.1.98 的动机）：v0.1.97 及以前数据写在 exe 同级
/// <c>data/</c>，而开发/CI 会反复删除 <c>bin/</c>（构建产物）——于是「删 bin 之前必须先备份 data/」
/// 成了一条**必须靠人记住**的红线，实际发生过丢失风险。分离之后删构建产物不再触碰用户数据。</para>
///
/// <para>旧数据由 <see cref="MigrateLegacyData"/> 首次运行时**复制**过来（不删除源目录）。</para>
/// </summary>
public static class Paths
{
    /// <summary>数据根覆盖用的环境变量名。</summary>
    public const string DataDirEnvVarName = "NEKO_DATA_DIR";

    /// <summary>便携模式开关文件名（放在程序根即启用）。</summary>
    public const string PortableFlagFileName = "portable.flag";

    /// <summary>「已从程序根 data/ 迁移过」的标记文件名（放在数据根内）。</summary>
    public const string MigrationMarkerFileName = ".migrated-from-exe-dir";

    private const string MovedNoticeFileName = "MOVED-TO-APPDATA.txt";

    /// <summary>程序根目录（exe 所在目录）。只放素材 / 插件 / 信任清单这类随包内容。</summary>
    public static string Root { get; } =
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// 旧版（v0.1.97 及以前）的数据目录：程序根下的 <c>data/</c>。
    /// 现在是**只读的历史位置**——仅用于迁移与诊断，不再作为活动数据根（除非便携模式）。
    /// </summary>
    public static string LegacyData => Path.Combine(Root, "data");

    private static readonly (string Dir, DataRootKind Kind) Resolved = ResolveDataRoot();

    /// <summary>数据根目录（唯一的可写用户数据位置）。</summary>
    public static string Data => Resolved.Dir;

    /// <summary>数据根的来源，便于日志与排查「数据到底写到哪了」。</summary>
    public static DataRootKind DataKind => Resolved.Kind;

    /// <summary>是否处于便携模式（数据写在程序根 <c>data/</c>）。</summary>
    public static bool IsPortable => Resolved.Kind == DataRootKind.Portable;

    /// <summary>用户插件目录（程序根下）。</summary>
    public static string Plugins => Path.Combine(Root, "plugins");

    /// <summary>配置文件。</summary>
    public static string ConfigFile => Path.Combine(Data, "config.json");

    /// <summary>插件私有数据根。</summary>
    public static string PluginData => Path.Combine(Data, "plugins");

    /// <summary>日志目录（v0.1.61：&lt;数据根&gt;/logs）。</summary>
    public static string Logs => Path.Combine(Data, "logs");

    /// <summary>内置素材目录。</summary>
    public static string Assets => Path.Combine(Root, "neko-assets");

    /// <summary>某插件的数据目录。</summary>
    public static string PluginDataDir(string pluginId) => Path.Combine(PluginData, pluginId);

    private static (string Dir, DataRootKind Kind) ResolveDataRoot()
    {
        // 1) 环境变量覆盖（测试隔离最关键：探针若写进真实数据根，会覆盖用户的 config 与好感度）
        var env = Environment.GetEnvironmentVariable(DataDirEnvVarName);
        if (!string.IsNullOrWhiteSpace(env))
        {
            try
            {
                return (Path.GetFullPath(env), DataRootKind.EnvOverride);
            }
            catch (Exception)
            {
                // 环境变量给了一个非法路径：不因此挡住启动，落到后面的正常分支
            }
        }

        // 2) 便携模式（显式选择，避免"绿色版"用户数据散落在 AppData 里）
        if (File.Exists(Path.Combine(Root, PortableFlagFileName)))
            return (LegacyData, DataRootKind.Portable);

        // 3) 默认
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
            return (Path.Combine(appData, "Neko"), DataRootKind.AppData);

        // 4) 兜底（理论上到不了）
        return (LegacyData, DataRootKind.ExeDirFallback);
    }

    /// <summary>
    /// v0.1.98：把旧版「程序根/data」里的用户数据**复制**到默认数据根（<c>%AppData%\Neko</c>）。
    ///
    /// <para>只在「当前用默认数据根 且 旧目录存在 且 尚未迁移过」时执行一次；
    /// <b>永不删除源目录</b>（源目录里另留一张 <c>MOVED-TO-APPDATA.txt</c> 说明它已不是活动数据）。
    /// <c>logs/</c> 属可弃诊断信息，**不迁移**。已存在的目标文件不覆盖（幂等）。
    /// 任何异常都不外抛——迁移失败不能挡住启动。</para>
    /// </summary>
    /// <returns>发生迁移时返回说明文本；不适用或已迁移过则返回 <c>null</c>。</returns>
    public static string? MigrateLegacyData()
    {
        // EnvOverride（测试隔离）/ Portable / ExeDirFallback 都不迁移：它们要么本就指在旧目录，要么是刻意指定的
        if (DataKind != DataRootKind.AppData)
            return null;

        var legacy = LegacyData;
        if (!Directory.Exists(legacy))
            return null;
        if (File.Exists(Path.Combine(Data, MigrationMarkerFileName)))
            return null;

        var marker = Path.Combine(Data, MigrationMarkerFileName);
        try
        {
            Directory.CreateDirectory(Data);
            var copied = 0;

            // 顶层子目录：跳过 logs（可弃 + 可能很大）
            foreach (var dir in Directory.EnumerateDirectories(legacy))
            {
                var name = Path.GetFileName(dir);
                if (string.Equals(name, "logs", StringComparison.OrdinalIgnoreCase))
                    continue;
                copied += CopyTree(dir, Path.Combine(Data, name));
            }
            // 顶层文件（config.json 等）
            foreach (var file in Directory.EnumerateFiles(legacy))
            {
                var name = Path.GetFileName(file);
                if (name is MigrationMarkerFileName or MovedNoticeFileName)
                    continue;
                var target = Path.Combine(Data, name);
                if (File.Exists(target))
                    continue;
                try
                {
                    File.Copy(file, target);
                    copied++;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            File.WriteAllText(marker,
                $"migrated-from: {legacy}{Environment.NewLine}" +
                $"migrated-to:   {Data}{Environment.NewLine}" +
                $"migrated-at:   {DateTimeOffset.Now:o}{Environment.NewLine}" +
                $"files-copied:  {copied}{Environment.NewLine}");

            // 在旧目录留一张"已搬家"说明——否则下次看见 data/ 会误以为它还是活动数据
            try
            {
                File.WriteAllText(Path.Combine(legacy, MovedNoticeFileName),
                    "此目录已不是活动数据目录（自 v0.1.98 起）。" + Environment.NewLine +
                    $"用户数据现位于：{Data}" + Environment.NewLine +
                    "本目录内容仅作为迁移来源保留，可以安全删除。" + Environment.NewLine +
                    "要让数据留在程序根（便携模式），请在本目录的上一级放置 portable.flag 文件。" + Environment.NewLine);
            }
            catch (Exception) { }

            return $"旧数据目录已迁移到 {Data}（复制 {copied} 个文件；跳过 logs/；源目录保留未删；"
                 + $"数据根来源={DataKind}）";
        }
        catch (Exception ex)
        {
            return $"迁移旧数据目录失败（已忽略，继续使用 {Data}）：{ex.GetType().Name}: {ex.Message}";
        }
    }

    private static int CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        var copied = 0;

        foreach (var file in Directory.EnumerateFiles(source))
        {
            var dst = Path.Combine(target, Path.GetFileName(file));
            if (File.Exists(dst))
                continue;                      // 不覆盖：让迁移可重复执行
            try
            {
                File.Copy(file, dst);
                copied++;
            }
            catch (IOException) { }             // 单个文件失败不中断整体迁移
            catch (UnauthorizedAccessException) { }
        }

        foreach (var dir in Directory.EnumerateDirectories(source))
            copied += CopyTree(dir, Path.Combine(target, Path.GetFileName(dir)));

        return copied;
    }

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(PluginData);
        Directory.CreateDirectory(Logs);
    }

    public static void EnsurePluginsCreated()
    {
        Directory.CreateDirectory(Plugins);
    }
}
