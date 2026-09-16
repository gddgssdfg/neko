"""真机验证工具：抓取 Neko 运行中的窗口真实截图。

为什么需要它：
  - Neko 的猫窗口是 WPF 分层窗口（AllowsTransparency / WS_EX_LAYERED），
    GDI 的 GetWindowDC + BitBlt 常常抓不到内容（黑块）。
  - 本脚本对每个候选窗口三种方式都试一遍，取"非空"的那张：
      ① PrintWindow + PW_RENDERFULLCONTENT (0x2)  —— 最可靠
      ② PrintWindow 普通模式 (0x0)
      ③ 全屏 ImageGrab + 按窗口矩形裁剪        —— 兜底（DWM 合成后通常有效）

用法：
    python tools/capture_pet.py --out _live_shot.png [--pid 1234] [--proc Neko.exe]

产物：一个横向拼接的 PNG（每格是一个窗口的截图）+ 每个窗口单独的 PNG。
"""

from __future__ import annotations

import argparse
import ctypes
import ctypes.wintypes as wt
import sys
from pathlib import Path

from PIL import Image, ImageDraw

user32 = ctypes.WinDLL("user32", use_last_error=True)
gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)

PW_RENDERFULLCONTENT = 0x00000002
SRCCOPY = 0x00CC0020
DIB_RGB_COLORS = 0


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG),
        ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
        ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG), ("biYPelsPerMeter", wt.LONG),
        ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD),
    ]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", wt.DWORD * 3)]


def _proc_pids(name: str) -> set[int]:
    """用 tasklist 取进程 PID（避免依赖 psutil）。"""
    import subprocess
    out = subprocess.run(
        ["tasklist", "/FI", f"IMAGENAME eq {name}", "/FO", "CSV", "/NH"],
        capture_output=True, text=True, errors="ignore",
    ).stdout
    pids = set()
    for line in out.splitlines():
        parts = [p.strip('"') for p in line.split('","')]
        if len(parts) >= 2 and parts[0].lower() == name.lower():
            try:
                pids.add(int(parts[1]))
            except ValueError:
                pass
    return pids


def _window_pid(hwnd: int) -> int:
    pid = wt.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    return pid.value


def _list_windows(pids: set[int]) -> list[tuple[int, str, tuple[int, int, int, int]]]:
    """列出属于这些 PID 的可见顶层窗口 (hwnd, title, (l,t,r,b))。"""
    found: list[tuple[int, str, tuple[int, int, int, int]]] = []
    WNDENUMPROC = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)

    def cb(hwnd, _lparam):
        if not user32.IsWindowVisible(hwnd):
            return True
        if pids and _window_pid(hwnd) not in pids:
            return True
        n = user32.GetWindowTextLengthW(hwnd)
        buf = ctypes.create_unicode_buffer(n + 1)
        user32.GetWindowTextW(hwnd, buf, n + 1)
        rect = wt.RECT()
        if not user32.GetWindowRect(hwnd, ctypes.byref(rect)):
            return True
        w, h = rect.right - rect.left, rect.bottom - rect.top
        if w <= 0 or h <= 0:
            return True
        if w > 4000 or h > 4000:      # 跳过全屏/超大窗口（通常是主窗口外的杂项）
            return True
        found.append((hwnd, buf.value, (rect.left, rect.top, rect.right, rect.bottom)))
        return True

    user32.EnumWindows(WNDENUMPROC(cb), 0)
    return found


