"""临时验证脚本（未跟踪的取证产物，不属于交付源码）：

在真机上对 Neko 猫窗口做**高频连拍**，跨过若干次「眨眼窗口」（~140ms）。
判据不是「整帧差分」（会被眼神追踪的 ±6px 平移污染），而是
**眼球带里的深色像素（瞳孔/闭合线）计数**：

  * 修复前 cat_hd：眨眼相位 query 全局 eyes_half/eyes_blink → 皮肤没有 → eye=null
    → 整个眼球层被丢掉 → 露出"被抹掉瞳孔的身体帧" → **瞳孔墨点骤降**（空白）；
  * 修复后 cat_hd：回退到自己的眼球层 → 瞳孔全程保留 → 墨点稳定（不眨眼）；
  * 修复后 pixel：有真实 eyes_half/eyes_blink → 眨眼时眼球层变"闭眼" → 墨点仍变化（眨眼正常）。

复用 tools/capture_pet.py 的 PrintWindow 抓取（分层窗口抓不到才退屏裁）。
用法：python _blink_probe.py --out _blink_x.png --dur 14 [--crop 眼带放大图前缀]
"""

from __future__ import annotations

import argparse
import ctypes
import sys
import time
from pathlib import Path

try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    try:
        ctypes.windll.user32.SetProcessDPIAware()
    except Exception:
        pass

from PIL import Image, ImageDraw  # noqa: E402

sys.path.insert(0, str(Path(__file__).resolve().parent / "tools"))
import capture_pet as cp  # noqa: E402


def pick_pet_window(pids: set[int]):
    cands = []
    for hwnd, title, rect in cp._list_windows(pids):
        w, h = rect[2] - rect[0], rect[3] - rect[1]
        if w < 64 or h < 64 or w > 900 or h > 900:
            continue
        if 0.5 <= w / h <= 2.0:
            cands.append((w * h, hwnd, title, rect))
    if not cands:
        return None
    cands.sort(reverse=True)
    return cands[0]


def eye_band(im: Image.Image) -> Image.Image:
    w, h = im.size
    return im.convert("RGBA").crop((0, int(h * 0.18), w, int(h * 0.72)))


def ink(im: Image.Image) -> int:
    band = eye_band(im)
    g = band.convert("L")
    a = band.getchannel("A")
    n = 0
    for l, al in zip(g.getdata(), a.getdata()):
        if al > 128 and l < 72:
            n += 1
    return n


def pct(xs: list[int], p: float) -> int:
    s = sorted(xs)
    return s[min(len(s) - 1, int(p * (len(s) - 1)))]


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--dur", type=float, default=14.0)
    ap.add_argument("--interval", type=float, default=0.008)
    ap.add_argument("--label", default="")
    ap.add_argument("--crop", default="", help="眼带放大图前缀（产出 _open.png / _blank.png）")
    args = ap.parse_args()

    pids = cp._proc_pids("Neko.exe")
    if not pids:
        print("[x] 未找到 Neko.exe")
        return 1
    picked = pick_pet_window(pids)
    if not picked:
        print("[x] 未找到接近正方形的猫窗口")
        for hwnd, title, rect in cp._list_windows(pids):
            print("   候选:", hex(hwnd), rect[2] - rect[0], rect[3] - rect[1], repr(title))
        return 1
    _, hwnd, title, rect = picked
    print(f"[i] 猫窗 0x{hwnd:X} {rect[2]-rect[0]}x{rect[3]-rect[1]} '{title}'")

    frames: list[tuple[float, Image.Image, int]] = []
    t0 = time.perf_counter()
    while time.perf_counter() - t0 < args.dur:
        im = cp._grab_printwindow(hwnd, rect)
        if im is None:
            continue
        frames.append((time.perf_counter() - t0, im, ink(im)))
        time.sleep(args.interval)
    if len(frames) < 5:
        print("[x] 抓到的帧太少:", len(frames))
        return 1

    inks = [f[2] for f in frames]
    base = pct(inks, 0.80)                    # 众数附近"睁眼/静止"基准墨点
    drop = base - max(int(base * 0.06), 24)   # 明显掉墨 = 疑似空白
    blank = [i for i, v in enumerate(inks) if v < drop]

    print(f"[i] 帧数={len(frames)} 时长={frames[-1][0]:.2f}s")
    print(f"[i] 眼球带墨点: base(p80)={base} min={min(inks)} max={max(inks)} "
          f"p50={pct(inks,0.5)} p20={pct(inks,0.2)} 空白阈值={drop}")
    print(f"[i] 空白帧数={len(blank)} 占比={len(blank)/len(frames):.1%} 前若干位置={blank[:40]}")
    # 连续段（疑似眨眼窗口）
    runs = []
    for i in blank:
        if runs and i == runs[-1][-1] + 1:
            runs[-1].append(i)
        else:
            runs.append([i])
    print(f"[i] 空白连续段数={len(runs)} 各段长度={[len(r) for r in runs][:20]}")

    # 拼图：均匀抽 20 帧 + 全部"最低墨点"帧（红框）
    order = sorted(range(len(frames)), key=lambda i: inks[i])
    low = sorted(set(order[:8]))
    picks = sorted(set([int(i * (len(frames) - 1) / 19) for i in range(20)] + low + blank[:20]))
    picks = [p for p in picks if p < len(frames)]
    CELL = 104
    cols = 14
    rows = (len(picks) + cols - 1) // cols
    sheet = Image.new("RGB", (cols * (CELL + 6) + 6, rows * (CELL + 26) + 30), (238, 239, 242))
    d = ImageDraw.Draw(sheet)
    d.text((6, 6), f"{args.label}  frames={len(frames)} blank={len(blank)} base={base} "
                   f"min={min(inks)} thr={drop}  (红框=墨点<阈值→空白眼)", fill=(20, 20, 20))
    for k, p in enumerate(picks):
        r, c = divmod(k, cols)
        cell = frames[p][1].convert("RGBA").resize((CELL, CELL), Image.NEAREST)
        bg = Image.new("RGBA", (CELL, CELL), (255, 255, 255, 255))
        bg.alpha_composite(cell)
        x = 6 + c * (CELL + 6)
        y = 30 + r * (CELL + 26)
        sheet.paste(bg.convert("RGB"), (x, y))
        is_blank = p in blank
        d.rectangle([x - 1, y - 1, x + CELL, y + CELL],
                    outline=(220, 30, 30) if is_blank else (150, 150, 150),
                    width=3 if is_blank else 1)
        d.text((x, y + CELL + 3), f"#{p} ink={inks[p]}", fill=(200, 20, 20) if is_blank else (60, 60, 60))
    Path(args.out).parent.mkdir(parents=True, exist_ok=True)
    sheet.save(args.out)
    print(f"[✓] 证据图: {args.out}")

    if args.crop:
        hi = frames[order[-1]][1]     # 墨点最多的帧 = 睁眼
        lo = frames[order[0]][1]      # 墨点最少的帧 = 最"空白"
        for tag, src, idx in (("open", hi, order[-1]), ("blank", lo, order[0])):
            b = eye_band(src).resize((eye_band(src).width * 3, eye_band(src).height * 3), Image.NEAREST)
            canvas = Image.new("RGB", (b.width, b.height + 18), (245, 245, 245))
            canvas.paste(b.convert("RGB"), (0, 18))
            ImageDraw.Draw(canvas).text((4, 3), f"{args.label} #{idx} ink={inks[idx]}", fill=(20, 20, 20))
            p = f"{args.crop}_{tag}.png"
            canvas.save(p)
            print(f"[✓] 眼带放大: {p}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
