"""拼豆资产级断言器 —— 读烘焙产物的 PNG + 模型，跑六项断言（A1..A6）。

**纯标准库**：解码由 :mod:`bead_bake` 自己写出的 PNG（filter=0），不依赖第三方。
（通用 PNG 解码不在范围内 —— 本器只用于校验本工具链的产物。）

断言：
  A1 烘焙确定性     同输入两次烘焙 → 每个文件 SHA-256 逐字节一致
  A2 调色板合规     每张 sheet / 眼球层的**每个不透明像素** ∈ palette 声明的 hex 集合
  A3 硬边 alpha     身体帧 / 眼球层 / 缩略图的非透明像素 alpha == 255（无半透明抗锯齿）
  A4 眼睛同源对齐   瞳孔 bbox ⊆ 眼眶(闭合层) bbox（眼层配对自 skin.json.eyeLayers 推导；无眼层则跳过）
  A5 基线稳定       所有状态**第 0 帧**的内容底边 y 与中心 x 两两一致（切状态不跳）
  A6 帧数一致性     每个 ``neko_<state>.png`` 宽度 == ``skin.json.frameWidth × frames``

用法::

    python tools/bead/bead_verify.py --assets neko-assets/bead --model neko-assets/bead/model/cat.beads.json
    python tools/bead/bead_verify.py --assets ./out --model ./out.model.beads.json

路径默认**相对当前工作目录**（``assets`` 默认 ``neko-assets/bead``），调色板随脚本走。
"""

from __future__ import annotations

import argparse
import json
import sys
import tempfile
from pathlib import Path

try:
    from .bead_bake import DEFAULT_MODEL, DEFAULT_PALETTE, bake, read_png_rgba, sha256_file
    from .bead_model import load_palette
except ImportError:  # 直接运行脚本
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    from bead_bake import DEFAULT_MODEL, DEFAULT_PALETTE, bake, read_png_rgba, sha256_file  # type: ignore[no-redef]
    from bead_model import load_palette  # type: ignore[no-redef]

DEFAULT_ASSETS = Path("neko-assets") / "bead"
"""默认皮肤目录（相对当前工作目录）。"""

BODY_SHEET_GLOB = "neko_*.png"
EYE_PREFIX = "neko_eyes_"
SHADOW_NAME = "neko_shadow.png"
THUMB_NAME = "thumb.png"


# ---------------------------------------------------------------------------
# 小工具
# ---------------------------------------------------------------------------


def _opaque_bbox(width: int, height: int, rgba: bytes) -> tuple[int, int, int, int] | None:
    """alpha>0 的最小包围盒 (x0,y0,x1,y1)；全透明返回 None。"""
    x0, y0, x1, y1 = width, height, -1, -1
    for y in range(height):
        base = y * width * 4
        for x in range(width):
            if rgba[base + x * 4 + 3] > 0:
                if x < x0:
                    x0 = x
                if x > x1:
                    x1 = x
                if y < y0:
                    y0 = y
                if y > y1:
                    y1 = y
    if x1 < 0:
        return None
    return x0, y0, x1, y1


def _sheet_frames(width: int, height: int, rgba: bytes, frame_width: int) -> list[bytes]:
    """把横排 sheet 切成逐帧 RGBA 字节。"""
    if width % frame_width != 0:
        raise ValueError(f"sheet 宽 {width} 不是 frameWidth {frame_width} 的整数倍")
    n = width // frame_width
    frames: list[bytes] = []
    for i in range(n):
        frame = bytearray()
        for y in range(height):
            row = y * width * 4
            start = row + i * frame_width * 4
            frame += rgba[start:start + frame_width * 4]
        frames.append(bytes(frame))
    return frames


# ---------------------------------------------------------------------------
# 断言
# ---------------------------------------------------------------------------


