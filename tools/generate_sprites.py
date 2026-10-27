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
  - 基础头部中心 (13, 11)，逐帧 eyeOffsets 同时驱动头部与眼球层
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
import json
from PIL import Image, ImageDraw, ImageFilter, ImageChops

SIZE = 32
NF = 4
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PREVIEW_OUT = os.path.join(ROOT, "_eval", "pixel-b01-actions")
OUT = os.path.join(ROOT, "neko-assets", "pixel")   # v0.1.74：像素皮肤迁入 neko-assets/pixel/（与 skin.json 同目录）

# 基础眼层锚点。动作的逐帧偏移从 skin.json 读取，身体头部与眼层共用。
with open(os.path.join(OUT, "skin.json"), encoding="utf-8") as manifest_file:
    META = json.load(manifest_file)
STATE_META = {state["name"]: state for state in META["states"]}
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


def head_at(c, state, f, expression="open"):
    offsets = STATE_META[state].get("eyeOffsets", [])
    anchor = offsets[f] if offsets else {"x":0,"y":0}
    cat_head(c, expression, HEAD_CX+anchor["x"], HEAD_CY+anchor["y"])


def limb(c, start, end, far=False):
    # Stop the square brush above the toe baseline so its cap cannot cross the frame margin.
    c.pline([start,(end[0],end[1]-1)],3,C_FUR_D if far else C_FUR_M)
    x,y=end
    c.rect(x-1,y-1,x+2,y,C_WSH if far else C_WHITE)
    c.rect(x,y,x,y,C_FUR_D if far else C_WSH)


def raised_tail(c, f):
    tip=[0,1,0,-1][f]
    c.pline([(22,24),(26,22),(28,18),(27,15+tip),(25,14+tip)],3,C_FUR_M)
    c.rect(26,19,28,20,C_FUR_D)
    c.rect(24,13+tip,26,15+tip,C_WHITE)


def st_idle(f):
    c=Cv()
    curled_tail(c,f)
    torso(c,23+[0,0,1,0][f])
    paw(c,8,28)
    paw(c,15,28)
    head_at(c,"idle",f)
    return c.out()


def st_walk(f):
    """Four-beat gait: rear pair and front pair alternate support and lift."""
    c=Cv()
    raised_tail(c,f)
    stride=[-2,0,2,0][f]
    limb(c,(13,23),(13-stride,27+(f==1)),far=True)
    limb(c,(22,23),(22+stride,27+(f==3)),far=True)
    c.ell(17,22+(f%2),10,4,C_FUR_M)
    c.ell(17,24+(f%2),6,2,C_WHITE)
    c.rect(23,21+(f%2),25,22+(f%2),C_FUR_D)
    limb(c,(9,23),(9+stride,[29,27,27,29][f]))
    limb(c,(23,23),(23-stride,[27,29,29,27][f]))
    head_at(c,"walk",f)
    return c.out()


def st_sit(f):
    c=Cv()
    curled_tail(c,(f+1)%4)
    c.ell(13,25,9,3,C_FUR_M)
    torso(c,24)
    paw(c,8,28)
    paw(c,15,28)
    head_at(c,"sit",f)
    return c.out()


def st_pet(f):
    """Nuzzle left/right and knead with alternating front mittens."""
    c=Cv()
    curled_tail(c,(f*2)%4)
    torso(c,24 if f==1 else 23)
    head_at(c,"pet",f,"pet")
    limb(c,(9,23),(8,28-(f%2)*2))
    limb(c,(17,23),(17,26+(f%2)*2))
    anchor=STATE_META["pet"]["eyeOffsets"][f]
    for x in (6,19):
        c.rect(x+anchor["x"],14+anchor["y"],x+1+anchor["x"],14+anchor["y"],C_PAW)
    return c.out()


def st_drag(f):
    """Suspended body: narrow torso, dangling paws and a loose lowered tail."""
    c=Cv()
    sway=[0,-1,0,1][f]
    c.pline([(20,23),(25+sway,25),(28,27),(26+sway,28)],3,C_FUR_M)
    c.rect(26,26,28,27,C_FUR_D)
    c.ell(13+sway,23,6,4,C_FUR_M)
    c.ell(13+sway,23,3,4,C_WHITE)
    limb(c,(9+sway,22),(7+sway,27+(f%2)))
    limb(c,(17+sway,22),(20+sway,28-(f%2)))
    limb(c,(11+sway,25),(10+sway,29))
    limb(c,(16+sway,25),(17+sway,29))
    head_at(c,"drag",f,"scared")
    return c.out()


