"""真机诊断：右键点猫打开径向菜单并截图，用来核对"菜单里到底有几个插件槽位"。

用法：
    python tools/probe_radial_menu.py --out _radial_shot.png

做的事：
    1. 找到 Neko 的猫窗口（进程下最小的那个可见顶层窗口）
    2. 在窗口中心合成一次鼠标右键按下+抬起（SendInput）
    3. 等菜单淡入动画结束，枚举该进程的全部新窗口并逐个截图
    4. 拼成一张总览图输出

纯 ctypes + Pillow 实现，不依赖 pywin32（沙箱里 Add-Type / 第三方包不可靠）。
"""

from __future__ import annotations

import argparse
import ctypes
import ctypes.wintypes as wt
import subprocess
import sys
import time
from pathlib import Path

from PIL import Image, ImageDraw

sys.path.insert(0, str(Path(__file__).resolve().parent))
from capture_pet import (  # noqa: E402
    BITMAPINFO, BITMAPINFOHEADER, DIB_RGB_COLORS, PW_RENDERFULLCONTENT,
    _is_blank, _list_windows, _proc_pids, gdi32, user32,
)

INPUT_MOUSE = 0
MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004
MOUSEEVENTF_RIGHTDOWN = 0x0008
MOUSEEVENTF_RIGHTUP = 0x0010
MOUSEEVENTF_MOVE = 0x0001
MOUSEEVENTF_ABSOLUTE = 0x8000


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [("dx", wt.LONG), ("dy", wt.LONG), ("mouseData", wt.DWORD),
                ("dwFlags", wt.DWORD), ("time", wt.DWORD),
                ("dwExtraInfo", ctypes.POINTER(wt.ULONG))]


class INPUT(ctypes.Structure):
    class _U(ctypes.Union):
        _fields_ = [("mi", MOUSEINPUT)]
    _anonymous_ = ("u",)
    _fields_ = [("type", wt.DWORD), ("u", _U)]


def _send_mouse(flags: int, x: int = 0, y: int = 0) -> None:
    inp = INPUT()
    inp.type = INPUT_MOUSE
    inp.mi = MOUSEINPUT(x, y, 0, flags, 0, None)
    user32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT))


