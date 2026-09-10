#!/usr/bin/env python3
"""v0.1.50：Neko 柔光皮肤重做 —— BongoCat 简笔画风格（用户：旧的柔光"太丑"，要像 bongocat 简笔画）。
特点：大圆头(占 60%) + 黑豆大眼(白高光) + 粗黑描边(先画黑大一圈再画彩小一圈) + 平涂色(无渐变无柔化)
     + 粉色腮红 + 小三角耳(粉内耳) + 简单嘴线。身体小、头大 = BongoCat 的萌感。
输出：neko-assets/soft/neko_<state>_<f>.png（128×128 独立帧，SpriteAtlas.LoadSoft 直接读）。
状态：idle/walk/sit/sleep/pet/drag/fall/climb/work（work=前爪伸向画面底沿=BongoCat 工作）。
用法：python generate_bongocat.py
"""
import os
from PIL import Image, ImageDraw

N = 512          # 画布（4x 超采样再缩小到 FRAME，边缘平滑但仍是"简笔画"）
FRAME = 128
OUT_SOFT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "neko-assets", "soft")

# ---- 简笔画配色（平涂，无渐变）----
C_OUT = (52, 48, 44, 255)       # 深棕黑描边
C_FUR = (255, 163, 77, 255)     # 橘
C_FUR_L = (255, 200, 130, 255)  # 浅橘（肚皮/嘴周/头顶高光）
C_FUR_D = (222, 128, 52, 255)   # 深橘（额头虎斑）v0.1.53
C_WHITE = (255, 255, 255, 255)
C_PINK = (255, 178, 194, 255)   # 腮红 / 内耳 / 肉垫
C_BLACK = (40, 36, 34, 255)     # 眼睛

STATES = ["idle", "walk", "sit", "sleep", "pet", "drag", "fall", "climb", "work"]


def ellipse(d, cx, cy, rx, ry, fill, outline=C_OUT, sw=6):
    """先画黑描边大一圈，再画彩色小一圈 = 均匀粗描边"""
    d.ellipse([cx - rx - sw, cy - ry - sw, cx + rx + sw, cy + ry + sw], fill=outline)
    d.ellipse([cx - rx, cy - ry, cx + rx, cy + ry], fill=fill)


def ear(d, cx, cy, dx, flop=0):
    """小三角耳（黑描边 + 橘 + 粉内耳）；flop=1 时耷拉"""
    pts = [(cx + dx, cy), (cx + dx + 58, cy - 95), (cx + dx + 110, cy - 25)]
    pts2 = [(cx + dx + 16, cy - 12), (cx + dx + 55, cy - 72), (cx + dx + 88, cy - 28)]
    d.polygon(pts, fill=C_OUT)
    d.polygon(pts2, fill=C_FUR)
    d.polygon([(p[0] + 10, p[1] + 10) for p in pts2[:2]] + [pts2[2]], fill=C_PINK)


def paw(d, cx, cy, r=30, toe=True):
    """简笔画圆爪：黑描边 + 橘 + 粉肉垫"""
    ellipse(d, cx, cy, r, r * 0.8, C_FUR)
    if toe:
        d.ellipse([cx - 14, cy - 6, cx + 14, cy + 18], fill=C_PINK)


def eye(d, cx, cy, r, blink=0, scared=0):
    """黑豆大眼：白高光点；blink=1 眯缝；scared=1 缩圆"""
    rr = r if not scared else r * 0.6
    if blink:
        d.arc([cx - rr, cy - 4, cx + rr, cy + 12], 180, 360, fill=C_BLACK, width=10)
    else:
        d.ellipse([cx - rr, cy - rr, cx + rr, cy + rr], fill=C_BLACK)
        d.ellipse([cx - rr + 10, cy - rr + 10, cx - rr + 22, cy - rr + 22], fill=C_WHITE)


def blush(d, cx, cy, r=16):
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=C_PINK)


