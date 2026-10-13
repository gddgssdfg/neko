"""拼豆烘焙器 —— ``neko.beads/1`` 模型 → 可被引擎直接加载的精灵素材。

输入：``<model>.beads.json``（模型）+ ``palette.json``（内置调色板）。
输出（写入 ``--out`` 目录）：::

    neko_<state>.png              # 每状态一张横排帧 sheet（frames × frameWidth 宽）
    neko_eyes_<st>.png            # 眼球（瞳孔）层：仅含瞳孔珠，其余全透明（方案 B）
    neko_eyes_<st>_closed.png     # 闭合层：眼眶遮罩 + 闭眼外观
    neko_shadow.png               # 影子层（hasShadow=true 时）
    thumb.png                     # 缩略图（32 珠 × 3 = 96px，整数倍）
    skin.json                     # 清单（布局/尺寸/帧数/眼层/状态）

**零第三方依赖**：PNG 由本文件内置的 writer 手写（``zlib`` + ``struct``，filter=0），
故同一模型两次烘焙**逐字节一致**（A1）。全部整数、无浮点采样、无随机、无重采样。

用法（路径默认相对**当前工作目录**）::

    python bead_bake.py --model my.beads.json --out ./assets/skins/hero
    python bead_bake.py                                     # 用默认 neko-assets/bead 布局
"""

from __future__ import annotations

import argparse
import hashlib
import json
import struct
import sys
import zlib
from pathlib import Path

try:  # 包内导入（python -m tools.bead.bead_bake）
    from .bead_model import Model, Palette, load_model, load_palette, validate_model
except ImportError:  # 直接运行脚本（python tools/bead/bead_bake.py）
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    from bead_model import Model, Palette, load_model, load_palette, validate_model  # type: ignore[no-redef]

# ---------------------------------------------------------------------------
# 约定
# ---------------------------------------------------------------------------

PUPIL_KEYS = frozenset({"e", "hi"})
"""被抽到独立眼球层的调色板 key（瞳孔 + 高光）。仅对声明了 ``eyeLayer`` 的状态生效。"""

FUR_KEY = "f"
"""闭眼层铺底用的毛色 key（= ``palette.get('f')``）。"""

LINE_KEY = "k"
"""闭眼睑线 key（= ``palette.get('k')`` 描边色）。"""

EYE_SOCKET_MARGIN = 1
"""眼眶（闭合层）在瞳孔 bbox 外扩的珠粒数 —— 给眼神位移留安全边。"""

DEFAULT_MODEL = Path("neko-assets") / "bead" / "model" / "cat.beads.json"
"""默认模型路径 —— **相对当前工作目录**（历史上 Neko 的布局）。

⚠️ 不再相对脚本位置向上找仓库根：本工具链要能脱离任何特定项目使用。
新项目请显式传 ``--model path/to/x.beads.json``（或把模型放在 ``neko-assets/bead/model/`` 下）。
"""

DEFAULT_PALETTE = Path(__file__).resolve().parent / "palette.json"
"""调色板随脚本走（工具自带资产）；可用 ``--palette`` 覆盖。"""

DEFAULT_OUT = Path("neko-assets") / "bead"
"""默认输出目录 —— **相对当前工作目录**；新项目请显式传 ``--out``。"""

THUMB_SCALE = 3
"""缩略图烘焙倍率：32 珠 × 3 = 96px（整数倍 → 每珠等宽，无 moiré）。"""


# ---------------------------------------------------------------------------
# PNG（纯标准库；filter=0，8-bit RGBA）
# ---------------------------------------------------------------------------


def _chunk(tag: bytes, data: bytes) -> bytes:
    return (
        struct.pack(">I", len(data))
        + tag
        + data
        + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
    )


