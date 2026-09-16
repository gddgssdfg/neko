"""把 AI 生成的 UI 素材图（图标表 / 气泡 / 键帽）切成运行时可直接用的 PNG。

为什么需要它：
  豆包交付的 UI 素材是"一整张图里画 N 个格子"，而插件只认一个个独立 PNG
  （`Icon = "book"` 之类）。切格必须**按内容包围盒裁齐**再等比缩放，否则图标
  在按钮里位置会飘。本脚本把这条流水线固定下来，避免每次手写一次性脚本。

它做三件事（对应三个任务）：
  1) icons    : ui_icons_5x4.png  → neko-assets/icons/   下 20 个 32×32 PNG（覆盖）
  2) settings : ui_settings_icons.png → neko-assets/ui/settings/s1..s9.png（先不接线）
  3) bubble/keycap : 气泡 + 键帽 → neko-assets/ui/（先不接线；键帽按下态对齐宽度）

另附：
  - probe     : 打印每格的形状指标（宽高比 / 主色 / 顶部开口数 / 左右对称度），
                用于在"读不了图"的情况下判断某格到底是眼睛还是爱心。
  - previews  : 生成 _icons_compare.png（新旧对照）和 _settings_icons_preview.png
  - ninepatch : 打印气泡/键帽的九宫格切片参数（供宿主侧接线用）
  - all       : 按 backup → icons → settings → bubble → keycap → previews → ninepatch 顺序全跑

用法：
    python tools/slice_ui_assets.py probe
    python tools/slice_ui_assets.py all
    python tools/slice_ui_assets.py icons --size 32
"""

from __future__ import annotations

import argparse
import shutil
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

# --------------------------------------------------------------------------- #
# 路径常量（脚本放在 tools/ 下，故父目录即项目根）
# --------------------------------------------------------------------------- #
ROOT = Path(__file__).resolve().parent.parent
RAW = ROOT / "neko-assets" / "_airaw"
ICONS_DIR = ROOT / "neko-assets" / "icons"
BACKUP_DIR = ICONS_DIR / "_backup_before_ai"
UI_DIR = ROOT / "neko-assets" / "ui"
SETTINGS_DIR = UI_DIR / "settings"

ICON_SHEET = RAW / "ui_icons_5x4.png"
SETTINGS_SHEET = RAW / "ui_settings_icons.png"
BUBBLE_SRC = RAW / "bubble_cloud.png"
KEYCAP_UP_SRC = RAW / "keycap_up.png"
KEYCAP_DOWN_SRC = RAW / "keycap_down.png"

COMPARE_PNG = ROOT / "_icons_compare.png"
SETTINGS_PREVIEW_PNG = ROOT / "_settings_icons_preview.png"

# --------------------------------------------------------------------------- #
# 图标表映射：模型按 (行, 列) 顺序画的，1-based。目标文件名严格保持既有 20 个名字。
#
# 第 8 / 11 格的"眼睛 vs 爱心"需要按**内容对得上名字**来定，见 probe 输出。
# probe 结论：第 8 格是眼睛（横向扁、顶部单一弧）→ eye.png；
#             第 11 格是爱心（顶端双瓣开口、底部收尖、左右高度对称）→ heart.png。
# --------------------------------------------------------------------------- #
ICON_MAP: list[tuple[int, int, str]] = [
    # 行 1
    (1, 1, "book.png"),
    (1, 2, "camera.png"),
    (1, 3, "clock.png"),
    (1, 4, "close.png"),
    (1, 5, "copy.png"),
    # 行 2
    (2, 1, "dance.png"),
    (2, 2, "default.png"),
    (2, 3, "eye.png"),      # 第 8 格：眼睛（好感度）——按内容命名
    (2, 4, "food.png"),
    (2, 5, "gear.png"),
    # 行 3
    (3, 1, "heart.png"),    # 第 11 格：爱心——按内容命名
    (3, 2, "home.png"),
    (3, 3, "info.png"),
    (3, 4, "next.png"),
    (3, 5, "pin.png"),
    # 行 4
    (4, 1, "play.png"),
    (4, 2, "prev.png"),
    (4, 3, "sleep.png"),
    (4, 4, "todo.png"),
    (4, 5, "wave.png"),
]

