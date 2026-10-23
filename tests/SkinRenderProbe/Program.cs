// =============================================================================================
// SkinRenderProbe —— 皮肤眼球层「逐相离屏渲染 + md5 比对」验收工具
// =============================================================================================
//
// 【它验什么】
//   皮肤的眼球是**独立叠加层**（只画瞳孔，眼白留在身体帧上）。PetRenderer 在 Draw 里按
//   「眨眼相位 / 键盘瞪眼」从**全局眼球层键**（eyes_half / eyes_blink / eyes_scared）选层；
//   皮肤没提供某个键时，若代码直接取到 null，就会把**整个眼球层丢掉** → 露出「被抹掉瞳孔的
//   身体帧」→ 眼睛短暂空白（像素系皮肤有这些键，AI 皮肤没有，于是只有新皮肤中招）。
//
//   这个工具把 PetRenderer.Draw 的每个「眼球层选择分支」**确定性地**驱动一遍并离屏出图，
//   对每张图取像素 md5。同一 (皮肤, 状态) 下各分支 md5 的**异同**就能暴露上述缺陷：
//     · 缺 blink 素材的皮肤，修复后 open == half == closed（不眨眼＝保持瞳孔）；修复前 closed ≠ open。
//     · 有 blink 素材的像素皮肤，three phase 互不相同（真眨眼），且修复前后逐字节一致（不回归）。
//     · 缺 eyes_scared 的皮肤，修复后 flash == open（瞪眼不生效但**不空白**）；修复前 flash ≠ open。
//
//   为什么用离屏渲染而不是真机连拍：真机上猫窗身体逐帧动 + 眼神追踪 ±2px 平移会污染整帧比对，
//   无法干净判定「相位是否空白」；离屏渲染排除这些干扰，且走的**就是线上那条 Draw(dc)**。
//
// 【怎么跑】
//   dotnet run --project tests/SkinRenderProbe -c Debug -- [选项]
//   或先构建再跑 exe（默认输出目录见下）。选项：
//     --skin   a,b       只验这些皮肤 id（逗号分隔）；缺省 = 扫描素材目录下所有含 skin.json 的皮肤
//     --assets <dir>     素材根（内含 <skin>/skin.json）；缺省 = 从 exe 目录向上查找仓库的 neko-assets
//     --out    <dir>     出图目录；缺省 = 输出目录/skinrender-out
//     --scale  <n>       覆盖缩放；缺省 = 皮肤 recommendedScale（未声明则 4）
//   例：dotnet run --project tests/SkinRenderProbe -c Debug -- --skin cat_hd,pixel --out G:/tmp/srp
//
// 【怎么读结果】
//   stdout 每行一个 (皮肤, 状态)：打印各分支 md5 与「是否 == open」。
//   出图目录里：
//     <skin>/<state>_<branch>.png —— 每个分支的原始渲染
//     _sheet_<skin>.png           —— 对照图：行=状态，列=open/half/closed/flash；
//                                    每个单元格标注 md5；**红框 = 与 open 逐字节不同**，绿框 = 相同。
//   ⚠️ 判据：**md5 相同 = 两张图逐字节一致**（该相位没有改变画面）。
//      · 修复目标：无 blink/scared 素材的皮肤，各相位应全绿（== open）。
//      · 反例防护：像素皮肤 half/closed/flash 应**红**（确有变化）；若变绿即回归，需深究。
//
// ⚠️ 本工程**不加入 src/DesktopPet.sln**（与其它 tests/ 工具一致，按需单跑）。
// =============================================================================================

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopPet.App.Renderer;
using DesktopPet.Core;

namespace DesktopPet.Tests.SkinRenderProbe;

internal static class Program
{
    /// <summary>眼球层选择分支：open=正常；half/closed=眨眼相位；flash=键盘瞪眼（eyes_scared）。</summary>
    private static readonly (string Key, string Label)[] Branches =
    {
        ("open", "open(正常)"),
        ("half", "half(半闭)"),
        ("closed", "closed(闭合)"),
        ("flash", "flash(瞪眼)"),
    };

    private static readonly (PetState State, string Name)[] TargetStates =
    {
        (PetState.Idle, "idle"),
        (PetState.Sit, "sit"),
    };

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[x] 未预期异常: " + ex);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        string? assetsArg = Arg(args, "--assets");
        string? outArg = Arg(args, "--out");
        string? skinArg = Arg(args, "--skin");
        string? scaleArg = Arg(args, "--scale");

        string? assets = assetsArg;
        if (assets is null)
        {
            string local = Path.Combine(AppContext.BaseDirectory, "neko-assets");
            assets = Directory.Exists(local) ? local : FindUpward("neko-assets");
        }
        if (assets is null || !Directory.Exists(assets))
        {
            Console.Error.WriteLine("[x] 找不到素材目录。请用 --assets <含 <skin>/skin.json 的目录>。");
            return 1;
        }

