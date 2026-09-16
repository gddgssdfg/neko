r"""为每套皮肤生成设置页用的预览缩略图 `neko-assets/<id>/thumb.png`。

⚠️ 文件名必须是 thumb.png，不能叫 preview.png —— csproj 里有
   `<None Remove="..\..\neko-assets\**\*preview*" />`（排除早期临时预览图），
   叫 preview.png 会被这条规则一并排除 → 打不进输出目录，设置页缩略图全空。

什么时候要跑：
  新增/替换皮肤素材后跑一次。设置页的「外观风格」列表会读这张图显示缩略图
  （`SettingsWindow.BuildSkinPicker` → `LoadSkinPreview`）。缺图时那一行显示"无预览"，不影响选择。

做法：
  取该皮肤 `neko_idle.png` 的第 0 帧（没有 idle 就退用第一个状态），
  等比放进 96×96 透明画布、**底对齐**（各皮肤看起来基座一致）。
  像素风（帧 ≤64px）用 NEAREST 放大保硬边；高清皮用 LANCZOS。

用法：
    python tools/gen_skin_previews.py            # 全部皮肤
    python tools/gen_skin_previews.py cat_pixel  # 指定皮肤
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

from PIL import Image

ROOT = Path(r"G:\lantetn-li\neko")
ASSETS = ROOT / "neko-assets"

CELL = 96
PAD = 6
PIXEL_THRESHOLD = 64      # 帧边长 ≤ 这个值视为像素风 → 用最近邻放大


def pick_idle_frame(skin_dir: Path, meta: dict) -> Image.Image | None:
    fw = int(meta.get("frameWidth", 32))
    fh = int(meta.get("frameHeight", 32))
    candidates = [skin_dir / "neko_idle.png"]
    # 没有 idle 的皮肤（理论上不该有）退用第一个非附属状态图
    candidates += [p for p in sorted(skin_dir.glob("neko_*.png"))
                   if not any(k in p.name for k in ("eyes", "shadow", "preview"))]
    for p in candidates:
        if p.is_file():
            im = Image.open(p).convert("RGBA")
            if im.width >= fw and im.height >= fh:
                return im.crop((0, 0, fw, fh))
    return None


def build_preview(skin_dir: Path, meta: dict) -> Image.Image | None:
    frame = pick_idle_frame(skin_dir, meta)
    if frame is None:
        return None
    resample = Image.NEAREST if max(frame.size) <= PIXEL_THRESHOLD else Image.LANCZOS
    inner = frame.copy()
    inner.thumbnail((CELL - PAD * 2, CELL - PAD * 2), resample)
    canvas = Image.new("RGBA", (CELL, CELL), (0, 0, 0, 0))
    canvas.paste(inner, ((CELL - inner.width) // 2, CELL - PAD - inner.height), inner)
    return canvas


def main() -> int:
    only = sys.argv[1] if len(sys.argv) > 1 else None
    done, skipped = [], []
    for manifest in sorted(ASSETS.glob("*/skin.json")):
        skin_dir = manifest.parent
        if only and skin_dir.name != only:
            continue
        meta = json.loads(manifest.read_text(encoding="utf-8"))
        prev = build_preview(skin_dir, meta)
        if prev is None:
            skipped.append(skin_dir.name)
            continue
        prev.save(skin_dir / "thumb.png")
        done.append((skin_dir.name, meta.get("displayName", ""), meta.get("frameWidth", "?")))

    print(f"✅ 生成 {len(done)} 张预览图（{CELL}x{CELL}，底对齐）：")
    for sid, name, fw in done:
        print(f"   {sid:<14} {name:<12} 源帧宽 {fw}px")
    if skipped:
        print(f"⚠️ 跳过 {len(skipped)} 个（没有可用帧）：{', '.join(skipped)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
