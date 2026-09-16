"""分析 AI 生成的九宫格 sprite sheet（判断是不是"真像素画"、内部像素网格多大）。

为什么需要：
  - 像素画降采样必须用**最近邻**且**整数倍**，否则会把像素格切碎、糊成一团。
  - 但 AI 出的"像素风"图通常是大图（例如每格 680px），内部其实有一个隐含的
    "逻辑像素"网格（比如每格 64×64 个方格，每个方格 10~11 屏幕像素）。
  - 本脚本估算这个网格尺寸 N：只要按 N 用最近邻缩放，就能还原出真正的像素画。

算法：
  1) 逐行取"颜色变化点"的 x 坐标；真像素画里这些坐标必然落在 N 的整数倍栅格上。
  2) 对候选 N（2..40）打分 = 变化点与栅格对齐的比例；取分数 ≥0.98 的最大 N
     （取最大是为了避免 N=2 这种"任意偶数都能对齐"的平凡解——先看整体一致性强不强）。
  3) 同时统计调色板大小与抗锯齿程度（唯一色数越多越像有抗锯齿）。

用法：
    python tools/analyze_ai_sheet.py neko-assets/_airaw/xxx.png [--grid 3]
"""

from __future__ import annotations

import argparse
import sys
from collections import Counter
from pathlib import Path

from PIL import Image


def content_bbox(img: Image.Image, tol: int = 18) -> tuple[int, int, int, int] | None:
    """非白/非透明内容的包围盒（用于判断每格猫的大小与位置）。"""
    rgb = img.convert("RGB")
    w, h = rgb.size
    px = rgb.load()
    minx, miny, maxx, maxy = w, h, -1, -1
    for y in range(h):
        for x in range(w):
            r, g, b = px[x, y]
            if not (r > 255 - tol and g > 255 - tol and b > 255 - tol):
                if x < minx: minx = x
                if y < miny: miny = y
                if x > maxx: maxx = x
                if y > maxy: maxy = y
    if maxx < 0:
        return None
    return (minx, miny, maxx + 1, maxy + 1)


def estimate_pixel_grid(img: Image.Image, max_n: int = 40) -> tuple[int, float]:
    """估算内部逻辑像素网格 N（每个逻辑像素占 N×N 屏幕像素）。"""
    rgb = img.convert("RGB")
    w, h = rgb.size
    px = rgb.load()

    changes: list[int] = []
    for y in range(0, h, max(1, h // 120)):          # 抽样行，够用且快
        prev = px[0, y]
        for x in range(1, w):
            cur = px[x, y]
            if cur != prev:
                changes.append(x)
                prev = cur
    if not changes:
        return 1, 1.0

    best_n, best_score = 1, 0.0
    for n in range(2, max_n + 1):
        hit = sum(1 for c in changes if c % n <= 1)
        score = hit / len(changes)
        if score >= 0.985 and n > best_n:
            best_n, best_score = n, score
    # 回退：取分数最高的
    if best_n == 1:
        scored = []
        for n in range(2, max_n + 1):
            hit = sum(1 for c in changes if c % n <= 1)
            scored.append((hit / len(changes), n))
        scored.sort(reverse=True)
        if scored:
            best_score, best_n = scored[0][0], scored[0][1]
    return best_n, best_score


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("image")
    ap.add_argument("--grid", type=int, default=3)
    args = ap.parse_args()

    path = Path(args.image).resolve()
    im = Image.open(path).convert("RGBA")
    W, H = im.size
    print(f"文件: {path.name}")
    print(f"尺寸: {W}×{H}  模式: {im.mode}")

    rgb = im.convert("RGB")
    corners = [rgb.getpixel(p) for p in ((2, 2), (W - 3, 2), (2, H - 3), (W - 3, H - 3))]
    print(f"四角像素: {corners}")
    white_ok = all(c == (255, 255, 255) for c in corners)
    print(f"背景纯白: {'✅' if white_ok else '⚠️ 不是纯白（抠图会更麻烦）'}")

    cw, ch = W // args.grid, H // args.grid
    print(f"每格: {cw}×{ch}")

    names = ["idle", "walk", "sit", "sleep", "pet", "drag", "fall", "climb", "work"]
    boxes = []
    print("\n每格内容包围盒（判断猫的大小/位置是否一致）：")
    for i in range(args.grid * args.grid):
        r, c = divmod(i, args.grid)
        cell = im.crop((c * cw, r * ch, (c + 1) * cw, (r + 1) * ch))
        bb = content_bbox(cell)
        nm = names[i] if i < len(names) else f"cell{i}"
        if bb is None:
            print(f"  {i+1:2d} {nm:6s}: ❌ 空")
            boxes.append(None)
            continue
        bw, bh = bb[2] - bb[0], bb[3] - bb[1]
        print(f"  {i+1:2d} {nm:6s}: 内容 {bw}×{bh}  左上({bb[0]},{bb[1]})")
        boxes.append(bb)

    valid = [b for b in boxes if b]
    if valid:
        ws = [b[2] - b[0] for b in valid]
        hs = [b[3] - b[1] for b in valid]
        print(f"\n内容尺寸范围: 宽 {min(ws)}~{max(ws)}  高 {min(hs)}~{max(hs)}")
        print(f"一致性: {'✅ 很一致' if max(ws) - min(ws) <= max(cw * 0.12, 20) else '⚠️ 差异较大'}")

    # 像素网格：取一格有内容的来分析
    idx = next((i for i, b in enumerate(boxes) if b), 0)
    r, c = divmod(idx, args.grid)
    cell = im.crop((c * cw, r * ch, (c + 1) * cw, (r + 1) * ch)).convert("RGB")
    n, score = estimate_pixel_grid(cell)
    print(f"\n内部逻辑像素网格估算: N = {n}（对齐度 {score:.3f}）")
    if n > 1:
        print(f"  → 按 N={n} 最近邻缩放，单格逻辑分辨率 ≈ {cw // n}×{ch // n}")
    else:
        print("  → 未检测到明显像素栅格（可能是软边/抗锯齿的伪像素画）")

    # 调色板与抗锯齿程度
    colors = Counter(cell.getdata())
    print(f"\n单格唯一色数: {len(colors)}  （真像素画通常 < 64；含抗锯齿会明显更多）")
    top = colors.most_common(6)
    print(f"  最常见色: {[(c, n_) for c, n_ in top]}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
