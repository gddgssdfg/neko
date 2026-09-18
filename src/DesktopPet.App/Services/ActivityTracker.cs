using System.IO;
using System.Text.Json;
using DesktopPet.App.Core;

namespace DesktopPet.App.Services;

/// <summary>
/// v0.1.40：活跃度跟踪——供气泡闲聊调度用。
/// 记录两件事：
///   ① 今日累计活跃分钟：每分钟内有键鼠输入即 +1（跨天重置，持久化到 data/host/activity.json）
///   ② 当前连续活跃分钟：输入间隔 &gt;3 分钟视为新会话（内存态，不落盘）
/// 数据消费方：ChatScheduler（"工作很久了"提醒靠 ②，天气/备忘录等靠插件自己 Say）。
/// </summary>
public sealed class ActivityTracker
{
    private readonly string _file;
    private readonly JsonSerializerOptions _opts = new() { WriteIndented = true };
    private DateTime _lastInput = DateTime.Now;
    private DateTime _sessionStart = DateTime.Now;
    private string _todayDate = "";
    private string _lastActiveMinute = "";
    private int _todayMinutes;

    public ActivityTracker(string dataFile)
    {
        _file = dataFile;
        Load();
    }

    /// <summary>今日累计活跃分钟（跨天自动重置为 0）</summary>
    public int TodayActiveMinutes { get { EnsureDate(); return _todayMinutes; } }

    /// <summary>当前连续活跃分钟（最近一次输入距今 &gt;3 分钟 → 0，表示已断链不算连续工作）</summary>
    public int ContinuousMinutes
    {
        get
        {
            if (DateTime.Now - _lastInput > TimeSpan.FromMinutes(3)) return 0;
            return Math.Max(0, (int)((DateTime.Now - _sessionStart).TotalMinutes));
        }
    }

    /// <summary>键鼠输入入口（宿主把 GlobalInput 原始事件接进来，DOWN 才计）</summary>
    public void NotifyInput()
    {
        var now = DateTime.Now;
        if (now - _lastInput > TimeSpan.FromMinutes(3)) _sessionStart = now;   // 断链超 3 分钟 = 新会话
        _lastInput = now;
        EnsureDate();
        var minuteKey = now.ToString("yyyy-MM-dd HH:mm");
        if (minuteKey != _lastActiveMinute)
        {
            _lastActiveMinute = minuteKey;
            _todayMinutes++;
            Save();
        }
    }

    private void EnsureDate()
    {
        var d = DateTime.Now.ToString("yyyy-MM-dd");
        if (d != _todayDate)
        {
            _todayDate = d;
            _todayMinutes = 0;      // 新的一天
            _lastActiveMinute = "";
        }
    }

    private void Load()
    {
        try
        {
            EnsureDate();
            if (!File.Exists(_file)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_file));
            var date = doc.RootElement.TryGetProperty("date", out var de) ? de.GetString() : "";
            var minutes = doc.RootElement.TryGetProperty("minutes", out var me) ? me.GetInt32() : 0;
            _todayMinutes = date == _todayDate ? Math.Max(0, minutes) : 0;   // 跨天从 0 开始
        }
        catch { EnsureDate(); }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_file, JsonSerializer.Serialize(new { date = _todayDate, minutes = _todayMinutes }, _opts));
        }
        catch (Exception ex)
        {
            // v0.1.82 P1-8：磁盘写失败（满/权限）会让"今日活跃分钟"这一跨进程统计静默丢失 → 记 Warn 可定位
            Log.Warn($"[活动统计] 落盘失败（今日活跃分钟可能无法跨进程保留）：{ex.Message}");
        }
    }
}