def write_png(path: str | Path, width: int, height: int, rgba: bytes) -> None:
    """把 row-major 的 RGBA 字节写为 PNG（确定性：固定 filter=0 + 固定压缩级别）。"""
    expected = width * height * 4
    if len(rgba) != expected:
        raise ValueError(f"rgba 长度 {len(rgba)} != {width}×{height}×4 = {expected}")
    stride = width * 4
    raw = bytearray((stride + 1) * height)
    for y in range(height):
        raw[y * (stride + 1)] = 0  # filter 类型 0（None）
        src = y * stride
        dst = y * (stride + 1) + 1
        raw[dst:dst + stride] = rgba[src:src + stride]
    blob = (
        b"\x89PNG\r\n\x1a\n"
        + _chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
        + _chunk(b"IDAT", zlib.compress(bytes(raw), 9))
        + _chunk(b"IEND", b"")
    )
    Path(path).write_bytes(blob)


def read_png_rgba(path: str | Path) -> tuple[int, int, bytes]:
    """读取由 :func:`write_png` 产出的 PNG（filter=0）。

    仅供资产级断言器解码**本烘焙器自己写出**的文件；不做通用 PNG 解码。
    """
    data = Path(path).read_bytes()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError(f"不是 PNG: {path}")
    pos = 8
    width = height = 0
    idat = bytearray()
    while pos < len(data):
        (length,) = struct.unpack(">I", data[pos:pos + 4])
        tag = data[pos + 4:pos + 8]
        body = data[pos + 8:pos + 8 + length]
        pos += 12 + length
        if tag == b"IHDR":
            width, height, depth, ctype, _, _, _ = struct.unpack(">IIBBBBB", body)
            if depth != 8 or ctype != 6:
                raise ValueError(f"仅支持 8-bit RGBA，实际 depth={depth} ctype={ctype}")
        elif tag == b"IDAT":
            idat += body
        elif tag == b"IEND":
            break
    raw = zlib.decompress(bytes(idat))
    stride = width * 4
    out = bytearray(width * height * 4)
    for y in range(height):
        base = y * (stride + 1)
        if raw[base] != 0:
            raise ValueError(f"仅支持 filter=0，行 {y} filter={raw[base]}")
        out[y * stride:(y + 1) * stride] = raw[base + 1:base + 1 + stride]
    return width, height, bytes(out)


def sha256_file(path: str | Path) -> str:
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


# ---------------------------------------------------------------------------
# 栅格化
# ---------------------------------------------------------------------------


def rasterize_grid(
    model: Model,
    frame,
    *,
    only_keys: frozenset[str] | None = None,
    skip_keys: frozenset[str] | None = None,
) -> list[list[str | None]]:
    """按 z 序把各部件贴进 ``rows×cols`` 网格；返回 ``grid[y][x]`` = 调色板 key 或 None。

    只看字符 → key 映射，不接触调色板颜色，故无需 ``palette``（颜色在 :func:`grid_to_rgba` 才解析）。

    - ``only_keys``：只贴这些 key 的珠粒（用于抽独立眼球层）。
    - ``skip_keys``：**跳过**这些 key 的珠粒 —— 关键是「不占位」，让**下层部件**透出来。
      身体帧抽走瞳孔时**必须用 skip**而非事后 drop：瞳孔珠与眼白珠同格，若先贴后删会留下
      **透明空洞**（眼白被覆盖后又删掉瞳孔 → 破洞）。skip 让 ``head`` 的眼白珠保留 → 眼睛不"白洞"。
    """
    grid: list[list[str | None]] = [[None] * model.cols for _ in range(model.rows)]
    for part in sorted(model.parts, key=lambda p: p.z):
        shape = frame.shape_of(part.id)
        offset = frame.offset(part.id)
        overrides = frame.overrides_of(part.id)
        for x, y, key in part.iter_beads(shape, offset, overrides):
            if only_keys is not None and key not in only_keys:
                continue
            if skip_keys is not None and key in skip_keys:
                continue
            if 0 <= x < model.cols and 0 <= y < model.rows:
                grid[y][x] = key
    return grid