def st_fall(f):
    """Open paws brace separately from the suspended drag silhouette."""
    c=Cv()
    raised_tail(c,(f+2)%4)
    c.ell(14,22,7,4,C_FUR_M)
    c.ell(14,23,4,3,C_WHITE)
    spread=[0,1,0,-1][f]
    limb(c,(9,21),(5-spread,23))
    limb(c,(18,21),(23+spread,23))
    limb(c,(10,25),(8-spread,28))
    limb(c,(17,25),(19+spread,28))
    head_at(c,"fall",f,"scared")
    c.rect(4-spread,22,5-spread,22,C_PAW)
    c.rect(22+spread,22,23+spread,22,C_PAW)
    return c.out()


def st_climb(f):
    """Alternating reaching forepaws and pushing back paws."""
    c=Cv()
    c.pline([(20,25),(26,26),(28,23),(27,21)],3,C_FUR_M)
    c.rect(26,22,28,23,C_FUR_D)
    torso(c,23)
    limb(c,(10,24),(8,[29,27,26,28][f]))
    limb(c,(18,24),(21,[26,28,29,27][f]))
    head_at(c,"climb",f)
    limb(c,(6,20),(3,[10,13,17,13][f]))
    limb(c,(21,20),(25,[17,13,10,13][f]))
    return c.out()


def st_work(f):
    """Lower chest and alternating downward taps, with distinct release frames."""
    c=Cv()
    curled_tail(c,f)
    c.ell(14,24,9,4,C_FUR_M)
    c.ell(14,24,4,3,C_WHITE)
    head_at(c,"work",f)
    limb(c,(8,22),(7,[29,27,26,28][f]))
    limb(c,(18,22),(19,[26,28,29,27][f]))
    return c.out()


def st_land(f):
    """Contact -> compress -> rebound -> settled; played once by the renderer."""
    if f==3:
        return st_idle(0)  # Exact resting pose avoids a pop when the one-shot finishes.
    c=Cv()
    curled_tail(c,[1,0,2,0][f])
    y=[25,24,22,23][f]
    c.ell(13,y,[10,9,7,8][f],[3,4,5,5][f],C_FUR_M)
    c.ell(13,y,4,3,C_WHITE)
    paw(c,[7,8,9,8][f],28)
    paw(c,[17,16,15,15][f],28)
    head_at(c,"land",f)
    return c.out()


def st_sleep(f):
    c=Cv()
    c.ell(18,25,10,4,C_FUR_M)
    c.rect(21,22,22,23,C_FUR_D)
    c.rect(25,24,27,25,C_FUR_D)
    cat_head(c,"sleep",12,20)
    c.pline([(22,28),(27,27),(28,24),(26,23)],3,C_FUR_M)
    c.rect(25,22,27,23,C_WHITE)
    return c.out()


def resting_frame(state, frame=0):
    """Flatten a canonical endpoint, including its exact registered eye anchor."""
    return composed_frame(STATE_FUNCTIONS[state](frame), {k: fn() for k, fn in EYE_LAYERS.items()}, state, frame)


def transition_pose(head_y, body_y, body_rx, body_ry, eyes="half", tail=0, work=False):
    c = Cv()
    curled_tail(c, tail)
    c.ell(14, body_y, body_rx, body_ry, C_FUR_M)
    c.ell(14, body_y, 4, max(2, body_ry-1), C_WHITE)
    paw(c, 7 if work else 8, 28)
    paw(c, 19 if work else 15, 28)
    cat_head(c, "open", HEAD_CX, head_y)
    layer = {"half": eyes_half, "open": eyes_open, "pet": eyes_pet, "closed": eyes_blink}[eyes]()
    c.im.alpha_composite(layer, (0, head_y-HEAD_CY))
    return c.out()


def st_sleep_enter(f):
    # Feet remain planted while the torso folds and the eyes close.
    if f == 0: return resting_frame("sit")
    if f == 3: return st_sleep(0)
    return transition_pose([12, 14, 17, 20][f], 25, 9, 3, "half" if f == 1 else "closed", tail=f)