        string outDir = outArg ?? Path.Combine(AppContext.BaseDirectory, "skinrender-out");
        Directory.CreateDirectory(outDir);

        string[] skins = skinArg is not null
            ? skinArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : DiscoverSkins(assets);
        if (skins.Length == 0)
        {
            Console.Error.WriteLine($"[x] 素材目录下没有发现任何皮肤（{assets}）。");
            return 1;
        }

        FieldInfo blinkField = typeof(PetRenderer).GetField("_blinkPhase", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Type blinkType = blinkField.FieldType;
        FieldInfo flashField = typeof(PetRenderer).GetField("_flashEyesUntilMs", BindingFlags.NonPublic | BindingFlags.Instance)!;

        Console.WriteLine($"[i] 素材根: {assets}");
        Console.WriteLine($"[i] 出图  : {outDir}");
        Console.WriteLine($"[i] 皮肤  : {string.Join(", ", skins)}");
        Console.WriteLine();

        int okSkins = 0;
        foreach (string skin in skins)
        {
            SpriteAtlas atlas;
            try { atlas = SpriteAtlas.LoadSkin(assets, skin); }
            catch (Exception ex) { Console.WriteLine($"!! {skin}: 加载失败 {ex.Message}"); continue; }

            double scale = scaleArg is not null && double.TryParse(scaleArg, NumberStyles.Float, CultureInfo.InvariantCulture, out double s) && s > 0
                ? s
                : (atlas.RecommendedScale > 0 ? atlas.RecommendedScale : 4.0);

            bool hasHalf = atlas.GetEye("eyes_half") is not null;
            bool hasBlink = atlas.GetEye("eyes_blink") is not null;
            bool hasScared = atlas.GetEye("eyes_scared") is not null;

            Console.WriteLine($"== {skin}  (scale={scale:0.##}  全局键: eyes_half={(hasHalf ? "有" : "缺")} "
                              + $"eyes_blink={(hasBlink ? "有" : "缺")} eyes_scared={(hasScared ? "有" : "缺")})");

            var renderer = new PetRenderer(atlas) { Scale = scale };
            string skinOut = Path.Combine(outDir, skin);
            Directory.CreateDirectory(skinOut);

            var rowLabels = new List<string>();
            var grid = new List<List<Cell>>();

            foreach ((PetState st, string stName) in TargetStates)
            {
                atlas.States.TryGetValue(stName, out SpriteState? stateObj);
                string eyeLayer = stateObj?.EyeLayer ?? "(无此状态)";
                Console.WriteLine($"   - state={stName}  eyeLayer={eyeLayer}");

                renderer.Play(st);

                var row = new List<Cell>();
                string openMd5 = "";
                foreach ((string key, string label) in Branches)
                {
                    (string md5, BitmapSource bmp) = RenderBranch(renderer, blinkField, blinkType, flashField, key);
                    SavePng(bmp, Path.Combine(skinOut, $"{stName}_{key}.png"));
                    if (key == "open") openMd5 = md5;
                    row.Add(new Cell { Md5 = md5, Bmp = bmp, Label = label, StateName = stName, Key = key });
                }

                for (int i = 0; i < row.Count; i++)
                {
                    Cell c = row[i];
                    string rel = i == 0 ? "(基准)" : (c.Md5 == openMd5 ? "== open" : "!= open");
                    Console.WriteLine($"        {c.Label,-14} md5={c.Md5}  {rel}");
                }

                rowLabels.Add($"{skin}\n{stName}\n({eyeLayer})");
                grid.Add(row);
                Console.WriteLine();
            }

            string sheet = Path.Combine(outDir, $"_sheet_{skin}.png");
            BuildSheet(sheet, skin, atlas, hasHalf, hasBlink, hasScared, grid, rowLabels);
            Console.WriteLine($"   [✓] 对照图: {sheet}");
            Console.WriteLine();
            okSkins++;
        }

        Console.WriteLine($"[done] {okSkins}/{skins.Length} 皮肤已出图 → out\\_sheet_<skin>.png 与 out\\<skin>\\<state>_<branch>.png");
        Console.WriteLine("       读法：同状态下 md5 相同 = 与 open 逐字节一致（相位未改变画面）；红框 = 与 open 不同。");
        return 0;
    }

