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
  - 头部中心统一固定在 (17, 11)，保证眼球层零偏移对齐
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
- 本脚本只生成 PNG 素材（neko_<state>.png 横排 4 帧 / 眼球层 / shadow / tray.ico），
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
OUT = os.path.join(ROOT, "neko-assets", "pixel")   # v0.1.74：像素皮肤迁入 neko-assets/pixel/（与 skin.json 同目录）

# 头部锚点：所有需要叠加眼球层的状态都必须用这个坐标
HEAD_CX, HEAD_CY = 17, 11

# ---------------- 调色板（严格 12 色）----------------
C_OUTLINE = (0x3A, 0x2A, 0x20, 255)
C_FUR_L   = (0xF7, 0xB8, 0x72, 255)
C_FUR_M   = (0xEF, 0xA0, 0x55, 255)
C_FUR_D   = (0xC9, 0x7A, 0x38, 255)
C_FUR_DP  = (0xA8, 0x5F, 0x26, 255)
C_WHITE   = (0xFF, 0xF8, 0xF0, 255)
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
def ears(c, hcx, hcy, twitch=0):
    # 左耳
    c.poly([(hcx - 7, hcy - 4), (hcx - 5, hcy - 9 - twitch), (hcx - 2, hcy - 5)], C_FUR_M)
    c.poly([(hcx - 6, hcy - 5), (hcx - 5, hcy - 8 - twitch), (hcx - 3, hcy - 5)], C_PAW)
    # 右耳
    c.poly([(hcx + 2, hcy - 5), (hcx + 5, hcy - 9 + twitch), (hcx + 7, hcy - 4)], C_FUR_M)
    c.poly([(hcx + 3, hcy - 5), (hcx + 5, hcy - 8 + twitch), (hcx + 6, hcy - 5)], C_PAW)


def head(c, hcx, hcy, rx=7, ry=6):
    c.ell(hcx, hcy, rx, ry, C_FUR_M)
    # 额头虎斑 M 纹：中间短两边长
    c.rect(hcx - 3, hcy - 5, hcx - 3, hcy - 3, C_FUR_D)
    c.rect(hcx - 1, hcy - 5, hcx - 1, hcy - 4, C_FUR_D)
    c.rect(hcx + 1, hcy - 5, hcx + 1, hcy - 4, C_FUR_D)
    c.rect(hcx + 3, hcy - 5, hcx + 3, hcy - 3, C_FUR_D)
    # v0.1.53 AI 造型学：头顶顶光晕（FUR_L 提亮两点，圆润立体感）
    c.rect(hcx - 4, hcy - 6, hcx - 3, hcy - 6, C_FUR_L)
    c.rect(hcx + 1, hcy - 7, hcx + 2, hcy - 7, C_FUR_L)


def _blush(c, hcx, hcy):
    """v0.1.53 AI 造型学：脸颊腮红（两侧 2x1 粉点，像素可爱风）"""
    c.rect(hcx - 7, hcy + 2, hcx - 6, hcy + 3, C_PAW)
    c.rect(hcx + 6, hcy + 2, hcx + 7, hcy + 3, C_PAW)


def face_open(c, hcx, hcy):
    """睁眼：只画眼白，瞳孔由眼球层叠加。v0.1.53：+腮红（AI 造型学）"""
    c.rect(hcx - 5, hcy - 2, hcx - 1, hcy + 3, C_WHITE)
    c.rect(hcx + 1, hcy - 2, hcx + 5, hcy + 3, C_WHITE)
    c.rect(hcx - 1, hcy + 4, hcx + 1, hcy + 5, C_NOSE)
    c.rect(hcx - 3, hcy + 5, hcx + 3, hcy + 6, C_WHITE)
    _blush(c, hcx, hcy)


def face_closed(c, hcx, hcy):
    """闭眼（sleep）：两端上翘的横线"""
    c.rect(hcx - 5, hcy + 2, hcx - 1, hcy + 2, C_OUTLINE)
    c.rect(hcx - 5, hcy + 1, hcx - 5, hcy + 1, C_OUTLINE)
    c.rect(hcx + 1, hcy + 2, hcx + 5, hcy + 2, C_OUTLINE)
    c.rect(hcx + 5, hcy + 1, hcx + 5, hcy + 1, C_OUTLINE)
    c.rect(hcx - 1, hcy + 4, hcx + 1, hcy + 5, C_NOSE)
    c.rect(hcx - 3, hcy + 5, hcx + 3, hcy + 6, C_WHITE)


