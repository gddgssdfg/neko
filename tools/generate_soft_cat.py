#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Neko 柔光版动画表生成器（v0.1.37，皮肤切换素材）
=================================================
像素版 generate_sprites.py 的柔光孪生：同样的 8 状态 × 4 帧 + 4 种眼球层，
但改用 4x 超采样 + LANCZOS 抗锯齿 + 径向渐变填充 + GaussianBlur 柔边。

输出：neko-assets/soft/neko_<state>_<f>.png（128×128/帧，透明背景，共 32 帧）
参考静态图：neko-assets/reference/neko_soft.png（保留）

用法：
  python tools/generate_soft_cat.py
"""

import os
from PIL import Image, ImageDraw, ImageFilter

FRAME = 128        # 输出帧尺寸
SS = 4             # 超采样倍率
N = FRAME * SS     # 绘制画布 512×512
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_REF = os.path.join(ROOT, "neko-assets", "reference")
OUT_SOFT = os.path.join(ROOT, "neko-assets", "soft")

# 调色（与像素版一致的色相，渐变层次）
C_OUT = (0x3A, 0x2A, 0x20, 255)
C_FUR = (0xEF, 0xA0, 0x55, 255)     # 主色
C_FUR_D = (0xC9, 0x7A, 0x38, 255)   # 深橘（渐变暗部）
C_FUR_L = (0xF7, 0xB8, 0x72, 255)   # 浅橘（渐变亮部）
C_WHITE = (0xFF, 0xF8, 0xF0, 255)
C_PAW = (0xF2, 0xA0, 0xA0, 255)
C_PUPIL = (0x2A, 0x1E, 0x18, 255)
C_HI = (0xFF, 0xFF, 0xFF, 255)
C_NOSE = (0xE8, 0x8B, 0x8B, 255)


def radial_gradient(cx, cy, rx, ry, c0, c1, r=0.9):
    """径向渐变椭圆块（柔边）"""
    w, h = int(rx * 2 + 2), int(ry * 2 + 2)
    block = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    px = block.load()
    for y in range(h):
        for x in range(w):
            dx = (x - rx) / max(rx, 1)
            dy = (y - ry) / max(ry, 1)
            d = (dx * dx + dy * dy) ** 0.5
            if d > 1:
                continue
            t = min(d / r, 1.0)
            col = tuple(int(c0[i] + (c1[i] - c0[i]) * t) for i in range(3))
            a = 255
            if d > 1 - 2.0 / max(rx, ry):
                a = int(255 * (1 - d) * max(rx, ry) / 2)
                a = max(0, min(255, a))
            px[x, y] = (col[0], col[1], col[2], a)
    return block, w, h


def soft_body(dst, cx, cy, rx, ry, c0, c1):
    block, w, h = radial_gradient(cx, cy, rx, ry, c0, c1)
    dst.alpha_composite(block, (int(cx - rx), int(cy - ry)))


def soft_ear(dst, pts, inner_pts):
    """柔边耳朵：多边形 + GaussianBlur"""
    ear = Image.new("RGBA", (N, N), (0, 0, 0, 0))
    de = ImageDraw.Draw(ear, "RGBA")
    de.polygon(pts, fill=C_FUR)
    de.polygon(inner_pts, fill=C_PAW)
    ear = ear.filter(ImageFilter.GaussianBlur(2))
    dst.alpha_composite(ear)


def draw_eyes(d, hcx, hcy, mode):
    """眼球模式：open / pet(眯) / closed / scared"""
    if mode == "closed":
        # 闭眼：两端上翘的柔和弧线
        d.line([(hcx - 84, hcy - 8), (hcx - 26, hcy - 14)], fill=C_OUT, width=10)
        d.line([(hcx + 22, hcy - 14), (hcx + 80, hcy - 8)], fill=C_OUT, width=10)
    elif mode == "pet":
        # 眯眼：细缝
        d.line([(hcx - 80, hcy - 6), (hcx - 24, hcy - 10)], fill=C_PUPIL, width=10)
        d.line([(hcx + 22, hcy - 10), (hcx + 78, hcy - 6)], fill=C_PUPIL, width=10)
    elif mode == "scared":
        # 惊恐：小眼白 + 大瞳孔 + 高光
        for ex in (hcx - 80, hcx + 22):
            d.ellipse([ex, hcy - 30, ex + 56, hcy + 26], fill=C_WHITE)
            d.ellipse([ex + 6, hcy - 18, ex + 50, hcy + 30], fill=C_PUPIL)
            d.ellipse([ex + 10, hcy - 12, ex + 22, hcy + 2], fill=C_HI)
    else:
        # 睁眼：眼白 + 瞳孔 + 高光
        for ex in (hcx - 80, hcx + 22):
            d.ellipse([ex, hcy - 35, ex + 58, hcy + 25], fill=C_WHITE)
            d.ellipse([ex + 10, hcy - 12, ex + 48, hcy + 30], fill=C_PUPIL)
            d.ellipse([ex + 14, hcy - 8, ex + 24, hcy + 2], fill=C_HI)


def draw_cat(d, state, frame, out_img):
    """参数化画一只柔光橘猫（512 坐标系）。state 状态名，frame 0-3 帧索引"""
    # ---- 状态参数（与像素版 generate_sprites.py 对齐）----
    bob = [0, 0, -1, 0][frame]          # idle 呼吸（像素单位 × 24）
    if state == "idle":
        dy_body = bob * 24
        tail_pts = [(150, 372 + dy_body), (110, 330 + dy_body), (95, 260 + dy_body), (115, 200)]
        eye = "open"
        swing = 0
    elif state == "walk":
        swing = [-3, -1, 3, 1][frame] * 24
        dy_body = [-1, 0, 1, 0][frame] * 24
        tail_pts = [(150, 372 + dy_body), (110, 330 + dy_body), (95, 260 + dy_body), (115, 200)]
        eye = "open"
    elif state == "sit":
        dy_body = 0
        swing = 0
        tail_pts = [(365, 430), (400, 400), (395, 350)]
        eye = "open"
    elif state == "sleep":
        dy_body = 40
        swing = 0
        tail_pts = [(380, 440), (440, 420), (430, 340), (370, 320)]
        eye = "closed"
    elif state == "pet":
        sq = [0, 1, 1, 0][frame]
        dy_body = sq * 20
        swing = 0
        tail_pts = [(150, 372 + dy_body), (110, 330 + dy_body), (95, 260 + dy_body), (115, 200)]
        eye = "pet"
    elif state == "drag":
        dy_body = 0
        swing = [-2, 0, 2, 0][frame] * 24
        tail_pts = [(150, 380), (90, 360), (55, 320), (40, 275)]   # 炸毛直尾
        eye = "scared"
    elif state == "fall":
        dy_body = 0
        swing = [0, 2, 4, 2][frame] * 24
        tail_pts = [(150, 380), (100, 345), (90, 290), (110, 245)]  # 尾巴上飘
        eye = "scared"
    else:  # climb
        dy_body = -[0, 1, 0, -1][frame] * 24
        swing = 0
        tail_pts = [(150, 372), (110, 340), (100, 290), (115, 240)]
        eye = "open"

    # v0.1.48：work 状态（BongoCat 化——猫伏低前探+前爪伸向画面底沿=指向下方键盘）
    if state == "work":
        bob = [0, 0, -1, 0][frame] * 12   # 慢呼吸（仅一半幅度）
        dy_body = 8 + bob
        tail_pts = [(140, 380 + dy_body), (100, 360 + dy_body), (80, 320 + dy_body), (90, 280)]
        eye = "open"
        swing = 0

    hcx, hcy = 272, 176 + dy_body
    bcy = 352 + dy_body

    # ---- 尾巴（先画）----
    d.line(tail_pts, fill=C_FUR, width=44)
    d.line(tail_pts, fill=C_FUR_D, width=18)

    # ---- 身体 ----
    if state == "climb":
        # 前爪上抓（竖直）
        soft_body(out_img, 200, 120 + dy_body, 45, 55, C_FUR_L, C_FUR_D)
        soft_body(out_img, 345, 120 + dy_body, 45, 55, C_FUR_L, C_FUR_D)
        soft_body(out_img, 265, 300 + dy_body, 105, 85, C_FUR_L, C_FUR_D)
    elif state == "sit":
        soft_body(out_img, 265, 340 + dy_body, 120, 90, C_FUR_L, C_FUR_D)
        soft_body(out_img, 150, 430 + dy_body, 55, 38, C_FUR_L, C_FUR_D)
        soft_body(out_img, 380, 430 + dy_body, 55, 38, C_FUR_L, C_FUR_D)
    elif state == "work":
        # v0.1.48：BongoCat 化——身体蹲低（横向略宽）+ 前爪伸到画面底沿（y=480+ = 伸向"桌面外"的下方键盘）
        soft_body(out_img, 265, 366 + dy_body, 155, 90, C_FUR_L, C_FUR_D)
        soft_body(out_img, 200, 484 + dy_body, 68, 46, C_FUR_L, C_FUR_D)
        soft_body(out_img, 330, 484 + dy_body, 68, 46, C_FUR_L, C_FUR_D)
    else:
        soft_body(out_img, 150 + swing, 400 + dy_body, 62, 40, C_FUR_L, C_FUR_D)   # 左前腿
        soft_body(out_img, 380 - swing, 400 + dy_body, 62, 40, C_FUR_L, C_FUR_D)   # 右前腿
        soft_body(out_img, 265, 360 + dy_body, 130, 80, C_FUR_L, C_FUR_D)          # 身体主体

    # ---- 耳朵（先于头）----
    soft_ear(out_img,
             [(hcx - 115, hcy - 40), (hcx - 75, hcy - 130), (hcx - 25, hcy - 55)],
             [(hcx - 98, hcy - 45), (hcx - 78, hcy - 110), (hcx - 40, hcy - 60)])
    soft_ear(out_img,
             [(hcx + 30, hcy - 55), (hcx + 80, hcy - 130), (hcx + 120, hcy - 40)],
             [(hcx + 45, hcy - 60), (hcx + 80, hcy - 110), (hcx + 100, hcy - 45)])

    # ---- 头（渐变）----
    soft_body(out_img, hcx, hcy, 115, 95, C_FUR_L, C_FUR_D)
    # 额头虎斑
    d.line([(hcx - 60, hcy - 85), (hcx - 48, hcy - 45)], fill=C_FUR_D, width=14)
    d.line([(hcx - 20, hcy - 90), (hcx - 18, hcy - 55)], fill=C_FUR_D, width=12)
    d.line([(hcx + 22, hcy - 90), (hcx + 20, hcy - 55)], fill=C_FUR_D, width=12)
    d.line([(hcx + 62, hcy - 85), (hcx + 50, hcy - 45)], fill=C_FUR_D, width=14)

    # ---- 脸 ----
    draw_eyes(d, hcx, hcy, eye)
    d.ellipse([hcx - 16, hcy + 42, hcx + 16, hcy + 66], fill=C_NOSE)
    d.line([(hcx, hcy + 66), (hcx, hcy + 78)], fill=C_OUT, width=6)
    d.arc([hcx - 34, hcy + 60, hcx, hcy + 92], 180, 300, fill=C_OUT, width=6)
    d.arc([hcx, hcy + 60, hcx + 34, hcy + 92], 240, 360, fill=C_OUT, width=6)

    # ---- 前爪（白袜 + 粉垫）----
    if state != "climb":
        for px_ in (150 + swing, 380 - swing):
            if state == "work": continue   # v0.1.48：work 已自画前爪伸到画面底沿
            soft_body(out_img, px_, 448 + dy_body, 55, 38, C_FUR_L, C_FUR_D)
            d.ellipse([px_ - 34, 470 + dy_body, px_ + 34, 500 + dy_body], fill=C_WHITE)
            for tx in (-22, 0, 22):
                d.ellipse([px_ + tx - 9, 462 + dy_body, px_ + tx + 9, 478 + dy_body], fill=C_PAW)


STATES = ["idle", "walk", "sit", "sleep", "pet", "drag", "fall", "climb", "work"]   # v0.1.48：work=BongoCat 化伏低前爪前伸


def make_animation_table():
    os.makedirs(OUT_SOFT, exist_ok=True)
    for state in STATES:
        for f in range(4):
            img = Image.new("RGBA", (N, N), (0, 0, 0, 0))
            d = ImageDraw.Draw(img, "RGBA")
            draw_cat(d, state, f, img)
            frame = img.resize((FRAME, FRAME), Image.LANCZOS)
            frame.save(os.path.join(OUT_SOFT, f"neko_{state}_{f}.png"))
        print(f"  soft/neko_{state}_0..3.png ({FRAME}x{FRAME}/帧)")


def make_reference():
    os.makedirs(OUT_REF, exist_ok=True)
    img = Image.new("RGBA", (N, N), (0, 0, 0, 0))
    d = ImageDraw.Draw(img, "RGBA")
    draw_cat(d, "idle", 0, img)
    out = img.resize((512, 512), Image.LANCZOS)
    final = Image.new("RGBA", (512, 512), (0, 0, 0, 0))
    shadow = Image.new("RGBA", (512, 512), (0, 0, 0, 0))
    ds = ImageDraw.Draw(shadow, "RGBA")
    ds.ellipse([60, 430, 452, 500], fill=(0, 0, 0, 90))
    shadow = shadow.filter(ImageFilter.GaussianBlur(18))
    final.alpha_composite(shadow)
    final.alpha_composite(out)
    final.save(os.path.join(OUT_REF, "neko_soft.png"))
    print(f"  reference/neko_soft.png (512x512 参考图)")


def main():
    make_animation_table()
    make_reference()


if __name__ == "__main__":
    main()
