"""判定 sprite sheet 是否真的被切成独立格子：沿 X / Y 做 alpha 投影，数"孤岛"个数。

为什么这是最关键的一道质检：
  图像模型经常把"6 格序列条"画成**一张连续的长图**（猫的身体跨过格线），
  这时按 512 硬切会把猫切成碎片。而"内容宽度正好等于格宽"正是它的典型征兆。

判据：
  - 沿 X 投影（某列只要有 1 个不透明像素就算有内容）→ 统计连续 True 的段数
  - 段数 == 期望格数 → ✅ 有真正的间隙，可以安全硬切
  - 段数 == 1        → ❌ 内容连成一片，硬切必碎
  - 段数 在中间      → ⚠️ 部分粘连，需要看具体位置

用法：
    python tools/find_grid.py <图片> [--cols 6] [--rows 1]
    python tools/find_grid.py --all          # 扫 _airaw 下所有序列条
"""

from __future__ import annotations

import sys
from pathlib import Path

from PIL import Image

RAW = Path(r"G:\lantetn-li\neko\neko-assets\_airaw")


def islands(flags: list[bool], min_gap: int = 1) -> list[tuple[int, int]]:
    """把布尔序列切成"连续 True 段"，间距 < min_gap 的段合并。"""
    runs: list[list[int]] = []
    start = None
    for i, v in enumerate(flags + [False]):
        if v and start is None:
            start = i
        elif not v and start is not None:
            runs.append([start, i])
            start = None
    # 合并很近的段（抗锯齿/细缝）
    merged: list[list[int]] = []
    for r in runs:
        if merged and r[0] - merged[-1][1] <= min_gap:
            merged[-1][1] = r[1]
        else:
            merged.append(r)
    return [(a, b) for a, b in merged]


def analyze(path: Path, cols: int, rows: int, thr: int = 8) -> None:
    im = Image.open(path).convert("RGBA")
    W, H = im.size
    a = im.getchannel("A").point(lambda v: 255 if v > thr else 0)
    px = a.load()

    col_has = [any(px[x, y] for y in range(0, H, 2)) for x in range(W)]
    row_has = [any(px[x, y] for x in range(0, W, 2)) for y in range(H)]

    ix = islands(col_has)
    iy = islands(row_has)

    print(f"\n{'='*70}\n{path.name}   {W}×{H}   期望 {cols}×{rows}")
    print(f"  X 方向孤岛数: {len(ix)}  （期望 {cols}）"
          f"  {'✅' if len(ix) == cols else ('❌ 连成一片，硬切会碎' if len(ix) < cols else '⚠️ 比预期多')}")
    if len(ix) <= 12:
        print("    孤岛区间: " + "  ".join(f"[{a_:4d},{b_:4d}]" for a_, b_ in ix))
    print(f"  Y 方向孤岛数: {len(iy)}  （期望 {rows}）"
          f"  {'✅' if len(iy) == rows else '⚠️'}")

    # 格线处是否有内容穿过（真正判定"跨格"）
    if cols > 1:
        crosses = []
        for k in range(1, cols):
            x = round(k * W / cols)
            band = sum(1 for dx in (-2, -1, 0, 1, 2) if 0 <= x+dx < W and col_has[x+dx])
            crosses.append(band)
        bad = [i+1 for i, b in enumerate(crosses) if b >= 3]
        print(f"  格线穿越检测（1~{cols-1} 号格线）: "
              f"{'❌ 以下格线有内容穿过 → ' + str(bad) + ' 号格线' if bad else '✅ 所有格线干净'}")
    if rows > 1:
        crosses = []
        for k in range(1, rows):
            y = round(k * H / rows)
            band = sum(1 for dy in (-2, -1, 0, 1, 2) if 0 <= y+dy < H and row_has[y+dy])
            crosses.append(band)
        bad = [i+1 for i, b in enumerate(crosses) if b >= 3]
        if bad:
            print(f"  ❌ 横向格线 {bad} 号有内容穿过")


NAMES = ["idle","walk","sit","sleep","pet","drag","fall","climb","work"]

def main() -> int:
    if "--all" in sys.argv:
        print("扫描所有序列条（期望 6×1）")
        for p in ("cat", "phoebe"):
            for n in NAMES:
                f = RAW / f"{p}_seq_{n}.png"
                if f.is_file():
                    analyze(f, 6, 1)
        print(f"\n{'#'*70}\n扫描九宫格（期望 3×3）")
        for f in ("cat_base_3x3.png", "cat_closed_3x3.png", "phoebe_base_3x3.png", "phoebe_closed_3x3.png"):
            p = RAW / f
            if p.is_file():
                analyze(p, 3, 3)
        print(f"\n{'#'*70}\n扫描 UI 素材")
        for f, c, r in (("ui_icons_5x4.png", 5, 4), ("ui_settings_icons.png", 2, 4),
                        ("ui_settings_icons.png", 4, 2)):
            p = RAW / f
            if p.is_file():
                analyze(p, c, r)
        return 0

    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    p = Path(sys.argv[1])
    cols = int(sys.argv[sys.argv.index("--cols")+1]) if "--cols" in sys.argv else 6
    rows = int(sys.argv[sys.argv.index("--rows")+1]) if "--rows" in sys.argv else 1
    analyze(p, cols, rows)
    return 0


if __name__ == "__main__":
    sys.exit(main())