def st_wake_idle(f):
    if f == 0: return st_sleep(0)
    if f == 3: return resting_frame("idle")
    return transition_pose([20, 17, 14, 11][f], 25 if f == 1 else 24, 9 if f == 1 else 8, 3 if f == 1 else 4,
                           "half" if f == 1 else "open", tail=0)


def st_wake_work(f):
    if f == 0: return st_sleep(0)
    if f == 3: return resting_frame("work")
    return transition_pose([20, 17, 14, 12][f], 25 if f == 1 else 24, 9, 3 if f == 1 else 4,
                           "half" if f == 1 else "open", tail=0, work=True)


def st_pet_end_idle(f):
    if f == 0: return resting_frame("pet", 3)
    if f == 3: return resting_frame("idle")
    return transition_pose(11, 24 if f == 1 else 23, 8, 5, "pet" if f == 1 else "half", tail=0)


def st_pet_end_work(f):
    if f == 0: return resting_frame("pet", 3)
    if f == 3: return resting_frame("work")
    return transition_pose(11 if f == 1 else 12, 24, 9, 4, "pet" if f == 1 else "half", tail=0, work=True)


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


STATE_FUNCTIONS = {
    "idle": st_idle, "walk": st_walk, "sit": st_sit, "sleep": st_sleep,
    "pet": st_pet, "drag": st_drag, "fall": st_fall, "climb": st_climb,
    "work": st_work, "land": st_land,
    "sleep_enter": st_sleep_enter, "wake_idle": st_wake_idle, "wake_work": st_wake_work,
    "pet_end_idle": st_pet_end_idle, "pet_end_work": st_pet_end_work,
}
STATES = [(state["name"], STATE_FUNCTIONS[state["name"]], state["fps"], state.get("eyeLayer"))
          for state in META["states"]]


EYE_LAYERS = {
    "eyes_open": eyes_open,
    "eyes_pet": eyes_pet,
    "eyes_scared": eyes_scared,
    "eyes_blink": eyes_blink,
    "eyes_half": eyes_half,   # v0.1.57：眨眼中间帧（半闭）
}

# 每个状态对应的眼球层（None 表示不叠加，如 sleep 自带闭眼）
STATE_EYE = {state["name"]: state.get("eyeLayer") for state in META["states"]}


def composed_frame(body_frame, eye_imgs, name, index):
    composed=body_frame.copy()
    eye_key=STATE_EYE[name]
    if eye_key:
        offsets=STATE_META[name].get("eyeOffsets", [])
        anchor=offsets[index] if offsets else {"x":0,"y":0}
        composed.alpha_composite(eye_imgs[eye_key],(anchor["x"],anchor["y"]))
    return composed



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
    """状态数行 x 4 列，每格放大 3 倍，左侧标注状态名，已叠加对应眼球层"""
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
            composed = composed_frame(f, eye_imgs, name, i)
            x = LBL + i * (S + PAD)
            bg.alpha_composite(composed.resize((S, S), Image.NEAREST), (x, y))
    bg.save(os.path.join(PREVIEW_OUT, "preview.png"))
    print(f"  preview.png ({w}x{h})")


def make_gif(sheets, eye_imgs):
    """所有状态依次播放，每状态循环 2 次，放大 3 倍，已叠加眼球层"""
    S = 96
    frames, durations = [], []
    for name, _, fps, _ in STATES:
        delay = max(int(1000 / fps), 40) if fps else 500
        imgs = sheets[name][0]
        eye_key = STATE_EYE[name]
        for _ in range(2 if name in {"idle", "walk", "sit", "sleep", "pet", "drag", "fall", "climb", "work"} else 1):
            for index, im in enumerate(imgs):
                bg = Image.new("RGBA", (S, S), (0xE8, 0xE6, 0xE1, 255))
                composed = composed_frame(im, eye_imgs, name, index)
                bg.alpha_composite(composed.resize((S, S), Image.NEAREST), (0, 0))
                frames.append(bg.convert("RGB"))
                durations.append(delay)
    frames[0].save(os.path.join(PREVIEW_OUT, "preview.gif"), save_all=True,
                   append_images=frames[1:], duration=durations,
                   loop=0, disposal=2)
    print(f"  preview.gif ({len(frames)} 帧)")


if __name__ == "__main__":
    main()