def draw_cat(d, state, frame):
    """简笔画 BongoCat 风猫（512 坐标系）。state 状态名，frame 0-3"""
    # ---- 基础动画参数（呼吸/摆动/眼神模式）----
    bob = [0, 0, -10, 0][frame]          # idle 呼吸
    sq = [0, 1, 1, 0][frame]             # pet 挤压
    if state == "walk":
        swing = [-20, -5, 20, 5][frame]
    elif state == "drag":
        swing = [-24, 0, 24, 0][frame]
    elif state == "fall":
        swing = [0, 20, 40, 20][frame]
    else:
        swing = 0

    if state == "climb":
        dy = -[0, 16, 0, -16][frame]
        eye_mode = 0
    elif state == "sleep":
        dy = 20
        eye_mode = 1
    elif state == "pet":
        dy = sq * 14
        eye_mode = 2            # 眯眼
    elif state == "drag":
        dy = 0
        eye_mode = 3            # 吓
    elif state == "fall":
        dy = 60 + frame * 8    # v0.1.50 修：fall 身体下压+掉
        eye_mode = 3            # 吓
    else:
        dy = bob
        eye_mode = 0

    # ---- 头/身体位置（按状态调）----
    if state == "climb":
        # v0.1.50 修：climb 身体整条贴墙（身体+头都偏下到画布下半，头在身体上端）——不再头与身体分离
        hcy = 180 + dy        # 头偏下到画布中下
        bcy = 310 + dy        # 身体中下（与头紧贴）
    elif state == "fall":
        # 下落：头偏下偏后，身体下压
        hcy = 230 + dy
        bcy = 440 + dy
    else:
        hcy = 216 + dy
        bcy = 430 + dy

    # ---- 尾巴（身体后，粗线）----
    if state == "fall":
        # v0.1.50 修：下落尾巴大角度上飘（像飘起来）
        tail_pts = [(400, bcy + 10), (440, bcy - 80), (460, bcy - 160), (430, bcy - 200)]
    elif state == "climb":
        # 挂墙：尾巴向下垂
        tail_pts = [(420, bcy + 30), (440, bcy + 90), (430, bcy + 140), (400, bcy + 160)]
    else:
        tail_pts = [(420, bcy - 10), (470, bcy - 40), (480, bcy - 80), (455, bcy - 105)]
    d.line(tail_pts, fill=C_OUT, width=26)
    d.line(tail_pts, fill=C_FUR, width=12)
    d.line(tail_pts[1:], fill=C_OUT, width=26, joint="curve")
    d.line(tail_pts[1:], fill=C_FUR, width=12, joint="curve")

    # ---- 身体（按状态）----
    if state == "climb":
        # v0.1.50 修：竖直挂墙（窄长椭圆 rx 50 ry 130）
        ellipse(d, 256 + swing, bcy, 50, 130, C_FUR)
        # 前爪朝上抓墙（身体上方两侧）
        paw(d, 232, bcy - 110, 30)
        paw(d, 280, bcy - 110, 30)
        # 后爪朝下（身体下方）
        paw(d, 232, bcy + 110, 30)
        paw(d, 280, bcy + 110, 30)
    elif state == "fall":
        # v0.1.50 修：下落——身体后倾+四仰八叉（猫在掉）
        ellipse(d, 256 + 20, bcy, 96, 64, C_FUR)   # 身体后倾
        # 四爪张开（表示惊慌）
        paw(d, 256 + 20 - 70, bcy - 30, 28)
        paw(d, 256 + 20 + 70, bcy - 30, 28)
        paw(d, 256 + 20 - 50, bcy + 50, 28)
        paw(d, 256 + 20 + 50, bcy + 50, 28)
    elif state == "work":
        # v0.1.50：猫蹲低（身体下压 30）+ 前爪伸到画面底沿（BongoCat 工作）
        ellipse(d, 256, bcy + 24, 102, 70, C_FUR)
        paw(d, 200, 508, 36)
        paw(d, 312, 508, 36)
    else:
        ellipse(d, 256 + swing, bcy, 96, 74, C_FUR)
        paw(d, 200, 480 + dy, 34)
        paw(d, 312, 480 + dy, 34)

    # ---- 耳朵（头之前）----
    ear(d, 256, hcy - 120, -105)
    ear(d, 256, hcy - 120, 105)

    # ---- 头（大圆，占 60%）----
    ellipse(d, 256, hcy, 128, 116, C_FUR)
    # 脸周浅色
    ellipse(d, 256, hcy + 18, 96, 70, C_FUR_L)
    # v0.1.53：额头虎斑（简笔画三竖线，学 AI 像素版 M 纹）
    d.line([(232, hcy - 88), (232, hcy - 58)], fill=C_FUR_D, width=12)
    d.line([(256, hcy - 96), (256, hcy - 66)], fill=C_FUR_D, width=10)
    d.line([(280, hcy - 88), (280, hcy - 58)], fill=C_FUR_D, width=12)
    # v0.1.53：头顶高光点（两浅色弧，圆润感）
    d.line([(210, hcy - 92), (238, hcy - 102)], fill=C_FUR_L, width=10)
    d.line([(274, hcy - 102), (302, hcy - 92)], fill=C_FUR_L, width=10)

    # ---- 眼睛（黑豆大眼 + 高光）----
    eye(d, 256 - 52, hcy - 8, 26, blink=1 if eye_mode == 2 else 0, scared=1 if eye_mode == 3 else 0)
    eye(d, 256 + 52, hcy - 8, 26, blink=1 if eye_mode == 2 else 0, scared=1 if eye_mode == 3 else 0)

    # ---- 腮红 ----
    blush(d, 256 - 95, hcy + 22)
    blush(d, 256 + 95, hcy + 22)

    # ---- 嘴（w 弧线）+ 鼻 ----
    d.ellipse([256 - 9, hcy + 34, 256 + 9, hcy + 50], fill=C_PINK)
    d.line([(256 - 30, hcy + 60), (256, hcy + 72), (256 + 30, hcy + 60)], fill=C_OUT, width=8, joint="curve")

    # ---- 胡须 ----
    d.line([(150, hcy + 10), (85, hcy)], fill=C_OUT, width=6)
    d.line([(150, hcy + 26), (85, hcy + 28)], fill=C_OUT, width=6)
    d.line([(362, hcy + 10), (427, hcy)], fill=C_OUT, width=6)
    d.line([(362, hcy + 26), (427, hcy + 28)], fill=C_OUT, width=6)


def main():
    os.makedirs(OUT_SOFT, exist_ok=True)
    for state in STATES:
        for f in range(4):
            img = Image.new("RGBA", (N, N), (0, 0, 0, 0))
            d = ImageDraw.Draw(img, "RGBA")
            draw_cat(d, state, f)
            frame = img.resize((FRAME, FRAME), Image.LANCZOS)
            frame.save(os.path.join(OUT_SOFT, f"neko_{state}_{f}.png"))
        print(f"  soft/neko_{state}_0..3.png ({FRAME}x{FRAME}/帧)")


if __name__ == "__main__":
    main()