def face_pet(c, hcx, hcy):
    """被抚摸：不画眼白（眼球层画弯月），加腮红"""
    c.rect(hcx - 1, hcy + 4, hcx + 1, hcy + 5, C_NOSE)
    c.rect(hcx - 3, hcy + 5, hcx + 3, hcy + 6, C_WHITE)
    c.rect(hcx - 7, hcy + 3, hcx - 6, hcy + 4, C_PAW)
    c.rect(hcx + 6, hcy + 3, hcx + 7, hcy + 4, C_PAW)
    _blush(c, hcx, hcy)


def face_scared(c, hcx, hcy):
    """惊恐：眼白 + 张嘴"""
    c.rect(hcx - 5, hcy - 2, hcx - 1, hcy + 3, C_WHITE)
    c.rect(hcx + 1, hcy - 2, hcx + 5, hcy + 3, C_WHITE)
    c.rect(hcx - 1, hcy + 4, hcx + 1, hcy + 5, C_NOSE)
    c.rect(hcx - 2, hcy + 6, hcx + 2, hcy + 8, C_TONGUE)


def body(c, bcx, bcy, rx, ry):
    c.ell(bcx, bcy, rx, ry, C_FUR_M)
    # 肚子白
    c.ell(bcx + 1, bcy + 2, max(rx - 3, 1), max(ry - 2, 1), C_WHITE)
    # 侧身条纹（v0.1.53：左右对称，AI 虎斑感更强）
    for i in range(2):
        x = bcx - rx + 2 + i * 3
        c.rect(x, bcy - 2, x, bcy + 1, C_FUR_D)
        xr = bcx + rx - 1 - i * 3
        c.rect(xr, bcy - 2, xr, bcy + 1, C_FUR_D)


def leg(c, x, y0, y1, w=3):
    c.rect(x, y0, x + w - 1, y1, C_FUR_M)
    c.rect(x, y1 - 1, x + w - 1, y1, C_WHITE)


def tail(c, pts, w=3, stripe=True):
    c.pline(pts, w, C_FUR_M)
    if stripe and len(pts) >= 3:
        i = len(pts) // 2
        c.pline(pts[i:i + 2], w, C_FUR_D)


# ---------------- 8 个状态 ----------------
def st_idle(f):
    c = Cv()
    # v0.1.57 两级呼吸：帧序 0:-1 / 1:-2 / 2:-1 / 3:0 —— 幅度 2px、有起有伏（比原 ±1px 更明显）
    # 只移身体/腿/尾（头部锚点 HEAD_CX/HEAD_CY 固定），eyes_open 眼球层与头严格对齐不受影响
    bob = [-1, -2, -1, 0][f]
    tip = [(8, 15), (7, 14), (8, 15), (9, 16)][f]
    bcy = 22 + bob
    tail(c, [(9, bcy + 2), (5, bcy - 1), (3, bcy - 5), tip])
    leg(c, 10, 24 + bob, 31)
    body(c, 15, bcy, 8, 5)
    leg(c, 19, 24 + bob, 31)
    ears(c, HEAD_CX, HEAD_CY)
    head(c, HEAD_CX, HEAD_CY)
    face_open(c, HEAD_CX, HEAD_CY)
    return c.out()


def st_walk(f):
    c = Cv()
    swing = [-3, -1, 3, 1][f]
    bob = [-1, 0, 1, 0][f]
    tip = [(8, 14), (7, 12), (8, 14), (9, 16)][f]
    bcy = 22 + bob
    tail(c, [(9, bcy + 2), (5, bcy - 1), (3, bcy - 5), tip])
    leg(c, 10 + swing, 24 + bob, 31)
    body(c, 15, bcy, 8, 5)
    leg(c, 19 - swing, 24 + bob, 31)
    ears(c, HEAD_CX, HEAD_CY)
    head(c, HEAD_CX, HEAD_CY)
    face_open(c, HEAD_CX, HEAD_CY)
    return c.out()