def grid_to_rgba(
    grid: list[list[str | None]],
    palette: Palette,
    scale: int,
) -> tuple[int, int, bytes]:
    """珠粒网格 → 硬边 RGBA 位图（每珠 ``scale×scale`` 纯色块，alpha ∈ {0,255}）。"""
    rows = len(grid)
    cols = len(grid[0]) if rows else 0
    width, height = cols * scale, rows * scale
    buf = bytearray(width * height * 4)
    for y in range(rows):
        row = grid[y]
        for x in range(cols):
            key = row[x]
            if key is None:
                continue
            r, g, b, a = palette.get(key).rgba()
            if a == 0:
                continue
            for dy in range(scale):
                base = ((y * scale + dy) * width + x * scale) * 4
                for dx in range(scale):
                    o = base + dx * 4
                    buf[o] = r
                    buf[o + 1] = g
                    buf[o + 2] = b
                    buf[o + 3] = 255
    return width, height, bytes(buf)


def hconcat(frames: list[tuple[int, int, bytes]]) -> tuple[int, int, bytes]:
    """把等高的帧位图横向拼成 sheet。"""
    if not frames:
        raise ValueError("没有帧可拼接")
    w, h, _ = frames[0]
    for fw, fh, _ in frames:
        if (fw, fh) != (w, h):
            raise ValueError(f"帧尺寸不一致：{(fw, fh)} vs {(w, h)}")
    total_w = w * len(frames)
    out = bytearray(total_w * h * 4)
    for i, (_, _, data) in enumerate(frames):
        for y in range(h):
            src = y * w * 4
            dst = (y * total_w + i * w) * 4
            out[dst:dst + w * 4] = data[src:src + w * 4]
    return total_w, h, bytes(out)


# ---------------------------------------------------------------------------
# 眼球层（方案 B）
# ---------------------------------------------------------------------------


def _pupil_bbox(grid: list[list[str | None]]) -> tuple[int, int, int, int] | None:
    minx = miny = 1 << 30
    maxx = maxy = -1
    for y, row in enumerate(grid):
        for x, key in enumerate(row):
            if key in PUPIL_KEYS:
                if x < minx:
                    minx = x
                if x > maxx:
                    maxx = x
                if y < miny:
                    miny = y
                if y > maxy:
                    maxy = y
    if maxx < 0:
        return None
    return minx, miny, maxx, maxy


def eye_pupil_grid(model: Model, frame) -> list[list[str | None]]:
    """只保留瞳孔珠（key ∈ PUPIL_KEYS），其余全空 —— 即 ``neko_eyes_<st>.png`` 的内容。"""
    return rasterize_grid(model, frame, only_keys=PUPIL_KEYS)


def eye_closed_grid(model: Model, frame) -> list[list[str | None]]:
    """闭合层：眼眶（覆盖整只眼）铺毛色 + 瞳孔顶行压一条睑线。

    - **alpha** 覆盖「瞳孔 bbox 外扩 ``EYE_SOCKET_MARGIN`` 珠」的矩形 → 引擎拿它当眼眶遮罩，
      平移后的瞳孔永远裁在眼内（``PetRenderer.SocketMaskBrush``）。
    - **内容** = 毛色 + 睑线：眨眼相位直接画它 → 盖住身体帧上的眼白，读作「闭眼」。
    """
    src = rasterize_grid(model, frame)
    out: list[list[str | None]] = [[None] * model.cols for _ in range(model.rows)]
    bb = _pupil_bbox(src)
    if bb is None:
        return out
    x0, y0, x1, y1 = bb
    sx0 = max(0, x0 - EYE_SOCKET_MARGIN)
    sy0 = max(0, y0 - EYE_SOCKET_MARGIN)
    sx1 = min(model.cols - 1, x1 + EYE_SOCKET_MARGIN)
    sy1 = min(model.rows - 1, y1 + EYE_SOCKET_MARGIN)
    for y in range(sy0, sy1 + 1):
        for x in range(sx0, sx1 + 1):
            out[y][x] = FUR_KEY
    for x in range(x0, x1 + 1):
        if src[y0][x] in PUPIL_KEYS:
            out[y0][x] = LINE_KEY
    return out


