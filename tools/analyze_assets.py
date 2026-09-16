"""批量体检 AI 交付的素材：画布 / 透明通道 / 网格切分 / 每格一致性与留白。

为什么需要它：
  当前模型读不了图片，所有"看起来对不对"的判断必须转成可读的数字。
  它回答四个问题：
    1) 有没有透明背景（有 → 不用抠图）
    2) 画布多大、按预期网格切出来每格多大
    3) 每格里内容占多大、位置在哪（判断"猫的大小是否一致""有没有跑偏""有没有多余道具"）
    4) 内容是否贴边（贴边 = 被裁切，模型画超出格子了）

用法：
    python tools/analyze_assets.py                 # 体检 _airaw 下所有已知素材
    python tools/analyze_assets.py 文件.png --grid 3x3
"""

from __future__ import annotations

import sys
from pathlib import Path

from PIL import Image

RAW = Path(r"G:\lantetn-li\neko\neko-assets\_airaw")

# 期望布局：文件名 → (列, 行, 说明)
LAYOUT: dict[str, tuple[int, int, str]] = {
    "cat_base_3x3.png":     (3, 3, "橘猫基准九宫格"),
    "cat_closed_3x3.png":   (3, 3, "橘猫闭眼版（做眼球层）"),
    "phoebe_base_3x3.png":  (3, 3, "菲比基准九宫格"),
    "phoebe_closed_3x3.png":(3, 3, "菲比闭眼版"),
    "ui_icons_5x4.png":     (5, 4, "UI 图标 20 个（径向菜单）"),
    "ui_settings_icons.png":(2, 4, "设置页图标 8 个"),
    "bubble_cloud.png":     (1, 1, "气泡云（单个）"),
    "keycap_up.png":        (1, 1, "键帽常态（单个）"),
    "keycap_down.png":      (1, 1, "键帽按下态（单个）"),
}
for _s in ("idle","walk","sit","sleep","pet","drag","fall","climb","work"):
    for _p, _t in (("cat", "橘猫"), ("phoebe", "菲比")):
        LAYOUT[f"{_p}_seq_{_s}.png"] = (6, 1, f"{_t} 动画序列条（{_s}）")


def bbox_alpha(img: Image.Image, thr: int = 8):
    a = img.getchannel("A")
    return a.point(lambda v: 255 if v > thr else 0).getbbox()


def bbox_nonwhite(img: Image.Image, tol: int = 14):
    """无 alpha 图用"非白"求包围盒。"""
    rgb = img.convert("RGB")
    w, h = rgb.size
    px = rgb.load()
    minx, miny, maxx, maxy = w, h, -1, -1
    for y in range(0, h):
        for x in range(0, w):
            r, g, b = px[x, y]
            if not (r > 255 - tol and g > 255 - tol and b > 255 - tol):
                if x < minx: minx = x
                if y < miny: miny = y
                if x > maxx: maxx = x
                if y > maxy: maxy = y
    return (minx, miny, maxx + 1, maxy + 1) if maxx >= 0 else None


def alpha_ratio(img: Image.Image) -> float:
    hist = img.getchannel("A").histogram()
    return sum(hist[16:]) / max(1, img.width * img.height)


def analyze(path: Path, cols: int, rows: int, label: str) -> None:
    print(f"\n{'='*72}\n{path.name}   [{label}]   期望 {cols}×{rows}")
    if not path.is_file():
        print("  ❌ 文件不存在")
        return
    im = Image.open(path)
    print(f"  画布 {im.width}×{im.height}  模式 {im.mode}  {path.stat().st_size/1024:.0f} KB")
    im = im.convert("RGBA")

    ar = alpha_ratio(im)
    has_alpha = ar < 0.995
    print(f"  不透明像素占比 {ar*100:.1f}%  →  {'✅ 已带透明背景' if has_alpha else '⚠️ 不透明（需抠图）'}")

    cw, ch = im.width // cols, im.height // rows
    if im.width % cols or im.height % rows:
        print(f"  ⚠️ 画布不能被 {cols}×{rows} 整除（{im.width}%{cols}, {im.height}%{rows}），切格会有余数")
    print(f"  每格 {cw}×{ch}")

    bboxes = []
    for r in range(rows):
        line = []
        for c in range(cols):
            cell = im.crop((c*cw, r*ch, (c+1)*cw, (r+1)*ch))
            bb = bbox_alpha(cell) if has_alpha else bbox_nonwhite(cell)
            line.append(bb)
            bboxes.append(bb)
        parts = []
        for bb in line:
            parts.append("空" if bb is None else f"{bb[2]-bb[0]}×{bb[3]-bb[1]}")
        print(f"    行{r+1}: " + "  ".join(f"{p:>9s}" for p in parts))

    valid = [b for b in bboxes if b]
    if not valid:
        print("  ❌ 全部格子为空")
        return
    ws = [b[2]-b[0] for b in valid]
    hs = [b[3]-b[1] for b in valid]
    empties = sum(1 for b in bboxes if not b)
    print(f"  内容尺寸: 宽 {min(ws)}~{min(max(ws),9999)}  高 {min(hs)}~{max(hs)}"
          f"   （平均 宽{sum(ws)/len(ws):.0f} 高{sum(hs)/len(hs):.0f}）")
    if empties:
        print(f"  ⚠️ 有 {empties} 个空格子")
    # 一致性：宽度离散度
    import statistics
    cv_w = statistics.pstdev(ws) / (sum(ws)/len(ws)) if len(ws) > 1 else 0
    cv_h = statistics.pstdev(hs) / (sum(hs)/len(hs)) if len(hs) > 1 else 0
    grade = "✅ 一致" if max(cv_w, cv_h) < 0.12 else ("⚠️ 略有差异" if max(cv_w, cv_h) < 0.25 else "❌ 差异很大")
    print(f"  一致性: {grade}  (离散度 宽{cv_w*100:.0f}% 高{cv_h*100:.0f}%)")

    # 贴边检测：内容触到格子边界 = 可能被裁切
    clipped = 0
    for bb in valid:
        if bb[0] <= 1 or bb[1] <= 1 or bb[2] >= cw-1 or bb[3] >= ch-1:
            clipped += 1
    if clipped:
        print(f"  ⚠️ {clipped}/{len(valid)} 格的内容贴到格子边缘（可能被裁切）")


def main() -> int:
    if len(sys.argv) >= 2 and not sys.argv[1].startswith("--"):
        p = Path(sys.argv[1])
        cols, rows = (3, 3)
        if "--grid" in sys.argv:
            g = sys.argv[sys.argv.index("--grid")+1]
            cols, rows = (int(v) for v in g.lower().split("x"))
        analyze(p, cols, rows, "手工指定")
        return 0

    if not RAW.is_dir():
        print(f"❌ 目录不存在: {RAW}")
        return 1
    print(f"体检目录: {RAW}")
    print(f"共 {len(list(RAW.glob('*.png')))} 个 PNG")
    for name, (cols, rows, label) in LAYOUT.items():
        analyze(RAW / name, cols, rows, label)

    extra = [p for p in RAW.glob("*.png") if p.name not in LAYOUT]
    if extra:
        print(f"\n{'='*72}\n（未在清单里的文件）")
        for p in extra:
            print(f"  {p.name}  {p.stat().st_size/1024:.0f} KB")
    return 0


if __name__ == "__main__":
    sys.exit(main())