def check_a1_determinism(model: Path, palette: Path) -> tuple[bool, str]:
    """A1：两次烘焙到两个临时目录 → 逐文件 SHA-256 一致。"""
    with tempfile.TemporaryDirectory() as d1, tempfile.TemporaryDirectory() as d2:
        bake(model, palette, d1)
        bake(model, palette, d2)
        f1 = sorted(p.name for p in Path(d1).iterdir())
        f2 = sorted(p.name for p in Path(d2).iterdir())
        if f1 != f2:
            return False, f"两次烘焙文件集合不同：{f1} vs {f2}"
        diffs = [n for n in f1 if sha256_file(Path(d1) / n) != sha256_file(Path(d2) / n)]
        if diffs:
            return False, f"{len(diffs)} 个文件不一致：{diffs}"
        return True, f"{len(f1)} 个文件 SHA-256 逐字节一致"


def check_a2_palette(assets: Path, palette_hexes: set[str]) -> tuple[bool, str]:
    """A2：sheet + 眼球层每个不透明像素 ∈ 调色板 hex 集合。"""
    bad: dict[str, int] = {}
    for p in sorted(assets.glob(BODY_SHEET_GLOB)):
        if p.name == SHADOW_NAME:
            continue
        w, h, data = read_png_rgba(p)
        cnt = 0
        for i in range(w * h):
            if data[i * 4 + 3] == 255:
                hx = "#%02x%02x%02x" % (data[i * 4], data[i * 4 + 1], data[i * 4 + 2])
                if hx not in palette_hexes:
                    cnt += 1
        if cnt:
            bad[p.name] = cnt
    if bad:
        return False, f"存在非调色板像素：{bad}"
    return True, "所有不透明像素均命中调色板 hex"


def check_a3_hard_edges(assets: Path) -> tuple[bool, str]:
    """A3：身体帧 / 眼球层 / 缩略图 alpha ∈ {0,255}（影子层按设计为 alpha=70，排除）。"""
    bad: dict[str, list[int]] = {}
    skip = {SHADOW_NAME}
    for p in sorted(assets.glob("*.png")):
        if p.name in skip:
            continue
        w, h, data = read_png_rgba(p)
        alphas = {data[i * 4 + 3] for i in range(w * h)}
        if not alphas <= {0, 255}:
            bad[p.name] = sorted(alphas)
    if bad:
        return False, f"存在半透明像素：{bad}"
    return True, "所有非背景像素 alpha ∈ {0,255}"


def eye_layer_pairs(skin: dict) -> list[tuple[str, str]]:
    """从 ``skin.json.eyeLayers`` 推导 ``(瞳孔层文件, 闭合层文件)`` 配对。

    只把「既声明了 ``X`` 又声明了 ``X_closed``」的层算作眼层（引擎也只在两者齐备时启用遮罩）。
    """
    layers = [str(x) for x in skin.get("eyeLayers", []) if isinstance(x, str)]
    pairs: list[tuple[str, str]] = []
    for name in layers:
        if name.endswith("_closed"):
            continue
        if name + "_closed" in layers:
            pairs.append((f"neko_{name}.png", f"neko_{name}_closed.png"))
    return pairs


def check_a4_eye_alignment(assets: Path, skin: dict) -> tuple[bool, str]:
    """A4：瞳孔 bbox ⊆ 眼眶(闭合层) bbox；两图层均在画幅内。无眼层则跳过。"""
    pairs = eye_layer_pairs(skin)
    if not pairs:
        return True, "皮肤未声明配对眼层（{X} + {X}_closed）→ 跳过（该皮肤眼睛烘在身体帧里）"
    msgs: list[str] = []
    for pupil_name, closed_name in pairs:
        pp, pc = assets / pupil_name, assets / closed_name
        if not pp.is_file() or not pc.is_file():
            return False, f"缺文件：{pupil_name} / {closed_name}"
        pw, ph, pdata = read_png_rgba(pp)
        cw, ch, cdata = read_png_rgba(pc)
        pb = _opaque_bbox(pw, ph, pdata)
        cb = _opaque_bbox(cw, ch, cdata)
        if pb is None or cb is None:
            return False, f"{pupil_name}/{closed_name} 存在空图层"
        px0, py0, px1, py1 = pb
        cx0, cy0, cx1, cy1 = cb
        if not (cx0 <= px0 and py0 >= cy0 and px1 <= cx1 and py1 <= cy1):
            return False, f"{pupil_name} 瞳孔 bbox {pb} 不在眼眶 bbox {cb} 内"
        msgs.append(f"{pupil_name}: 瞳{pb} ⊆ 眶{cb}")
    return True, "；".join(msgs)


