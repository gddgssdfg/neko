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

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "neko-assets"

CELL = 96
PAD = 6
PIXEL_THRESHOLD = 64      # 帧边长 ≤ 这个值视为像素风 → 用最近邻放大


def pick_idle_frame(skin_dir: Path, meta: dict) -> Image.Image | None:
    fw = int(meta.get("frameWidth", 32))
    fh = int(meta.get("frameHeight", 32))
    idle_file = "neko_idle_0.png" if meta.get("layout", "sheet").lower() == "files" else "neko_idle.png"
    candidates = [skin_dir / idle_file]
    # 没有 idle 的皮肤（理论上不该有）退用第一个非附属状态图
    candidates += [p for p in sorted(skin_dir.glob("neko_*.png"))
                   if not any(k in p.name for k in ("eyes", "shadow", "preview"))]
    for p in candidates:
        if p.is_file():
            with Image.open(p) as source:
                im = source.convert("RGBA")
            files_layout = meta.get("layout", "sheet").lower() == "files"
            if files_layout or (im.width >= fw and im.height >= fh):
                # File-layout sources are complete frames. The renderer also uses their full
                # bitmap, which may be higher resolution than the manifest's logical dimensions.
                frame = im if files_layout else im.crop((0, 0, fw, fh))
                state_name = p.stem.removeprefix("neko_").removesuffix("_0")
                state = next((s for s in meta.get("states", []) if s.get("name") == state_name), {})
                eye_key = state.get("eyeLayer")
                eye_file = skin_dir / f"neko_{eye_key}.png"
                if eye_key and eye_file.is_file():
                    with Image.open(eye_file) as source:
                        eyes = source.convert("RGBA")
                    if eyes.size == frame.size:
                        frame.alpha_composite(eyes)
                return frame
    return None


def build_preview(skin_dir: Path, meta: dict) -> Image.Image | None:
    frame = pick_idle_frame(skin_dir, meta)
    if frame is None:
        return None
    pixel_art = meta.get("pixelArt", max(frame.size) <= PIXEL_THRESHOLD)
    bounds = frame.getchannel("A").getbbox()
    if bounds is None:
        return None
    frame = frame.crop(bounds)
    resample = Image.NEAREST if pixel_art else Image.LANCZOS
    scale = (CELL - PAD * 2) / max(frame.size)
    inner = frame.resize((max(1, round(frame.width * scale)), max(1, round(frame.height * scale))), resample)
    canvas = Image.new("RGBA", (CELL, CELL), (0, 0, 0, 0))
    canvas.alpha_composite(inner, ((CELL - inner.width) // 2, CELL - PAD - inner.height))
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