# ---------------------------------------------------------------------------
# 影子
# ---------------------------------------------------------------------------


def make_shadow(scale: int = 2) -> tuple[int, int, bytes]:
    """硬边黑色椭圆（alpha=70），与内置 ``pixel`` 皮肤同款口径；尺寸 32×16 × ``scale``。

    ``PetRenderer.Draw`` 把影子画进固定的 ``32×16`` 逻辑矩形 → 源图按 ``scale`` 出，
    与身体帧同密度（避免影子比猫更"糊"）。
    """
    width, height = 32 * scale, 16 * scale
    cx, cy = width / 2.0, height / 2.0
    rx, ry = width * 0.34, height * 0.40
    buf = bytearray(width * height * 4)
    for y in range(height):
        fy = (y + 0.5 - cy) / ry
        for x in range(width):
            fx = (x + 0.5 - cx) / rx
            if fx * fx + fy * fy <= 1.0:
                o = (y * width + x) * 4
                buf[o + 3] = 70  # 纯黑、alpha=70（与 pixel 皮肤一致）
    return width, height, bytes(buf)


# ---------------------------------------------------------------------------
# skin.json
# ---------------------------------------------------------------------------


def _num(value: float) -> int | float:
    return int(value) if float(value).is_integer() else float(value)


def build_skin_json(
    model: Model,
    *,
    skin_id: str = "bead",
    display_name: str = "拼豆橘猫",
    recommended_scale: float = 4.0,
    has_shadow: bool = True,
) -> dict:
    """构造 ``skin.json`` 结构（字段与 ``SkinRegistry.TryLoad`` / ``SpriteAtlas.LoadFromInfo`` 对齐）。"""
    eye_layers: list[str] = []
    for pose in model.poses:
        if pose.eye_layer:
            eye_layers.append(pose.eye_layer)
            eye_layers.append(pose.eye_layer + "_closed")
    return {
        "id": skin_id,
        "displayName": display_name,
        "layout": "sheet",
        "frameWidth": model.frame_width,
        "frameHeight": model.frame_height,
        "frames": model.skin_frames,
        "hasShadow": has_shadow,
        "eyeLayers": eye_layers,
        "recommendedScale": recommended_scale,
        "states": [
            {"name": p.name, "fps": _num(p.fps), "eyeLayer": p.eye_layer} for p in model.poses
        ],
    }


def dumps_skin_json(meta: dict) -> str:
    return json.dumps(meta, ensure_ascii=False, indent=2) + "\n"


# ---------------------------------------------------------------------------
# 主流程
# ---------------------------------------------------------------------------