def st_sit(f):
    c = Cv()
    # v0.1.57 呼吸：坐姿身体轻起伏（帧 1/2 上浮 1px，底缘仍被盘坐后腿压住，不露缝）
    bob = [0, -1, -1, 0][f]
    tip = [(25, 25), (26, 24), (25, 25), (24, 26)][f]
    tail(c, [(22, 26), (25, 25), tip])
    body(c, 15, 22 + bob, 7, 6)            # 身体竖起
    leg(c, 12, 25 + bob, 30)
    leg(c, 18, 25 + bob, 30)
    c.ell(14, 30 + bob, 6, 2, C_WHITE)     # 盘坐的后腿（更明显）
    c.ell(13, 31 + bob, 2, 1, C_PAW)       # 露出的肉垫
    ears(c, HEAD_CX, HEAD_CY)
    head(c, HEAD_CX, HEAD_CY)
    face_open(c, HEAD_CX, HEAD_CY)
    return c.out()


# v0.1.48：work 状态（BongoCat 化——猫伏低前探+前爪伸向下方键盘=画面底沿）
def st_work(f):
    c = Cv()
    bcy = 22 - [0, 0, 1, 0][f]                  # 慢呼吸（f=2 时下压 1px）
    # 尾巴自然垂
    tail(c, [(23, 25), (26, 27), (28, 30), (29, 31)])
    # 身体蹲低（更宽更扁）
    body(c, 15, bcy, 9, 5)
    # 前爪：圆形掌+肉垫（不画 leg——leg 太直像"站姿的两条腿"）—— 伸到画面底沿（y=31）
    c.ell(12, 30, 3, 2, C_FUR_M)                 # 左前掌（橙）
    c.ell(18, 30, 3, 2, C_FUR_M)                 # 右前掌（橙）
    c.ell(12, 30, 1, 1, C_PAW)                   # 左肉垫
    c.ell(18, 30, 1, 1, C_PAW)                   # 右肉垫
    ears(c, HEAD_CX, HEAD_CY)
    head(c, HEAD_CX, HEAD_CY)
    face_open(c, HEAD_CX, HEAD_CY)
    return c.out()


def st_sleep(f):
    c = Cv()
    br = [0, 1, 1, 0][f]
    tail(c, [(24, 27), (28, 25), (27, 20), (23, 19)])
    body(c, 16, 24, 9, 5)
    ears(c, 12, 21)
    head(c, 12, 21, 6, 5)
    face_closed(c, 12, 21)
    del br
    return c.out()


def st_pet(f):
    c = Cv()
    sq = [0, 1, 1, 0][f]
    tip = [(8, 13), (6, 11), (8, 13), (10, 15)][f]
    bcy = 22 + sq
    tail(c, [(9, bcy + 2), (5, bcy - 1), (3, bcy - 5), tip])
    leg(c, 10, 24 + sq, 31)
    body(c, 15, bcy, 8 + sq, 5 - sq)       # squash 挤压
    leg(c, 19, 24 + sq, 31)
    ears(c, HEAD_CX, HEAD_CY)
    head(c, HEAD_CX, HEAD_CY)
    face_pet(c, HEAD_CX, HEAD_CY)
    return c.out()


def st_drag(f):
    c = Cv()
    w = [-2, 0, 2, 0][f]
    c.pline([(9, 24), (5, 22), (2, 20), (1, 17)], 4, C_FUR_M)   # 尾巴炸毛
    c.pline([(3, 21), (1, 19)], 2, C_FUR_D)
    leg(c, 7 + w, 25, 30)
    body(c, 15, 22, 8, 5)
    leg(c, 21 - w, 25, 30)
    ears(c, HEAD_CX, HEAD_CY)
    head(c, HEAD_CX, HEAD_CY)
    face_scared(c, HEAD_CX, HEAD_CY)
    return c.out()


def st_fall(f):
    c = Cv()
    w = [0, 2, 4, 2][f]
    c.pline([(7, 22), (3, 17), (4, 11), (8, 7)], 3, C_FUR_M)   # v0.1.51：尾巴大幅上飘（像飘起来）
    c.rect(6, 24, 10 + w, 27, C_FUR_M)
    c.rect(6, 24, 7, 27, C_WHITE)
    body(c, 15, 23, 7, 4)                                 # 身体更小（缩+4 帧 0-3 跳）
    c.rect(20 - w, 24, 25, 27, C_FUR_M)
    c.rect(24, 24, 25, 27, C_WHITE)
    ears(c, HEAD_CX, HEAD_CY)
    head(c, HEAD_CX, HEAD_CY, 5, 4)                        # 头小一圈（缩+惊）
    face_scared(c, HEAD_CX, HEAD_CY)
    return c.out()


