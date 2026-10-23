#!/usr/bin/env python3
"""AI 九宫格 sprite sheet → Neko 皮肤素材（v0.1.81）

用途
----
把豆包/其他 AI 生成的「3×3 多姿态拼图」切成 9 个状态图，做后处理，
产出可直接被 SkinRegistry 扫描的皮肤目录：

    neko-assets/<skinId>/
        skin.json
        <skinId>_idle.png     ← 每状态 1 张（layout=files）
        <skinId>_walk.png
        ...

为什么用「一张拼图」而不是 9 张单图
----------------------------------
AI 分 9 次生成会画出 9 只不同的猫；一张图内 9 格天然是同一只，角色一致性有保障。

处理步骤
--------
1. 3×3 等分切格
2. 抠背景（纯白/近白 → 透明，带容差 + 边缘抗锯齿过渡）
3. 裁到猫的包围盒（去空白）
4. 统一画布尺寸（取 9 格最大包围盒）并**按底部中心对齐**（保证站立基线一致）
5. 尺寸自适应：统一画布若超过 --max-frame，按比例 LANCZOS 下采样（保持宽高比）
6. 输出 PNG + skin.json（含 recommendedScale 声明）

用法
----
    python tools/ai_sheet_to_skin.py <输入九宫格.png> <输出皮肤目录>
        [--id hd] [--name "AI 高清"] [--max-frame 256] [--recommended-scale 4.0]

参数
----
--max-frame            单帧最大边长（默认 256）。统一画布宽或高超过该值时，
                       按比例 LANCZOS 高质量下采样到该尺寸内（保持宽高比，取整）。
                       AI 高清源图（如 677×668）直接进游戏会让位图常驻 60MB+，
                       降采样到 256 后位图常驻降到 ~9MB。
--recommended-scale    写入输出 skin.json 的 recommendedScale 字段（默认 4.0）。
                       语义 = 作者建议的显示倍率（1.0–8.0）；宿主在用户切换皮肤时套用，
                       让高清皮肤开箱即 1:1 显示，无需用户手动把缩放开到 4。

例：
    python tools/ai_sheet_to_skin.py neko-assets/_airaw/orange-cat-spritesheet-3x3.png \
        neko-assets/hd --id hd --name "AI 高清橘猫" --max-frame 256 --recommended-scale 4
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

from PIL import Image

# 九宫格顺序 → Neko 状态名（与生成 prompt 的 ①..⑨ 对应）
STATES = [
    ("idle",  1, 4),
    ("walk",  2, 8),
    ("sit",   3, 4),
    ("sleep", 4, 1),
    ("pet",   5, 10),
    ("drag",  6, 6),
    ("fall",  7, 12),
    ("climb", 8, 4),
    ("work",  9, 2),
]

BG_TOLERANCE = 24      # 与背景色的最大通道差；小于此视为背景
EDGE_FEATHER = 10      # 边缘过渡带宽（消除白边锯齿）
PAD = 8                # 统一画布四周留白
FRAMES = 4             # 每状态帧数（由单帧程序生成微动）
DEFAULT_MAX_FRAME = 256          # 单帧最大边长（超过则 LANCZOS 下采样）
DEFAULT_RECOMMENDED_SCALE = 4.0  # 写入 skin.json 的推荐显示倍率

# ---- 像素模式（--pixel）----
# 背景：豆包出的"像素风"其实是高分辨率插画（单格 ~680px、3 万多种颜色、带抗锯齿），
# 必须先降采样再**量化调色板 + alpha 二值化**才能得到硬边像素画。
DEFAULT_PIXEL_SIZE = 64          # 像素模式下的正方帧边长（64 → Scale 4 时正好 2× 整数放大）
DEFAULT_PIXEL_COLORS = 24        # 量化后的调色板颜色数（真像素画通常 16~32）
PIXEL_ALPHA_CUTOFF = 128         # alpha 二值化阈值：≥ 视为实心，< 视为全透（硬边）

# 像素模式的微动：**只允许整数像素位移**（缩放/旋转会重采样，像素会糊、颜色会溢出调色板）
PIXEL_MOTION: dict[str, list[tuple[int, int]]] = {   # (dx, dy) 每帧
    "idle":  [(0, 0), (0, -1), (0, 0), (0, -1)],       # 呼吸
    "walk":  [(0, 0), (-2, 0), (0, 0), (2, 0)],        # 颠簸
    "sit":   [(0, 0), (0, -1), (0, 0), (0, -1)],
    "sleep": [(0, 0), (0, -1), (0, 0), (0, -1)],
    "pet":   [(0, 0), (0, -1), (1, 0), (0, -1)],       # 舒服地蹭
    "drag":  [(0, 0), (1, -1), (0, 0), (-1, -1)],      # 被拎着晃
    "fall":  [(0, 0), (1, 0), (0, 0), (-1, 0)],
    "climb": [(0, 0), (0, -2), (0, 0), (0, -1)],       # 往上够
    "work":  [(0, 0), (0, 1), (0, 0), (0, 1)],         # 点头
}


def remove_bg(img: Image.Image, tol: int = BG_TOLERANCE) -> Image.Image:
    """把**与图像边缘连通的**近白背景转透明（flood fill）。

    为什么不用「全图近白 → 透明」：橘猫的胸腹是奶白色，与白底极接近，
    简单按色差抠会把肚皮一起抠掉（v0.1.81 实测踩过）。
    改用从四边向内的 BFS 只清除**连通到边缘**的背景，内部白色（肚皮）保留。
    """
    img = img.convert("RGBA")
    px = img.load()
    w, h = img.size

    def is_bg(x: int, y: int) -> bool:
        r, g, b, _a = px[x, y]
        return max(255 - r, 255 - g, 255 - b) <= tol

    visited = bytearray(w * h)
    stack: list[tuple[int, int]] = []
    # 四边所有背景像素入栈
    for x in range(w):
        if is_bg(x, 0):
            stack.append((x, 0))
        if is_bg(x, h - 1):
            stack.append((x, h - 1))
    for y in range(h):
        if is_bg(0, y):
            stack.append((0, y))
        if is_bg(w - 1, y):
            stack.append((w - 1, y))

    while stack:
        x, y = stack.pop()
        idx = y * w + x
        if visited[idx]:
            continue
        visited[idx] = 1
        r, g, b, _a = px[x, y]
        px[x, y] = (r, g, b, 0)
        for nx, ny in ((x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)):
            if 0 <= nx < w and 0 <= ny < h and not visited[ny * w + nx] and is_bg(nx, ny):
                stack.append((nx, ny))

    # 边缘羽化：对"紧邻透明区"的不透明像素按与背景的接近度降 alpha，消除白边
    for y in range(h):
        for x in range(w):
            r, g, b, a = px[x, y]
            if a == 0:
                continue
            touch_bg = any(
                0 <= nx < w and 0 <= ny < h and px[nx, ny][3] == 0
                for nx, ny in ((x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1))
            )
            if touch_bg:
                diff = max(255 - r, 255 - g, 255 - b)
                if diff <= tol + EDGE_FEATHER:
                    px[x, y] = (r, g, b, int(255 * (diff - tol) / EDGE_FEATHER))
    return img


def has_alpha_cutout(img: Image.Image, min_ratio: float = 0.04) -> bool:
    """判断图是否**已经**带了透明背景（豆包可直接出透明图，那就不用再抠）。

    判据：全透明（alpha < 16）像素占比超过 min_ratio。
    用"全透明"而不是"半透明"是为了避开抗锯齿边缘——那些像素本来就不是背景。
    """
    if img.mode != "RGBA":
        return False
    a = img.getchannel("A")
    hist = a.histogram()
    transparent = sum(hist[:16])
    return transparent / max(1, img.width * img.height) >= min_ratio


def crop_to_content(img: Image.Image, tol: int = 12) -> Image.Image:
    """裁到不透明内容的包围盒。"""
    alpha = img.getchannel("A")
    # 只把 alpha > tol 的算作内容
    mask = alpha.point(lambda v: 255 if v > tol else 0)
    box = mask.getbbox()
    return img.crop(box) if box else img


def fit_within(w: int, h: int, max_side: int) -> tuple[int, int]:
    """若任一边超过 max_side，按比例缩到不超过 max_side（保持宽高比）。

    受限（较长）的那条边会被精确置为 max_side，避免浮点截断把 256 变成 255；
    另一条边按比例取整。max_side <= 0 表示不限制。
    """
    if max_side <= 0 or (w <= max_side and h <= max_side):
        return w, h
    scale = max_side / max(w, h)
    nw, nh = int(w * scale), int(h * scale)
    if w >= h:
        nw = max_side
    else:
        nh = max_side
    return max(1, nw), max(1, nh)


# 每状态的微动参数：(垂直位移 px, 缩放幅度, 旋转角度, 水平位移 px)，共 4 帧
# 单帧图不加动效 = 静止图片，看不出"活着"。
MOTION: dict[str, list[tuple[float, float, float, float]]] = {
    "idle":  [(0, 0.000, 0, 0), (0, 0.018, 0, 0), (0, 0.006, 0, 0), (0, 0.012, 0, 0)],    # 呼吸
    "walk":  [(0, 0, 0, 0), (-6, 0.010, -1.5, 2), (0, 0, 0, 0), (-6, 0.010, 1.5, -2)],   # 颠簸
    "sit":   [(0, 0.000, 0, 0), (0, 0.014, 0, 0), (0, 0.004, 0, 0), (0, 0.010, 0, 0)],    # 轻呼吸
    "sleep": [(0, 0.000, 0, 0), (0, 0.024, 0, 0), (0, 0.008, 0, 0), (0, 0.016, 0, 0)],    # 深睡呼吸
    "pet":   [(0, 0.010, -1.2, 0), (0, 0.020, 0, 0), (0, 0.010, 1.2, 0), (0, 0.020, 0, 0)],  # 舒服地晃
    "drag":  [(0, 0, -5, 0), (0, 0, 0, 0), (0, 0, 5, 0), (0, 0, 0, 0)],                    # 被拎着摇
    "fall":  [(0, 0, -4, 0), (0, 0, 0, 0), (0, 0, 4, 0), (0, 0, 0, 0)],                    # 下落翻转
    "climb": [(0, 0, 0, 0), (-5, 0, 0, 0), (0, 0, 0, 0), (-3, 0, 0, 0)],                   # 往上够
    "work":  [(0, 0, 0, 0), (2, 0, 0, 0), (0, 0, 0, 0), (1, 0, 0, 0)],                     # 点头敲键盘
}


def make_motion_frames(base: Image.Image, state: str) -> list[Image.Image]:
    """由单帧生成 FRAMES 帧微动（位移 / 缩放 / 旋转），画布尺寸不变。

    锚点取**底边中心**：缩放与旋转都不抬脚，保证站立基线一致。
    """
    params = MOTION.get(state, MOTION["idle"])
    cw, ch = base.size
    out: list[Image.Image] = []
    for dy, scale, angle, dx in params[:FRAMES]:
        img = base
        if scale:
            s = 1.0 + scale
            img = img.resize((int(cw * s), int(ch * s)), Image.LANCZOS)
        if angle:
            img = img.rotate(angle, resample=Image.BICUBIC, expand=False)
        canvas = Image.new("RGBA", (cw, ch), (0, 0, 0, 0))
        canvas.alpha_composite(img, ((cw - img.width) // 2 + int(dx), ch - img.height + int(dy)))
        out.append(canvas)
    while len(out) < FRAMES:
        out.append(base)
    return out


def build_pixel_palette(images: list[Image.Image], colors: int) -> Image.Image:
    """Train on visible pixels only; one shared palette keeps colors stable across poses."""
    if not 1 <= colors <= 256:
        raise ValueError("pixel colors must be between 1 and 256")
    samples = []
    for image in images:
        data = image.convert("RGBA").tobytes()
        samples.extend((data[i], data[i + 1], data[i + 2]) for i in range(0, len(data), 4)
                       if data[i + 3] >= PIXEL_ALPHA_CUTOFF)
    training = Image.new("RGB", (max(1, len(samples)), 1))
    if samples:
        training.putdata(samples)
    return training.quantize(colors=colors, method=Image.MEDIANCUT)


def pixelize(img: Image.Image, colors: int, palette: Image.Image | None = None) -> Image.Image:
    """把高分辨率插画降成硬边像素画：量化调色板 + alpha 二值化。

    为什么两步都要：
      - 只降采样不量化 → 边缘仍是抗锯齿的灰阶，放大后是"糊的方块"而不是像素画；
      - 只二值化 alpha 不量化 → 颜色仍有上万种，放大后色块边界不干净。
    """
    img = img.convert("RGBA")
    # alpha 二值化：硬边（半透明像素在桌面上会显出桌面颜色，破坏像素感）
    alpha = img.getchannel("A").point(lambda v: 255 if v >= PIXEL_ALPHA_CUTOFF else 0)
    palette = palette if palette is not None else build_pixel_palette([img], colors)
    quant = img.convert("RGB").quantize(palette=palette, dither=Image.Dither.NONE)
    out = quant.convert("RGBA")
    out.putalpha(alpha)
    return out


def make_pixel_motion_frames(base: Image.Image, state: str) -> list[Image.Image]:
    """像素模式微动：只用整数像素平移（不缩放/不旋转），保证像素不被重采样。"""
    params = PIXEL_MOTION.get(state, PIXEL_MOTION["idle"])
    cw, ch = base.size
    out: list[Image.Image] = []
    for i in range(FRAMES):
        dx, dy = params[i % len(params)]
        canvas = Image.new("RGBA", (cw, ch), (0, 0, 0, 0))
        canvas.alpha_composite(base, (dx, dy))
        out.append(canvas)
    return out


def arg_value(flag: str) -> str | None:
    """读取 `--flag value` 形式的 CLI 参数值；不存在返回 None。"""
    if flag in sys.argv:
        idx = sys.argv.index(flag) + 1
        if idx < len(sys.argv):
            return sys.argv[idx]
    return None


def main() -> int:
    if len(sys.argv) < 3:
        print(__doc__)
        return 2

    src = Path(sys.argv[1])
    out_dir = Path(sys.argv[2])
    skin_id = arg_value("--id") or "hd"
    skin_name = arg_value("--name") or "AI 高清"
    max_frame = int(arg_value("--max-frame") or DEFAULT_MAX_FRAME)
    recommended_scale = float(arg_value("--recommended-scale") or DEFAULT_RECOMMENDED_SCALE)

    if not src.is_file():
        print(f"❌ 找不到输入图: {src}")
        return 2

    sheet = Image.open(src).convert("RGBA")
    W, H = sheet.size
    cw, ch = W // 3, H // 3
    # Generated atlases may have uneven gutters. Explicit cuts avoid borrowing a neighbor's ears.
    def cuts(flag: str, length: int) -> list[int]:
        raw = arg_value(flag)
        inner = [int(v) for v in raw.split(",")] if raw else [length // 3, length * 2 // 3]
        if len(inner) != 2 or not 0 < inner[0] < inner[1] < length:
            raise ValueError(f"{flag}: expected two increasing coordinates within 0..{length}")
        return [0, *inner, length]
    xs, ys = cuts("--grid-x", W), cuts("--grid-y", H)
    print(f"输入: {src.name}  {W}x{H}  格子 {cw}x{ch}")

    # 1) 切格 + 抠背 + 裁包围盒
    pieces: list[tuple[str, int, int, Image.Image]] = []
    cutout_mode: bool | None = None      # 首格判定一次即可（同一张图各格情况一致）
    for idx, (name, cell, fps) in enumerate(STATES):
        r, c = (cell - 1) // 3, (cell - 1) % 3
        cell_img = sheet.crop((xs[c], ys[r], xs[c + 1], ys[r + 1]))
        if cutout_mode is None:
            cutout_mode = has_alpha_cutout(cell_img)
            print("背景：已带透明通道 → 跳过抠图" if cutout_mode
                  else "背景：不透明 → flood-fill 抠白底")
        if not cutout_mode:
            cell_img = remove_bg(cell_img)
        cell_img = crop_to_content(cell_img)
        pieces.append((name, cell, fps, cell_img))
        print(f"  ①..⑨ 第 {cell} 格 → {name:<6} 内容尺寸 {cell_img.size}")

    # 2) 统一画布：取最大宽高，按**底部中心**对齐（站立基线一致）
    max_w = max(p[3].width for p in pieces)
    max_h = max(p[3].height for p in pieces)
    canvas_w, canvas_h = max_w + PAD * 2, max_h + PAD * 2
    print(f"统一画布: {canvas_w}x{canvas_h}（底部中心对齐）")

    # 2.4) 尺寸自适应：统一画布若超过 --max-frame，按比例 LANCZOS 下采样（保持宽高比）
    pixel_mode = "--pixel" in sys.argv
    pixel_size = int(arg_value("--pixel-size") or DEFAULT_PIXEL_SIZE)
    pixel_colors = int(arg_value("--pixel-colors") or DEFAULT_PIXEL_COLORS)
    if pixel_mode and pixel_size < 8:
        raise ValueError("pixel size must be at least 8")

    if pixel_mode:
        frame_w = frame_h = pixel_size
        print(f"像素模式: 帧 {pixel_size}×{pixel_size}，调色板 {pixel_colors} 色"
              f"（LANCZOS 降采样 → 调色板量化 → alpha 二值化，微动只用整数位移）")
    else:
        frame_w, frame_h = fit_within(canvas_w, canvas_h, max_frame)
        if (frame_w, frame_h) != (canvas_w, canvas_h):
            print(f"下采样: {canvas_w}x{canvas_h} → {frame_w}x{frame_h}（LANCZOS，max-frame={max_frame}）")

    prepared: list[tuple[str, Image.Image]] = []
    for name, _cell, _fps, img in pieces:
        canvas = Image.new("RGBA", (canvas_w, canvas_h), (0, 0, 0, 0))
        x = (canvas_w - img.width) // 2
        y = canvas_h - PAD - img.height          # 底对齐
        canvas.alpha_composite(img, (x, y))

        # 2.4) 尺寸自适应：对统一画布下采样（每状态等宽，保证「总宽 / frames」整除）
        if pixel_mode:
            # 先保持宽高比缩进 pixel_size 方框（thumbnail 不放大、不变形），再底对齐合成正方帧
            inner = canvas.copy()
            motion_margin = 3
            inner.thumbnail((pixel_size - motion_margin * 2, pixel_size - motion_margin * 2), Image.LANCZOS)
            sq = Image.new("RGBA", (pixel_size, pixel_size), (0, 0, 0, 0))
            sq.alpha_composite(inner, ((pixel_size - inner.width) // 2, pixel_size - motion_margin - inner.height))
            canvas = sq
        else:
            if (frame_w, frame_h) != (canvas_w, canvas_h):
                canvas = canvas.resize((frame_w, frame_h), Image.LANCZOS)
        prepared.append((name, canvas))

    shared_palette = build_pixel_palette([canvas for _, canvas in prepared], pixel_colors) if pixel_mode else None
    out_dir.mkdir(parents=True, exist_ok=True)
    for name, canvas in prepared:
        if pixel_mode:
            canvas = pixelize(canvas, pixel_colors, shared_palette)
            frames = make_pixel_motion_frames(canvas, name)
        else:
            # 2.5) 单帧 → 4 帧微动（否则猫是静态图，不会呼吸）
            frames = make_motion_frames(canvas, name)
        fw, fh = canvas.size
        sheet = Image.new("RGBA", (fw * len(frames), fh), (0, 0, 0, 0))
        for i, f in enumerate(frames):
            sheet.alpha_composite(f, (i * fw, 0))
        dst = out_dir / f"neko_{name}.png"
        sheet.save(dst)
        print(f"  写出 {dst.name}  {sheet.size}（{len(frames)} 帧 × {fw}px）")

    # 3) skin.json（layout=sheet：每状态一张横排雪碧图）
    #    frameWidth/frameHeight 写**实际输出图**的单帧尺寸（下采样后；= 总宽 / frames，每帧等宽）。
    skin = {
        "id": skin_id,
        "displayName": skin_name,
        "layout": "sheet",
        "frameWidth": frame_w,
        "frameHeight": frame_h,
        "frames": FRAMES,
        "hasShadow": False,
        "pixelArt": pixel_mode,
        "eyeLayers": [],
        "recommendedScale": recommended_scale,
        "states": [
            {"name": name, "fps": fps, "eyeLayer": None}
            for name, _cell, fps, _img in pieces
        ],
    }
    (out_dir / "skin.json").write_text(
        json.dumps(skin, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    print(f"\n✅ 完成：{out_dir}（{len(pieces)} 状态 × {FRAMES} 帧 + skin.json）")
    print(f"   帧尺寸 {frame_w}x{frame_h}，推荐缩放 {recommended_scale}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