def bake(
    model_path: str | Path = DEFAULT_MODEL,
    palette_path: str | Path = DEFAULT_PALETTE,
    out_dir: str | Path = DEFAULT_OUT,
    *,
    skin_id: str = "bead",
    display_name: str = "拼豆橘猫",
    recommended_scale: float = 4.0,
) -> list[Path]:
    """把模型烘焙成皮肤素材；返回写出的文件路径列表（顺序稳定）。"""
    model = load_model(model_path)
    palette = load_palette(palette_path)
    errors = validate_model(model, palette)
    if errors:
        raise ValueError("模型非法，已中止烘焙:\n  - " + "\n  - ".join(errors))

    out = Path(out_dir)
    out.mkdir(parents=True, exist_ok=True)
    scale = model.bake_scale
    frames_n = model.skin_frames
    written: list[Path] = []

    for pose in model.poses:
        # 皮肤级统一帧数：帧少的姿态循环补齐（静态态 = 同一帧重复填满）。
        cycle = [pose.frames[i % len(pose.frames)] for i in range(frames_n)]

        bitmaps = [
            grid_to_rgba(
                rasterize_grid(
                    model,
                    fr,
                    # 有独立眼层时：身体帧**跳过**瞳孔珠（不占位）→ 露出 head 的眼白珠，避免"白洞"
                    skip_keys=PUPIL_KEYS if pose.eye_layer else None,
                ),
                palette,
                scale,
            )
            for fr in cycle
        ]
        sheet_path = out / f"neko_{pose.name}.png"
        w, h, data = hconcat(bitmaps)
        write_png(sheet_path, w, h, data)
        written.append(sheet_path)

        if pose.eye_layer:
            first = cycle[0]  # 眼球层是单帧：取该姿态第 0 帧（与 head 静止一致）
            ew, eh, ed = grid_to_rgba(eye_pupil_grid(model, first), palette, scale)
            p_pupil = out / f"neko_{pose.eye_layer}.png"
            write_png(p_pupil, ew, eh, ed)
            written.append(p_pupil)

            cw, ch, cd = grid_to_rgba(eye_closed_grid(model, first), palette, scale)
            p_closed = out / f"neko_{pose.eye_layer}_closed.png"
            write_png(p_closed, cw, ch, cd)
            written.append(p_closed)

    sw, sh, sd = make_shadow(scale)
    p_shadow = out / "neko_shadow.png"
    write_png(p_shadow, sw, sh, sd)
    written.append(p_shadow)

    # 缩略图：idle 第 0 帧 × THUMB_SCALE（32×3 = 96px，整数倍）
    idle = model.pose("idle").frames[0]
    tw, th, td = grid_to_rgba(rasterize_grid(model, idle), palette, THUMB_SCALE)
    p_thumb = out / "thumb.png"
    write_png(p_thumb, tw, th, td)
    written.append(p_thumb)

    meta = build_skin_json(
        model,
        skin_id=skin_id,
        display_name=display_name,
        recommended_scale=recommended_scale,
    )
    p_skin = out / "skin.json"
    p_skin.write_text(dumps_skin_json(meta), encoding="utf-8")
    written.append(p_skin)

    return written


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="拼豆烘焙器：neko.beads/1 → 皮肤素材")
    ap.add_argument("--model", default=str(DEFAULT_MODEL), help="模型 JSON 路径")
    ap.add_argument("--palette", default=str(DEFAULT_PALETTE), help="调色板 JSON 路径")
    ap.add_argument("--out", default=str(DEFAULT_OUT), help="输出目录")
    ap.add_argument("--id", default="bead", help="skin.json 的皮肤 id")
    ap.add_argument("--name", default="拼豆橘猫", help="skin.json 的 displayName")
    ap.add_argument("--scale", type=float, default=4.0, help="skin.json 的 recommendedScale")
    args = ap.parse_args(argv)

    if not Path(args.model).is_file():
        print(
            f"[x] 找不到模型：{args.model}\n"
            f"    请用 --model 指定模型 JSON（例如 --model my.beads.json），\n"
            f"    或把模型放到 {DEFAULT_MODEL}（相对当前工作目录）。",
            file=sys.stderr,
        )
        return 2
    if not Path(args.palette).is_file():
        print(f"[x] 找不到调色板：{args.palette}", file=sys.stderr)
        return 2

    try:
        written = bake(
            args.model,
            args.palette,
            args.out,
            skin_id=args.id,
            display_name=args.name,
            recommended_scale=args.scale,
        )
    except ValueError as exc:
        # 模型非法 / JSON 结构错 / 调色板错 —— 给**友好**报错（与 verify 的 PASS 摘要同风格），
        # 而不是把内部异常当 traceback 抛给用户；以非 0 退出码收尾。
        print(f"[x] 烘焙失败：{exc}", file=sys.stderr)
        return 1
    print(f"烘焙完成 → {args.out}（{len(written)} 个文件）")
    for p in written:
        print(f"  {p.name:28s} {p.stat().st_size:>8d} B  sha256={sha256_file(p)[:16]}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
