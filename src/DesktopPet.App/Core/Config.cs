using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopPet.App.Core;

/// <summary>应用配置（数据根下的 config.json；见 <see cref="Paths.Data"/>）</summary>
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

    // ---- v0.1.94 闲时走动开关 ----
    /// <summary>
    /// 闲时走动。**默认关**（v0.1.94）：猫的走路素材腿摆无视觉幅度（正面啾比、腿被身体挡死），
    /// 真位移后只会变成滑行 → 默认不再自动走动（见 CHANGELOG 0.1.94）。
    /// 置 true 可恢复：`skin.json` 的 walk fps 与 `PetStateMachine.WalkSpeed`（C 档零打滑）均保留。
    /// 老配置文件无此键 → 反序列化取默认 false，行为正确。
    /// </summary>
    public bool WalkEnabled { get; set; } = false;

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
    /// <summary>日志级别（"off"/"debug"/"info"/"warn"=默认/"error"）；warn 及以上默认输出到 数据根/logs</summary>
    public string LogLevel { get; set; } = "warn";

    // ---- v0.1.71 径向菜单配置（顺序覆盖 + 显隐 + 全局可见数量上限）----
    /// <summary>径向菜单用户配置。缺失时保持默认（不限 / 不覆盖 / 全显示）。</summary>
    public RadialMenuConfig RadialMenu { get; set; } = new();

    // ---- v0.1.77 自动更新（阶段1：检查 → 提示 → 打开下载页）----
    /// <summary>是否启用自动检查更新（默认开）。关闭后启动路径不查；设置页手动「检查更新」仍可用。</summary>
    public bool UpdateCheckEnabled { get; set; } = true;
    /// <summary>上次自动检查的日期（yyyy-MM-dd），用于「每天只检查一次」去重。</summary>
    public string LastUpdateCheckDay { get; set; } = "";

    // ---- v0.1.74 未知字段透传（借鉴 BongoCat extensions_json）----
    /// <summary>
    /// 未识别的顶层字段原样保留（读写往返不丢）。
    /// 目的：旧版本 Neko 读新版（或插件自行写入）的 config.json 时，不会把新字段抹掉——
    /// 直接支撑「Core API 只增不改 + 插件生态」的数据兼容性。
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
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

    /// <summary>
    /// 读取配置。解析失败时**把损坏的原文件改名备份**（<c>config.json.rejected-&lt;时间戳&gt;</c>），
    /// 再返回默认配置——避免后续 <see cref="Save"/> 用默认值静默覆盖用户数据（v0.1.74，借鉴 BongoCat 的失败隔离）。
    /// </summary>
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
                PreserveCorruptFile(path, ex);
            }
        }
        return new AppConfig();
    }

    /// <summary>
    /// 保存配置：**原子写**——先写 <c>path.tmp</c>，再原子替换目标文件。
    /// 写入中途失败只会留下 tmp，绝不产生半截损坏的 config.json（v0.1.74，借鉴 BongoCat 的 tmp→fsync→rename）。
    /// </summary>
    public static void Save(string path, AppConfig config)
    {
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(config, JsonOpts));
        }
        catch (Exception ex)
        {
            Log.Error("配置写入临时文件失败", ex);
            return;
        }

        // v0.1.86（试运行实测）：**原子替换会瞬时失败，必须兜住**。
        // 证据：真机 errors.log 出现 `[ERROR] 配置保存失败` + `IOException: 操作成功完成`
        // 来自 FileSystem.ReplaceFile —— 这是 Win32 ReplaceFile 的经典怪象，
        // 通常是**有进程/杀毒软件正短暂持有目标文件**。同一台机器 46 秒后的下一次保存就成功了。
        // 危害：失败一次就等于**用户这次改的设置丢了**；而旧代码还会把 tmp 删掉 → 连恢复机会都没有。
        // 策略：**重试几次 → 仍不行就退回非原子 Move（保住数据优先于原子性）→ 全失败则保留 tmp 不删**。
        const int attempts = 3;
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                if (File.Exists(path))
                    File.Replace(tmp, path, null);      // 原子替换（Windows 走 ReplaceFile）
                else
                    File.Move(tmp, path);
                return;                                  // 成功
            }
            catch (Exception ex) when (i < attempts - 1)
            {
                Log.Warn($"配置原子替换失败（第 {i + 1}/{attempts} 次），{RetryDelayMs}ms 后重试：{ex.Message}");
                try { Thread.Sleep(RetryDelayMs); } catch { /* Sleep 不该失败，真失败就继续重试 */ }
            }
            catch (Exception ex)
            {
                // 最后一次原子替换仍失败 → 退回非原子覆盖写（牺牲原子性，优先不丢用户设置）
                try
                {
                    File.Move(tmp, path, overwrite: true);
                    Log.Warn($"配置已用【非原子】方式保存（原子替换连续 {attempts} 次失败）：{ex.Message}");
                    return;
                }
                catch (Exception ex2)
                {
                    Log.Error("配置保存失败（原子替换与非原子覆盖均失败）", ex2);
                    // ⚠️ **故意不删 tmp**：留着，用户/下次启动还有机会找回刚才那次设置。
                    //    （旧实现失败即删 tmp —— 那等于把"刚才那次修改"彻底抹掉。）
                    return;
                }
            }
        }
    }

    /// <summary>原子替换失败后的重试间隔（毫秒）。120ms 足够让"短暂持有文件的进程/杀软"松开。</summary>
    private const int RetryDelayMs = 120;

    /// <summary>把解析失败的配置文件改名保留，供用户手工找回；改名失败仅记日志，不抛异常。</summary>
    private static void PreserveCorruptFile(string path, Exception cause)
    {
        var backup = $"{path}.rejected-{DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            File.Move(path, backup);
            Log.Warn($"配置解析失败（{cause.Message}），原文件已备份到 {backup}，本次使用默认值");
        }
        catch (Exception ex)
        {
            Log.Warn($"配置解析失败（{cause.Message}），备份原文件也失败（{ex.Message}），本次使用默认值");
        }
    }
}
