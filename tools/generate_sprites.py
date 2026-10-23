#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Neko 像素猫雪碧图生成器
========================
用几何图元绘制 + 自动描边，输出 32x32 精灵（4 帧/状态），眼球独立分层。

设计约束（与 docs/skin-development-guide.md 一致）：
  - 画布 32x32，放大 3 倍 = 96x96 显示
  - 每状态 4 帧，横向排列成 128x32 雪碧图
  - 眼球独立分层，坐标与身体层严格对齐
  - 头部中心统一固定在 (13, 11)，保证眼球层零偏移对齐
  - sleep 状态自带闭眼，不叠加眼球层
  - 纯硬边像素，无抗锯齿

用法：
  python tools/generate_sprites.py

皮肤契约（v0.1.76 起）：皮肤目录内的 skin.json 是素材侧唯一权威
-----------------------------------------------------------------------------
  neko-assets/<skinId>/skin.json
  {
    "id": "pixel", "displayName": "像素风", "layout": "sheet",
    "frameWidth": 32, "frameHeight": 32, "frames": 4, "hasShadow": true,
    "eyeLayers": ["eyes_open", "eyes_pet", "eyes_blink", "eyes_half", "eyes_scared"],
    "states": [ { "name": "idle", "fps": 4, "eyeLayer": "eyes_open" }, ... ]
  }
- "states[].fps"：状态名 → 每秒帧数，**宿主读它作为 fps 唯一来源**（skin.json 优先，无二次覆盖）。
- 本脚本只生成 PNG 素材（neko_<state>.png 横排 4 帧 / 眼球层 / shadow），
  **不再生成 anim.json**——旧的 anim.json fps 覆盖表会静默盖掉 skin.json（作者陷阱），已从宿主移除。
  skin.json 由作者手写/维护（本脚本不覆盖它，避免把手调的 fps 冲掉）。
- 新增皮肤 = 新目录（如 neko-assets/pikachu/）+ skin.json（清单）+ 同名 PNG（neko_<state>.png / 眼球层）。
  宿主零改动（v0.1.74 起由 SkinRegistry 扫目录发现）。
