using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopPet.App.Core;

/// <summary>应用配置（data/config.json）</summary>
public sealed class AppConfig
{
    public bool StartWithWindows { get; set; } = false;
    public List<string> DisabledPlugins { get; set; } = [];
    public double Scale { get; set; } = 3.2; // 显示倍率（32px 精灵 ×3.2 ≈ 102px；灯灯要求缩小 20%）
    public HotKeyConfig MenuHotKey { get; set; } = new(); // 径向菜单热键（BongoCat 借鉴：可配置）
    public HotKeyConfig TogglePetHotKey { get; set; } = new() { Ctrl = true, Shift = true, Key = "H" }; // 显隐老板键（BongoCat 借鉴 Ctrl+Shift+B）
    public bool KeyboardReactionsEnabled { get; set; } = true; // 键鼠反应（打字/点击瞪眼）开关
    public double Opacity { get; set; } = 1.0; // 窗口透明度 0.2-1.0（BongoCat window.opacity 借鉴）
    public double? PosX { get; set; } // 上次位置（DIP，BongoCat 位置记忆）：null = 用默认右下角
    public double? PosY { get; set; }

    // ---- v0.1.27（BongoCat 借鉴）----
    public bool GameModeEnabled { get; set; } = false;    // 游戏模式：检测到全屏应用时低干扰
    public string GameModeAction { get; set; } = "hide";  // 动作：hide=自动藏猫 / through=自动鼠标穿透
    // 附加热键（Ctrl+Alt+Shift 组合避开编辑器/浏览器常用快捷键；Key 支持方向键名 Up/Down/Left/Right）
    public HotKeyConfig OpacityUpHotKey { get; set; } = new() { Ctrl = true, Alt = true, Shift = true, Key = "Up" };
    public HotKeyConfig OpacityDownHotKey { get; set; } = new() { Ctrl = true, Alt = true, Shift = true, Key = "Down" };
    public HotKeyConfig ScaleUpHotKey { get; set; } = new() { Ctrl = true, Alt = true, Shift = true, Key = "Right" };
    public HotKeyConfig ScaleDownHotKey { get; set; } = new() { Ctrl = true, Alt = true, Shift = true, Key = "Left" };
    public HotKeyConfig ResetPosHotKey { get; set; } = new() { Ctrl = true, Alt = true, Shift = true, Key = "R" };

    // ---- v0.1.28（BongoCat 键盘映射核心视觉）----
    public bool KeyboardMirrorEnabled { get; set; } = true;   // 键鼠映射窗（桌面显示键盘+鼠标，实时高亮）
    public double? MirrorPosX { get; set; }                    // 映射窗位置记忆（DIP，null=默认右下角）
    public double? MirrorPosY { get; set; }

    // ---- v0.1.30 双模态：slack=摸鱼（只显示猫）、work=工作（猫+键鼠映射二合一）----
    public string AppMode { get; set; } = "slack";              // 兼容旧配置：键鼠映射默认开但模式默认摸鱼（首次启动要看用户切）

    // ---- v0.1.39 ----
    /// <summary>键鼠映射窗水平镜像：false=键盘朝我（Esc 在左上，用户视角）；true=键盘朝猫（猫坐在对面看到的镜像布局）</summary>
    public bool KeyboardMirrorFlipped { get; set; } = false;
    /// <summary>摸鱼模式下猫是否吸附在屏幕边缘（v0.1.39：slack 模式猫挂边上不碍事，且鼠标走远也不自动脱离）</summary>
    public bool SlackEdgeAttach { get; set; } = true;

    // ---- v0.1.40 气泡对话 ----
    /// <summary>气泡对话总开关（像素云朵 + 猫主动闲聊；插件 Say 通知是否显示气泡）</summary>
    public bool ChatEnabled { get; set; } = true;
    /// <summary>连续工作多少分钟提醒休息（气泡闲聊触发阈值）</summary>
    public int ChatRestMinutes { get; set; } = 45;

    // ---- v0.1.41 首启引导（封包需求核心：首次安装需设置称呼+取名）----
    /// <summary>猫的名字（气泡对话/升级等场景会用到，空=未设置→首启引导）</summary>
    public string CatName { get; set; } = "";
    /// <summary>猫如何称呼你（"主人"/"小可爱"等，出现在气泡对话里；空=未设置→首启引导）</summary>
    public string OwnerTitle { get; set; } = "";

    // ---- v0.1.48 皮肤切换 ----
    /// <summary>皮肤名（"pixel"=默认像素风 / "soft"=柔光版 v0.1.37 32 帧素材）</summary>
    public string Skin { get; set; } = "pixel";

    // ---- v0.1.50 主题与语言 ----
    /// <summary>主题（"dark"=深色默认 / "light"=浅色）</summary>
    public string Theme { get; set; } = "dark";
    /// <summary>语言（"zh"=中文默认 / "en"=English）</summary>
    public string Language { get; set; } = "zh";

    // ---- v0.1.61 日志 ----
    /// <summary>日志级别（"off"/"debug"/"info"/"warn"=默认/"error"）；warn 及以上默认输出到 data/logs</summary>
    public string LogLevel { get; set; } = "warn";

    // ---- v0.1.71 径向菜单配置（顺序覆盖 + 显隐 + 全局可见数量上限）----
    /// <summary>径向菜单用户配置。缺失时保持默认（不限 / 不覆盖 / 全显示）。</summary>
    public RadialMenuConfig RadialMenu { get; set; } = new();
}

/// <summary>热键配置（可持久化）</summary>
public sealed class HotKeyConfig
{
    public bool Ctrl { get; set; } = true;
    public bool Alt { get; set; } = true;
    public bool Shift { get; set; }
    /// <summary>主键（单字符字母或符号，如 "N"；VK 码由宿主转换）</summary>
    public string Key { get; set; } = "N";
}

public static class ConfigManager
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AppConfig Load(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOpts);
                if (cfg is not null) return cfg;
            }
            catch (Exception ex)
            {
                Log.Warn($"配置解析失败，使用默认值: {ex.Message}");
            }
        }
        return new AppConfig();
    }

    public static void Save(string path, AppConfig config)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOpts));
        }
        catch (Exception ex)
        {
            Log.Error("配置保存失败", ex);
        }
    }
}
