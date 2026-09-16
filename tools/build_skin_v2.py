"""把豆包的素材组装成 Neko 皮肤（v2：静态图 + 序列条混合）。

与 v1（build_skin_anim.py 初版）的区别：
  - **待机/坐/睡用静态图**（`{prefix}_still_*.png`）而不是序列条 —— 落实"猫大部分时间不动"，
    呼吸/眨眼/随机小动作交给程序做。
  - **分段策略改成"先试等分，验证通过就用"**：v2 素材是按 512 等分排的（实测内容高离散 0~8%），
    但上一版素材位置会漂移。所以先按等分切并校验（无空格 + 高度离散 <20%），不合格再退回孤岛切。
  - **跨画布尺寸归一**：序列条每格 512、静态图整张 1024，两者内容占比都约 78%，
    所以按"画布比"缩放（1024→×0.5）就能对齐，不能直接用原始像素尺寸。

用法：
    python tools/build_skin_v2.py --prefix cat    --mode pixel --id cat_pixel --name "像素猫（动态）"
    python tools/build_skin_v2.py --prefix cat    --mode hd    --id cat_hd    --name "高清猫（动态）"
    python tools/build_skin_v2.py --prefix phoebe --mode hd    --id phoebe_hd --name "菲比（高清）"
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from statistics import median

from PIL import Image, ImageChops, ImageFilter

ROOT = Path(r"G:\lantetn-li\neko")
RAW = ROOT / "neko-assets" / "_airaw"

# 状态顺序与引擎一致；前 3 个走静态图，后 6 个走序列条
STILL_STATES = ["idle", "sit", "sleep"]
SEQ_STATES = ["walk", "pet", "drag", "fall", "climb", "work"]
STATES = ["idle", "walk", "sit", "sleep", "pet", "drag", "fall", "climb", "work"]

FPS = {"idle": 0, "sit": 0, "sleep": 0,          # 0 = 静态（引擎只画第 0 帧）
       "walk": 10, "pet": 8, "drag": 8, "fall": 12, "climb": 8, "work": 8}

PAD = 4
ALPHA_THR = 8
REF_CELL = 512          # 统一的"一格"参考边长：序列条每格就是这个尺寸
PIXEL_SIZE = 64
PIXEL_COLORS = 24


def bbox_of_alpha(img: Image.Image):
    a = img.getchannel("A").point(lambda v: 255 if v > ALPHA_THR else 0)
    return a.getbbox()


def col_occupancy(img: Image.Image) -> list[int]:
    a = img.getchannel("A")
    px = a.load()
    W, H = img.size
    step = max(1, H // 200)
    return [sum(1 for y in range(0, H, step) if px[x, y] > ALPHA_THR) for x in range(W)]


def segment_nominal(img: Image.Image, n: int) -> list[tuple[int, int]]:
    W = img.width
    return [(round(i * W / n), round((i + 1) * W / n)) for i in range(n)]


def segment_by_islands(img: Image.Image, min_w: int = 60) -> list[tuple[int, int]]:
    occ = col_occupancy(img)
    W = len(occ)
    segs: list[list[int]] = []
    start, gap = None, 0
    for x in range(W):
        if occ[x] > 0:
            if start is None:
                start = x
            gap = 0
        elif start is not None:
            gap += 1
            if gap > 6:
                segs.append([start, x - gap + 1])
                start = None
    if start is not None:
        segs.append([start, W])
    return [(a, b) for a, b in segs if b - a >= min_w]


def cut_quality(img: Image.Image, segs: list[tuple[int, int]]) -> tuple[int, float]:
    """返回 (空格数, 内容高离散度)。"""
    hs = []
    empt = 0
    for (x0, x1) in segs:
        c = img.crop((x0, 0, x1, img.height))
        b = bbox_of_alpha(c)
        if b is None:
            empt += 1
            hs.append(0)
        else:
            hs.append(b[3] - b[1])
    nz = [h for h in hs if h > 0]
    spread = (max(nz) - min(nz)) / max(1, max(nz)) if nz else 1.0
    return empt, spread


def segment_auto(img: Image.Image, expected: int) -> tuple[list[tuple[int, int]], str]:
    """先试等分；校验通过就用（v2 素材就是等分排的）。否则退回孤岛切。"""
    nom = segment_nominal(img, expected)
    empt, spread = cut_quality(img, nom)
    if empt == 0 and spread < 0.20:
        return nom, f"等分{expected}格(离散{spread*100:.0f}%)"
    isl = segment_by_islands(img)
    if len(isl) >= 2:
        e2, s2 = cut_quality(img, isl)
        if e2 == 0 and s2 <= spread:
            return isl, f"孤岛{len(isl)}段(离散{s2*100:.0f}%)"
    return nom, f"等分{expected}格(强制,离散{spread*100:.0f}%)"


# ---------------------------------------------------------------- 眼球层

def extract_pupil_layer(base_cell: Image.Image, closed_cell: Image.Image, blk: int = 16):
    """从 (基准, 闭眼) 差分里定位眼睛区域，再取其中的深色瞳孔作为眼球层。

    只取瞳孔（不取整只眼）：整只眼取出来就得把脸上的眼睛抹掉（需 inpaint），
    抹掉后一平移会露出色块缺口；只取瞳孔则眼白留在身体上，瞳孔在其内部平移——经典眼神追踪做法。
    """
    b = base_cell.convert("RGBA")
    c = closed_cell.convert("RGBA")
    W, H = b.size
    diff = ImageChops.difference(b.convert("RGB"), c.convert("RGB")).convert("L")
    dpx = diff.load()
    bw, bh = W // blk, H // blk
    grid = []
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
    hot = {(bx, by) for by, row in enumerate(grid) for bx, v in enumerate(row) if v > peak * 0.45}
    seen, groups = set(), []
    for p in hot:
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
                    if q in hot and q not in seen:
                        seen.add(q)
                        stack.append(q)
        groups.append(comp)
    groups.sort(key=len, reverse=True)
    main = groups[0]
    xs = [x for x, _ in main]
    ys = [y for _, y in main]
    x0, x1 = min(xs) * blk, (max(xs) + 1) * blk
    y0, y1 = min(ys) * blk, (max(ys) + 1) * blk

    region = b.crop((x0, y0, x1, y1)).convert("RGBA")
    rw, rh = region.size
    lum = region.convert("L")
    lpx = lum.load()
    vals = sorted(lpx[x, y] for y in range(0, rh, 2) for x in range(0, rw, 2))
    thr = max(60, vals[int(len(vals) * 0.35)])

    pupil = Image.new("RGBA", (rw, rh), (0, 0, 0, 0))
    rpx, ppx = region.load(), pupil.load()
    light = []
    for y in range(rh):
        for x in range(rw):
            r, g, bb, a = rpx[x, y]
            if a > ALPHA_THR and lpx[x, y] < thr:
                ppx[x, y] = (r, g, bb, a)
            elif a > ALPHA_THR:
                light.append((r, g, bb))
    pm = pupil.getchannel("A").filter(ImageFilter.MinFilter(3)).filter(ImageFilter.MaxFilter(3))
    pupil.putalpha(pm)
    if not light:
        return None, None
    fill = tuple(int(median([c_[i] for c_ in light])) for i in range(3))

    body = b.copy()
    bpx = body.load()
    p2 = pupil.load()
    for y in range(rh):
        for x in range(rw):
            if p2[x, y][3] > 100:
                bpx[x0 + x, y0 + y] = (fill[0], fill[1], fill[2], bpx[x0 + x, y0 + y][3])

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
    ap.add_argument("--no-eyes", action="store_true")
    args = ap.parse_args()

    out_dir = Path(args.out) if args.out else ROOT / "neko-assets" / args.skin_id
    base_sheet = RAW / f"{args.prefix}_base_3x3.png"
    closed_sheet = RAW / f"{args.prefix}_closed_3x3.png"

    print(f"=== 构建皮肤 {args.skin_id}（{args.name}）模式={args.mode} ===")

    # ---------- 1) 取帧 ----------
    pieces: dict[str, list[tuple[Image.Image, int, int]]] = {}   # state → [(内容图, 离底, 格宽)]
    for st in STATES:
        if st in STILL_STATES:
            p = RAW / f"{args.prefix}_still_{st}.png"
            if not p.is_file():
                print(f"  ⚠️ 缺 {p.name}，跳过 {st}")
                continue
            src = Image.open(p).convert("RGBA")
            # 跨画布归一：静态图整张 1024、序列条每格 512 → 按画布比缩放
            sc = REF_CELL / max(src.width, src.height)
            if abs(sc - 1.0) > 1e-6:
                src = src.resize((max(1, int(src.width * sc)), max(1, int(src.height * sc))), Image.LANCZOS)
            bb = bbox_of_alpha(src)
            if bb is None:
                continue
            content = src.crop(bb)
            gap = src.height - bb[3]
            pieces[st] = [(content, gap, src.width)]
            print(f"  {st:<6} 静态图 → 1 帧  归一化系数 {sc:.2f}  内容 {content.size}")
        else:
            p = RAW / f"{args.prefix}_seq_{st}.png"
            if not p.is_file():
                print(f"  ⚠️ 缺 {p.name}，跳过 {st}")
                continue
            strip = Image.open(p).convert("RGBA")
            segs, how = segment_auto(strip, 8)
            fps_frames = []
            for (x0, x1) in segs:
                cell = strip.crop((x0, 0, x1, strip.height))
                bb = bbox_of_alpha(cell)
                if bb is None:
                    continue
                fps_frames.append((cell.crop(bb), cell.height - bb[3], x1 - x0))
            print(f"  {st:<6} {how} → {len(fps_frames)} 帧")
            if fps_frames:
                pieces[st] = fps_frames

    if not pieces:
        print("❌ 没有任何可用素材")
        return 1

    # ---------- 2) 帧数统一 ----------
    # 引擎按 skin.json 的 frames 从每张图固定切 N 帧；各状态帧数不一致会切到画布外（空白帧闪烁）。
    seq_counts = [len(v) for k, v in pieces.items() if k in SEQ_STATES]
    n_frames = min(seq_counts) if seq_counts else 1
    for st in list(pieces):
        if len(pieces[st]) > n_frames:
            pieces[st] = pieces[st][:n_frames]
        elif len(pieces[st]) < n_frames:      # 静态图/帧少的：循环补齐（内容相同，视觉静止）
            base = pieces[st]
            while len(pieces[st]) < n_frames:
                pieces[st].append(base[len(pieces[st]) % len(base)])
    print(f"统一帧数: {n_frames}")

    # ---------- 3) 公共画布 ----------
    max_w = max(fr[0].width for v in pieces.values() for fr in v)
    max_gap = max(fr[1] for v in pieces.values() for fr in v)
    max_h = max(fr[0].height + fr[1] for v in pieces.values() for fr in v)
    cw, ch = max_w + PAD * 2, max_h + PAD * 2
    print(f"公共画布: {cw}x{ch}（最大内容 {max_w}px，最大离底 {max_gap}px）")
    out_dir.mkdir(parents=True, exist_ok=True)

    # ---------- 4) 渲染到公共画布 ----------
    rendered: dict[str, list[Image.Image]] = {}
    for st, frs in pieces.items():
        # 水平锚点：取各帧内容中心的中位数，保留帧间相对位移（逐帧重新居中会把动作抹平）
        anchor = median([(fr[0].width) / 2 + fr[1] * 0 for fr in frs]) if False else None
        centers = []
        for (content, gap, cell_w) in frs:
            centers.append(cell_w / 2)      # 内容已被裁到包围盒，用格心代表原位
        out = []
        for (content, gap, cell_w) in frs:
            canvas = Image.new("RGBA", (cw, ch), (0, 0, 0, 0))
            x = (cw - content.width) // 2
            y = ch - PAD - gap - content.height
            canvas.paste(content, (x, max(0, y)), content)
            out.append(canvas)
        rendered[st] = out

    # ---------- 5) 尺寸 / 量化 ----------
    if args.mode == "pixel":
        fw = fh = PIXEL_SIZE
        flat = [f for v in rendered.values() for f in v]
        thumb = []
        for c in flat:
            inner = c.copy()
            inner.thumbnail((PIXEL_SIZE, PIXEL_SIZE), Image.LANCZOS)
            sq = Image.new("RGBA", (PIXEL_SIZE, PIXEL_SIZE), (0, 0, 0, 0))
            sq.paste(inner, ((PIXEL_SIZE - inner.width) // 2, PIXEL_SIZE - inner.height), inner)
            thumb.append(sq)
        print(f"像素模式：帧 {fw}x{fh}，全皮肤共享调色板 {PIXEL_COLORS} 色（{len(thumb)} 帧统一量化）")
        big = Image.new("RGBA", (PIXEL_SIZE * len(thumb), PIXEL_SIZE), (0, 0, 0, 0))
        for i, t in enumerate(thumb):
            big.paste(t, (i * PIXEL_SIZE, 0), t)
        pal = big.convert("RGB").quantize(colors=PIXEL_COLORS, method=Image.MEDIANCUT)
        finals = []
        for t in thumb:
            a = t.getchannel("A").point(lambda v: 255 if v >= 128 else 0)
            q = t.convert("RGB").quantize(palette=pal).convert("RGBA")
            q.putalpha(a)
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

    # ---------- 6) 写出 ----------
    states_meta = []
    for st in STATES:
        if st not in rendered:
            continue
        frames = rendered[st]
        sheet = Image.new("RGBA", (fw * len(frames), fh), (0, 0, 0, 0))
        for i, f in enumerate(frames):
            sheet.paste(f, (i * fw, 0), f)
        sheet.save(out_dir / f"neko_{st}.png")
        print(f"  写出 neko_{st}.png  {sheet.size}（{len(frames)} 帧 × {fw}px）")
        states_meta.append({"name": st, "fps": FPS.get(st, 8), "eyeLayer": None})

    # ---------- 7) 眼球层 ----------
    eye_keys: list[str] = []
    if not args.no_eyes and base_sheet.is_file() and closed_sheet.is_file():
        bs = Image.open(base_sheet).convert("RGBA")
        cs = Image.open(closed_sheet).convert("RGBA")
        BW, BH = bs.size
        # 基准九宫格每格尺寸 → 和序列条格宽对齐（同样按画布比）
        sc = REF_CELL / (BW // 3)
        fw_cell = max(1, int((BW // 3) * sc))
        # 先全部提取，再做离群校验：
        # sleep / pet 这类"本来就是闭眼/眯眼"的状态，差分出来的不是眼睛（实测 112x14、15x32 这种畸形尺寸），
        # 直接把畸形结果画上去会在脸上多出一块错位的色斑，必须剔除。
        raw: list[tuple[str, Image.Image, tuple[int, int, int, int]]] = []
        for i, st in enumerate(STATES):
            r, c = divmod(i, 3)
            box = (c * BW // 3, r * BH // 3, (c + 1) * BW // 3, (r + 1) * BH // 3)
            bcell = bs.crop(box).resize((fw_cell, fw_cell), Image.LANCZOS)
            ccell = cs.crop(box).resize((fw_cell, fw_cell), Image.LANCZOS)
            _body, layer = extract_pupil_layer(bcell, ccell)
            if layer is None:
                print(f"  ⚠️ {st}: 没找到集中的眼睛区域，跳过眼球层")
                continue
            lb = layer.getbbox()
            if lb is None:
                continue
            raw.append((st, layer, lb))

        if raw:
            mw = median([lb[2] - lb[0] for _, _, lb in raw])
            mh = median([lb[3] - lb[1] for _, _, lb in raw])
            print(f"  瞳孔尺寸中位数 {mw:.0f}x{mh:.0f}，按 0.35 倍下限做离群校验")
            for st, layer, lb in raw:
                w, h = lb[2] - lb[0], lb[3] - lb[1]
                if w < mw * 0.35 or h < mh * 0.35:
                    print(f"  ✗ {st}: 瞳孔 {w}x{h} 明显偏离（该状态本就闭眼/眯眼），剔除眼球层")
                    continue
                if args.mode == "pixel":
                    inner = layer.copy()
                    inner.thumbnail((PIXEL_SIZE, PIXEL_SIZE), Image.LANCZOS)
                    sq = Image.new("RGBA", (PIXEL_SIZE, PIXEL_SIZE), (0, 0, 0, 0))
                    sq.paste(inner, ((PIXEL_SIZE - inner.width) // 2, PIXEL_SIZE - inner.height), inner)
                    lay = sq.convert("RGB").quantize(colors=PIXEL_COLORS).convert("RGBA")
                    lay.putalpha(sq.getchannel("A").point(lambda v: 255 if v >= 128 else 0))
                else:
                    lay = layer.resize((fw, fh), Image.LANCZOS)
                lay.save(out_dir / f"neko_eyes_{st}.png")
                eye_keys.append(f"eyes_{st}")
                print(f"  ✓ 眼球层 {st}: 瞳孔 {w}x{h}")
        if eye_keys:
            for m in states_meta:
                if f"eyes_{m['name']}" in eye_keys:
                    m["eyeLayer"] = f"eyes_{m['name']}"
        print(f"  眼球层共 {len(eye_keys)}/9（其余用整帧图里的原眼睛）")
    elif args.no_eyes:
        print("（按 --no-eyes 跳过眼球层）")

    # ---------- 8) skin.json ----------
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
    print(f"\n✅ {out_dir}  帧 {fw}x{fh} × {n_frames}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
