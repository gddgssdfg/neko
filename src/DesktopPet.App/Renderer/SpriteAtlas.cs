using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopPet.App.Renderer;

/// <summary>
/// 雪碧图加载。每个状态一张 128x32（4 帧横排）PNG（像素版）/ 4 个独立 128x128 PNG（柔光版）。
/// 帧由源矩形裁剪而来，供 WPF 直接绘制。
/// v0.1.48：加 skin 参数（pixel=横排 4 帧 + 独立眼球层 / soft=每帧独立 PNG，眼睛画在身体里）
/// — FrameW/FrameH 保持 const=32 作为"绘制尺寸"（WPF 自动把软版 128 像素缩放到 32*Scale=102 视觉尺寸）。
/// </summary>
public sealed class SpriteFrame
{
    public required BitmapSource Bitmap { get; init; }
}

public sealed class SpriteState
{
    public required string Name { get; init; }
    public required SpriteFrame[] Frames { get; init; }
    public required double Fps { get; init; }
    /// <summary>此状态叠加的眼球层 key（null 表示不叠加，如 sleep 或柔光版——眼睛已画在身体里）</summary>
    public string? EyeLayer { get; init; }
}

public sealed class SpriteAtlas
{
    /// <summary>绘制尺寸（逻辑单位）。像素版实际像素=32；柔光版 Bitmap 实际 128 像素，由 WPF 在 Draw 时自动缩放到 FrameW*Scale</summary>
    public const int FrameW = 32;
    public const int FrameH = 32;

    private readonly Dictionary<string, SpriteState> _states = new();
    private readonly Dictionary<string, BitmapSource> _eyes = new();
    public BitmapSource? Shadow { get; private set; }
    /// <summary>v0.1.48：皮肤名（"pixel"/"soft"），影响加载路径与 EyeLayer 处理</summary>
    public string Skin { get; private set; } = "pixel";

    public IReadOnlyDictionary<string, SpriteState> States => _states;

    /// <summary>像素版加载（向后兼容）。新代码用 Load(dir, skin)。</summary>
    public static SpriteAtlas Load(string assetsDir) => Load(assetsDir, "pixel");

    /// <summary>v0.1.48：按皮肤加载。
    /// pixel：每状态 4 帧横排 PNG + 独立眼球层 + shadow
    /// soft：每状态 4 个独立 128x128 PNG，眼睛已画在身体里（EyeLayer=null），无 shadow</summary>
    public static SpriteAtlas Load(string assetsDir, string skin)
    {
        return skin == "soft" ? LoadSoft(assetsDir) : LoadPixel(assetsDir);
    }

    private static SpriteAtlas LoadPixel(string assetsDir)
    {
        var atlas = new SpriteAtlas { Skin = "pixel" };

        // v0.1.58(任务6)：皮肤契约 anim.json —— 目录下若有 anim.json，用其中 fps 覆盖内置默认表；
        // 缺失键 / 文件缺失 / 文件损坏一律静默回退内置表（皮肤缺 anim.json 也能跑）。
        var fpsOverrides = ReadAnimFpsOverrides(assetsDir);

        // 8 个行为状态：文件名 → 帧率 + 眼球层
        var stateSpecs = new (string file, double fps, string? eye)[]
        {
            ("idle",   4,  "eyes_open"),    // v0.1.57：idle 2→4fps（4 帧两级呼吸更顺滑）
            ("walk",   8,  "eyes_open"),
            ("sit",    4,  "eyes_open"),    // v0.1.57：sit 2→4fps（呼吸幅度 1px）
            ("sleep",  1,  null),
            ("pet",    10, "eyes_pet"),
            ("drag",   6,  "eyes_scared"),
            ("fall",   12, "eyes_scared"),
            ("climb",  4,  "eyes_open"),
            ("work",   2,  "eyes_open"),   // v0.1.48：BongoCat 化工作姿态
        };

        foreach (var (file, fps, eye) in stateSpecs)
        {
            var path = Path.Combine(assetsDir, $"neko_{file}.png");
            if (!File.Exists(path)) continue;
            var sheet = LoadPng(path);
            var frames = new SpriteFrame[4];
            for (int i = 0; i < 4; i++)
            {
                frames[i] = new SpriteFrame
                {
                    Bitmap = Crop(sheet, i * FrameW, 0, FrameW, FrameH),
                };
            }
            var effectiveFps = fpsOverrides is not null && fpsOverrides.TryGetValue(file, out var f) ? f : fps;
            atlas._states[file] = new SpriteState { Name = file, Frames = frames, Fps = effectiveFps, EyeLayer = eye };
        }

        // 眼球层
        foreach (var eye in new[] { "eyes_open", "eyes_pet", "eyes_blink", "eyes_half", "eyes_scared" })
        {
            var p = Path.Combine(assetsDir, $"neko_{eye}.png");
            if (File.Exists(p)) atlas._eyes[eye] = LoadPng(p);
        }

        var shadowPath = Path.Combine(assetsDir, "neko_shadow.png");
        if (File.Exists(shadowPath)) atlas.Shadow = LoadPng(shadowPath);

        return atlas;
    }

