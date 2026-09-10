using System.IO;
using System.Text;

namespace DesktopPet.App.Core;

/// <summary>日志级别（数值越大越严重；默认输出级别为 Warning）。</summary>
public enum LogLevel
{
    Off = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
}

/// <summary>
/// 轻量文件日志（v0.1.61）：
///   - 落盘 data/logs/neko-YYYYMMDD.log，按天滚动；单文件超 5MB 追加序号滚动。
///   - 启动时清理 7 天前的旧日志。
///   - 线程安全（lock）；自身 IO 失败一律静默（绝不影响宿主）。
///   - 默认级别 Warning 及以上，避免频繁刷盘；Info/Debug 需配置开启。
///
/// 设计取舍：
///   - 每次写入用 File.AppendAllText 重新打开文件（不常驻句柄）——调用频率不高，
///     且避免单文件被外部进程占用时长期持有句柄。
///   - Error 级同时追加到 data/errors.log，保持对既有 errors.log 的向后兼容。
/// </summary>
public static class Log
{
    /// <summary>单文件滚动阈值：超过则写 neko-YYYYMMDD-N.log。</summary>
    private const long MaxFileBytes = 5 * 1024 * 1024;

    /// <summary>旧日志保留天数。</summary>
    private const int RetainDays = 7;

    /// <summary>日志文件名前缀。</summary>
    private const string FilePrefix = "neko-";

    /// <summary>兼容性：Error 级别同时写入的旧日志文件名。</summary>
    private const string LegacyErrorFile = "errors.log";

    /// <summary>写入串行化锁（多线程 tick / 插件回调 / UI 线程都会调）。</summary>
    private static readonly object Lock = new();

    /// <summary>当前输出级别（可由 AppConfig.LogLevel 配置）。</summary>
    private static LogLevel _minLevel = LogLevel.Warn;

    /// <summary>日志目录（默认 Paths.Logs；可显式指定以便测试隔离）。</summary>
    private static string _logsDir = Paths.Logs;

    /// <summary>是否已完成启动清理（避免重复扫描目录）。</summary>
    private static bool _initialized;

    /// <summary>当前生效的输出级别。</summary>
    public static LogLevel MinLevel => _minLevel;

    /// <summary>
    /// 初始化日志：设置级别、清理过期日志。App 启动时调用一次。
    /// level 为 null 时保持默认（Warning）。任何字符串解析失败都回退默认，不抛异常。
    /// </summary>
    /// <param name="level">级别名："off" / "debug" / "info" / "warn" / "error"（大小写不敏感）。</param>
    /// <param name="logsDir">可选：日志目录；null 用 Paths.Logs。</param>
    public static void Initialize(string? level = null, string? logsDir = null)
    {
        lock (Lock)
        {
            _minLevel = ParseLevel(level);
            if (!string.IsNullOrEmpty(logsDir)) _logsDir = logsDir!;
            try
            {
                Directory.CreateDirectory(_logsDir);
                CleanupOldLogs();
            }
            catch
            {
                // 初始化失败静默——不允许日志设施搞崩宿主
            }
            _initialized = true;
        }
    }

    /// <summary>测试用：直接设置级别并确保目录存在（不清理旧日志）。</summary>
    public static void SetLevel(LogLevel level)
    {
        lock (Lock) _minLevel = level;
    }

    /// <summary>解析级别字符串；未知/空 → 默认 Warning。</summary>
    public static LogLevel ParseLevel(string? level)
    {
        if (string.IsNullOrWhiteSpace(level)) return LogLevel.Warn;
        return level.Trim().ToLowerInvariant() switch
        {
            "off" => LogLevel.Off,
            "debug" => LogLevel.Debug,
            "info" => LogLevel.Info,
            "warn" or "warning" => LogLevel.Warn,
            "error" => LogLevel.Error,
            _ => LogLevel.Warn,
        };
    }

    public static void Debug(string msg) => Write(LogLevel.Debug, msg, null);
    public static void Info(string msg) => Write(LogLevel.Info, msg, null);
    public static void Warn(string msg) => Write(LogLevel.Warn, msg, null);
    public static void Error(string msg, Exception? ex = null) => Write(LogLevel.Error, msg, ex);

