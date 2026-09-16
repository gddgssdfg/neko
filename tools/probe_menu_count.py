"""真机诊断：打开 Neko 的径向菜单，数出菜单里实际渲染了几个槽位。

背景（踩坑记录）：
  - 本机显示器是 125% 缩放（物理 2560x1440 / 逻辑 2048x1152）。
  - 非 DPI-aware 的 Python 进程调用 GetWindowRect/GetSystemMetrics 拿到的是**逻辑坐标**，
    而 Pillow 的 ImageGrab 抓的是**物理像素** → 直接按 GetWindowRect 裁剪会错位 1.25 倍。
  - 解法：进程启动时声明 Per-Monitor DPI Aware，之后所有窗口坐标即物理像素，与抓屏一致。

判据：
  - 菜单窗口尺寸 = 2*(OuterRadius+2) 高 + 多页时再加 HeaderH(26)。
    所以「窗口高 == 宽」⇔ 单页 ⇔ 总槽位 <= 8（每页 8 个）。
  - 有槽位时会画中心洞（透出猫）；无槽位时画整盘 + "无快捷项"。

用法：
    python tools/probe_menu_count.py [--out _menu_probe.png]
"""

from __future__ import annotations

import argparse
import ctypes
import ctypes.wintypes as wt
import math
import sys
import time
from collections import deque
from pathlib import Path

# ---- 关键：先声明 DPI 感知，必须在任何窗口 API 之前调用 ----
try:
    ctypes.WinDLL("shcore").SetProcessDpiAwareness(2)   # PROCESS_PER_MONITOR_DPI_AWARE
except Exception:
    try:
        ctypes.WinDLL("user32").SetProcessDPIAware()
    except Exception:
        pass

from PIL import Image, ImageGrab   # noqa: E402

u = ctypes.WinDLL("user32", use_last_error=True)

MOVE, AB, LB, LU, RB, RU = 0x0001, 0x8000, 0x0002, 0x0004, 0x0008, 0x0010


class MI(ctypes.Structure):
    _fields_ = [("dx", wt.LONG), ("dy", wt.LONG), ("md", wt.DWORD),
                ("fl", wt.DWORD), ("tm", wt.DWORD), ("ex", ctypes.POINTER(wt.ULONG))]


class IN(ctypes.Structure):
    class _U(ctypes.Union):
        _fields_ = [("mi", MI)]
    _anonymous_ = ("u",)
    _fields_ = [("type", wt.DWORD), ("u", _U)]


def mouse(flags: int, x: int, y: int) -> None:
    i = IN()
    i.type = 0
    i.mi = MI(x, y, 0, flags, 0, None)
    u.SendInput(1, ctypes.byref(i), ctypes.sizeof(IN))


def absxy(x: int, y: int) -> tuple[int, int]:
    vx, vy = u.GetSystemMetrics(76), u.GetSystemMetrics(77)
    vw, vh = u.GetSystemMetrics(78), u.GetSystemMetrics(79)
    return (int((x - vx) * 65535 / max(vw - 1, 1)),
            int((y - vy) * 65535 / max(vh - 1, 1)))


def enum_windows() -> list[tuple[int, int, int, int, int, int, int]]:
    out: list[tuple[int, int, int, int, int, int, int]] = []
    CB = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)

    def cb(h, _l):
        if not u.IsWindowVisible(h):
            return True
        r = wt.RECT()
        if u.GetWindowRect(h, ctypes.byref(r)):
            out.append((h, r.left, r.top, r.right, r.bottom,
                        r.right - r.left, r.bottom - r.top))
        return True

    u.EnumWindows(CB(cb), 0)
    return out