    /// <summary>v0.1.48：柔光版加载（generate_soft_cat.py 产出：每状态每帧独立 128x128，眼睛已画在身体里）</summary>
    private static SpriteAtlas LoadSoft(string assetsDir)
    {
        var atlas = new SpriteAtlas { Skin = "soft" };
        var softDir = Path.Combine(assetsDir, "soft");
        if (!Directory.Exists(softDir)) return atlas;   // 缺素材时返回空 atlas（不抛）

        // v0.1.58(任务6)：soft 皮肤目录（assetsDir/soft/anim.json）也可带 fps 覆盖，规则同 pixel
        var fpsOverrides = ReadAnimFpsOverrides(softDir);

        // 软版状态规格（与像素版同 fps，眼睛=null 因已画在身体里）
        var stateSpecs = new (string file, double fps)[]
        {
            ("idle",   4), ("walk", 8), ("sit",  4), ("sleep", 1),   // v0.1.57：idle/sit 2→4fps
            ("pet",   10), ("drag", 6), ("fall", 12), ("climb", 4),
            ("work",   2),   // v0.1.48：BongoCat 化工作姿态
        };

        foreach (var (file, fps) in stateSpecs)
        {
            var frames = new SpriteFrame[4];
            bool allExist = true;
            for (int f = 0; f < 4; f++)
            {
                var p = Path.Combine(softDir, $"neko_{file}_{f}.png");
                if (!File.Exists(p)) { allExist = false; break; }
                frames[f] = new SpriteFrame { Bitmap = LoadPng(p) };
            }
            if (allExist)
            {
                var effectiveFps = fpsOverrides is not null && fpsOverrides.TryGetValue(file, out var f) ? f : fps;
                atlas._states[file] = new SpriteState { Name = file, Frames = frames, Fps = effectiveFps, EyeLayer = null };
            }
        }

        // 柔光版无 shadow / 独立眼球层（眼睛已画在身体里）
        return atlas;
    }

    /// <summary>
    /// v0.1.58(任务6)：读取皮肤目录 anim.json 的 fps 覆盖表。
    /// 契约（见 tools/generate_sprites.py 头部文档）：
    ///   { "fps": { "idle": 4, ... }, "meta": { "frameWidth":32, "frameHeight":32, "framesPerState":4 } }
    /// 只消费 fps；缺文件 / JSON 损坏 / fps 缺失 / 值为非数字 → 返回 null，调用方回退内置默认表（静默）。
    /// </summary>
    private static Dictionary<string, double>? ReadAnimFpsOverrides(string assetsDir)
    {
        var path = Path.Combine(assetsDir, "anim.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("fps", out var fps) || fps.ValueKind != JsonValueKind.Object)
                return null;
            var map = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var prop in fps.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Number)
                    map[prop.Name] = prop.Value.GetDouble();
            }
            return map.Count > 0 ? map : null;
        }
        catch (Exception)
        {
            // 文件损坏/字段异常：静默回退内置表，皮肤缺 anim.json 也能跑
            return null;
        }
    }

    /// <summary>获取某状态的第 n 帧（帧数循环）</summary>
    public SpriteFrame GetFrame(string stateName, int frameIndex)
    {
        if (!_states.TryGetValue(stateName, out var st)) throw new KeyNotFoundException($"未知状态: {stateName}");
        return st.Frames[frameIndex % st.Frames.Length];
    }

    public BitmapSource? GetEye(string key) => _eyes.GetValueOrDefault(key);

    /// <summary>
    /// 加载 PNG 为冻结的 BitmapSource。
    /// 用 FileStream + BitmapImage 而非 PngBitmapDecoder(file URI)：后者对含 alpha 的 PNG
    /// 偶发返回黑帧，导致 DrawImage 画黑方块（根因 2026-09-01 排查）。
    /// </summary>
    private static BitmapSource LoadPng(string path)
    {
        using var fs = File.OpenRead(path);
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bi.StreamSource = fs;
        bi.EndInit();
        if (bi.CanFreeze) bi.Freeze();
        return bi;
    }

    private static BitmapSource Crop(BitmapSource src, int x, int y, int w, int h)
        => new CroppedBitmap(src, new Int32Rect(x, y, w, h));
}