ICON_COLS, ICON_ROWS = 5, 4
SETTINGS_COLS, SETTINGS_ROWS = 3, 3

ALPHA_THR = 8  # 低于此 alpha 视为透明（抗锯齿噪点）


# --------------------------------------------------------------------------- #
# 基础图像工具
# --------------------------------------------------------------------------- #
def load_rgba(path: Path) -> Image.Image:
    """读图并统一成 RGBA。"""
    return Image.open(path).convert("RGBA")


def alpha_mask(img: Image.Image, thr: int = ALPHA_THR) -> Image.Image:
    """返回二值化的 alpha 掩膜（L 模式，255=有内容）。"""
    return img.getchannel("A").point(lambda v: 255 if v > thr else 0)


def content_bbox(img: Image.Image, thr: int = ALPHA_THR) -> tuple[int, int, int, int] | None:
    """内容包围盒（基于 alpha）。返回 (l, t, r, b)，全透明时返回 None。"""
    return alpha_mask(img, thr).getbbox()


def detect_cuts(img: Image.Image, cols: int, rows: int, thr: int = ALPHA_THR,
                min_gap: int = 1) -> tuple[list[int], list[int]]:
    """在 X / Y 上找内容"孤岛"，用孤岛间隙的中点作为切格线。

    比等分更稳：画布尺寸不能被格数整除时（如 1024/3）也不会切歪。
    若孤岛数与期望不一致（内容粘连/多块），退化为等分。
    """
    mask = alpha_mask(img, thr)
    px = mask.load()
    w, h = img.size

    def islands(flags: list[bool]) -> list[tuple[int, int]]:
        runs: list[list[int]] = []
        start = None
        for i, v in enumerate(flags + [False]):
            if v and start is None:
                start = i
            elif not v and start is not None:
                runs.append([start, i])
                start = None
        merged: list[list[int]] = []
        for r in runs:
            if merged and r[0] - merged[-1][1] <= min_gap:
                merged[-1][1] = r[1]
            else:
                merged.append(r)
        return [(a, b) for a, b in merged]

    col_has = [any(px[x, y] for y in range(h)) for x in range(w)]
    row_has = [any(px[x, y] for x in range(w)) for y in range(h)]
    ix = islands(col_has)
    iy = islands(row_has)

    if len(ix) == cols:
        cuts_x = [0] + [ (ix[k][1] + ix[k + 1][0]) // 2 for k in range(len(ix) - 1) ] + [w]
    else:
        cuts_x = [round(k * w / cols) for k in range(cols + 1)]
    if len(iy) == rows:
        cuts_y = [0] + [ (iy[k][1] + iy[k + 1][0]) // 2 for k in range(len(iy) - 1) ] + [h]
    else:
        cuts_y = [round(k * h / rows) for k in range(rows + 1)]
    return cuts_x, cuts_y


def crop_to_content(img: Image.Image, pad: int = 0) -> Image.Image:
    """裁到内容包围盒，再在四周补统一的透明留白 pad。"""
    bb = content_bbox(img)
    if bb is None:
        return img
    l, t, r, b = bb
    if pad > 0:
        l, t = max(0, l - pad), max(0, t - pad)
        r, b = min(img.width, r + pad), min(img.height, b + pad)
    return img.crop((l, t, r, b))


def resize_rgba(img: Image.Image, size: tuple[int, int]) -> Image.Image:
    """等比/指定尺寸缩放假透明（先转预乘 RGBa 再插值，避免暗边光晕）。"""
    return img.convert("RGBa").resize(size, Image.LANCZOS).convert("RGBA")


def resize_to_width(img: Image.Image, width: int) -> Image.Image:
    """按目标宽度等比缩放（四舍五入高度，至少 1px）。"""
    w, h = img.size
    if w == 0:
        return img
    nw = max(1, width)
    nh = max(1, round(h * nw / w))
    return resize_rgba(img, (nw, nh))


def fit_square(img: Image.Image, size: int, pad_ratio: float = 0.06) -> Image.Image:
    """把内容等比缩进 size×size 的透明画布并居中（四周留 pad_ratio 边距）。

    图标统一缩到 32×32 时，若直接用 resize 到正方形会**拉歪**（非等比）。
    这里保持宽高比，居中放置，保证图标不变形。
    """
    inner = max(1, round(size * (1 - 2 * pad_ratio)))
    w, h = img.size
    scale = min(inner / w, inner / h)
    nw, nh = max(1, round(w * scale)), max(1, round(h * scale))
    scaled = resize_rgba(img, (nw, nh))
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    canvas.paste(scaled, ((size - nw) // 2, (size - nh) // 2), scaled)
    return canvas


# --------------------------------------------------------------------------- #
# 形状指标（用于判断"眼睛 vs 爱心"，read-only 参考）
# --------------------------------------------------------------------------- #
def shape_metrics(cell: Image.Image) -> dict:
    """对裁剪后的单格算一组可读指标。"""
    img = crop_to_content(cell)
    bb = content_bbox(cell)
    w, h = img.size
    mask = alpha_mask(img)
    mpx = mask.load()

    def row_width(y: int) -> int:
        return sum(1 for x in range(w) if mpx[x, y])

    def band_runs(y0: int, y1: int) -> int:
        """统计 [y0,y1) 行带内某行最多有几个连续段（探顶部是否开口）。"""
        best = 0
        for y in range(y0, min(y1, h)):
            runs, prev = 0, False
            for x in range(w):
                cur = mpx[x, y] > 0
                if cur and not prev:
                    runs += 1
                prev = cur
            best = max(best, runs)
        return best

    top_w = max([row_width(y) for y in range(0, max(1, h // 20))] or [0]) if h else 0
    bot_w = max([row_width(y) for y in range(h - max(1, h // 20), h)] or [0]) if h else 0
    mid_w = max([row_width(y) for y in range(h // 2 - max(1, h // 20), h // 2 + max(1, h // 20) + 1)] or [0]) if h else 0

    # 不透明像素的均值色
    rgba = img.load()
    tr = tg = tb = n = 0
    for y in range(h):
        for x in range(w):
            r, g, b, a = rgba[x, y]
            if a > ALPHA_THR:
                tr += r; tg += g; tb += b; n += 1
    mean = (tr // n, tg // n, tb // n) if n else (0, 0, 0)

    # 左右镜像对称度
    same = tot = 0
    for y in range(h):
        for x in range(w // 2):
            a1 = mpx[x, y] > 0
            a2 = mpx[w - 1 - x, y] > 0
            tot += 1
            same += (a1 == a2)
    sym = same / tot if tot else 0.0

    return {
        "bbox": bb,
        "size": (w, h),
        "aspect": round(max(w, h) / max(1, min(w, h)), 2),
        "orient": "横" if w >= h else "纵",
        "opaque_ratio": round((sum(img.getchannel("A").histogram()[ALPHA_THR + 1:])) / max(1, w * h), 3),
        "top_runs": band_runs(0, max(1, h // 12)),
        "top_w": top_w,
        "bot_w": bot_w,
        "mid_w": mid_w,
        "bot_over_top": round(bot_w / top_w, 2) if top_w else 0.0,
        "sym": round(sym, 2),
        "mean_rgb": mean,
    }


def guess_symbol(m: dict) -> str:
    """粗判：爱心 = 顶端开口(双瓣) + 底部收窄；眼睛 = 横向椭圆、顶底同宽。"""
    if m["top_runs"] >= 2 and m["bot_over_top"] < 0.55:
        return "爱心?"
    if m["orient"] == "横" and m["bot_over_top"] > 0.5 and m["top_runs"] <= 1:
        return "眼睛?"
    return ""


# --------------------------------------------------------------------------- #
# 渲染辅助（对照图 / 预览图）
# --------------------------------------------------------------------------- #
def load_font(size: int) -> ImageFont.ImageFont:
    for cand in (r"C:\Windows\Fonts\arial.ttf", r"C:\Windows\Fonts\segoeui.ttf"):
        try:
            return ImageFont.truetype(cand, size)
        except OSError:
            continue
    try:
        return ImageFont.load_default(size=size)
    except TypeError:
        return ImageFont.load_default()


def checkerboard(size: tuple[int, int], square: int = 8,
                 c1: tuple[int, int, int] = (235, 235, 235),
                 c2: tuple[int, int, int] = (205, 205, 205)) -> Image.Image:
    """浅灰棋盘底，让浅色/深色图标都能看清。"""
    w, h = size
    bg = Image.new("RGB", (w, h), c1)
    d = ImageDraw.Draw(bg)
    for y in range(0, h, square):
        for x in range(0, w, square):
            if ((x // square) + (y // square)) % 2:
                d.rectangle([x, y, x + square - 1, y + square - 1], fill=c2)
    return bg


def paste_icon(bg: Image.Image, icon: Image.Image | None, box: tuple[int, int, int, int]) -> None:
    """把图标等比放进 box 区域并居中。"""
    x0, y0, x1, y1 = box
    if icon is None:
        return
    bw, bh = x1 - x0, y1 - y0
    w, h = icon.size
    scale = min(bw / w, bh / h)
    nw, nh = max(1, round(w * scale)), max(1, round(h * scale))
    scaled = resize_rgba(icon, (nw, nh))
    bg.paste(scaled, (x0 + (bw - nw) // 2, y0 + (bh - nh) // 2), scaled)


# --------------------------------------------------------------------------- #
# 任务 1：图标
# --------------------------------------------------------------------------- #
def backup_icons() -> int:
    """把当前 icons/ 下 20 个 PNG 备份到 _backup_before_ai/（已存在则跳过）。"""
    if BACKUP_DIR.exists():
        print(f"  备份目录已存在，跳过：{BACKUP_DIR}")
        return len(list(BACKUP_DIR.glob("*.png")))
    BACKUP_DIR.mkdir(parents=True, exist_ok=True)
    n = 0
    for p in sorted(ICONS_DIR.glob("*.png")):
        shutil.copy2(p, BACKUP_DIR / p.name)
        n += 1
    print(f"  ✅ 已备份 {n} 个图标 → {BACKUP_DIR}")
    return n


def slice_icons(size: int = 32) -> list[Path]:
    """切 ui_icons_5x4.png → icons/ 下 20 个 size×size PNG（覆盖）。"""
    ICONS_DIR.mkdir(parents=True, exist_ok=True)
    img = load_rgba(ICON_SHEET)
    cx, cy = detect_cuts(img, ICON_COLS, ICON_ROWS)
    print(f"  画布 {img.width}×{img.height}  切格线 X={cx} Y={cy}")
    written: list[Path] = []
    for row, col, name in ICON_MAP:
        cell = img.crop((cx[col - 1], cy[row - 1], cx[col], cy[row]))
        tight = crop_to_content(cell)
        out = fit_square(tight, size)
        dst = ICONS_DIR / name
        out.save(dst)
        written.append(dst)
        print(f"    行{row}列{col} → {name:12s} 内容 {tight.width:>4}×{tight.height:<4} → {size}×{size}")
    return written


# --------------------------------------------------------------------------- #
# 任务 2：设置页图标
# --------------------------------------------------------------------------- #
def slice_settings(size: int = 32) -> list[Path]:
    """切 ui_settings_icons.png → ui/settings/s1..s9.png（先不接线）。"""
    SETTINGS_DIR.mkdir(parents=True, exist_ok=True)
    img = load_rgba(SETTINGS_SHEET)
    cx, cy = detect_cuts(img, SETTINGS_COLS, SETTINGS_ROWS)
    print(f"  画布 {img.width}×{img.height}  切格线 X={cx} Y={cy}")
    written: list[Path] = []
    idx = 0
    for r in range(SETTINGS_ROWS):
        for c in range(SETTINGS_COLS):
            idx += 1
            cell = img.crop((cx[c], cy[r], cx[c + 1], cy[r + 1]))
            tight = crop_to_content(cell)
            out = fit_square(tight, size)
            dst = SETTINGS_DIR / f"s{idx}.png"
            out.save(dst)
            written.append(dst)
            print(f"    行{r+1}列{c+1} → s{idx}.png  内容 {tight.width:>4}×{tight.height:<4} → {size}×{size}")
    return written


# --------------------------------------------------------------------------- #
# 任务 3：气泡 / 键帽（先不接线，仅产出 + 算九宫格参数）
# --------------------------------------------------------------------------- #
def process_bubble(width: int = 512, pad_ratio: float = 0.05) -> Path:
    """气泡：裁齐 + 统一留白 + 等比缩到 width。"""
    UI_DIR.mkdir(parents=True, exist_ok=True)
    img = load_rgba(BUBBLE_SRC)
    tight = crop_to_content(img)
    pad = max(8, round(pad_ratio * min(tight.size)))
    canvas = Image.new("RGBA", (tight.width + 2 * pad, tight.height + 2 * pad), (0, 0, 0, 0))
    canvas.paste(tight, (pad, pad), tight)
    out = resize_to_width(canvas, width)
    dst = UI_DIR / "bubble.png"
    out.save(dst)
    print(f"  bubble: 原 {img.width}×{img.height} → 内容 {tight.width}×{tight.height}"
          f" → 留白{pad} → {out.width}×{out.height} → {dst}")
    return dst


def process_keycaps(width: int = 128) -> tuple[Path, Path, tuple[int, int]]:
    """键帽：按下态先对齐到常态同宽，再各自裁齐缩到 width。返回 (up, down, up_size)。"""
    UI_DIR.mkdir(parents=True, exist_ok=True)
    up = crop_to_content(load_rgba(KEYCAP_UP_SRC))
    down = crop_to_content(load_rgba(KEYCAP_DOWN_SRC))
    print(f"  裁齐后 常态 {up.width}×{up.height}  按下 {down.width}×{down.height}")

    # 关键：按下态内容比常态大 ~12%，按比例缩到与常态同宽，避免按键跳大小。
    if down.width != up.width:
        ratio = up.width / down.width
        down = resize_rgba(down, (up.width, max(1, round(down.height * ratio))))
        print(f"  按下态按比例对齐到常态宽度 {up.width} → {down.width}×{down.height}")

    up_out = resize_to_width(up, width)
    down_out = resize_to_width(down, width)
    up_dst, down_dst = UI_DIR / "keycap_up.png", UI_DIR / "keycap_down.png"
    up_out.save(up_dst)
    down_out.save(down_dst)
    print(f"  keycap_up   → {up_out.width}×{up_out.height} → {up_dst}")
    print(f"  keycap_down → {down_out.width}×{down_out.height} → {down_dst}")
    return up_dst, down_dst, (up_out.width, up_out.height)


def nine_patch_params(w: int, h: int, ratio: float = 0.25) -> dict:
    """算九宫格九宫格切片内缩参数：四角各取 min(w,h)*ratio，留出中间不重叠。"""
    corner = round(min(w, h) * ratio)
    # 保证中间至少 1px，四角互不重叠
    corner = max(1, min(corner, (min(w, h) - 1) // 2))
    return {
        "size": (w, h),
        "corner": corner,
        "left": corner, "top": corner, "right": corner, "bottom": corner,
        "middle": (w - 2 * corner, h - 2 * corner),
    }


# --------------------------------------------------------------------------- #
# 预览图
# --------------------------------------------------------------------------- #
def make_icons_compare(out: Path = COMPARE_PNG) -> Path:
    """4×5 对照图：每格左=旧图标，右=新图标，标注文件名。"""
    old_dir = BACKUP_DIR if BACKUP_DIR.exists() else ICONS_DIR
    cell_w, cell_h = 210, 150
    title_h = 46
    W = cell_w * ICON_COLS
    H = title_h + cell_h * ICON_ROWS
    bg = checkerboard((W, H), square=10)
    d = ImageDraw.Draw(bg)
    d.rectangle([0, 0, W, title_h], fill=(32, 32, 38))
    d.text((14, 12), "Neko UI icons  —  LEFT: old    RIGHT: new (AI)", font=load_font(20), fill=(255, 255, 255))
    f_lab = load_font(15)
    f_sm = load_font(12)

    for i, (row, col, name) in enumerate(ICON_MAP):
        r, c = divmod(i, ICON_COLS)
        x0 = c * cell_w
        y0 = title_h + r * cell_h
        d.rectangle([x0, y0, x0 + cell_w - 1, y0 + cell_h - 1], outline=(120, 120, 130))
        # 两个图标区
        half = (cell_w - 24) // 2
        box_old = (x0 + 10, y0 + 26, x0 + 10 + half - 6, y0 + 26 + half - 6)
        box_new = (x0 + 16 + half, y0 + 26, x0 + 16 + half + half - 6, y0 + 26 + half - 6)
        old_p, new_p = old_dir / name, ICONS_DIR / name
        paste_icon(bg, load_rgba(old_p) if old_p.exists() else None, box_old)
        paste_icon(bg, load_rgba(new_p) if new_p.exists() else None, box_new)
        d.text((x0 + 10, y0 + 6), f"{name}", font=f_lab, fill=(20, 20, 20))
        d.text((box_old[0], box_old[3] + 4), "old", font=f_sm, fill=(90, 90, 90))
        d.text((box_new[0], box_new[3] + 4), "new", font=f_sm, fill=(0, 90, 160))
    bg.convert("RGB").save(out)
    print(f"  ✅ 对照图 → {out}  ({W}×{H})")
    return out


def make_settings_preview(out: Path = SETTINGS_PREVIEW_PNG, scale: int = 5) -> Path:
    """3×3 放大预览，标注 s1..s9。"""
    tile = 32 * scale
    pad = 18
    title_h = 40
    W = SETTINGS_COLS * (tile + pad) + pad
    H = title_h + SETTINGS_ROWS * (tile + pad + 20) + pad
    bg = checkerboard((W, H), square=12)
    d = ImageDraw.Draw(bg)
    d.rectangle([0, 0, W, title_h], fill=(32, 32, 38))
    d.text((14, 10), "Neko settings icons (ui/settings)  —  3x3, not wired yet",
           font=load_font(18), fill=(255, 255, 255))
    f = load_font(14)
    idx = 0
    for r in range(SETTINGS_ROWS):
        for c in range(SETTINGS_COLS):
            idx += 1
            p = SETTINGS_DIR / f"s{idx}.png"
            x = pad + c * (tile + pad)
            y = title_h + pad + r * (tile + pad + 20)
            d.rectangle([x, y, x + tile, y + tile], outline=(120, 120, 130))
            if p.exists():
                icon = resize_rgba(load_rgba(p), (tile, tile))
                bg.paste(icon, (x, y), icon)
            d.text((x, y + tile + 3), f"s{idx}.png", font=f, fill=(20, 20, 20))
    bg.convert("RGB").save(out)
    print(f"  ✅ 设置预览 → {out}  ({W}×{H})")
    return out


# --------------------------------------------------------------------------- #
# probe
# --------------------------------------------------------------------------- #
def probe() -> None:
    """打印每格形状指标，供人工判断内容（眼睛/爱心等）。"""
    for path, cols, rows, label in (
        (ICON_SHEET, ICON_COLS, ICON_ROWS, "图标表 5×4"),
        (SETTINGS_SHEET, SETTINGS_COLS, SETTINGS_ROWS, "设置图标 3×3"),
    ):
        img = load_rgba(path)
        cx, cy = detect_cuts(img, cols, rows)
        print(f"\n{'='*84}\n{path.name}  [{label}]  {img.width}×{img.height}")
        print(f"  切格线 X={cx}  Y={cy}")
        names = {**{(r, c): n for r, c, n in ICON_MAP}} if cols == ICON_COLS else {}
        i = 0
        for r in range(rows):
            for c in range(cols):
                i += 1
                cell = img.crop((cx[c], cy[r], cx[c + 1], cy[r + 1]))
                m = shape_metrics(cell)
                nm = names.get((r + 1, c + 1), f"s{i}")
                print(f"  行{r+1}列{c+1} #{i:>2} → {nm:12s} "
                      f"bx={m['bbox']} 尺寸{m['size'][0]:>4}×{m['size'][1]:<4} "
                      f"{m['orient']} 宽高比{m['aspect']:>5} 不透明{m['opaque_ratio']*100:>5.1f}% "
                      f"顶段{m['top_runs']} 顶宽{m['top_w']:>4} 中宽{m['mid_w']:>4} 底宽{m['bot_w']:>4} "
                      f"底/顶{m['bot_over_top']:>5} 色#{m['mean_rgb'][0]:02x}{m['mean_rgb'][1]:02x}{m['mean_rgb'][2]:02x} "
                      f"对称{m['sym']*100:>3.0f}% {guess_symbol(m)}")


# --------------------------------------------------------------------------- #
# CLI
# --------------------------------------------------------------------------- #
def cmd_all(args: argparse.Namespace) -> int:
    print("== 1) 备份现有图标 ==")
    backup_icons()
    print("== 2) 切图标 ==")
    slice_icons(args.size)
    print("== 3) 切设置图标 ==")
    slice_settings(args.size)
    print("== 4) 处理气泡 ==")
    bubble = process_bubble()
    print("== 5) 处理键帽 ==")
    up, down, up_size = process_keycaps()
    print("== 6) 预览图 ==")
    make_icons_compare()
    make_settings_preview()
    print("== 7) 九宫格切片参数 ==")
    for tag, p in (("bubble", bubble), ("keycap_up", up), ("keycap_down", down)):
        im = Image.open(p)
        prm = nine_patch_params(im.width, im.height)
        print(f"  {tag:11s} {prm}")
    return 0


def build_parser() -> argparse.ArgumentParser:
    ap = argparse.ArgumentParser(description="切分 Neko AI UI 素材")
    ap.add_argument("mode", choices=["all", "probe", "icons", "settings",
                                     "bubble", "keycap", "previews", "ninepatch"],
                    help="要执行的动作")
    ap.add_argument("--size", type=int, default=32, help="图标边长（默认 32）")
    ap.add_argument("--bubble-width", type=int, default=512)
    ap.add_argument("--keycap-width", type=int, default=128)
    return ap


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if args.mode == "probe":
        probe()
        return 0
    if args.mode == "icons":
        backup_icons()
        slice_icons(args.size)
        return 0
    if args.mode == "settings":
        slice_settings(args.size)
        return 0
    if args.mode == "bubble":
        process_bubble(args.bubble_width)
        return 0
    if args.mode == "keycap":
        process_keycaps(args.keycap_width)
        return 0
    if args.mode == "previews":
        make_icons_compare()
        make_settings_preview()
        return 0
    if args.mode == "ninepatch":
        for tag, p in (("bubble", UI_DIR / "bubble.png"),
                       ("keycap_up", UI_DIR / "keycap_up.png"),
                       ("keycap_down", UI_DIR / "keycap_down.png")):
            if p.exists():
                im = Image.open(p)
                print(f"  {tag:11s} {nine_patch_params(im.width, im.height)}")
        return 0
    return cmd_all(args)


if __name__ == "__main__":
    sys.exit(main())