def check_a5_baseline(assets: Path, skin: dict) -> tuple[bool, str]:
    """A5：所有状态**第 0 帧**的内容底边 y 与中心 x 一致。"""
    fw = int(skin["frameWidth"])
    bottoms: dict[str, int] = {}
    centers: dict[str, float] = {}
    for state in skin["states"]:
        p = assets / f"neko_{state['name']}.png"
        if not p.is_file():
            return False, f"缺 sheet：{p.name}"
        w, h, data = read_png_rgba(p)
        frame0 = _sheet_frames(w, h, data, fw)[0]
        bb = _opaque_bbox(fw, h, frame0)
        if bb is None:
            return False, f"{p.name} 第 0 帧全透明"
        x0, y0, x1, y1 = bb
        bottoms[state["name"]] = y1
        centers[state["name"]] = (x0 + x1) / 2.0
    if len(set(bottoms.values())) != 1:
        return False, f"底边 y 不一致：{bottoms}"
    cvals = sorted(set(centers.values()))
    if cvals[-1] - cvals[0] > 0.5:
        return False, f"中心 x 不一致：{centers}"
    return True, f"底边 y={next(iter(bottoms.values()))}，中心 x={cvals[0]}（{len(bottoms)} 态一致）"


def check_a6_frame_count(assets: Path, skin: dict) -> tuple[bool, str]:
    """A6：每个 sheet 宽度 == frameWidth × frames。"""
    fw = int(skin["frameWidth"])
    fn = int(skin["frames"])
    bad: dict[str, str] = {}
    for state in skin["states"]:
        p = assets / f"neko_{state['name']}.png"
        w, _h, _ = read_png_rgba(p)
        if w != fw * fn:
            bad[p.name] = f"{w} != {fw}×{fn}={fw * fn}"
    if bad:
        return False, f"帧数不一致：{bad}"
    return True, f"{len(skin['states'])} 张 sheet 宽度均 = {fw}×{fn} = {fw * fn}"


# ---------------------------------------------------------------------------
# 主流程
# ---------------------------------------------------------------------------


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="拼豆资产级断言器")
    ap.add_argument("--assets", default=str(DEFAULT_ASSETS), help="皮肤目录")
    ap.add_argument("--model", default=str(DEFAULT_MODEL), help="模型 JSON")
    ap.add_argument("--palette", default=str(DEFAULT_PALETTE), help="调色板 JSON")
    args = ap.parse_args(argv)

    assets = Path(args.assets)
    skin_path = assets / "skin.json"
    if not skin_path.is_file():
        print(f"[FAIL] 找不到 {skin_path}（先运行 bead_bake.py）")
        return 1
    skin = json.loads(skin_path.read_text(encoding="utf-8"))
    pal = load_palette(args.palette)

    checks = [
        ("A1 烘焙确定性", lambda: check_a1_determinism(Path(args.model), Path(args.palette))),
        ("A2 调色板合规", lambda: check_a2_palette(assets, pal.hexes())),
        ("A3 硬边 alpha", lambda: check_a3_hard_edges(assets)),
        ("A4 眼睛同源对齐", lambda: check_a4_eye_alignment(assets, skin)),
        ("A5 基线稳定", lambda: check_a5_baseline(assets, skin)),
        ("A6 帧数一致性", lambda: check_a6_frame_count(assets, skin)),
    ]

    all_ok = True
    print("=" * 74)
    print(" 拼豆资产级断言")
    print("=" * 74)
    for name, fn in checks:
        ok, detail = fn()
        all_ok &= ok
        print(f" [{'PASS' if ok else 'FAIL'}] {name:<16s} {detail}")
    print("=" * 74)
    print(" 结论：" + ("全部通过 ✅" if all_ok else "存在失败 ❌"))
    print("=" * 74)
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
