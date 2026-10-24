"""把豆包的「动画序列条」+「基准九宫格」+「闭眼九宫格」组装成一套 Neko 皮肤。

############################################################################
# ⚠️⚠️  已弃用（DEPRECATED）——v0.1.94 起，请勿再运行本脚本  ⚠️⚠️
############################################################################
# 本文件是 **旧 v1 生成器**，已被 `tools/build_skin_v2.py` 完全取代（v2 多了：
# 静态图/序列条混合、逐状态定标、眼球层差分、眨眼闭合层、周期感知抽帧对接等）。
#
# 为什么不许再用（"契约要说实话"，见 docs/plugin-development-guide.md）：
#   * 本文件 :48 硬编码 `FPS["walk"] = 10`，而它自己产出的 `neko-assets/pixel/skin.json`
#     里 walk.fps = **8** —— 生成器与产物**自相矛盾**；
#   * 更与当前零打滑口径 `WalkSpeed=72`（对应 walk fps=**7**，见 v0.1.92~0.1.93）不一致。
#   一个"说谎"的脚本会诱导后人照着它写出错代码。
#
# 若确需产出 legacy 皮肤：请先对齐 walk fps 口径（build_skin_v2 的 FPS["walk"] 与
# PetStateMachine.WalkSpeed），并优先改用 `tools/build_skin_v2.py`。
############################################################################

与 `ai_sheet_to_skin.py` 的区别：
  - 那个处理"一张九宫格 → 9 状态各 1 张静态图 + 程序微动"
  - 这个处理"9 张序列条 → 每状态 N 个**真动画帧**"，并可选从基准/闭眼图做**眼球层**

关键设计（踩过的坑都写在这）：

1) **按内容分段，不按固定格线**
   AI 画的序列条里，姿势位置会漂移（实测偏最多 38px），而且**有时只画 5 格而不是 6 格**。
   按 512 硬切会把猫身削掉。做法：在每条理想格线 ±窗口内，取"列占用最少"的 x 下刀。

2) **动画帧绝不能逐帧重新居中**
   逐帧居中 = 把帧间位移抹平，动画就死了。做法：以"内容中心"的中位数为锚点，
   每帧按 (自身中心 − 锚点) 的相对偏移贴入公共画布。

3) **底边对齐用"离格底的距离"而不是绝对坐标**
   让站姿基线一致（切换状态不跳），同时保住动画里的上下起伏。

4) **眼球层 = 基准图 − 闭眼图的差分**
   差分天然对齐，绕开"AI 各格头部位置不一致"的死结。

用法：
    python tools/build_skin_anim.py --prefix cat   --mode hd    --id cat_hd    --name "AI 高清橘猫"
    python tools/build_skin_anim.py --prefix cat   --mode pixel --id cat_pixel --name "AI 像素橘猫"
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from statistics import median

from PIL import Image, ImageChops, ImageDraw, ImageFilter

sys.path.insert(0, str(Path(__file__).resolve().parent))
from ai_sheet_to_skin import (  # noqa: E402
    FRAMES as _UNUSED, pixelize, has_alpha_cutout, remove_bg,
)

ROOT = Path(__file__).resolve().parents[1]
RAW = ROOT / "neko-assets" / "_airaw"

STATES = ["idle", "walk", "sit", "sleep", "pet", "drag", "fall", "climb", "work"]
FPS = {"idle": 6, "walk": 10, "sit": 6, "sleep": 3, "pet": 8,
       "drag": 8, "fall": 12, "climb": 8, "work": 8}

PAD = 4
CUT_SLACK = 90          # 在理想格线 ±这个范围内找真正的空白列
ALPHA_THR = 8

# 像素模式参数
PIXEL_SIZE = 64
PIXEL_COLORS = 24


# ---------------------------------------------------------------- 分段

def col_occupancy(img: Image.Image) -> list[int]:
    """每列有多少个不透明像素（抽样行，够用且快）。"""
    a = img.getchannel("A")
    px = a.load()
    W, H = img.size
    step = max(1, H // 200)
    return [sum(1 for y in range(0, H, step) if px[x, y] > ALPHA_THR) for x in range(W)]


def segment_by_islands(img: Image.Image, min_w: int = 60) -> list[tuple[int, int]]:
    """把序列条按**内容孤岛**分段 —— 有几个姿势就切几段。

    为什么不能按固定格线切：AI 画序列条时姿势位置会漂移（偏最多 38px），
    而且**有时只画 5 格不是 6 格**。按理想格线硬切会把猫身削掉（实测出现"半只猫"）。
    按孤岛切则天然对得上：有几个姿势就几段，宽度不用均匀。
    """
    occ = col_occupancy(img)
    W = len(occ)
    segs: list[list[int]] = []
    start = None
    gap = 0
    GAP_MERGE = 6            # 小于这个宽度的空白视为噪声（毛发的断续），不算分隔
    for x in range(W):
        if occ[x] > 0:
            if start is None:
                start = x
            gap = 0
        elif start is not None:
            gap += 1
            if gap > GAP_MERGE:
                segs.append([start, x - gap + 1])
                start = None
    if start is not None:
        segs.append([start, W])
    return [(a, b) for a, b in segs if b - a >= min_w]


def actual_islands(img: Image.Image, min_w: int = 60) -> int:
    return len(segment_by_islands(img, min_w))


def bbox_of_alpha(img: Image.Image):
    a = img.getchannel("A").point(lambda v: 255 if v > ALPHA_THR else 0)
    return a.getbbox()


# ---------------------------------------------------------------- 眼球层

def extract_pupil_layer(base_cell: Image.Image, closed_cell: Image.Image, blk: int = 16):
    """从 (基准, 闭眼) 差分里找出眼睛区域，再取出其中的**深色瞳孔**作为眼球层。

    为什么只取瞳孔而不是整只眼：
      整只眼取出来就必须把脸上原来的眼睛"抹掉"（需要 inpaint），抹掉后一平移就会露出色块缺口。
      只取瞳孔则眼睛的白底留在身体上，瞳孔在其内部平移 —— 这正是经典的眼神追踪做法。
    """
    b = base_cell.convert("RGBA")
    c = closed_cell.convert("RGBA")
    W, H = b.size
    diff = ImageChops.difference(b.convert("RGB"), c.convert("RGB")).convert("L")

    # 块密度找眼睛区域（差分里细密的轮廓噪声是 1~2px 宽，块密度低；眼睛是成片的）
    bw, bh = W // blk, H // blk
    grid = []
    dpx = diff.load()
    for by in range(bh):
        row = []
        for bx in range(bw):
            cnt = 0
            for y in range(by * blk, (by + 1) * blk, 2):
                for x in range(bx * blk, (bx + 1) * blk, 2):
                    if dpx[x, y] > 25:
                        cnt += 1
            row.append(cnt)
        grid.append(row)
    peak = max(v for row in grid for v in row)
    if peak == 0:
        return None, None
    hot = [(v, bx, by) for by, row in enumerate(grid) for bx, v in enumerate(row) if v > peak * 0.45]
    if not hot:
        return None, None

    # 取最密集的连通热区
    hotset = {(bx, by) for _, bx, by in hot}
    seen, groups = set(), []
    for p in hotset:
        if p in seen:
            continue
        stack, comp = [p], []
        seen.add(p)
        while stack:
            x, y = stack.pop()
            comp.append((x, y))
            for dx in (-1, 0, 1):
                for dy in (-1, 0, 1):
                    q = (x + dx, y + dy)
                    if q in hotset and q not in seen:
                        seen.add(q)
                        stack.append(q)
        groups.append(comp)
    groups.sort(key=len, reverse=True)
    main = groups[0]
    xs = [x for x, _ in main]
    ys = [y for _, y in main]
    x0, x1 = min(xs) * blk, (max(xs) + 1) * blk
    y0, y1 = min(ys) * blk, (max(ys) + 1) * blk

    # 眼睛区域内的"深色像素"= 瞳孔/虹膜
    region = b.crop((x0, y0, x1, y1)).convert("RGBA")
    rw, rh = region.size
    lum = region.convert("L")
    lpx = lum.load()
    vals = [lpx[x, y] for y in range(0, rh, 2) for x in range(0, rw, 2)]
    vals.sort()
    thr = max(60, vals[int(len(vals) * 0.35)])     # 比区域中位数暗 → 瞳孔

    pupil = Image.new("RGBA", (rw, rh), (0, 0, 0, 0))
    rpx, ppx = region.load(), pupil.load()
    for y in range(rh):
        for x in range(rw):
            r, g, bb, a = rpx[x, y]
            if a > ALPHA_THR and lpx[x, y] < thr:
                ppx[x, y] = (r, g, bb, a)

    # 形态学清理：去掉细碎噪点
    pm = pupil.getchannel("A")
    pm = pm.filter(ImageFilter.MinFilter(3)).filter(ImageFilter.MaxFilter(3))
    pupil.putalpha(pm)

    # 眼睛区域里"非瞳孔"部分的中位色（≈眼白/毛色），用来把身体上的瞳孔抹平成同色
    light = [(rpx[x, y][0], rpx[x, y][1], rpx[x, y][2])
             for y in range(0, rh, 2) for x in range(0, rw, 2)
             if rpx[x, y][3] > ALPHA_THR and lpx[x, y] >= thr]
    if not light:
        return None, None
    fill = tuple(int(median([c[i] for c in light])) for i in range(3))

    body = b.copy()
    bpx = body.load()
    ppx2 = pupil.load()
    for y in range(rh):
        for x in range(rw):
            if ppx2[x, y][3] > 100:
                bpx[x0 + x, y0 + y] = (fill[0], fill[1], fill[2], bpx[x0 + x, y0 + y][3])

    # 眼球层做成整帧大小（引擎按整帧坐标绘制）
    layer = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    layer.paste(pupil, (x0, y0), pupil)
    return body, layer


# ---------------------------------------------------------------- 主流程

def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--prefix", required=True)
    ap.add_argument("--mode", choices=["hd", "pixel"], required=True)
    ap.add_argument("--id", required=True, dest="skin_id")
    ap.add_argument("--name", required=True)
    ap.add_argument("--out", default=None)
    ap.add_argument("--max-frame", type=int, default=256)
    ap.add_argument("--recommended-scale", type=float, default=4.0)
    ap.add_argument("--no-eyes", action="store_true", help="跳过眼球层")
    args = ap.parse_args()

    out_dir = Path(args.out) if args.out else ROOT / "neko-assets" / args.skin_id
    base_sheet = RAW / f"{args.prefix}_base_3x3.png"
    closed_sheet = RAW / f"{args.prefix}_closed_3x3.png"

    print(f"=== 构建皮肤 {args.skin_id}（{args.name}）模式={args.mode} ===")

    # ---------- 1) 逐状态：分段 → 取帧 ----------
    per_state: dict[str, list[Image.Image]] = {}
    for st in STATES:
        p = RAW / f"{args.prefix}_seq_{st}.png"
        if not p.is_file():
            print(f"  ⚠️ 缺少 {p.name}，跳过 {st}")
            continue
        strip = Image.open(p).convert("RGBA")
        segs = segment_by_islands(strip)
        frames = []
        for (x0, x1) in segs:
            cell = strip.crop((x0, 0, x1, strip.height))
            bb = bbox_of_alpha(cell)
            if bb is None:
                continue
            frames.append((cell.crop(bb), bb, x1 - x0, cell.height))
        print(f"  {st:<6} 切成 {len(frames)} 帧"
              f"  内容高 {[f[0].height for f in frames]}")
        if frames:
            per_state[st] = frames

    if not per_state:
        print("❌ 没有任何序列条")
        return 1

    # ⚠️ 所有状态必须帧数一致：引擎按 skin.json 的 frames 从每张横排图里固定切 N 帧，
    #    某状态若只有 5 帧而 frames 写 6，第 6 帧会切到画布外 → 空白帧闪烁。
    #    AI 不同状态画了 5 或 6 格，取最小值截断（多余的那帧本来最接近第 1 帧，丢掉不影响循环）。
    n_frames = min(len(v) for v in per_state.values())
    for st in list(per_state):
        if len(per_state[st]) > n_frames:
            print(f"  {st}: {len(per_state[st])} 帧 → 截断到 {n_frames} 帧（与其它状态对齐）")
            per_state[st] = per_state[st][:n_frames]
    print(f"统一帧数: {n_frames}")

    # ---------- 2) 公共画布 ----------
    # 尺寸：所有帧里最大的内容；对齐：内容中心的中位数当锚点，底边按"离格底距离"保基线
    max_w = max(fr[0].width for frs in per_state.values() for fr in frs)
    gap_bottom = [fr[3] - fr[1][3] for frs in per_state.values() for fr in frs]
    max_gap = max(gap_bottom)
    max_h = max(fr[0].height + (fr[3] - fr[1][3]) for frs in per_state.values() for fr in frs)
    cw, ch = max_w + PAD * 2, max_h + PAD * 2
    print(f"公共画布: {cw}x{ch}（最大内容 {max_w}px，最大离底 {max_gap}px）")

    out_dir.mkdir(parents=True, exist_ok=True)

    # ---------- 3) 渲染每状态到公共画布（先只做几何，量化留到全局一起做）----------
    rendered: dict[str, list[Image.Image]] = {}
    for st, frames in per_state.items():
        centers = [(fr[1][0] + fr[1][2]) / 2 for fr in frames]
        anchor = median(centers)
        out = []
        for (content, bb, cell_w, cell_h) in frames:
            dx = (bb[0] + bb[2]) / 2 - cell_w / 2          # 相对格心的水平偏移
            dy = cell_h - bb[3]                            # 离格底的距离（保基线）
            canvas = Image.new("RGBA", (cw, ch), (0, 0, 0, 0))
            x = int(cw / 2 + (dx - (anchor - cell_w / 2)) - content.width / 2)
            y = int(ch - PAD - dy - content.height)
            canvas.paste(content, (x, y), content)
            out.append(canvas)
        rendered[st] = out

    # ---------- 4) 尺寸/量化 ----------
    if args.mode == "pixel":
        fw = fh = PIXEL_SIZE
        # ⚠️ 调色板必须**全皮肤共享**：逐帧各自量化会让每帧调色板不同，
        #    同一块毛色在帧间轻微跳变 → 动画看起来在闪。
        #    做法：把所有状态×帧拼成一张长图统一定调色板，再用该调色板套用到每一帧。
        flat = [f for frs in rendered.values() for f in frs]
        thumb = []
        for c in flat:
            inner = c.copy()
            inner.thumbnail((PIXEL_SIZE, PIXEL_SIZE), Image.LANCZOS)
            sq = Image.new("RGBA", (PIXEL_SIZE, PIXEL_SIZE), (0, 0, 0, 0))
            sq.paste(inner, ((PIXEL_SIZE - inner.width) // 2, PIXEL_SIZE - inner.height), inner)
            thumb.append(sq)
        print(f"像素模式：帧 {fw}x{fh}，全皮肤共享调色板 {PIXEL_COLORS} 色（{len(thumb)} 帧统一量化）")
        base = Image.new("RGBA", (PIXEL_SIZE * len(thumb), PIXEL_SIZE), (0, 0, 0, 0))
        for i, t in enumerate(thumb):
            base.paste(t, (i * PIXEL_SIZE, 0), t)
        palette = base.convert("RGB").quantize(colors=PIXEL_COLORS, method=Image.MEDIANCUT)
        finals: list[Image.Image] = []
        for t in thumb:
            alpha = t.getchannel("A").point(lambda v: 255 if v >= 128 else 0)
            q = t.convert("RGB").quantize(palette=palette).convert("RGBA")
            q.putalpha(alpha)
            finals.append(q)
        idx = 0
        for st in rendered:
            rendered[st] = finals[idx:idx + len(rendered[st])]
            idx += len(rendered[st])
    else:
        scale = min(1.0, args.max_frame / max(cw, ch))
        fw, fh = max(1, int(cw * scale)), max(1, int(ch * scale))
        print(f"高清模式：帧 {fw}x{fh}（下采样 {scale:.3f}）")
        for st in rendered:
            rendered[st] = [c.resize((fw, fh), Image.LANCZOS) for c in rendered[st]]

    # ---------- 5) 写出 + 收集状态元数据 ----------
    states_meta = []
    for st, frames in rendered.items():
        sheet = Image.new("RGBA", (fw * len(frames), fh), (0, 0, 0, 0))
        for i, f in enumerate(frames):
            sheet.paste(f, (i * fw, 0), f)
        sheet.save(out_dir / f"neko_{st}.png")
        print(f"  写出 neko_{st}.png  {sheet.size}（{len(frames)} 帧 × {fw}px）")
        states_meta.append({"name": st, "fps": FPS.get(st, 6),
                            "eyeLayer": None if args.no_eyes else f"eyes_{st}"})

    # ---------- 4) 眼球层 ----------
    eye_keys: list[str] = []
    if not args.no_eyes and base_sheet.is_file() and closed_sheet.is_file():
        bs = Image.open(base_sheet).convert("RGBA")
        cs = Image.open(closed_sheet).convert("RGBA")
        BW, BH = bs.size
        ok = 0
        for i, st in enumerate(STATES):
            r, c = divmod(i, 3)
            box = (c * BW // 3, r * BH // 3, (c + 1) * BW // 3, (r + 1) * BH // 3)
            bcell, ccell = bs.crop(box), cs.crop(box)
            body, layer = extract_pupil_layer(bcell, ccell)
            if layer is None:
                print(f"  ⚠️ {st}: 差分里没找到集中的眼睛区域，跳过眼球层")
                continue
            lb = layer.getbbox()
            if lb is None or (lb[2] - lb[0]) < 8 or (lb[3] - lb[1]) < 4:
                print(f"  ⚠️ {st}: 提取到的瞳孔太小，跳过")
                continue
            # 眼球层也要走同一套缩放
            if args.mode == "pixel":
                inner = layer.copy()
                inner.thumbnail((PIXEL_SIZE, PIXEL_SIZE), Image.LANCZOS)
                sq = Image.new("RGBA", (PIXEL_SIZE, PIXEL_SIZE), (0, 0, 0, 0))
                sq.paste(inner, ((PIXEL_SIZE - inner.width) // 2, PIXEL_SIZE - inner.height), inner)
                lay = pixelize(sq, PIXEL_COLORS)
            else:
                lay = layer.resize((fw, fh), Image.LANCZOS)
            lay.save(out_dir / f"neko_eyes_{st}.png")
            eye_keys.append(f"eyes_{st}")
            ok += 1
            print(f"  眼球层 {st}: 瞳孔区域 {lb[2]-lb[0]}x{lb[3]-lb[1]}")
        print(f"  眼球层共 {ok}/9 个")
    else:
        for m in states_meta:
            m["eyeLayer"] = None

    # ---------- 5) skin.json ----------
    skin = {
        "id": args.skin_id,
        "displayName": args.name,
        "layout": "sheet",
        "frameWidth": fw,
        "frameHeight": fh,
        "frames": n_frames,
        "hasShadow": False,
        "eyeLayers": eye_keys,
        "recommendedScale": args.recommended_scale,
        "states": states_meta,
    }
    (out_dir / "skin.json").write_text(
        json.dumps(skin, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"\n✅ {out_dir}  帧 {fw}x{fh}  每状态帧数 "
          f"{ {k: len(v) for k, v in per_state.items()} }")
    return 0


if __name__ == "__main__":
    sys.exit(main())
