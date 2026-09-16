using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopPet.App.Core;

namespace DesktopPet.App.Renderer;

/// <summary>
/// 雪碧图加载（v0.1.74：由「硬编码 pixel/soft 两种」改为「按 skin.json 驱动的数据驱动皮肤」）。
///
/// <para>皮肤发现与元数据见 <see cref="SkinRegistry"/>：宿主扫描 <c>neko-assets/&lt;skinId&gt;/skin.json</c>，
/// 本类只负责按该清单把帧读成 WPF 可直接绘制的 <see cref="BitmapSource"/>。</para>
///
/// <para>两种帧组织方式（由 skin.json 的 <c>layout</c> 决定）：</para>
/// <list type="bullet">
///   <item><c>sheet</c>：每状态一张 <c>neko_&lt;state&gt;.png</c>，横向排列 frames 帧（像素版）。</item>
///   <item><c>files</c>：每状态每帧一张 <c>neko_&lt;state&gt;_&lt;index&gt;.png</c>（柔光版）。</item>
/// </list>
///
/// <para><b>FrameW/FrameH 恒为 32</b>：这是宿主绘制尺寸（逻辑单位）；柔光版源位图是 128 像素，
/// 由 WPF 在 Draw 时自动缩放到 FrameW*Scale。窗口尺寸计算依赖这两个常量，故不随皮肤变化。</para>
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
    /// <summary>绘制尺寸（逻辑单位）。皮肤源位图可大于此值，由 WPF 在 Draw 时自动缩放。</summary>
    public const int FrameW = 32;
    public const int FrameH = 32;

    private readonly Dictionary<string, SpriteState> _states = new();
    private readonly Dictionary<string, BitmapSource> _eyes = new();
    public BitmapSource? Shadow { get; private set; }
    /// <summary>皮肤 id（来自 skin.json）。</summary>
    public string Skin { get; private set; } = SkinRegistry.DefaultSkinId;

    /// <summary>
    /// v0.1.81：皮肤作者建议的显示倍率（透传自 skin.json 的 <c>recommendedScale</c>）。
    /// <para><c>&gt; 0</c> 时宿主在切换本皮肤后应把缩放套用到该值；
    /// <c>&lt;= 0</c>（默认）表示未声明，宿主不得改用户缩放。</para>
    /// </summary>
    public double RecommendedScale { get; private set; }

    public IReadOnlyDictionary<string, SpriteState> States => _states;

    /// <summary>按皮肤 id 加载（向后兼容旧签名，内部走 <see cref="LoadSkin"/>）。</summary>
    public static SpriteAtlas Load(string assetsDir, string skin) => LoadSkin(assetsDir, skin);

    /// <summary>
    /// v0.1.74：按 <c>&lt;assetsRoot&gt;/&lt;skinId&gt;/skin.json</c> 加载皮肤。
    /// <para>skinId 未注册 → 回退默认皮肤 → 再回退内置规格；任何缺失/损坏都不抛异常。</para>
    /// </summary>
    public static SpriteAtlas LoadSkin(string assetsRoot, string? skinId)
    {
        var info = SkinRegistry.Resolve(assetsRoot, skinId);
        // 内置兜底没有目录信息时，按约定指向 &lt;assetsRoot&gt;/&lt;id&gt;
        var dir = string.IsNullOrEmpty(info.Directory) && !string.IsNullOrEmpty(assetsRoot)
            ? Path.Combine(assetsRoot, info.Id)
            : info.Directory;
        return LoadFromInfo(info, dir);
    }

    /// <summary>
    /// 按已解析的皮肤元数据加载素材。
    /// <para><b>fps 唯一权威 = skin.json 的 states[].fps</b>（v0.1.76 起）。
    /// 历史上曾有第二处 fps 来源（皮肤目录内的 anim.json 覆盖表，v0.1.58 引入），
    /// 它会静默盖掉 skin.json，导致用户自制皮肤「只改 skin.json 的 fps 不生效」——
    /// 该覆盖路径已移除，skin.json 是素材侧唯一 fps 声明点。</para>
    /// </summary>
    private static SpriteAtlas LoadFromInfo(SkinInfo info, string skinDir)
    {
        var atlas = new SpriteAtlas { Skin = info.Id, RecommendedScale = info.RecommendedScale };

        foreach (var state in info.States)
        {
            var frames = new SpriteFrame[info.Frames];
            bool complete = true;

            if (info.Layout == SkinLayout.Files)
            {
                for (int f = 0; f < info.Frames; f++)
                {
                    var p = Path.Combine(skinDir, $"neko_{state.Name}_{f}.png");
                    if (!File.Exists(p)) { complete = false; break; }
                    frames[f] = new SpriteFrame { Bitmap = LoadPng(p) };
                }
            }
            else
            {
                var p = Path.Combine(skinDir, $"neko_{state.Name}.png");
                if (!File.Exists(p)) complete = false;
                else
                {
                    var sheet = LoadPng(p);
                    for (int f = 0; f < info.Frames; f++)
                    {
                        frames[f] = new SpriteFrame
                        {
                            Bitmap = Crop(sheet, f * info.FrameWidth, 0, info.FrameWidth, info.FrameHeight),
                        };
                    }
                }
            }

            if (!complete) continue;   // 缺帧 → 跳过该状态（不崩，行为同旧版）

            atlas._states[state.Name] = new SpriteState
            {
                Name = state.Name,
                Frames = frames,
                Fps = state.Fps,
                EyeLayer = state.EyeLayer,
            };
        }

        // 眼球层（仅清单声明的 key；文件缺失则跳过）
        foreach (var eye in info.EyeLayers)
        {
            var p = Path.Combine(skinDir, $"neko_{eye}.png");
            if (File.Exists(p)) atlas._eyes[eye] = LoadPng(p);
        }

        // 影子层
        if (info.HasShadow)
        {
            var shadowPath = Path.Combine(skinDir, "neko_shadow.png");
            if (File.Exists(shadowPath)) atlas.Shadow = LoadPng(shadowPath);
        }

        return atlas;
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