    /// <summary>把渲染器驱动到指定分支并离屏出图；返回像素 md5（不是 PNG 文件 md5，避免编码器差异）。</summary>
    private static (string Md5, BitmapSource Bmp) RenderBranch(
        PetRenderer r, FieldInfo blinkField, Type blinkType, FieldInfo flashField, string branchKey)
    {
        // blink 相位：flash 分支用 Open（它的画面由 _flashEyesUntilMs 决定）
        string phaseName = branchKey switch
        {
            "half" => "Half",
            "closed" => "Closed",
            _ => "Open",
        };
        blinkField.SetValue(r, Enum.Parse(blinkType, phaseName));
        // 瞪眼截止：flash 分支设为 double.MaxValue（_clockMs < 截止 恒真）；其余归 0（早已过期）
        flashField.SetValue(r, branchKey == "flash" ? double.MaxValue : 0.0);

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, r.Atlas.PixelArt ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        using (DrawingContext dc = visual.RenderOpen())
        {
            r.Draw(dc);
        }

        int w = (int)Math.Round(SpriteAtlas.FrameW * r.Scale);
        int h = (int)Math.Round(SpriteAtlas.FrameH * r.Scale);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();

        var pixels = new byte[w * h * 4];
        rtb.CopyPixels(pixels, w * 4, 0);
        string md5 = Convert.ToHexString(MD5.HashData(pixels)).ToLowerInvariant()[..10];
        return (md5, rtb);
    }

    private static void BuildSheet(
        string path, string skin, SpriteAtlas atlas, bool hasHalf, bool hasBlink, bool hasScared,
        List<List<Cell>> grid, List<string> rowLabels)
    {
        const int Cell = 132, LabelW = 176, Pad = 10, CapH = 34, HeadH = 62;
        int cols = Branches.Length;
        int rows = grid.Count;
        int w = LabelW + cols * (Cell + Pad) + Pad;
        int h = HeadH + rows * (Cell + CapH + Pad) + Pad + 20;

        var dv = new DrawingVisual();
        using (DrawingContext dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(240, 241, 244)), null, new Rect(0, 0, w, h));
            DrawText(dc, $"皮肤 {skin}   [half={(hasHalf ? "有" : "缺")} blink={(hasBlink ? "有" : "缺")} "
                         + $"scared={(hasScared ? "有" : "缺")}]   frame={atlas.States.Count} 状态", 10, 8, 15, Brushes.Black);
            DrawText(dc, "读法：md5 相同 = 与 open 逐字节一致（该相位未改变画面）；红框 = 与 open 不同。"
                         + "无素材皮肤应全绿；像素皮肤 half/closed/flash 应为红。", 10, 34, 12, Brushes.DimGray);

            for (int c = 0; c < cols; c++)
            {
                DrawText(dc, Branches[c].Label, LabelW + c * (Cell + Pad) + 4, HeadH - 20, 12, Brushes.Black);
            }

            int y = HeadH;
            for (int ri = 0; ri < rows; ri++)
            {
                DrawText(dc, rowLabels[ri], 10, y + Cell / 2 - 14, 13, Brushes.Black);
                List<Cell> row = grid[ri];
                string openMd5 = row[0].Md5;
                for (int ci = 0; ci < cols; ci++)
                {
                    Cell cell = row[ci];
                    int x = LabelW + ci * (Cell + Pad);
                    dc.DrawImage(cell.Bmp, new Rect(x, y, Cell, Cell));

                    bool differs = ci != 0 && cell.Md5 != openMd5;
                    Brush pen = ci == 0
                        ? Brushes.Gray
                        : differs ? Brushes.Crimson : Brushes.SeaGreen;
                    dc.DrawRectangle(null, new Pen(pen, ci == 0 ? 1 : (differs ? 3 : 2)), new Rect(x, y, Cell, Cell));

                    string note = ci == 0 ? $"md5={cell.Md5}" : $"md5={cell.Md5} {(differs ? "!=open" : "==open")}";
                    DrawText(dc, note, x + 2, y + Cell + 4, 11, differs ? Brushes.Crimson : Brushes.DarkSlateGray);
                }
                y += Cell + CapH + Pad;
            }
        }

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        SavePng(rtb, path);
    }

    private static void DrawText(DrawingContext dc, string text, double x, double y, double size, Brush brush)
    {
        var typeface = new Typeface(new FontFamily("Microsoft YaHei"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, size, brush, 1.0);
        dc.DrawText(ft, new Point(x, y));
    }

    private static void SavePng(BitmapSource bmp, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using FileStream fs = File.Create(path);
        enc.Save(fs);
    }

    private static string[] DiscoverSkins(string assetsRoot)
        => Directory.GetDirectories(assetsRoot)
            .Where(d => File.Exists(Path.Combine(d, "skin.json")))
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

    private static string? FindUpward(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            string cand = Path.Combine(dir.FullName, name);
            if (Directory.Exists(cand)) return cand;
        }
        return null;
    }

    private static string? Arg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }
        return null;
    }

    private sealed class Cell
    {
        public required string Md5 { get; init; }
        public required BitmapSource Bmp { get; init; }
        public required string Label { get; init; }
        public required string StateName { get; init; }
        public required string Key { get; init; }
    }
}
