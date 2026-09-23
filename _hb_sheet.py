"""把 _hb_before / _hb_after 两组离屏渲染拼成对照证据图 + 数值表（仓外/未跟踪的取证产物）。"""
from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

BASE = Path("G:/lantetn-li/neko")
_FP = "C:/Windows/Fonts/msyh.ttc"
FONT = ImageFont.truetype(_FP, 13)
FONT_S = ImageFont.truetype(_FP, 11)


def T(d, xy, s, fill=(0, 0, 0), small=False):
    d.text(xy, s, fill=fill, font=FONT_S if small else FONT)
SKINS = ["cat_hd", "phoebe_hd", "cat_pixel", "phoebe_pixel", "pixel", "soft"]
STATES = ["idle", "sit"]
PHASES = ["open", "half", "closed"]


def load(tag: str, skin: str, state: str, phase: str) -> Image.Image:
    return Image.open(BASE / f"_hb_{tag}" / f"_hb_{skin}_{state}_{phase}.png").convert("RGBA")


def ink(im: Image.Image) -> int:
    """全图深色墨点（瞳孔/闭眼线）数量。"""
    a = im.getchannel("A")
    g = im.convert("L")
    return sum(1 for l, al in zip(g.getdata(), a.getdata()) if al > 128 and l < 70)


def diff(pa: Image.Image, pb: Image.Image) -> float:
    a = pa.resize((64, 64)).tobytes()
    b = pb.resize((64, 64)).tobytes()
    s = n = 0
    for i in range(0, len(a), 4):
        s += abs(a[i] - b[i]) + abs(a[i + 1] - b[i + 1]) + abs(a[i + 2] - b[i + 2])
        n += 3
    return s / n


def blanked(o: Image.Image, c: Image.Image) -> bool:
    """Closed 相相比 Open 相是否掉了瞳孔（深色墨点显著减少）= 空白眼。"""
    io, ic = ink(o), ink(c)
    return (io - ic) > max(0.05 * io, 20)


def paste_cell(sheet, im, x, y, size, outline, width, cap, cap_fill):
    cell = im.resize((size, size), Image.NEAREST)
    bg = Image.new("RGBA", (size, size), (255, 255, 255, 255))
    bg.alpha_composite(cell)
    sheet.paste(bg.convert("RGB"), (x, y))
    d = ImageDraw.Draw(sheet)
    d.rectangle([x - 1, y - 1, x + size, y + size], outline=outline, width=width)
    T(d, (x, y + size + 3), cap, fill=cap_fill, small=True)


def headline():
    """cat_hd idle：修复前 Closed=空白眼；修复后 Closed=Open。"""
    size = 168
    HEAD = 34
    sheet = Image.new("RGB", (700, HEAD + (size + 26) * 2 + 20), (240, 241, 244))
    d = ImageDraw.Draw(sheet)
    T(d, (8, 8), "cat_hd / idle  —— 眨眼相位离屏渲染（确定性逐相驱动 BlinkPhase）", fill=(15, 15, 15))

    def row(tag, label, y):
        T(d, (8, y - 14), label, fill=(15, 15, 15))
        o = load(tag, "cat_hd", "idle", "open")
        c = load(tag, "cat_hd", "idle", "closed")
        dd = diff(o, c)
        blank = blanked(o, c)
        paste_cell(sheet, o, 8, y, size, (110, 110, 110), 1, f"Open  ink={ink(o)}", (60, 60, 60))
        paste_cell(sheet, c, 8 + size + 44, y, size,
                   (220, 30, 30) if blank else (30, 160, 60), 4 if blank else 3,
                   f"Closed ink={ink(c)}  diff={dd:.1f}  {'← 瞳孔消失(空白眼)' if blank else '← 与 Open 一致(瞳孔保留)'}",
                   (200, 20, 20) if blank else (20, 130, 50))

    row("before", "修复前 (缺 blink 素材 → 眼球层被丢)", HEAD + 12)
    row("after", "修复后 (回退到自己眼球层)", HEAD + 12 + (size + 24))
    out = BASE / "_blink_ab_cat_hd.png"
    sheet.save(out)
    print("[✓]", out)


def allsheet():
    size = 84
    HEAD = 30
    cols = 6
    rowh = size + 26
    sheet = Image.new("RGB", (60 + size * cols + 70, HEAD + rowh * (len(SKINS) * len(STATES)) + 20),
                      (240, 241, 244))
    d = ImageDraw.Draw(sheet)
    T(d, (8, 6), "所有皮肤 / idle+sit  —— 左3列=修复前(Open/Half/Closed)，右3列=修复后", fill=(15, 15, 15))
    x0 = 60
    for ci, ph in enumerate(PHASES + PHASES):
        T(d, (x0 + ci * (size + 8) + 2, 18), ("BEFORE " if ci < 3 else "AFTER ") + ph, fill=(40, 40, 40), small=True)
    y = HEAD + 4
    print(f"{'skin/state':<22} {'before d(o,c)':>13} {'after d(o,c)':>13} {'before ink o/c':>16} {'after ink o/c':>16}")
    for skin in SKINS:
        for state in STATES:
            T(d, (6, y + size // 2 - 8), f"{skin}\n{state}", fill=(40, 40, 40), small=True)
            b_o, b_c = load("before", skin, state, "open"), load("before", skin, state, "closed")
            a_o, a_c = load("after", skin, state, "open"), load("after", skin, state, "closed")
            bd, ad = diff(b_o, b_c), diff(a_o, a_c)
            for ci, (im, tag) in enumerate([(b_o, "b"), (load("before", skin, state, "half"), "b"),
                                            (b_c, "b"), (a_o, "a"),
                                            (load("after", skin, state, "half"), "a"), (a_c, "a")]):
                outline = (110, 110, 110)
                w = 1
                if ci in (2, 5):
                    changed = blanked(b_o, b_c) if ci == 2 else blanked(a_o, a_c)
                    outline = (220, 30, 30) if changed else (30, 160, 60)
                    w = 3
                paste_cell(sheet, im, x0 + ci * (size + 8), y, size, outline, w, "", (0, 0, 0))
            print(f"{skin+'/'+state:<22} {bd:>13.1f} {ad:>13.1f} "
                  f"{str(ink(b_o))+'/'+str(ink(b_c)):>16} {str(ink(a_o))+'/'+str(ink(a_c)):>16}")
            y += rowh
    out = BASE / "_blink_ab_all.png"
    sheet.save(out)
    print("[✓]", out)


if __name__ == "__main__":
    headline()
    allsheet()