def _to_absolute(x: int, y: int) -> tuple[int, int]:
    """屏幕像素 → SendInput 的 0..65535 归一化坐标。"""
    vx = user32.GetSystemMetrics(76)   # SM_XVIRTUALSCREEN
    vy = user32.GetSystemMetrics(77)   # SM_YVIRTUALSCREEN
    vw = user32.GetSystemMetrics(78)   # SM_CXVIRTUALSCREEN
    vh = user32.GetSystemMetrics(79)   # SM_CYVIRTUALSCREEN
    return int((x - vx) * 65535 / max(vw - 1, 1)), int((y - vy) * 65535 / max(vh - 1, 1))


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--proc", default="Neko.exe")
    ap.add_argument("--pid", type=int, default=0)
    ap.add_argument("--out", default="_radial_shot.png")
    ap.add_argument("--wait", type=float, default=0.9, help="菜单淡入等待秒数")
    args = ap.parse_args()

    pids = {args.pid} if args.pid else _proc_pids(args.proc)
    if not pids:
        print(f"[x] 未找到进程 {args.proc}")
        return 1

    before = {h for h, _, _ in _list_windows(pids)}
    wins = _list_windows(pids)
    if not wins:
        print("[x] 无可见窗口")
        return 1

    # 猫窗口 = 面积最小的**正方形**可见窗口（镜像窗 229x32 长条、菜单窗出现在点击之后）
    squares = [w for w in wins if (w[2][2] - w[2][0]) == (w[2][3] - w[2][1])]
    pool = squares or wins
    cat_hwnd, cat_title, cat_rect = min(
        pool, key=lambda w: (w[2][2] - w[2][0]) * (w[2][3] - w[2][1]))
    cx = (cat_rect[0] + cat_rect[2]) // 2
    cy = (cat_rect[1] + cat_rect[3]) // 2
    print(f"[i] 猫窗口 0x{cat_hwnd:X} rect={cat_rect} 中心=({cx},{cy})")

    ax, ay = _to_absolute(cx, cy)

    # 先左键点屏幕远处，确保没有已打开的菜单（菜单遇外部点击即关）
    fx, fy = _to_absolute(5, 5)
    _send_mouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE, fx, fy)
    _send_mouse(MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_ABSOLUTE, fx, fy)
    time.sleep(0.05)
    _send_mouse(MOUSEEVENTF_LEFTUP | MOUSEEVENTF_ABSOLUTE, fx, fy)
    time.sleep(0.35)

    _send_mouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE, ax, ay)
    time.sleep(0.15)
    _send_mouse(MOUSEEVENTF_RIGHTDOWN | MOUSEEVENTF_ABSOLUTE, ax, ay)
    time.sleep(0.05)
    _send_mouse(MOUSEEVENTF_RIGHTUP | MOUSEEVENTF_ABSOLUTE, ax, ay)
    print("[i] 已右键点击猫，等待菜单淡入…")
    time.sleep(args.wait)

    after = _list_windows(pids)
    new_wins = [w for w in after if w[0] not in before] or after
    print(f"[i] 窗口数 {len(before)} → {len(after)}，新增 {len(after) - len(before)} 个")

    shots: list[tuple[Image.Image, str]] = []
    for hwnd, title, rect in sorted(new_wins, key=lambda w: -(w[2][2] - w[2][0])):
        w, h = rect[2] - rect[0], rect[3] - rect[1]
        if w <= 0 or h <= 0:
            continue
        hdc = user32.GetWindowDC(hwnd)
        memdc = gdi32.CreateCompatibleDC(hdc)
        bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
        old = gdi32.SelectObject(memdc, bmp)
        img = None
        try:
            for flag in (PW_RENDERFULLCONTENT, 0):
                if user32.PrintWindow(hwnd, memdc, flag):
                    bi = BITMAPINFO()
                    bi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
                    bi.bmiHeader.biWidth = w
                    bi.bmiHeader.biHeight = -h
                    bi.bmiHeader.biPlanes = 1
                    bi.bmiHeader.biBitCount = 32
                    buf = ctypes.create_string_buffer(w * h * 4)
                    if gdi32.GetDIBits(memdc, bmp, 0, h, buf, ctypes.byref(bi), DIB_RGB_COLORS):
                        img = Image.frombuffer("RGBA", (w, h), buf, "raw", "BGRA", 0, 1).copy()
                        break
        finally:
            gdi32.SelectObject(memdc, old)
            gdi32.DeleteObject(bmp)
            gdi32.DeleteDC(memdc)
            user32.ReleaseDC(hwnd, hdc)
        if img is None:
            continue
        blank = _is_blank(img)
        tag = "空白" if blank else "有内容"
        print(f"  [+] 0x{hwnd:X} {w}x{h} '{title}' → {tag}")
        single = Path(args.out).resolve().parent / f"_menu_0x{hwnd:X}.png"
        img.save(single)
        print(f"      → {single.name}")
        shots.append((img, f"{w}x{h} {title or '(无标题)'} [{tag}]"))

    if not shots:
        print("[x] 没抓到任何窗口")
        return 1

    cell_h = max(i.height for i, _ in shots) + 40
    cell_w = sum(i.width for i, _ in shots) + 16 * (len(shots) + 1)
    sheet = Image.new("RGB", (max(cell_w, 320), cell_h + 16), (235, 236, 238))
    d = ImageDraw.Draw(sheet)
    x = 16
    for im, label in shots:
        sheet.paste(im, (x, 16 + 24), im)
        d.text((x, 6), label, fill=(30, 30, 30))
        d.rectangle([x - 1, 16 + 24 - 1, x + im.width, 16 + 24 + im.height], outline=(140, 140, 140))
        x += im.width + 16
    out = Path(args.out).resolve()
    sheet.save(out)
    print(f"\n[✓] {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
