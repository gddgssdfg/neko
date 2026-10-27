using System.Text.Json;

namespace DesktopPet.App.Core;

/// <summary>皮肤帧的组织方式。</summary>
public enum SkinLayout
{
    /// <summary>横排雪碧图：每状态一张 <c>neko_&lt;state&gt;.png</c>，横向排列 frames 帧。</summary>
    Sheet,

    /// <summary>独立帧文件：每状态每帧一张 <c>neko_&lt;state&gt;_&lt;index&gt;.png</c>。</summary>
    Files,
}

/// <summary>原画方向。未声明时不随行走方向镜像，兼容旧皮肤。</summary>
public enum SkinFacing { None, Left, Right }

/// <summary>皮肤中的一个动画状态（来自 skin.json）。</summary>
public sealed class SkinState
{
    /// <summary>状态名（对应 <c>neko_&lt;name&gt;.png</c> 的 name，如 idle/pet/drag）。</summary>
    public required string Name { get; init; }

    /// <summary>该状态的播放帧率（帧/秒）。</summary>
    public required double Fps { get; init; }

    /// <summary>叠加的眼球层 key（<c>neko_&lt;eyeLayer&gt;.png</c>）；null 表示不叠加（如 sleep，或眼睛已画在身体里）。</summary>
    public string? EyeLayer { get; init; }

    /// <summary>每帧眼层锚点相对原画的平移（源像素）；缺省不偏移。</summary>
    public IReadOnlyList<SkinEyeOffset> EyeOffsets { get; init; } = Array.Empty<SkinEyeOffset>();
    public SkinFacing Facing { get; init; }
}

public readonly record struct SkinEyeOffset(double X, double Y);

/// <summary>
/// 单个皮肤的元数据（由 <c>neko-assets/&lt;skinId&gt;/skin.json</c> 解析而来）。
/// 宿主只依赖本对象加载素材，皮肤的全部差异（状态表 / 帧率 / 图层 / 尺寸）都在素材侧声明。
/// </summary>
public sealed class SkinInfo
{
    public SkinMotion? Motion { get; init; }
    public IReadOnlyList<SkinTransition> Transitions { get; init; } = Array.Empty<SkinTransition>();
    /// <summary>皮肤 id（= 目录名，用于持久化到 AppConfig.Skin）。</summary>
    public required string Id { get; init; }

    /// <summary>给用户看的名称。</summary>
    public required string DisplayName { get; init; }

    /// <summary>皮肤素材目录的绝对路径。</summary>
    public required string Directory { get; init; }

    /// <summary>帧组织方式。</summary>
    public SkinLayout Layout { get; init; } = SkinLayout.Sheet;

    /// <summary>单帧绘制尺寸（逻辑单位，WPF 会按需缩放实际位图）。</summary>
    public int FrameWidth { get; init; } = 32;

    /// <inheritdoc cref="FrameWidth"/>
    public int FrameHeight { get; init; } = 32;

    /// <summary>每状态的帧数。</summary>
    public int Frames { get; init; } = 4;

    /// <summary>是否加载 <c>neko_shadow.png</c> 影子层。</summary>
    public bool HasShadow { get; init; }

    /// <summary>像素画用最近邻；手绘皮肤可声明 false 使用平滑缩放。缺省保持旧皮肤行为。</summary>
    public bool PixelArt { get; init; } = true;

    /// <summary>眼球层 key 列表（仅 sheet 布局使用；按需探测文件是否存在）。</summary>
    public IReadOnlyList<string> EyeLayers { get; init; } = Array.Empty<string>();

    /// <summary>状态表。</summary>
    public IReadOnlyList<SkinState> States { get; init; } = Array.Empty<SkinState>();

