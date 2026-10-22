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
    public bool PixelArt { get; private set; } = true;

    /// <summary>
    /// v0.1.88：皮肤源位图相对宿主逻辑尺寸（<see cref="FrameW"/> = 32）的倍数 = 「源像素 / 逻辑像素」。
    /// 眼神追踪的偏移量以逻辑像素给出，落到源图上会被放大这个倍数 ——
    /// 高清皮 256 宽时 k=8（±2 逻辑px → ±16 源px），而眼白只有 ~35px 宽 → 瞳孔会甩出眼眶。
    /// </summary>
    public double SourcePixelsPerLogical { get; private set; } = 1.0;

    /// <summary>
    /// v0.1.88：各眼球层允许的最大位移（**源位图像素**，X/Y 分轴）。超过就会「瞳孔跑出眼眶」。
    /// 由「瞳孔 bbox」与「闭合层 bbox（同源、覆盖整只眼，可当眼眶）」逐眼作差得出：
    /// <c>safe = 0.45 × (眼眶 − 瞳孔)</c>，取两眼较小者。</summary>
    private readonly Dictionary<string, Vector> _eyeSafeShift = new();

    /// <summary>
    /// v0.1.89（性能）：各眼球层非透明像素的包围盒（**源位图像素**）。
    /// <para>用途：渲染时把眼球层的绘制区裁到该 bbox（+ 偏移 + 余量）——该矩形覆盖所有可能产生
    /// 非透明像素的位置，被裁掉的都是**已经透明**的像素，故输出逐像素不变；却能把
    /// <c>PushOpacityMask</c> 的中间表面与位图栅格化面积从「整画幅」缩到「眼睛实际占用区」。</para>
    /// <para>只对**新管线皮肤**（有 <c>{layer}_closed</c>，即会用遮罩的皮肤）填充——内置像素皮等
    /// 旧皮肤无此数据 → 渲染路径完全不变（零回归）。</para>
    /// </summary>
    private readonly Dictionary<string, Int32Rect> _eyeBBox = new();

    /// <summary>取某眼球层非透明像素包围盒（源位图像素）。返回 false 表示无数据（调用方不做裁剪）。</summary>
    public bool TryGetEyeBBox(string key, out Int32Rect bbox) => _eyeBBox.TryGetValue(key, out bbox);

    /// <summary>该层允许的最大位移（源像素）。返回 (0,0) 表示**无数据、调用方不要限制**（保持旧皮肤行为）。</summary>
    public Vector GetEyeSafeShift(string key) => _eyeSafeShift.GetValueOrDefault(key);

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
        var atlas = new SpriteAtlas { Skin = info.Id, RecommendedScale = info.RecommendedScale, PixelArt = info.PixelArt };

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

        // v0.1.88：算各眼球层的「安全位移」（源像素）。
        // 为什么需要：眼神追踪的偏移量原本锚在**画幅**上（±2 逻辑px × Scale），
        // 而皮肤源图尺寸差异极大（pixel 32 宽 / cat_hd 256 宽）→ 同一个逻辑偏移落到源图上
        // 会被放大 frameWidth/32 倍（高清皮 = ±16 源px），而瞳孔几乎填满眼白（只差 3~5px）
        // → 瞳孔甩出眼眶。灯灯实机反馈「高清版眼睛超出眼眶，看着太恐怖」即此。
        // 门控：只对**新管线皮肤**生效（它们才有 `{key}_closed`）—— 内置像素皮等旧皮肤**完全保持原行为**。
        atlas.SourcePixelsPerLogical = Math.Max(1.0, info.FrameWidth / (double)FrameW);
        foreach (var eye in info.EyeLayers)
        {
            if (!atlas._eyes.TryGetValue(eye, out var pupil)) continue;
            if (!atlas._eyes.TryGetValue(eye + "_closed", out var socket)) continue;   // 旧皮肤（无闭合层）→ 不限制、不裁剪
            var safe = ComputeEyeSafeShift(pupil);
            if (safe is null) continue;
            atlas._eyeSafeShift[eye] = safe.Value;
            // 闭合层共用同一上限：眨眼时眼睛不该比睁眼时跑得更远
            atlas._eyeSafeShift[eye + "_closed"] = safe.Value;
            // v0.1.89（性能）：记录瞳孔/闭合层的非透明包围盒（源像素）——渲染时据此裁剪绘制区，
            // 把遮罩中间表面从整画幅缩到眼睛区。仅新管线皮肤有数据，旧皮肤渲染路径零变化。
            atlas._eyeBBox[eye] = AlphaBBox(pupil);
            atlas._eyeBBox[eye + "_closed"] = AlphaBBox(socket);
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
    /// v0.1.88：算「眼睛允许的瞳孔位移」（源位图像素，X/Y 同一标量）。
    /// <para><b>为什么以瞳孔自身尺寸为锚</b>：位移的合理上限取决于「瞳孔相对眼眶有多大」。
    /// 实测高清皮的瞳孔几乎填满眼白（`cat_hd/idle` 瞳 29×29 vs 眶 34×32，只差 3~5px），
    /// 所以不能按画幅比例给位移 —— 那正是「瞳孔甩出眼眶」的根因。</para>
    /// <para>做法：列投影切出各瞳孔块 → 丢掉面积 &lt; 最大块 10% 的噪声块
    /// （闭眼差分会留零星像素，实测有 2×3 / 1×1 的碎块，用 min 会被噪声带偏）
    /// → 取**保留块的联合 bbox**，其**较小边 × 0.18** 即允许位移。</para>
    /// </summary>
    private static Vector? ComputeEyeSafeShift(BitmapSource pupil)
    {
        int w = pupil.PixelWidth, h = pupil.PixelHeight;
        if (w <= 0 || h <= 0) return null;

        const byte TH = 16;
        var mask = AlphaMask(pupil, TH);
        var runs = Runs(AlphaColumns(pupil, TH));       // 两眼水平分开 → 列投影天然分段
        if (runs.Count == 0) return null;

        // 各块的 bbox 与面积
        var blocks = new List<(int x0, int y0, int x1, int y1, int area)>();
        foreach (var (x0, x1) in runs)
        {
            int bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = -1, by1 = -1, area = 0;
            for (int x = x0; x <= x1; x++)
                for (int y = 0; y < h; y++)
                    if (mask[y * w + x])
                    {
                        area++;
                        if (x < bx0) bx0 = x;
                        if (x > bx1) bx1 = x;
                        if (y < by0) by0 = y;
                        if (y > by1) by1 = y;
                    }
            if (area > 0) blocks.Add((bx0, by0, bx1, by1, area));
        }
        if (blocks.Count == 0) return null;

        int maxArea = 0;
        foreach (var b in blocks) maxArea = Math.Max(maxArea, b.area);
        var kept = blocks.Where(b => b.area >= maxArea * 0.10).ToList();   // 去噪
        if (kept.Count == 0) return null;

        int ux0 = kept.Min(b => b.x0), ux1 = kept.Max(b => b.x1);
        int uy0 = kept.Min(b => b.y0), uy1 = kept.Max(b => b.y1);
        double minDim = Math.Min(ux1 - ux0 + 1, uy1 - uy0 + 1);
        double safe = Math.Max(0.5, 0.18 * minDim);        // 至少 0.5 源px，避免完全不能动
        return new Vector(safe, safe);
    }

    /// <summary>读 alpha（统一转 Bgra32）为布尔掩码，返回**行优先**的一维数组。</summary>
    private static bool[] AlphaMask(BitmapSource src, byte threshold)
    {
        var cv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = cv.PixelWidth, h = cv.PixelHeight, stride = w * 4;
        var buf = new byte[stride * h];
        cv.CopyPixels(buf, stride, 0);
        var m = new bool[w * h];
        for (int i = 0; i < m.Length; i++) m[i] = buf[i * 4 + 3] > threshold;
        return m;
    }

    /// <summary>v0.1.89：源位图 alpha&gt;16 的包围盒（源像素）。全透明 / 空图返回 (0,0,0,0)。</summary>
    private static Int32Rect AlphaBBox(BitmapSource src)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        if (w <= 0 || h <= 0) return new Int32Rect(0, 0, 0, 0);
        var m = AlphaMask(src, 16);
        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (m[y * w + x])
                {
                    if (x < x0) x0 = x;
                    if (x > x1) x1 = x;
                    if (y < y0) y0 = y;
                    if (y > y1) y1 = y;
                }
        if (x1 < x0 || y1 < y0) return new Int32Rect(0, 0, 0, 0);
        return new Int32Rect(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }
    /// <summary>读 alpha（统一转 Bgra32），返回「每列是否存在不透明像素」。</summary>
    private static bool[] AlphaColumns(BitmapSource src, byte threshold)
    {
        var cv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = cv.PixelWidth, h = cv.PixelHeight, stride = w * 4;
        var buf = new byte[stride * h];
        cv.CopyPixels(buf, stride, 0);
        var cols = new bool[w];
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
                if (buf[y * stride + x * 4 + 3] > threshold) { cols[x] = true; break; }
        return cols;
    }

    /// <summary>布尔序列的连续 true 段。</summary>
    private static List<(int x0, int x1)> Runs(bool[] cols)
    {
        var list = new List<(int x0, int x1)>();
        int s = -1;
        for (int i = 0; i <= cols.Length; i++)
        {
            bool hit = i < cols.Length && cols[i];
            if (hit && s < 0) s = i;
            else if (!hit && s >= 0) { list.Add((s, i - 1)); s = -1; }
        }
        return list;
    }

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