"""

import os
from PIL import Image, ImageDraw, ImageFilter, ImageChops

SIZE = 32
NF = 4
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PREVIEW_OUT = os.path.join(ROOT, "_eval", "pixel-b01-redraw")
OUT = os.path.join(ROOT, "neko-assets", "pixel")   # v0.1.74：像素皮肤迁入 neko-assets/pixel/（与 skin.json 同目录）

# 头部锚点：所有需要叠加眼球层的状态都必须用这个坐标
HEAD_CX, HEAD_CY = 13, 11

# ---------------- 调色板（严格 12 色）----------------
C_OUTLINE = (0x3A, 0x2A, 0x20, 255)
C_FUR_L   = (0xFF, 0xC5, 0x74, 255)
C_FUR_M   = (0xF7, 0xA3, 0x3F, 255)
C_FUR_D   = (0xD4, 0x7A, 0x28, 255)
C_FUR_DP  = (0xA8, 0x5F, 0x26, 255)
C_WHITE   = (0xFF, 0xF0, 0xD4, 255)
C_WSH     = (0xE8, 0xDC, 0xCB, 255)
C_PUPIL   = (0x2A, 0x1E, 0x18, 255)
C_HI      = (0xFF, 0xFF, 0xFF, 255)
C_NOSE    = (0xE8, 0x8B, 0x8B, 255)
C_PAW     = (0xF2, 0xA0, 0xA0, 255)
C_TONGUE  = (0xD9, 0x73, 0x8C, 255)


# ---------------- 画布 ----------------
class Cv:
    def __init__(self, w=SIZE, h=SIZE):
        self.im = Image.new("RGBA", (w, h), (0, 0, 0, 0))
        self.d = ImageDraw.Draw(self.im)

    def ell(self, cx, cy, rx, ry, col):
        self.d.ellipse([cx - rx, cy - ry, cx + rx, cy + ry], fill=col)

    def rect(self, x0, y0, x1, y1, col):
        x0, x1 = sorted((int(x0), int(x1)))
        y0, y1 = sorted((int(y0), int(y1)))
        self.d.rectangle([x0, y0, x1, y1], fill=col)

    def poly(self, pts, col):
        self.d.polygon([(int(x), int(y)) for x, y in pts], fill=col)

    def pline(self, pts, w, col):
        """硬边粗折线：沿线采样，每点画 w x w 方块"""
        pts = [(int(x), int(y)) for x, y in pts]
        off = w // 2
        for i in range(len(pts) - 1):
            x0, y0 = pts[i]
            x1, y1 = pts[i + 1]
            n = max(abs(x1 - x0), abs(y1 - y0))
            for s in range(n + 1):
                t = s / n if n else 0
                x = round(x0 + (x1 - x0) * t)
                y = round(y0 + (y1 - y0) * t)
                self.rect(x - off, y - off, x - off + w - 1, y - off + w - 1, col)

    def out(self, col=C_OUTLINE):
        """自动描边：alpha 膨胀 -> 与原图差分 -> 描边环"""
        a = self.im.split()[3]
        dil = a.filter(ImageFilter.MaxFilter(3))
        ring = ImageChops.subtract(dil, a)
        o = Image.new("RGBA", self.im.size, col)
        o.putalpha(ring)
        return Image.alpha_composite(o, self.im)


# ---------------- 部件 ----------------
def cat_head(c, expression="open", cx=HEAD_CX, cy=HEAD_CY):
    """B01: round cheeks, short ears, small dark eyes and cream muzzle."""
    def points(ps):
        return [(cx+x,cy+y) for x,y in ps]
    c.poly(points([(-8,-4),(-8,-8),(-6,-8),(-3,-5),(3,-5),(6,-8),(8,-8),(8,-4)]), C_FUR_M)
    c.poly(points([(-7,-6),(-6,-6),(-4,-4),(-7,-4)]), C_PAW)
    c.poly(points([(7,-6),(6,-6),(4,-4),(7,-4)]), C_PAW)
    c.poly(points([(-5,-5),(5,-5),(8,-3),(10,0),(10,3),(8,6),(4,8),(-4,8),(-8,6),(-10,3),(-10,0),(-8,-3)]), C_FUR_M)
    for x in (-3,0,3):
        c.rect(cx+x,cy-5,cx+x,cy-3+(x==0),C_FUR_D)
    for side in (-1,1):
        c.rect(cx+side*7,cy+1,cx+side*9,cy+1,C_FUR_D)
        c.rect(cx+side*6,cy+3,cx+side*8,cy+3,C_FUR_D)
        c.rect(cx+side*6,cy+5,cx+side*7,cy+5,C_FUR_D)
    c.poly(points([(0,1),(2,3),(4,3),(6,5),(4,7),(1,8),(-1,8),(-4,7),(-6,5),(-4,3),(-2,3)]), C_WHITE)
    c.rect(cx,cy+3,cx+1,cy+3,C_NOSE)
    c.rect(cx,cy+4,cx,cy+5,C_OUTLINE)
    c.rect(cx-1,cy+6,cx-1,cy+6,C_OUTLINE)
    c.rect(cx+1,cy+6,cx+1,cy+6,C_OUTLINE)
    if expression=="sleep":
        for x in (-5,3):
            c.rect(cx+x,cy, cx+x+2,cy,C_PUPIL)
    elif expression=="scared":
        c.rect(cx,cy+6,cx+1,cy+7,C_PUPIL)


def curled_tail(c, f, y=26):
    tip=[0,-1,0,1][f]
    c.pline([(21,y+2),(26,y+2),(28,y),(28,y-3),(26,y-5+tip),(24,y-5+tip)],3,C_FUR_M)
    c.rect(25,y+1,26,y+3,C_FUR_D)
    c.rect(27,y-2,29,y-1,C_FUR_D)
    c.rect(23,y-6+tip,25,y-4+tip,C_WHITE)


def torso(c, y=23):
    c.ell(13,y,8,5,C_FUR_M)
    c.poly([(9,y-5),(17,y-5),(17,y-2),(15,y+1),(13,y+3),(11,y+1),(9,y-2)],C_WHITE)
    c.rect(6,y-1,8,y,C_FUR_D)
    c.rect(18,y+1,20,y+2,C_FUR_D)
    c.rect(6,y+2,7,y+3,C_FUR_D)


def paw(c,x,y):
    c.rect(x,y-3,x+3,y,C_FUR_M)
    c.rect(x,y-1,x+3,y,C_WHITE)
    c.rect(x+1,y,x+1,y,C_WSH)


def pose(f, state):
    c=Cv()
    bob=[0,-1,0,1][f]
    curled_tail(c,f,26)
    torso(c,23+(bob if state in ("idle","pet") else 0))
    if state=="walk":
        swing=[-1,0,1,0][f]
        paw(c,8+swing,28-(f==1)*2)
        paw(c,16-swing,28-(f==3)*2)
    elif state in ("drag","fall"):
        spread=[0,1,0,-1][f]
        paw(c,5-spread,25)
        paw(c,19+spread,25)
        c.rect(6-spread,24,7-spread,24,C_PAW)
        c.rect(20+spread,24,21+spread,24,C_PAW)
    elif state=="climb":
        paw(c,3,17+[0,2,4,2][f])
        paw(c,23,21-[0,2,4,2][f])
        paw(c,9,28-(f%2))
        paw(c,16,27+(f%2))
    elif state=="work":
        paw(c,7,28-(f%2)*3)
        paw(c,17,25+(f%2)*3)
    else:
        paw(c,8,28)
        paw(c,15,28)
    cat_head(c,"pet" if state=="pet" else "scared" if state in ("drag","fall") else "open")
    if state=="pet":
        c.rect(6,14,7,14,C_PAW)
        c.rect(19,14,20,14,C_PAW)
    return c.out()


def st_idle(f): return pose(f,"idle")
def st_walk(f): return pose(f,"walk")
def st_sit(f): return pose((f+1)%4,"sit")
def st_pet(f): return pose(f,"pet")
def st_drag(f): return pose(f,"drag")
def st_fall(f): return pose((f+1)%4,"fall")
def st_climb(f): return pose(f,"climb")
def st_work(f): return pose(f,"work")


def st_sleep(f):
    c=Cv()
    c.ell(18,24,10,5,C_FUR_M)
    c.rect(22,20,23,22,C_FUR_D)
    c.rect(26,23,28,24,C_FUR_D)
    cat_head(c,"sleep",12,20)
    c.pline([(23,28),(27,27),(28,24),(26,23)],3,C_FUR_M)
    c.rect(25,22,27,23,C_WHITE)
    return c.out()


def eyes_open():
    c=Cv()
    for x in (8,16):
        c.rect(x,10,x+2,12,C_PUPIL)
        c.rect(x+1,9,x+1,13,C_PUPIL)
        c.rect(x+1,10,x+1,10,C_HI)
    return c.im


def eyes_pet():
    c=Cv()
    for x in (8,16):
        c.rect(x,11,x+2,11,C_PUPIL)
        c.rect(x+1,10,x+1,10,C_PUPIL)
        c.rect(x,12,x,12,C_PUPIL)
        c.rect(x+2,12,x+2,12,C_PUPIL)
    return c.im


def eyes_blink():
    c=Cv()
    for x in (8,16):
        c.rect(x,11,x+2,11,C_PUPIL)
    return c.im


def eyes_half():
    c=Cv()
    for x in (8,16):
        c.rect(x,11,x+2,12,C_PUPIL)
    return c.im


def eyes_scared():
    c=Cv()
    for x in (8,16):
        c.rect(x,9,x+2,13,C_PUPIL)
        c.rect(x+1,9,x+1,10,C_HI)
    return c.im


def make_shadow():
    im = Image.new("RGBA", (32, 16), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.ellipse([3, 2, 28, 13], fill=(0, 0, 0, 70))
    return im


# ---------------- 托盘图标 ----------------
def tray_32():
    """猫头特写 32x32"""
    c = Cv()
    c.poly([(5, 9), (8, 2), (12, 8)], C_FUR_M)
    c.poly([(7, 8), (8, 4), (10, 8)], C_PAW)
    c.poly([(19, 8), (23, 2), (26, 9)], C_FUR_M)
    c.poly([(21, 8), (23, 4), (24, 8)], C_PAW)
    c.ell(16, 17, 10, 9, C_FUR_M)
    c.rect(13, 19, 13, 15, C_FUR_D)
    c.rect(16, 20, 16, 18, C_FUR_D)
    c.rect(19, 20, 19, 18, C_FUR_D)
    c.rect(22, 20, 22, 16, C_FUR_D)
    c.rect(11, 15, 15, 20, C_WHITE)
    c.rect(17, 15, 21, 20, C_WHITE)
    c.rect(13, 15, 15, 19, C_PUPIL)
    c.rect(11, 15, 11, 15, C_HI)
    c.rect(19, 15, 21, 19, C_PUPIL)
    c.rect(17, 15, 17, 15, C_HI)
    c.rect(15, 22, 17, 23, C_NOSE)
    c.rect(13, 23, 19, 25, C_WHITE)
    return c.out()


def tray_16():
    """16x16 简化版（必须手画，缩放会糊）"""
    c = Cv(16, 16)
    c.poly([(2, 6), (4, 1), (6, 5)], C_FUR_M)
    c.poly([(10, 5), (12, 1), (14, 6)], C_FUR_M)
    c.ell(8, 10, 6, 5, C_FUR_M)
    c.rect(5, 9, 6, 11, C_WHITE)
    c.rect(10, 9, 11, 11, C_WHITE)
    c.rect(5, 9, 6, 11, C_PUPIL)
    c.rect(9, 9, 10, 11, C_PUPIL)
    c.rect(7, 12, 8, 13, C_NOSE)
    return c.out()


# ---------------- 键鼠映射窗爪子（v0.1.29，BongoCat 爪子动画借鉴）----------------
def paw_frame(down=False):
    """橘猫爪俯视：橙色绒毛 + 粉色肉垫，14x14 自动描边后 ×4 输出 56px。
    down=True 时整体下压并轻压扁（拍键帧）。"""
    c = Cv(14, 14)
    dy = 2 if down else 0
    sq = 1 if down else 0
    # 三根脚趾（橙）
    c.ell(4, 4 + dy, 2, 2 - sq, C_FUR_M)
    c.ell(7, 3 + dy, 2, 2 - sq, C_FUR_M)
    c.ell(10, 4 + dy, 2, 2 - sq, C_FUR_M)
    # 掌（橙）
    c.ell(7, 8 + dy, 5, 4 - sq, C_FUR_M)
    # 趾尖粉色小肉垫
    c.ell(4, 4 + dy, 1, 1, C_PAW)
    c.ell(7, 3 + dy, 1, 1, C_PAW)
    c.ell(10, 4 + dy, 1, 1, C_PAW)
    # 掌心大肉垫（粉）
    c.ell(7, 9 + dy, 3, 2 - sq, C_PAW)
    return c.out()


def make_paw_assets():
    outdir = os.path.join(OUT, "mirror")
    os.makedirs(outdir, exist_ok=True)
    for name, down in (("paw_up", False), ("paw_down", True)):
        im = paw_frame(down).resize((56, 56), Image.NEAREST)
        im.save(os.path.join(outdir, f"{name}.png"))
        print(f"  mirror/{name}.png (56x56)")


# ---------------- 组装与输出 ----------------
def strip(frames):
    """4 帧横向拼接成 128x32"""
    im = Image.new("RGBA", (SIZE * NF, SIZE), (0, 0, 0, 0))
    for i, f in enumerate(frames):
        im.paste(f, (i * SIZE, 0))
    return im


STATES = [
    ("idle",  st_idle,  4,  "eyes_open"),   # v0.1.57：idle 2→4fps（4 帧呼吸循环更顺滑）
    ("walk",  st_walk,  8,  "eyes_open"),
    ("sit",   st_sit,   4,  "eyes_open"),   # v0.1.57：sit 2→4fps
    ("sleep", st_sleep, 0,  None),
    ("pet",   st_pet,   10, "eyes_pet"),
    ("drag",  st_drag,  6,  "eyes_scared"),
    ("fall",  st_fall,  12, "eyes_scared"),
    ("climb", st_climb, 4,  "eyes_open"),
    ("work",  st_work,  2,  "eyes_open"),   # v0.1.48：BongoCat 化——猫伏低前探+前爪伸向下方键盘
]


EYE_LAYERS = {
    "eyes_open": eyes_open,
    "eyes_pet": eyes_pet,
    "eyes_scared": eyes_scared,
    "eyes_blink": eyes_blink,
    "eyes_half": eyes_half,   # v0.1.57：眨眼中间帧（半闭）
}

# 每个状态对应的眼球层（None 表示不叠加，如 sleep 自带闭眼）
STATE_EYE = {
    "idle":  "eyes_open",
    "walk":  "eyes_open",
    "sit":   "eyes_open",
    "sleep": None,
    "pet":   "eyes_pet",
    "drag":  "eyes_scared",
    "fall":  "eyes_scared",
    "climb": "eyes_open",
    "work":  "eyes_open",   # v0.1.48：工作模式睁眼看键盘
}


def main():
    os.makedirs(OUT, exist_ok=True)
    os.makedirs(PREVIEW_OUT, exist_ok=True)
    sheets = {}

    for name, fn, fps, _ in STATES:
        frames = [fn(f) for f in range(NF)]
        sheet = strip(frames)
        sheet.save(os.path.join(OUT, f"neko_{name}.png"))
        sheets[name] = (frames, fps)
        print(f"  neko_{name}.png  ({sheet.size[0]}x{sheet.size[1]})")

    eye_imgs = {k: fn() for k, fn in EYE_LAYERS.items()}
    for k, im in eye_imgs.items():
        im.save(os.path.join(OUT, f"neko_{k}.png"))
    make_shadow().save(os.path.join(OUT, "neko_shadow.png"))
    print(f"  眼球分层 x{len(eye_imgs)} + 阴影")

    make_preview(sheets, eye_imgs)
    make_gif(sheets, eye_imgs)

    # v0.1.76：不再生成 anim.json（其 fps 覆盖表会静默盖掉 skin.json 的 fps，宿主侧已移除该路径）。
    # fps 的唯一权威是皮肤目录内的 skin.json（由作者维护，本脚本不覆盖它）。
    print(f"\n输出目录: {OUT}")


def make_preview(sheets, eye_imgs):
    """9 行 x 4 列，每格放大 3 倍，左侧标注状态名，已叠加对应眼球层"""
    S = 96
    PAD, LBL = 6, 86
    w = LBL + NF * S + (NF - 1) * PAD + 20
    h = len(STATES) * (S + PAD) + 20
    bg = Image.new("RGBA", (w, h), (0xE8, 0xE6, 0xE1, 255))
    d = ImageDraw.Draw(bg)
    for r, (name, _, fps, _) in enumerate(STATES):
        y = 10 + r * (S + PAD)
        d.text((10, y + S // 2 - 6), f"{name}", fill=(0x44, 0x44, 0x44, 255))
        d.text((10, y + S // 2 + 8), f"{fps}fps", fill=(0x88, 0x88, 0x88, 255))
        frames = sheets[name][0]
        eye_key = STATE_EYE[name]
        for i, f in enumerate(frames):
            composed = f.copy()
            if eye_key:
                composed.alpha_composite(eye_imgs[eye_key])
            x = LBL + i * (S + PAD)
            bg.alpha_composite(composed.resize((S, S), Image.NEAREST), (x, y))
    bg.save(os.path.join(PREVIEW_OUT, "preview.png"))
    print(f"  preview.png ({w}x{h})")


def make_gif(sheets, eye_imgs):
    """9 个状态依次播放，每状态循环 2 次，放大 3 倍，已叠加眼球层"""
    S = 96
    frames, durations = [], []
    for name, _, fps, _ in STATES:
        delay = max(int(1000 / fps), 40) if fps else 500
        imgs = sheets[name][0]
        eye_key = STATE_EYE[name]
        for _ in range(2):
            for im in imgs:
                bg = Image.new("RGBA", (S, S), (0xE8, 0xE6, 0xE1, 255))
                composed = im.copy()
                if eye_key:
                    composed.alpha_composite(eye_imgs[eye_key])
                bg.alpha_composite(composed.resize((S, S), Image.NEAREST), (0, 0))
                frames.append(bg.convert("RGB"))
                durations.append(delay)
    frames[0].save(os.path.join(PREVIEW_OUT, "preview.gif"), save_all=True,
                   append_images=frames[1:], duration=durations,
                   loop=0, disposal=2)
    print(f"  preview.gif ({len(frames)} 帧)")


if __name__ == "__main__":
    main()
