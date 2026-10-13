"""静态 PNG → 拼豆网格（``neko.beads/1``）转换器。

把**任意静态图**（AI 生图、截图、手绘导出）降采样成规整的「珠粒网格」，输出可直接烘焙的
``neko.beads/1`` 模型 JSON（1 个部件、1 个形状变体）。这样新素材不必手画网格。

算法（按序，全整数/确定性，无随机）
----------------------------------
1. **前景判定**：``alpha < tau_alpha``（默认 8）→ 透明格。
2. **背景键控**：取图像四角各 ``8×8`` 区域 RGB 中位数作背景色；前景像素若与背景色欧氏距离
   ``< tau_bg``（默认 48）**且**位于图像外圈 2 格内 → 也判透明（吃掉 AI 生图常见的浅底/白底）。
3. **降采样**：按目标网格切块，每块取**前景像素的 alpha 加权主色**（先按 8 级/通道分桶取最大权重
   桶，桶内再按 alpha 加权平均）；块内前景占比 ``< coverage``（默认 0.25）→ 透明格。
4. **调色板映射**：单元 RGB → 最近邻，距离用 **CIELAB ΔE**（sRGB→线性→XYZ→Lab，D65 白点），
   不用裸 RGB 欧氏距离。并列时取调色板声明序在前者（确定性）。
5. **对齐**：内容按 **底边对齐（``baseline_row``，默认 25）+ 水平居中** 贴到 ``canvas×canvas`` 画布。
6. **输出**：``neko.beads/1`` JSON；``--preview`` 时额外打印 ASCII 预览（``#`` = 有色，``.`` = 透明）。

用法::

    python tools/bead/bead_convert.py in.png out.beads.json --preview
    python tools/bead/bead_convert.py cat_sit.png sit.beads.json --canvas 32 --tau-bg 48

依赖：解码优先用 **Pillow**（可读任意 PNG/其它格式）；无 Pillow 时回退到 :mod:`bead_bake`
自带的 ``read_png_rgba``（仅能读本工具链写出的 filter=0 PNG）。CIELAB 与降采样为纯 Python，
**不依赖 numpy**。
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

try:  # 包内导入
    from .bead_model import (
        FORMAT,
        Frame,
        Model,
        Part,
        Pose,
        dumps_model,
        load_palette,
    )
except ImportError:  # 直接运行脚本
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    from bead_model import (  # type: ignore[no-redef]
        FORMAT,
        Frame,
        Model,
        Part,
        Pose,
        dumps_model,
        load_palette,
    )

DEFAULT_PALETTE = Path(__file__).resolve().parent / "palette.json"
"""调色板随脚本走（工具自带资产）；可用 ``--palette`` 覆盖。"""

DEFAULT_CANVAS = 32
DEFAULT_TAU_ALPHA = 8
DEFAULT_TAU_BG = 48
DEFAULT_COVERAGE = 0.25
DEFAULT_BASELINE_ROW = 25
"""内容底边所在行（0 起，画布行坐标）。沿用 Neko 拼豆烘焙的 baseline：64px 帧里最后一行不透明像素 y=51 → 32 格画布第 25 行。"""

DEFAULT_BAKE_SCALE = 2
CORNER_SAMPLE = 8
BUCKET_SHIFT = 3  # 每通道 >>3 → 32 级（8 级合并成 1 桶的粒度可调）

_CHAR_POOL = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ"
"""单字符编码池（``.`` 保留给透明）。"""


# ---------------------------------------------------------------------------
# 颜色（纯 Python）
# ---------------------------------------------------------------------------


def _srgb_to_linear(v: int) -> float:
    c = v / 255.0
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def rgb_to_lab(rgb: tuple[int, int, int]) -> tuple[float, float, float]:
    """sRGB → CIE L*a*b*（D65 白点）。"""
    r, g, b = (_srgb_to_linear(v) for v in rgb)
    # sRGB → XYZ（D65）
    x = r * 0.4124564 + g * 0.3575761 + b * 0.1804375
    y = r * 0.2126729 + g * 0.7151522 + b * 0.0721750
    z = r * 0.0193339 + g * 0.1191920 + b * 0.9503041
    # 归一到 D65 白点
    x /= 0.95047
    y /= 1.00000
    z /= 1.08883

    def f(t: float) -> float:
        return t ** (1.0 / 3.0) if t > 0.008856 else (7.787 * t + 16.0 / 116.0)

    fx, fy, fz = f(x), f(y), f(z)
    return (116.0 * fy - 16.0, 500.0 * (fx - fy), 200.0 * (fy - fz))


def _nearest_key(rgb: tuple[int, int, int], lab_table: list[tuple[str, tuple[float, float, float]]]) -> str:
    """按 CIE76 ΔE（Lab 欧氏）取最近调色板 key；并列取声明序在前者（确定性）。"""
    lab = rgb_to_lab(rgb)
    best_key = lab_table[0][0]
    best_d = None
    for key, plab in lab_table:
        d = (lab[0] - plab[0]) ** 2 + (lab[1] - plab[1]) ** 2 + (lab[2] - plab[2]) ** 2
        if best_d is None or d < best_d:
            best_d = d
            best_key = key
    return best_key


# ---------------------------------------------------------------------------
# 解码
# ---------------------------------------------------------------------------


def load_rgba(path: str | Path) -> tuple[int, int, list[tuple[int, int, int, int]]]:
    """解码图片为 ``(width, height, pixels)``，``pixels`` 为 row-major 的 RGBA 元组列表。

    优先 Pillow；无 Pillow 时回退到 :func:`bead_bake.read_png_rgba`（仅 filter=0）。
    """
    try:
        from PIL import Image  # type: ignore
    except ImportError:
        Image = None  # type: ignore

    if Image is not None:
        with Image.open(path) as im:
            rgba = im.convert("RGBA")
            w, h = rgba.size
            # 用 tobytes() 而非已弃用的 getdata()（Pillow ≥ 12 会告警）。
            raw = rgba.tobytes()
            pixels = [(raw[i], raw[i + 1], raw[i + 2], raw[i + 3]) for i in range(0, len(raw), 4)]
            return w, h, pixels

    try:
        from .bead_bake import read_png_rgba
    except ImportError:
        from bead_bake import read_png_rgba  # type: ignore[no-redef]

    w, h, raw = read_png_rgba(path)
    pixels = [tuple(raw[i:i + 4]) for i in range(0, len(raw), 4)]  # type: ignore[misc]
    return w, h, pixels  # type: ignore[return-value]


def _median(values: list[int]) -> int:
    values = sorted(values)
    return values[len(values) // 2]


def corner_background(
    pixels: list[tuple[int, int, int, int]], width: int, height: int, k: int = CORNER_SAMPLE
) -> tuple[int, int, int]:
    """四角各 ``k×k`` 区域 RGB 的**通道中位数**（对噪点/水印稳健）。"""
    corners = ((0, 0), (width - k, 0), (0, height - k), (width - k, height - k))
    rs: list[int] = []
    gs: list[int] = []
    bs: list[int] = []
    for cx, cy in corners:
        for y in range(max(0, cy), min(height, cy + k)):
            base = y * width
            for x in range(max(0, cx), min(width, cx + k)):
                r, g, b, _a = pixels[base + x]
                rs.append(r)
                gs.append(g)
                bs.append(b)
    return (_median(rs), _median(gs), _median(bs))


# ---------------------------------------------------------------------------
# 前景掩膜 + 网格
# ---------------------------------------------------------------------------


def foreground_mask(
    pixels: list[tuple[int, int, int, int]],
    width: int,
    height: int,
    bg: tuple[int, int, int],
    tau_alpha: int,
    tau_bg: int,
    margin_x: float,
    margin_y: float,
) -> bytearray:
    """1 = 前景，0 = 透明。含「外圈 margin 内 + 接近背景色」的键控。"""
    mask = bytearray(width * height)
    bgr, bgg, bgb = bg
    tau2 = tau_bg * tau_bg
    for y in range(height):
        base = y * width
        border_y = y < margin_y or y >= height - margin_y
        for x in range(width):
            r, g, b, a = pixels[base + x]
            if a < tau_alpha:
                continue
            dr = r - bgr
            dg = g - bgg
            db = b - bgb
            if dr * dr + dg * dg + db * db < tau2 and (
                border_y or x < margin_x or x >= width - margin_x
            ):
                continue
            mask[base + x] = 1
    return mask


def _mask_bbox(mask: bytearray, width: int, height: int) -> tuple[int, int, int, int] | None:
    x0, y0, x1, y1 = width, height, -1, -1
    for y in range(height):
        base = y * width
        for x in range(width):
            if mask[base + x]:
                if x < x0:
                    x0 = x
                if x > x1:
                    x1 = x
                if y < y0:
                    y0 = y
                if y > y1:
                    y1 = y
    if x1 < 0:
        return None
    return x0, y0, x1, y1


def _cell_color(
    pixels: list[tuple[int, int, int, int]],
    mask: bytearray,
    width: int,
    sx0: int,
    sy0: int,
    sx1: int,
    sy1: int,
    coverage: float,
    tau_alpha: int,
) -> tuple[int, int, int] | None:
    """取一格前景像素的 alpha 加权主色；覆盖不足返回 None。"""
    total = (sx1 - sx0) * (sy1 - sy0)
    if total <= 0:
        return None
    fg = 0
    buckets: dict[tuple[int, int, int], list[float]] = {}
    for y in range(sy0, sy1):
        base = y * width
        for x in range(sx0, sx1):
            if not mask[base + x]:
                continue
            r, g, b, a = pixels[base + x]
            if a < tau_alpha:
                continue
            fg += 1
            bkey = (r >> BUCKET_SHIFT, g >> BUCKET_SHIFT, b >> BUCKET_SHIFT)
            acc = buckets.get(bkey)
            if acc is None:
                buckets[bkey] = [float(a), r * a, g * a, b * a]
            else:
                acc[0] += a
                acc[1] += r * a
                acc[2] += g * a
                acc[3] += b * a
    if fg / total < coverage or not buckets:
        return None
    # 最大权重桶；并列取桶键最小者（确定性）
    best_key = min(buckets, key=lambda kk: (-buckets[kk][0], kk))
    wsum, rsum, gsum, bsum = buckets[best_key]
    if wsum <= 0:
        return None
    return (
        int(round(rsum / wsum)),
        int(round(gsum / wsum)),
        int(round(bsum / wsum)),
    )


def to_grid(
    bbox: tuple[int, int, int, int],
    canvas: int,
    max_h: int,
) -> list[list[tuple[int, int, int, int]]]:
    """把 bbox 内容按纵横比缩到 ``≤canvas × ≤max_h`` 的网格，返回每格的源像素矩形。"""
    bx0, by0, bx1, by1 = bbox
    bw = bx1 - bx0 + 1
    bh = by1 - by0 + 1
    scale = min(canvas / bw, max_h / bh)
    gw = min(canvas, max(1, int(round(bw * scale))))
    gh = min(max_h, max(1, int(round(bh * scale))))

    grid: list[list[tuple[int, int, int, int]]] = []
    for cy in range(gh):
        sy0 = by0 + (cy * bh) // gh
        sy1 = by0 + ((cy + 1) * bh) // gh
        if sy1 <= sy0:
            sy1 = min(by1 + 1, sy0 + 1)
        row: list[tuple[int, int, int, int]] = []
        for cx in range(gw):
            sx0 = bx0 + (cx * bw) // gw
            sx1 = bx0 + ((cx + 1) * bw) // gw
            if sx1 <= sx0:
                sx1 = min(bx1 + 1, sx0 + 1)
            row.append((sx0, sy0, sx1, sy1))
        grid.append(row)
    return grid


# ---------------------------------------------------------------------------
# 主转换
# ---------------------------------------------------------------------------


def convert(
    image_path: str | Path,
    palette_path: str | Path = DEFAULT_PALETTE,
    *,
    canvas: int = DEFAULT_CANVAS,
    tau_alpha: int = DEFAULT_TAU_ALPHA,
    tau_bg: int = DEFAULT_TAU_BG,
    coverage: float = DEFAULT_COVERAGE,
    baseline_row: int = DEFAULT_BASELINE_ROW,
    bake_scale: int = DEFAULT_BAKE_SCALE,
    part_id: str = "body",
    shape_name: str = "base",
) -> tuple[Model, list[list[str | None]], dict]:
    """转换并返回 ``(model, canvas_grid, stats)``。``canvas_grid[y][x]`` = 调色板 key 或 None。"""
    palette = load_palette(palette_path)
    lab_table = [(c.key, rgb_to_lab(c.rgb())) for c in palette.colors if c.hex is not None]
    if not lab_table:
        raise ValueError(f"调色板 {palette_path} 没有任何有色条目（hex 全为 null）")

    width, height, pixels = load_rgba(image_path)
    if width <= 0 or height <= 0:
        raise ValueError(f"图片尺寸非法：{width}×{height}")

    bg = corner_background(pixels, width, height)

    # 第一遍：仅按 alpha 估前景 bbox → 推出「2 格」在源图上的像素边距
    alpha_mask = bytearray(width * height)
    for y in range(height):
        base = y * width
        for x in range(width):
            if pixels[base + x][3] >= tau_alpha:
                alpha_mask[base + x] = 1
    abbox = _mask_bbox(alpha_mask, width, height)
    if abbox is None:
        margin_x = margin_y = 0.0
    else:
        abw = abbox[2] - abbox[0] + 1
        abh = abbox[3] - abbox[1] + 1
        margin_x = 2.0 * abw / canvas
        margin_y = 2.0 * abh / canvas

    # 第二遍：键控后的最终掩膜 + bbox
    mask = foreground_mask(pixels, width, height, bg, tau_alpha, tau_bg, margin_x, margin_y)
    bbox = _mask_bbox(mask, width, height)

    if bbox is None:
        # 全透明（或全部被键控掉）：输出合法但全空的模型
        return convert_empty(image_path, palette, canvas, baseline_row, bake_scale, part_id, shape_name)

    # 逐格取色（复用键控掩膜）
    max_h = baseline_row + 1
    cell_rects = to_grid(bbox, canvas, max_h)
    gh = len(cell_rects)
    gw = len(cell_rects[0]) if gh else 0
    content: list[list[str | None]] = []
    for row in cell_rects:
        out_row: list[str | None] = []
        for sx0, sy0, sx1, sy1 in row:
            rgb = _cell_color(pixels, mask, width, sx0, sy0, sx1, sy1, coverage, tau_alpha)
            out_row.append(None if rgb is None else _nearest_key(rgb, lab_table))
        content.append(out_row)

    # 若整块都降为空（覆盖不足），退化为空模型
    if not any(k is not None for r in content for k in r):
        return convert_empty(image_path, palette, canvas, baseline_row, bake_scale, part_id, shape_name)

    left = (canvas - gw) // 2
    top = baseline_row - gh + 1

    grid = [[None] * canvas for _ in range(canvas)]
    for cy in range(gh):
        for cx in range(gw):
            grid[top + cy][left + cx] = content[cy][cx]

    used = sorted({k for r in content for k in r if k is not None})
    if len(used) > len(_CHAR_POOL):
        raise ValueError(f"用到的调色板键 {len(used)} 个 > 单字符编码池 {len(_CHAR_POOL)}")
    char_of = {k: _CHAR_POOL[i] for i, k in enumerate(used)}
    rows = tuple(
        "".join(char_of[k] if k is not None else "." for k in row) for row in content
    )
    char_map = {ch: k for k, ch in char_of.items()}

    part = Part(id=part_id, anchor=[left, top], z=0, shapes={shape_name: rows}, map=char_map)
    model = Model(
        cols=canvas,
        rows=canvas,
        bake_scale=bake_scale,
        palette_name=palette.name,
        parts=(part,),
        poses=(Pose(name="idle", fps=0.0, eye_layer=None, frames=(Frame(),)),),
        fmt=FORMAT,
    )

    usage: dict[str, int] = {}
    for r in content:
        for k in r:
            if k is not None:
                usage[k] = usage.get(k, 0) + 1

    return model, grid, {
        "bbox": bbox,
        "grid": (gw, gh),
        "anchor": [left, top],
        "background": bg,
        "usage": usage,
        "empty": False,
    }


def convert_empty(
    image_path: str | Path,
    palette,
    canvas: int,
    baseline_row: int,
    bake_scale: int,
    part_id: str,
    shape_name: str,
) -> tuple[Model, list[list[str | None]], dict]:
    """全空模型的构造（供 :func:`convert` 复用）。"""
    transparent = next((c.key for c in palette.colors if c.hex is None), palette.colors[0].key)
    left = (canvas - 1) // 2
    part = Part(
        id=part_id,
        anchor=[left, baseline_row],
        z=0,
        shapes={shape_name: (".",)},
        map={"n": transparent},
    )
    model = Model(
        cols=canvas,
        rows=canvas,
        bake_scale=bake_scale,
        palette_name=palette.name,
        parts=(part,),
        poses=(Pose(name="idle", fps=0.0, eye_layer=None, frames=(Frame(),)),),
        fmt=FORMAT,
    )
    return model, [[None] * canvas for _ in range(canvas)], {
        "bbox": None,
        "grid": (0, 0),
        "anchor": [left, baseline_row],
        "background": None,
        "usage": {},
        "empty": True,
    }


def ascii_preview(grid: list[list[str | None]], mark: str = "#") -> str:
    """``#`` = 有色，``.`` = 透明。"""
    return "\n".join("".join(mark if k is not None else "." for k in row) for row in grid)


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="静态 PNG → 拼豆网格（neko.beads/1）转换器")
    ap.add_argument("input", help="输入图片（PNG 等）")
    ap.add_argument("output", help="输出 .beads.json")
    ap.add_argument("--palette", default=str(DEFAULT_PALETTE), help="调色板 JSON")
    ap.add_argument("--canvas", type=int, default=DEFAULT_CANVAS, help="画布边长（格）")
    ap.add_argument("--tau-alpha", type=int, default=DEFAULT_TAU_ALPHA, help="前景 alpha 阈值")
    ap.add_argument("--tau-bg", type=int, default=DEFAULT_TAU_BG, help="背景键控欧氏距离阈值")
    ap.add_argument("--coverage", type=float, default=DEFAULT_COVERAGE, help="成珠最小前景覆盖率")
    ap.add_argument("--baseline-row", type=int, default=DEFAULT_BASELINE_ROW, help="内容底边所在行")
    ap.add_argument("--bake-scale", type=int, default=DEFAULT_BAKE_SCALE, help="写回模型的 bakeScale")
    ap.add_argument("--part-id", default="body", help="生成部件的 id")
    ap.add_argument("--shape", default="base", help="生成形状变体名（应为 schema 默认 base）")
    ap.add_argument("--preview", action="store_true", help="打印 ASCII 预览")
    args = ap.parse_args(argv)

    model, grid, stats = convert(
        args.input,
        args.palette,
        canvas=args.canvas,
        tau_alpha=args.tau_alpha,
        tau_bg=args.tau_bg,
        coverage=args.coverage,
        baseline_row=args.baseline_row,
        bake_scale=args.bake_scale,
        part_id=args.part_id,
        shape_name=args.shape,
    )

    out = Path(args.output)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(dumps_model(model), encoding="utf-8")

    print(f"输入      : {args.input}  (bg={stats['background']})")
    print(f"输出      : {out}")
    print(f"前景 bbox : {stats['bbox']}")
    print(f"网格      : {stats['grid'][0]}×{stats['grid'][1]}  锚点(左上)={stats['anchor']}")
    print(f"用色      : {stats['usage']}")
    if args.preview:
        print("-" * args.canvas)
        print(ascii_preview(grid))
        print("-" * args.canvas)
    return 0


if __name__ == "__main__":
    sys.exit(main())
