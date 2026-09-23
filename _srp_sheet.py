"""把 SkinRenderProbe 修复前/后两张对照图竖排成一张 A/B 图（仓外取证用）。"""
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

BASE = Path("G:/lantetn-li/neko")
FONT = ImageFont.truetype("C:/Windows/Fonts/msyh.ttc", 16)


def stack(skin: str, out_name: str, caption: str):
    b = Image.open(BASE / "_srp_before" / f"_sheet_{skin}.png").convert("RGB")
    a = Image.open(BASE / "_srp_after" / f"_sheet_{skin}.png").convert("RGB")
    w = max(b.width, a.width)
    bar = 26
    head = 30
    sheet = Image.new("RGB", (w, head + (bar + b.height) + (bar + a.height) + 12), (255, 255, 255))
    d = ImageDraw.Draw(sheet)
    d.text((8, 6), caption, fill=(10, 10, 10), font=FONT)
    y = head
    d.rectangle([0, y, w, y + bar], fill=(245, 214, 214))
    d.text((8, y + 3), "修复前（flash 瞪眼相 != open → 眼睛空白）", fill=(150, 20, 20), font=FONT)
    y += bar
    sheet.paste(b, (0, y))
    y += b.height
    d.rectangle([0, y, w, y + bar], fill=(214, 240, 214))
    d.text((8, y + 3), "修复后（flash 瞪眼相 == open → 瞳孔保留）", fill=(20, 120, 40), font=FONT)
    y += bar
    sheet.paste(a, (0, y))
    sheet.save(BASE / out_name)
    print("[✓]", BASE / out_name)


if __name__ == "__main__":
    stack("cat_hd", "_srp_ab_cat_hd.png",
          "cat_hd  —  SkinRenderProbe 逐相渲染（行=idle/sit，列=open/half/closed/flash）")
    stack("pixel", "_srp_ab_pixel.png",
          "pixel  —  SkinRenderProbe 逐相渲染（行=idle/sit，列=open/half/closed/flash）")