    /// <summary>
    /// 核心写入：级别过滤 → 格式化 → 追加文件。全程 try/catch，保证永不抛异常。
    /// </summary>
    private static void Write(LogLevel level, string msg, Exception? ex)
    {
        try
        {
            lock (Lock)
            {
                // Off 表示完全关闭；其余按级别过滤（level 数值越大越严重）
                if (_minLevel == LogLevel.Off) return;
                if (level < _minLevel) return;
                if (!_initialized) // 未显式 Initialize 时懒初始化（首次写入即建目录）
                {
                    try { Directory.CreateDirectory(_logsDir); CleanupOldLogs(); } catch { }
                    _initialized = true;
                }

                var line = Format(level, msg, ex);
                AppendLine(ChooseFile(level), line);
                // Error 兼容：同时写 errors.log
                if (level == LogLevel.Error)
                {
                    try { AppendLine(Path.Combine(_logsDir, LegacyErrorFile), line); } catch { }
                }
            }
        }
        catch
        {
            // 日志自身写失败：静默丢弃，绝不影响宿主
        }
    }

    /// <summary>格式化单行：[yyyy-MM-dd HH:mm:ss.fff] [LEVEL] [ThreadId] 消息（+异常）。</summary>
    private static string Format(LogLevel level, string msg, Exception? ex)
    {
        var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var tid = Environment.CurrentManagedThreadId;
        var sb = new StringBuilder();
        sb.Append('[').Append(ts).Append("] [").Append(LevelName(level)).Append("] [")
          .Append(tid).Append("] ").Append(msg ?? string.Empty);
        if (ex is not null)
        {
            sb.AppendLine();
            sb.Append(ex);
        }
        return sb.ToString();
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO",
        LogLevel.Warn => "WARN",
        LogLevel.Error => "ERROR",
        _ => "OFF",
    };

    /// <summary>按天 + 体积选择目标文件；超阈值时追加 -N 序号。</summary>
    private static string ChooseFile(LogLevel level)
    {
        var datePart = DateTime.Now.ToString("yyyyMMdd");
        var baseName = $"{FilePrefix}{datePart}";
        var path = Path.Combine(_logsDir, baseName + ".log");
        if (!File.Exists(path) || LengthOf(path) < MaxFileBytes) return path;

        // 滚动：neko-YYYYMMDD-1.log, -2.log ...
        for (var i = 1; i < 1000; i++)
        {
            var rolled = Path.Combine(_logsDir, $"{baseName}-{i}.log");
            if (!File.Exists(rolled) || LengthOf(rolled) < MaxFileBytes) return rolled;
        }
        return path; // 极端兜底
    }

    private static long LengthOf(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    /// <summary>追加一行（含换行）。IOException 由上层吞掉。</summary>
    private static void AppendLine(string file, string line)
    {
        File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
    }

    /// <summary>清理 RetainDays 天前的 neko-*.log（按文件名日期解析；解析失败按文件时间兜底）。</summary>
    private static void CleanupOldLogs()
    {
        try
        {
            if (!Directory.Exists(_logsDir)) return;
            var cutoff = DateTime.Now.Date.AddDays(-RetainDays);
            foreach (var f in Directory.EnumerateFiles(_logsDir, FilePrefix + "*.log"))
            {
                try
                {
                    var name = Path.GetFileNameWithoutExtension(f); // neko-YYYYMMDD 或 neko-YYYYMMDD-N
                    if (TryParseDate(name, out var d))
                    {
                        if (d < cutoff) File.Delete(f);
                    }
                    else if (File.GetLastWriteTime(f) < cutoff)
                    {
                        File.Delete(f);
                    }
                }
                catch
                {
                    // 单个文件清理失败不影响其余
                }
            }
        }
        catch
        {
            // 目录枚举失败静默
        }
    }

    /// <summary>从 neko-YYYYMMDD[-N] 解析日期。</summary>
    private static bool TryParseDate(string baseName, out DateTime date)
    {
        date = default;
        var rest = baseName.StartsWith(FilePrefix, StringComparison.Ordinal)
            ? baseName[FilePrefix.Length..]
            : baseName;
        // 去掉 -N 序号
        var dash = rest.IndexOf('-');
        if (dash >= 0) rest = rest[..dash];
        return DateTime.TryParseExact(rest, "yyyyMMdd", null,
            System.Globalization.DateTimeStyles.None, out date);
    }
}