def st_climb(f):
    c = Cv()
    d = [0, 1, 0, -1][f]
    c.rect(14, 2, 16, 6 + d, C_FUR_M)      # 前爪抓上边缘
    c.rect(14, 2, 16, 3, C_WHITE)
    c.rect(19, 2, 21, 6 - d, C_FUR_M)
    c.rect(19, 2, 21, 3, C_WHITE)
    body(c, 16, 21, 6, 6)                   # 身体下垂
    c.pline([(16, 27), (16, 29), (14 + d, 31)], 3, C_FUR_M)
    ears(c, HEAD_CX, HEAD_CY)
    head(c, HEAD_CX, HEAD_CY)
    face_open(c, HEAD_CX, HEAD_CY)
    return c.out()


# ---------------- 眼球层（独立，坐标基于 HEAD_CX/CY）----------------
def eyes_open():
    c = Cv()
    for x0 in (13, 19):
        c.rect(x0, 9, x0 + 2, 12, C_PUPIL)
        c.rect(x0, 9, x0, 9, C_HI)
    return c.im


def eyes_pet():
    """眯眼弯月 ∩"""
    c = Cv()
    for x0 in (12, 18):
        c.rect(x0 + 1, 10, x0 + 3, 10, C_PUPIL)
        c.rect(x0, 11, x0, 11, C_PUPIL)
        c.rect(x0 + 4, 11, x0 + 4, 11, C_PUPIL)
    return c.im


def eyes_blink():
    c = Cv()
    for x0 in (12, 18):
        c.rect(x0, 11, x0 + 4, 11, C_PUPIL)
    return c.im


def eyes_half():
    """半闭（v0.1.57 眨眼中间帧）：眼睑下压盖住眼白上 2px（y9-10），瞳孔只露下半 2px（y11-12）。
    坐标与 eyes_open/eyes_blink 一致，基于 HEAD_CX=17/HEAD_CY=11 的眼球锚区。"""
    c = Cv()
    for x0 in (12, 18):
        # 眼睑：毛色盖住眼白上半 → 视觉上眼皮压下来
        c.rect(x0, 9, x0 + 4, 10, C_FUR_M)
        # 眼睑下缘：半闭细缝上沿的阴影线
        c.rect(x0, 10, x0 + 4, 10, C_OUTLINE)
    for x0 in (13, 19):
        # 露出的下半瞳孔 + 高光
        c.rect(x0, 11, x0 + 2, 12, C_PUPIL)
        c.rect(x0, 11, x0, 11, C_HI)
    return c.im


def eyes_scared():
    c = Cv()
    for x0 in (12, 18):
        c.rect(x0 + 1, 8, x0 + 3, 13, C_PUPIL)
        c.rect(x0 + 1, 8, x0 + 1, 8, C_HI)
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
    ("sleep", st_sleep, 1,  None),
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

    t32 = tray_32()
    t32.save(os.path.join(OUT, "tray.ico"),
             sizes=[(16, 16), (32, 32), (48, 48), (256, 256)])
    print("  tray.ico (16/32/48/256)")

    make_paw_assets()
    make_preview(sheets, eye_imgs)
    make_gif(sheets, eye_imgs)

    # v0.1.76：不再生成 anim.json（其 fps 覆盖表会静默盖掉 skin.json 的 fps，宿主侧已移除该路径）。
    # fps 的唯一权威是皮肤目录内的 skin.json（由作者维护，本脚本不覆盖它）。
    print(f"\n输出目录: {OUT}")


def make_preview(sheets, eye_imgs):
    """8 行 x 4 列，每格放大 3 倍，左侧标注状态名，已叠加对应眼球层"""
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
    bg.save(os.path.join(OUT, "preview.png"))
    print(f"  preview.png ({w}x{h})")


def make_gif(sheets, eye_imgs):
    """8 个状态依次播放，每状态循环 2 次，放大 3 倍，已叠加眼球层"""
    S = 96
    frames, durations = [], []
    for name, _, fps, _ in STATES:
        delay = max(int(1000 / fps), 40)
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
    frames[0].save(os.path.join(OUT, "preview.gif"), save_all=True,
                   append_images=frames[1:], duration=durations,
                   loop=0, disposal=2)
    print(f"  preview.gif ({len(frames)} 帧)")


if __name__ == "__main__":
    main()