def classify(p, plate) -> str:
    d = max(abs(p[0] - plate[0]), abs(p[1] - plate[1]), abs(p[2] - plate[2]))
    v = sum(p[:3]) / 3
    if d <= 14:
        return "."
    if v < 60:
        return "@"
    if max(p[:3]) - min(p[:3]) > 45:
        return "#"
    if v > 200:
        return "o"
    return ":"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="_menu_probe.png")
    ap.add_argument("--wait", type=float, default=1.0)
    args = ap.parse_args()

    print(f"[i] DPI 感知已启用；逻辑屏 {u.GetSystemMetrics(78)}x{u.GetSystemMetrics(79)}"
          f"（若与抓屏尺寸一致则无错位）")

    before = {w[0] for w in enum_windows()}
    cats = [w for w in enum_windows() if w[5] == w[6] and 100 <= w[5] <= 400]
    if not cats:
        print("[x] 找不到方形的猫窗口 —— Neko 在跑吗？")
        return 1
    cat = min(cats, key=lambda w: w[5])
    lx, ly = (cat[1] + cat[3]) // 2, (cat[2] + cat[4]) // 2
    print(f"[i] 猫窗 {cat[5]}x{cat[6]}px  中心 ({lx},{ly})")

    # 关掉可能已开的菜单
    fx, fy = absxy(6, 6)
    mouse(MOVE | AB, fx, fy)
    mouse(LB | AB, fx, fy)
    time.sleep(0.06)
    mouse(LU | AB, fx, fy)
    time.sleep(0.6)

    ax, ay = absxy(lx, ly)
    mouse(MOVE | AB, ax, ay)
    time.sleep(0.2)
    mouse(RB | AB, ax, ay)
    time.sleep(0.06)
    mouse(RU | AB, ax, ay)
    time.sleep(args.wait)

    # 菜单窗口是复用的（同一个 hwnd 反复开关），所以不能靠"新出现的 hwnd"判定，
    # 改为按尺寸定位：菜单在物理像素上 = 250 DIP × 1.25 ≈ 312，多页时高再 +26 DIP。
    cands = [w for w in enum_windows()
             if 240 <= w[5] <= 420 and w[5] != w[6] == w[5] or (240 <= w[5] <= 420 and abs(w[6] - w[5]) <= 40)]
    cands = [w for w in cands if abs((w[1] + w[3]) // 2 - lx) < 60 and abs((w[2] + w[4]) // 2 - ly) < 60]
    if not cands:
        print("[x] 菜单没开出来（按尺寸/位置都没找到贴着猫的方形窗口）")
        return 1
    m = max(cands, key=lambda w: w[5] * w[6])
    hwnd, l, t, r, b, w, h = m
    print(f"[i] 菜单窗口 (physical) {w}x{h} @({l},{t})")

    multi = h > w
    pages = max(1, round((h - w) / 26) + 1) if multi else 1
    print(f"[i] 高{'!=' if multi else '=='}宽 → {'多页' if multi else '单页'}"
          f"；据此推算总槽位 {'>8（约 %d 页）' % pages if multi else '<= 8'}")

    full = ImageGrab.grab(all_screens=True).convert("RGB")
    print(f"[i] 抓屏 {full.size}（与逻辑屏一致 = DPI 无错位）")
    img = full.crop((l, t, r, b))
    out = Path(args.out).resolve()
    img.save(out)

    W, H = img.size
    # 盘面色：取最右下角内侧
    plate = img.getpixel((W - 4, H - 4))
    print(f"[i] 盘面色 {plate}")

    cx, cy = W // 2, H // 2
    cen = img.getpixel((cx, cy))
    print(f"[i] 中心像素 {cen}  → {'洞（透出猫）' if cen != plate and classify(cen, plate) != '.' else '盘面（疑似空菜单）'}")

    # 槽位圆心理论半径：_holeR = 猫半宽+10 = 32*Scale/2+10；SlotRadius = holeR+3+19
    hole_r = (W / 2 - 2) - 38 - 8 - 3          # 由 ring=2*(SlotR+19+8+2) 反推
    slot_r = hole_r + 3 + 19
    print(f"[i] 推算 洞半径={hole_r:.0f}  槽位圆心半径={slot_r:.0f}")

    # 沿槽位圆环采样：统计"非盘面"的角度区间 -> 槽位个数
    hits = []
    N = 1440
    for i in range(N):
        deg = 360.0 * i / N
        x = int(cx + slot_r * math.cos(math.radians(deg)))
        y = int(cy + slot_r * math.sin(math.radians(deg)))
        if 0 <= x < W and 0 <= y < H:
            hits.append(classify(img.getpixel((x, y)), plate) != ".")
    runs = []
    if any(hits):
        start = None
        for i in range(N * 2):
            cur = hits[i % N]
            if cur and start is None:
                start = i
            elif not cur and start is not None:
                runs.append((start, i))
                start = None
        # 合并首尾相接的同一段
        if len(runs) >= 2 and runs[0][0] == 0 and runs[-1][1] == 2 * N:
            runs = [(runs[-1][0] - N, runs[0][1])] + runs[1:-1]
    print(f"[i] 槽位圆环上检测到 {len(runs)} 个非盘面区段")

    # 非盘面像素的总量（含图标/文字/描边）
    nonplate = sum(1 for y in range(0, H, 2) for x in range(0, W, 2)
                   if classify(img.getpixel((x, y)), plate) not in (".", "@"))
    print(f"[i] 盘面内的非盘面像素（2px 抽样）: {nonplate}")

    # ASCII
    AW, AH = 64, 34
    sm = img.resize((AW, AH), Image.BOX)
    print("\n=== 菜单 ASCII（. 盘面  @ 近黑/桌面  # 彩色  o 浅色  : 中间调）===")
    for y in range(AH):
        print(f"{y:2d}|" + "".join(classify(sm.getpixel((x, y)), plate) for x in range(AW)) + "|")

    print(f"\n[✓] 截图: {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
