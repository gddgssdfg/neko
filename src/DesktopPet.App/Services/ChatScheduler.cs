using System.Windows.Threading;

namespace DesktopPet.App.Services;

/// <summary>
/// v0.1.40：气泡闲聊调度——猫"时不时说话"（灯灯需求）。
/// 触发规则（每分钟 tick）：
///   ① 连续工作 ≥ ChatRestMinutes 分钟 → 休息提醒（两次提醒间隔 ≥ 60 分钟）
///   ② 每 12~20 分钟随机闲聊一次（仅当最近 3 分钟有活动——挂机不打扰）
/// 语气：读好感度插件数据（data/plugins/com.lantern.affection/score.json）→ 4 级决定台词亲密度。
/// 台词：内置台词库（静态数组，{0}=称呼 {1}=猫名；L2/L3 阶段融入避免生疏期喊得太亲昵）。
/// </summary>
public sealed class ChatScheduler
{
    private readonly Action<string, int> _say;
    private readonly ActivityTracker _activity;
    private readonly string _affectionFile;
    private readonly int _restMinutes;
    private readonly string _catName;
    private readonly string _ownerTitle;
    private readonly Random _rng = new();
    private DispatcherTimer? _timer;
    private DateTime _nextChat = DateTime.Now;
    private DateTime _lastRestReminder = DateTime.MinValue;

    public ChatScheduler(Action<string, int> say, ActivityTracker activity, string affectionScoreFile, int restMinutes, string catName, string ownerTitle)
    {
        _say = say;
        _activity = activity;
        _affectionFile = affectionScoreFile;
        _restMinutes = Math.Max(15, restMinutes);
        // v0.1.41：称呼/猫名为空时给个合理兜底（首启引导后必有真值；兜底防极端配置）
        _catName = string.IsNullOrWhiteSpace(catName) ? "Neko" : catName.Trim();
        _ownerTitle = string.IsNullOrWhiteSpace(ownerTitle) ? "主人" : ownerTitle.Trim();
    }

    public void Start()
    {
        _nextChat = DateTime.Now.AddMinutes(8 + _rng.Next(0, 13));   // 启动后 8~20 分钟才第一次闲聊
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
    }

    public void Stop() => _timer?.Stop();

    private void OnTick()
    {
        int level = ReadAffectionLevel();
        var now = DateTime.Now;

        // ① 连续工作提醒（连续活跃超阈值，且距上次提醒 ≥ 60 分钟——不刷屏）
        if (_activity.ContinuousMinutes >= _restMinutes && (now - _lastRestReminder).TotalMinutes >= 60)
        {
            Say(Pick(RestLines[level]), 5000);
            _lastRestReminder = now;
            _nextChat = now.AddMinutes(12 + _rng.Next(0, 9));   // 提醒后推迟闲聊，避免连说
            return;
        }

        // ② 随机闲聊：到点 + 最近有活动（ContinuousMinutes > 0 即最近 3 分钟内动过）
        if (now >= _nextChat && _activity.ContinuousMinutes > 0)
        {
            Say(Pick(ChatLines[level]), 3500);
            _nextChat = now.AddMinutes(12 + _rng.Next(0, 9));
        }
    }

    /// <summary>从档位数组里抽一条，用称呼/猫名填充 {0}/{1} 占位符</summary>
    private string Pick(string[] arr) => string.Format(arr[_rng.Next(arr.Length)], _ownerTitle, _catName);

    private void Say(string text, int ms) { try { _say(text, ms); } catch { /* 闲聊气泡纯点缀：说失败也绝不外抛拖垮调度 */ } }

    /// <summary>读好感度等级 0..3（文件缺失/解析失败 → 0 陌生）。对齐 AffectionPlugin 的分档阈值。
    /// v0.1.59(⑨)：逻辑提取到 AffectionReader，与 PetWindow 心情驱动动画共用单一数据源。</summary>
    private int ReadAffectionLevel() => AffectionReader.ReadLevel(_affectionFile);

    // ---- 台词库（按好感度 4 级分组，每场景 4 条；{0}=称呼 {1}=猫名；L2/L3 融入避免生疏期喊太亲昵）----

    private static readonly string[][] RestLines =
    {
        new[] { "工作很久了，要不要起来活动一下？", "坐了好久了，休息一下吧。", "记得喝口水哦。", "该放松放松了。" },
        new[] { "你已经坐很久啦，起来喝口水吧～", "好认真哦……但身体要紧呀。", "要不要伸个懒腰？", "我帮你盯着，休息会儿吧～" },
        new[] { "{0}，坐了好久呢，陪我玩会儿再继续吧？", "{0}，工作辛苦啦！起来转转吧～", "你这样我好心疼，{0}休息下嘛～", "劳逸结合才最棒！" },
        new[] { "工作辛苦啦！{0}，休息一下，我陪你～", "{0}，你累了吧？靠过来歇会儿吧。", "再忙也要记得休息哦，{0}，{1}最喜欢你了～", "{0}，摸摸头，去喝口水吧！" },
    };

    private static readonly string[][] ChatLines =
    {
        new[] { "喵～今天过得怎么样？", "（好奇地歪头看你）", "好安静呀……", "在忙什么呢？" },
        new[] { "我在看着你哦～", "要不要摸摸我？", "今天也是元气满满呢！", "窗外的天气不错呢～" },
        new[] { "最喜欢{0}啦～", "工作加油，{0}，{1}我一直陪着你！", "尾巴好痒，{0}帮我挠挠～", "你今天看起来特别厉害！" },
        new[] { "{0}是我最好的人类！", "（蹭蹭{0}的手）", "什么时候带{1}晒太阳呀～", "有{0}在，{1}什么都不怕！" },
    };
}