    /// <summary>
    /// v0.1.81：作者建议的显示倍率（来自 skin.json 的 <c>recommendedScale</c>）。
    /// <para>语义：<c>&gt; 0</c> 表示皮肤作者建议宿主在用户切换到本皮肤时把缩放套用到该值，
    /// 让高清皮肤开箱即 1:1 显示（无需用户手动把缩放开到 4）；
    /// <c>&lt;= 0</c>（默认）表示未声明推荐缩放，宿主<b>不得</b>改动用户当前缩放。</para>
    /// <para>仅接受 1.0–8.0；缺失 / NaN / 越界 / 负数一律落回 0（不影响皮肤可用性）。</para>
    /// </summary>
    public double RecommendedScale { get; init; }

    /// <summary>是否来自内置兜底（skin.json 缺失/损坏时的最后手段），供诊断使用。</summary>
    public bool IsBuiltinFallback { get; init; }
}

/// <summary>
/// 皮肤注册表：扫 <c>&lt;assetsRoot&gt;/*/skin.json</c> 自动发现皮肤。
/// <para>设计要点（借鉴自 BongoCat 的 model_catalog「扫目录即发现」）：</para>
/// <list type="bullet">
///   <item>新增皮肤 = 在 neko-assets 下建一个目录 + 放 skin.json + 帧 PNG，宿主零改动。</item>
///   <item>任何解析失败（文件缺失/JSON 损坏/字段非法）都 <b>跳过该皮肤并 Log.Warn</b>，绝不抛异常。</item>
///   <item>一个都没扫到 → 回退到内置默认（保证 pixel 永远可用，绝不崩宿主）。</item>
/// </list>
/// </summary>
public static class SkinRegistry
{
    /// <summary>皮肤清单文件名。</summary>
    public const string ManifestFileName = "skin.json";

    /// <summary>内置默认皮肤 id（也是兜底皮肤 id）。</summary>
    public const string DefaultSkinId = "pixel";

    /// <summary>默认帧数（兜底用）。</summary>
    private const int DefaultFrames = 4;

    /// <summary>默认单帧尺寸（兜底用）。</summary>
    private const int DefaultFrameSize = 32;

    /// <summary>
    /// 扫描 <paramref name="assetsRoot"/> 下的全部皮肤。
    /// <para>返回列表保证非空（扫不到时返回仅含内置兜底的单元素列表）。</para>
    /// </summary>
    public static IReadOnlyList<SkinInfo> Scan(string assetsRoot)
    {
        var skins = new List<SkinInfo>();
        if (!string.IsNullOrWhiteSpace(assetsRoot) && System.IO.Directory.Exists(assetsRoot))
        {
            string[] dirs;
            try
            {
                dirs = System.IO.Directory.GetDirectories(assetsRoot);
            }
            catch (Exception ex)
            {
                Log.Warn($"[皮肤] 扫描目录失败：{ex.Message}");
                dirs = Array.Empty<string>();
            }

            foreach (var dir in dirs)
            {
                var manifest = System.IO.Path.Combine(dir, ManifestFileName);
                if (!System.IO.File.Exists(manifest)) continue;   // 无清单 → 不是皮肤目录（如 icons/mirror/reference）

                var info = TryLoad(dir, manifest);
                if (info is null)
                {
                    Log.Warn($"[皮肤] 跳过非法皮肤目录：{dir}");
                    continue;
                }
                skins.Add(info);
            }
        }

        if (skins.Count == 0)
        {
            Log.Warn($"[皮肤] 未发现任何有效皮肤（{assetsRoot}），回退内置默认。");
            return new[] { BuiltinFallback() };
        }

        // 默认皮肤排最前，其余按 id 字典序，保证下拉顺序稳定
        skins.Sort((a, b) =>
        {
            bool aDef = string.Equals(a.Id, DefaultSkinId, StringComparison.OrdinalIgnoreCase);
            bool bDef = string.Equals(b.Id, DefaultSkinId, StringComparison.OrdinalIgnoreCase);
            if (aDef != bDef) return aDef ? -1 : 1;
            return string.CompareOrdinal(a.Id, b.Id);
        });
        return skins;
    }