def _grab_printwindow(hwnd: int, rect) -> Image.Image | None:
    """PrintWindow → RGBA。flags=2 时用 PW_RENDERFULLCONTENT。"""
    w, h = rect[2] - rect[0], rect[3] - rect[1]
    hdc = user32.GetWindowDC(hwnd)
    if not hdc:
        return None
    memdc = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    old = gdi32.SelectObject(memdc, bmp)
    ok = False
    try:
        for flag in (PW_RENDERFULLCONTENT, 0):
            if user32.PrintWindow(hwnd, memdc, flag):
                ok = True
                break
        if not ok:
            return None
        bi = BITMAPINFO()
        bi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
        bi.bmiHeader.biWidth = w
        bi.bmiHeader.biHeight = -h        # 负数 = top-down
        bi.bmiHeader.biPlanes = 1
        bi.bmiHeader.biBitCount = 32
        bi.bmiHeader.biCompression = 0
        buf = ctypes.create_string_buffer(w * h * 4)
        if not gdi32.GetDIBits(memdc, bmp, 0, h, buf, ctypes.byref(bi), DIB_RGB_COLORS):
            return None
        return Image.frombuffer("RGBA", (w, h), buf, "raw", "BGRA", 0, 1).copy()
    finally:
        gdi32.SelectObject(memdc, old)
        gdi32.DeleteObject(bmp)
        gdi32.DeleteDC(memdc)
        user32.ReleaseDC(hwnd, hdc)


def _grab_screen_crop(rect) -> Image.Image:
    from PIL import ImageGrab
    im = ImageGrab.grab(bbox=rect, all_screens=True)
    return im.convert("RGBA")


def _is_blank(im: Image.Image, thresh: int = 6) -> bool:
    """判断图是否基本是空的（全黑 / 全透明 / 单色）。"""
    if im.mode != "RGBA":
        im = im.convert("RGBA")
    small = im.resize((32, 32))
    alphas = list(small.getchannel("A").getdata())
    if max(alphas) < thresh:
        return True
    colors = list(small.convert("RGB").getdata())
    return len(set(colors)) <= 2 and max(alphas) < 200


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--proc", default="Neko.exe")
    ap.add_argument("--pid", type=int, default=0, help="不指定则用该进程名的所有 PID")
    ap.add_argument("--out", default="_live_shot.png")
    ap.add_argument("--outdir", default=None, help="单窗口截图的输出目录，默认与 --out 同目录")
    args = ap.parse_args()

    pids = {args.pid} if args.pid else _proc_pids(args.proc)
    if not pids:
        print(f"[x] 未找到进程 {args.proc}")
        return 1
    print(f"[i] 进程 PID: {sorted(pids)}")

    wins = _list_windows(pids)
    if not wins:
        print("[x] 未找到可见顶层窗口")
        return 1

    out = Path(args.out).resolve()
    outdir = Path(args.outdir).resolve() if args.outdir else out.parent
    outdir.mkdir(parents=True, exist_ok=True)

    grads: list[tuple[Image.Image, str]] = []
    for hwnd, title, rect in wins:
        w, h = rect[2] - rect[0], rect[3] - rect[1]
        img = _grab_printwindow(hwnd, rect)
        src = "PrintWindow"
        if img is None or _is_blank(img):
            alt = _grab_screen_crop(rect)
            if not _is_blank(alt):
                img, src = alt, "ScreenCrop"
        if img is None:
            print(f"  [-] 0x{hwnd:X} {w}x{h} '{title}' → 抓取失败")
            continue
        tag = f"0x{hwnd:X}"
        p = outdir / f"_win_{tag}.png"
        img.save(p)
        blank = _is_blank(img)
        print(f"  [+] 0x{hwnd:X} {w}x{h} '{title}' via {src}{' (疑似空白)' if blank else ''} → {p.name}")
        grads.append((img, f"{w}x{h} {title or '(无标题)'}"))

    if not grads:
        print("[x] 没有任何窗口抓到内容")
        return 1

    # 横向拼接总览图
    CELL_H = max(im.height for im, _ in grads) + 34
    CELL_W = sum(im.width for im, _ in grads) + 16 * (len(grads) + 1)
    sheet = Image.new("RGB", (max(CELL_W, 320), CELL_H + 16), (235, 236, 238))
    d = ImageDraw.Draw(sheet)
    x = 16
    for im, label in grads:
        sheet.paste(im, (x, 16 + 22), im)
        d.text((x, 6), label, fill=(40, 40, 40))
        d.rectangle([x - 1, 16 + 22 - 1, x + im.width, 16 + 22 + im.height], outline=(150, 150, 150))
        x += im.width + 16
    sheet.save(out)
    print(f"\n[✓] 总览图: {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