    /// <summary>按 id 解析皮肤；找不到时回退默认皮肤，再回退内置兜底（永不返回 null）。</summary>
    public static SkinInfo Resolve(string assetsRoot, string? skinId)
    {
        var skins = Scan(assetsRoot);
        if (!string.IsNullOrWhiteSpace(skinId))
        {
            var hit = skins.FirstOrDefault(s =>
                string.Equals(s.Id, skinId, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        var fallback = skins.FirstOrDefault(s =>
            string.Equals(s.Id, DefaultSkinId, StringComparison.OrdinalIgnoreCase));
        return fallback ?? BuiltinFallback();
    }

    /// <summary>判断给定 id 是否为已注册的皮肤。</summary>
    public static bool Exists(string assetsRoot, string? skinId)
    {
        if (string.IsNullOrWhiteSpace(skinId)) return false;
        return Scan(assetsRoot).Any(s =>
            string.Equals(s.Id, skinId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 内置兜底皮肤（pixel 的硬编码规格）。
    /// 仅当 skin.json 全部缺失/损坏时使用，保证程序永不因皮肤问题崩。
    /// </summary>
    public static SkinInfo BuiltinFallback() => new()
    {
        Id = DefaultSkinId,
        DisplayName = "像素风",
        Directory = string.Empty,
        Layout = SkinLayout.Sheet,
        FrameWidth = DefaultFrameSize,
        FrameHeight = DefaultFrameSize,
        Frames = DefaultFrames,
        HasShadow = true,
        EyeLayers = new[] { "eyes_open", "eyes_pet", "eyes_blink", "eyes_half", "eyes_scared" },
        States = new[]
        {
            new SkinState { Name = "idle",  Fps = 4,  EyeLayer = "eyes_open" },
            new SkinState { Name = "walk",  Fps = 7,  EyeLayer = "eyes_open" },  // C 档：与 WalkSpeed=72 零打滑
            new SkinState { Name = "sit",   Fps = 4,  EyeLayer = "eyes_open" },
            new SkinState { Name = "sleep", Fps = 0,  EyeLayer = null },
            new SkinState { Name = "pet",   Fps = 10, EyeLayer = "eyes_pet" },
            new SkinState { Name = "drag",  Fps = 6,  EyeLayer = "eyes_scared" },
            new SkinState { Name = "fall",  Fps = 12, EyeLayer = "eyes_scared" },
            new SkinState { Name = "climb", Fps = 4,  EyeLayer = "eyes_open" },
            new SkinState { Name = "work",  Fps = 2,  EyeLayer = "eyes_open" },
        },
        IsBuiltinFallback = true,
    };

    // ------------------------------------------------------------------
    // 解析
    // ------------------------------------------------------------------

    /// <summary>解析单个 skin.json；任何异常都捕获并返回 null。</summary>
    public static SkinInfo InspectFolder(string directory)
        => TryLoad(directory, System.IO.Path.Combine(directory, ManifestFileName))
            ?? throw new FormatException("skin.json 无效：请检查 id 与目录名、帧尺寸及 states 声明；详细原因见日志。");

    private static SkinInfo? TryLoad(string skinDir, string manifestPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(manifestPath));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            // id：缺省用目录名
            var id = GetString(root, "id");
            if (string.IsNullOrWhiteSpace(id)) id = System.IO.Path.GetFileName(skinDir.TrimEnd('\\', '/'));
            if (!IsAssetName(id!) || !string.Equals(id, System.IO.Path.GetFileName(skinDir.TrimEnd('\\', '/')), StringComparison.OrdinalIgnoreCase))
                throw new FormatException("id 必须与皮肤目录名一致，且不能包含路径字符");

            var displayName = GetString(root, "displayName");
            if (string.IsNullOrWhiteSpace(displayName)) displayName = id;

            var layoutName = GetString(root, "layout") ?? "sheet";
            if (!string.Equals(layoutName, "sheet", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(layoutName, "files", StringComparison.OrdinalIgnoreCase))
                throw new FormatException($"未知 layout '{layoutName}'，请使用 sheet 或 files");
            var layout = string.Equals(layoutName, "files", StringComparison.OrdinalIgnoreCase)
                ? SkinLayout.Files : SkinLayout.Sheet;

            int frameWidth = GetInt(root, "frameWidth", DefaultFrameSize);
            int frameHeight = GetInt(root, "frameHeight", DefaultFrameSize);
            int frames = GetInt(root, "frames", DefaultFrames);
            if (frameWidth <= 0 || frameHeight <= 0 || frames <= 0) return null;
            if (frames > 256) throw new FormatException("frames 不能超过 256");

            bool hasShadow = GetBool(root, "hasShadow", false);

            // v0.1.81：推荐缩放（可选字段）。校验仅在 1.0–8.0 内接受；
            // 缺失 / NaN / 越界 / 负数一律落回 0（未声明）——绝不让它把整张皮肤判为损坏。
            double recommendedScale = 0;
            double recRaw = GetDouble(root, "recommendedScale", 0);
            if (!double.IsNaN(recRaw) && recRaw > 0)
            {
                if (recRaw >= 1.0 && recRaw <= 8.0) recommendedScale = recRaw;
                else Log.Warn($"[皮肤] {manifestPath} 的 recommendedScale={recRaw} 越界（有效范围 1.0–8.0），已忽略。");
            }

            var eyeLayers = new List<string>();
            if (root.TryGetProperty("eyeLayers", out var eyes) && eyes.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in eyes.EnumerateArray())
                {
                    if (e.ValueKind == JsonValueKind.String)
                    {
                        var v = e.GetString();
                        if (!string.IsNullOrWhiteSpace(v))
                        {
                            if (!IsAssetName(v)) throw new FormatException($"非法眼层名 '{v}'");
                            if (!eyeLayers.Contains(v, StringComparer.OrdinalIgnoreCase)) eyeLayers.Add(v);
                        }
                    }
                }
            }

            var states = new List<SkinState>();
            if (root.TryGetProperty("states", out var st) && st.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in st.EnumerateArray())
                {
                    if (s.ValueKind != JsonValueKind.Object) continue;
                    var name = GetString(s, "name");
                    if (string.IsNullOrWhiteSpace(name)) continue;   // 无名状态无意义，跳过该条
                    if (!IsAssetName(name) || states.Any(state => string.Equals(state.Name, name, StringComparison.OrdinalIgnoreCase)))
                        throw new FormatException($"状态名非法或重复：'{name}'");
                    double fps = GetDouble(s, "fps", 0);
                    // v0.1.82：**fps == 0 是合法的「静止」声明**，不能判为损坏。
                    // 反例（真踩过）：为了做"猫大部分时间不动"，待机/坐/睡的帧数写 1、fps 写 0，
                    // 结果旧校验 `fps <= 0 → return null` 把**整套皮肤**判为非法并跳过——
                    // 表现为"新皮肤在设置页里根本不存在"，而且日志里只有一行难懂的"跳过非法皮肤目录"。
                    // 引擎侧是支持静止的：FrameAnimator.Tick 里 `if (fps <= 0) return false;`（帧永不推进）。
                    // 这里只拦真正非法的值：负数 / NaN（缺字段时 GetDouble 回落成 0 → 视为静止，偏宽容但有意义）。
                    if (fps < 0 || !double.IsFinite(fps)) throw new FormatException($"状态 '{name}' 的 fps 必须为有限非负数");
                    var eye = GetString(s, "eyeLayer");
                    if (!string.IsNullOrWhiteSpace(eye) && !IsAssetName(eye)) throw new FormatException($"非法眼层引用 '{eye}'");
                    var eyeOffsets = new List<SkinEyeOffset>();
                    if (s.TryGetProperty("eyeOffsets", out var offsets))
                    {
                        if (offsets.ValueKind != JsonValueKind.Array || offsets.GetArrayLength() != frames)
                            throw new FormatException($"状态 '{name}' 的 eyeOffsets 必须与 frames 数量一致");
                        foreach (var offset in offsets.EnumerateArray())
                        {
                            if (offset.ValueKind != JsonValueKind.Object) throw new FormatException("eyeOffsets 每项必须为对象");
                            var x = GetDouble(offset, "x", double.NaN);
                            var y = GetDouble(offset, "y", double.NaN);
                            if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) > frameWidth || Math.Abs(y) > frameHeight)
                                throw new FormatException($"状态 '{name}' 的眼层偏移必须为画幅范围内的有限数字");
                            eyeOffsets.Add(new SkinEyeOffset(x, y));
                        }
                    }
                    var facing = GetString(s, "facing")?.ToLowerInvariant() switch
                    {
                        null or "none" => SkinFacing.None,
                        "left" => SkinFacing.Left,
                        "right" => SkinFacing.Right,
                        var invalid => throw new FormatException($"状态 '{name}' 的 facing '{invalid}' 无效，应为 left/right/none"),
                    };
                    states.Add(new SkinState { Name = name!, Fps = fps, EyeLayer = string.IsNullOrWhiteSpace(eye) ? null : eye, EyeOffsets = eyeOffsets, Facing = facing });
                }
            }
            if (states.Count == 0) return null;   // 没有可用状态的皮肤不可用

            var transitions = new List<SkinTransition>();
            if (root.TryGetProperty("transitions", out var routes))
            {
                if (routes.ValueKind != JsonValueKind.Array || routes.GetArrayLength() > 32) throw new FormatException("transitions 必须为至多 32 项的数组");
                var mainStates = new HashSet<string>(new[] { "idle", "sit", "sleep", "work" }, StringComparer.OrdinalIgnoreCase);
                foreach (var route in routes.EnumerateArray())
                {
                    if (route.ValueKind != JsonValueKind.Object) throw new FormatException("transition 必须为对象");
                    var from = GetString(route, "from")?.ToLowerInvariant();
                    var to = GetString(route, "to")?.ToLowerInvariant();
                    var via = GetString(route, "via")?.ToLowerInvariant();
                    if (from is null || to is null || via is null || !mainStates.Contains(from) || !mainStates.Contains(to) || from == to ||
                        !states.Any(s => string.Equals(s.Name, from, StringComparison.OrdinalIgnoreCase)) || !states.Any(s => string.Equals(s.Name, to, StringComparison.OrdinalIgnoreCase)) || !states.Any(s => string.Equals(s.Name, via, StringComparison.OrdinalIgnoreCase) && s.Fps > 0) ||
                        transitions.Any(t => t.From == from && t.To == to)) throw new FormatException("过渡需引用已声明的状态和正帧率中间动作，且不能重复或拦截直接交互");
                    transitions.Add(new SkinTransition(from, to, via));
                }
            }

            return new SkinInfo
            {
                Id = id!,
                DisplayName = displayName!,
                Directory = skinDir,
                Layout = layout,
                FrameWidth = frameWidth,
                FrameHeight = frameHeight,
                Frames = frames,
                HasShadow = hasShadow,
                PixelArt = GetBool(root, "pixelArt", true),
                EyeLayers = eyeLayers,
                States = states,
                RecommendedScale = recommendedScale,
                Motion = SkinMotion.Parse(root), Transitions = transitions,
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"[皮肤] 解析 {manifestPath} 失败：{ex.Message}");
            return null;
        }
    }

    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool IsAssetName(string value) => value.Length > 0 && value is not "." and not ".."
        && value == value.Trim() && !value.EndsWith('.')
        && value.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0
        && !value.Contains('/') && !value.Contains('\\');

    private static bool GetBool(JsonElement obj, string name, bool fallback)
        => obj.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean() : fallback;

    private static int GetInt(JsonElement obj, string name, int fallback)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : fallback;

    private static double GetDouble(JsonElement obj, string name, double fallback)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : fallback;
}
