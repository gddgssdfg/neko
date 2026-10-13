"""``neko.beads/1`` 拼豆模型 —— 数据模型 + 加载 / 校验 / 序列化。

**纯标准库**（只用 ``json`` / ``dataclasses`` / ``typing``），无任何第三方依赖。

设计契约（见 ``_eval/bead-implementation-plan.md`` §2）
------------------------------------------------------
- 画布是 ``cols × rows`` 的**珠粒网格**；每个珠粒要么是「空珠位」（透明），要么是
  某个调色板 key 对应的纯色珠。
- 一个「部件（part）」= ``anchor``（``base`` 形状左上角落在画布的格坐标）+ 一组
  ``shapes``（形状变体名 → 字符行数组）+ ``map``（字符 → 调色板 key）+ ``z``（绘制序）。
- ``.`` 恒表示「空珠位」；其余字符查 ``map`` 得到调色板 key。
- 一个「姿态（pose）」= 一组 ``frames``；每帧可给每个部件一个整数位移 ``offsets``、
  一个形状变体 ``shapes``、以及珠粒级字符覆写 ``overrides``。

**确定性**：全部整数、无浮点、无随机、无插值、无重采样 —— 同一模型两次烘焙逐字节一致。
坐标约定：``x`` 向右、``y`` 向下；``anchor`` 是形状左上角；``offsets`` 是 `(dx, dy)` 整数珠粒位移。
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from pathlib import Path
from typing import Iterator

# ---------------------------------------------------------------------------
# 常量
# ---------------------------------------------------------------------------

FORMAT = "neko.beads/1"
"""格式版本闸门；不匹配即判为非法模型。"""

DEFAULT_SHAPE = "base"
"""部件的默认形状变体名。每个部件都必须提供它。"""

TRANSPARENT_CHAR = "."
"""字符网格里表示「空珠位」（不放珠粒）的字符。"""

# ---------------------------------------------------------------------------
# 调色板
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class Color:
    """一个调色板条目。``hex`` 为 ``None`` 表示该 key 是**透明/空珠位**。"""

    key: str
    code: str | None
    hex: str | None
    name: str | None = None
    role: str | None = None

    @property
    def is_transparent(self) -> bool:
        return self.hex is None

    def rgb(self) -> tuple[int, int, int]:
        """返回 ``(r, g, b)``；透明条目返回 ``(0, 0, 0)``（调用方应先看 :attr:`is_transparent`）。"""
        if self.hex is None:
            return (0, 0, 0)
        h = self.hex.lstrip("#")
        return (int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16))

    def rgba(self) -> tuple[int, int, int, int]:
        """返回 ``(r, g, b, a)``；透明条目 alpha=0。"""
        r, g, b = self.rgb()
        return (r, g, b, 0 if self.is_transparent else 255)


@dataclass(frozen=True)
class Palette:
    """内置调色板（``tools/bead/palette.json``）。"""

    name: str
    colors: tuple[Color, ...]
    source: str = ""
    license: str = ""
    status: str = ""
    status_note: str = ""
    brand: str = ""
    unit: str = "bead"

    def __post_init__(self) -> None:
        by_key: dict[str, Color] = {}
        for c in self.colors:
            if c.key in by_key:
                raise ValueError(f"调色板存在重复 key: {c.key!r}")
            by_key[c.key] = c
        # frozen dataclass：用 object.__setattr__ 填缓存
        object.__setattr__(self, "_by_key", by_key)

    # _by_key 通过 __post_init__ 注入
    _by_key: dict[str, Color] = field(default_factory=dict, repr=False, compare=False)

    def get(self, key: str) -> Color:
        try:
            return self._by_key[key]
        except KeyError:
            raise KeyError(f"调色板中没有 key: {key!r}（可用：{sorted(self._by_key)}）") from None

    def __contains__(self, key: object) -> bool:
        return key in self._by_key

    def keys(self) -> set[str]:
        return set(self._by_key)

    def hexes(self) -> set[str]:
        """全部非透明条目的 hex 集合（小写，含 ``#``）。"""
        return {c.hex.lower() for c in self.colors if c.hex is not None}  # type: ignore[union-attr]

    def names(self) -> dict[str, str]:
        return {c.key: (c.name or c.key) for c in self.colors}


def load_palette(path: str | Path) -> Palette:
    """从 JSON 载入调色板。任何结构错误抛 :class:`ValueError`。"""
    raw = json.loads(Path(path).read_text(encoding="utf-8"))
    if not isinstance(raw, dict):
        raise ValueError("palette 顶层必须是对象")
    colors_raw = raw.get("colors")
    if not isinstance(colors_raw, list) or not colors_raw:
        raise ValueError("palette.colors 必须是非空数组")
    colors: list[Color] = []
    for i, c in enumerate(colors_raw):
        if not isinstance(c, dict):
            raise ValueError(f"palette.colors[{i}] 必须是对象")
        key = c.get("key")
        if not isinstance(key, str) or not key:
            raise ValueError(f"palette.colors[{i}].key 必须是非空字符串")
        hexv = c.get("hex")
        if hexv is not None and not (isinstance(hexv, str) and len(hexv.lstrip("#")) == 6):
            raise ValueError(f"palette.colors[{i}].hex 非法: {hexv!r}")
        colors.append(
            Color(
                key=key,
                code=c.get("code"),
                hex=hexv,
                name=c.get("name"),
                role=c.get("role"),
            )
        )
    return Palette(
        name=str(raw.get("name", "unnamed")),
        colors=tuple(colors),
        source=str(raw.get("source", "")),
        license=str(raw.get("license", "")),
        status=str(raw.get("status", "")),
        status_note=str(raw.get("statusNote", "")),
        brand=str(raw.get("brand", "")),
        unit=str(raw.get("unit", "bead")),
    )


# ---------------------------------------------------------------------------
# 模型：部件 / 帧 / 姿态
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class Part:
    """一个可独立位移的部件（头 / 躯干 / 腿 / 尾 / 耳 / 脸…）。"""

    id: str
    anchor: tuple[int, int]
    z: int
    shapes: dict[str, tuple[str, ...]]
    map: dict[str, str]

    def shape(self, name: str | None = None) -> tuple[str, ...]:
        """取形状变体行数组；``name`` 为 None 用 :data:`DEFAULT_SHAPE`。找不到抛 KeyError。"""
        name = name or DEFAULT_SHAPE
        if name not in self.shapes:
            raise KeyError(f"部件 {self.id!r} 没有形状变体 {name!r}（可用：{sorted(self.shapes)}）")
        return self.shapes[name]

    def shape_size(self, name: str | None = None) -> tuple[int, int]:
        rows = self.shape(name)
        return (max((len(r) for r in rows), default=0), len(rows))

    def palette_key(self, ch: str, overrides: dict[str, str] | None = None) -> str | None:
        """字符 → 调色板 key。``.`` 或不在 ``map`` 的字符 → None（空珠位）。"""
        if overrides and ch in overrides:
            ch = overrides[ch]
        if ch == TRANSPARENT_CHAR:
            return None
        return self.map.get(ch)

    def iter_beads(
        self,
        shape_name: str | None = None,
        offset: tuple[int, int] = (0, 0),
        overrides: dict[str, str] | None = None,
    ) -> Iterator[tuple[int, int, str]]:
        """产出该部件某形状的 ``(x, y, palette_key)``，坐标已含 anchor + offset。``.`` 不产出。"""
        rows = self.shape(shape_name)
        ax, ay = self.anchor
        for r, row in enumerate(rows):
            for c, ch in enumerate(row):
                key = self.palette_key(ch, overrides)
                if key is None:
                    continue
                yield (ax + c + offset[0], ay + r + offset[1], key)


@dataclass(frozen=True)
class Frame:
    """姿态里的一帧：对每个部件可给整数位移 / 形状变体 / 珠粒级覆写。"""

    offsets: dict[str, tuple[int, int]] = field(default_factory=dict)
    shapes: dict[str, str] = field(default_factory=dict)
    overrides: dict[str, dict[str, str]] = field(default_factory=dict)

    def offset(self, part_id: str) -> tuple[int, int]:
        return self.offsets.get(part_id, (0, 0))

    def shape_of(self, part_id: str) -> str:
        return self.shapes.get(part_id, DEFAULT_SHAPE)

    def overrides_of(self, part_id: str) -> dict[str, str]:
        return self.overrides.get(part_id, {})


@dataclass(frozen=True)
class Pose:
    """一个状态（idle / sit / walk…）的帧序列。"""

    name: str
    fps: float
    eye_layer: str | None
    frames: tuple[Frame, ...]


@dataclass(frozen=True)
class Model:
    """整只猫的拼豆模型。"""

    cols: int
    rows: int
    bake_scale: int
    palette_name: str
    parts: tuple[Part, ...]
    poses: tuple[Pose, ...]
    fmt: str = FORMAT
    frames: int | None = None
    """皮肤级播放帧数（可选，**只增字段**）。

    ``None`` → 由「所有姿态帧数的最大值」推导；显式给出时，烘焙器把每个姿态的帧
    **循环补齐**到该帧数（静态态 = 同一帧重复填满）。这直接对应 ``skin.json.frames`` ——
    ``SpriteAtlas`` 按皮肤级单一 ``frames`` 裁剪每张 sheet，故每张 sheet 横向帧数必须 == 它。
    """

    @property
    def skin_frames(self) -> int:
        """皮肤级播放帧数：显式 ``frames`` 优先，否则取所有姿态帧数的最大值。"""
        if self.frames is not None:
            return self.frames
        return max((len(p.frames) for p in self.poses), default=1)

    def pose(self, name: str) -> Pose:
        for p in self.poses:
            if p.name == name:
                return p
        raise KeyError(f"模型没有姿态 {name!r}（可用：{[p.name for p in self.poses]}）")

    def part(self, part_id: str) -> Part:
        for p in self.parts:
            if p.id == part_id:
                return p
        raise KeyError(f"模型没有部件 {part_id!r}")

    @property
    def frame_width(self) -> int:
        return self.cols * self.bake_scale

    @property
    def frame_height(self) -> int:
        return self.rows * self.bake_scale

    def eye_part(self) -> Part | None:
        """承载「瞳孔」的部件 —— 约定：``map`` 里映射出 key ``'e'`` 的那个部件。"""
        for p in self.parts:
            if "e" in p.map.values():
                return p
        return None


# ---------------------------------------------------------------------------
# 加载
# ---------------------------------------------------------------------------


def _as_int_pair(v, where: str) -> tuple[int, int]:
    if not (isinstance(v, (list, tuple)) and len(v) == 2):
        raise ValueError(f"{where} 必须是 [int, int]")
    try:
        return (int(v[0]), int(v[1]))
    except (TypeError, ValueError):
        raise ValueError(f"{where} 必须是两个整数") from None


def _frame_from_dict(d: dict, where: str) -> Frame:
    if not isinstance(d, dict):
        raise ValueError(f"{where} 必须是对象")
    offsets: dict[str, tuple[int, int]] = {}
    for k, v in (d.get("offsets") or {}).items():
        offsets[k] = _as_int_pair(v, f"{where}.offsets[{k}]")
    shapes: dict[str, str] = {}
    for k, v in (d.get("shapes") or {}).items():
        if not isinstance(v, str):
            raise ValueError(f"{where}.shapes[{k}] 必须是字符串")
        shapes[k] = v
    overrides: dict[str, dict[str, str]] = {}
    for k, v in (d.get("overrides") or {}).items():
        if not isinstance(v, dict):
            raise ValueError(f"{where}.overrides[{k}] 必须是对象")
        overrides[k] = {str(a): str(b) for a, b in v.items()}
    return Frame(offsets=offsets, shapes=shapes, overrides=overrides)


def model_from_dict(raw: dict) -> Model:
    """从已解析的 dict 构建 :class:`Model`（不做完整性校验，交给 :func:`validate_model`）。"""
    if not isinstance(raw, dict):
        raise ValueError("model 顶层必须是对象")
    fmt = raw.get("format", "")
    canvas = raw.get("canvas") or {}
    cols = int(canvas.get("cols", 0))
    rows = int(canvas.get("rows", 0))
    bake_scale = int(raw.get("bakeScale", 0))
    palette_name = str(raw.get("palette", ""))

    parts: list[Part] = []
    for i, p in enumerate(raw.get("parts") or []):
        if not isinstance(p, dict):
            raise ValueError(f"parts[{i}] 必须是对象")
        pid = p.get("id")
        if not isinstance(pid, str) or not pid:
            raise ValueError(f"parts[{i}].id 必须是非空字符串")
        anchor = _as_int_pair(p.get("anchor", [0, 0]), f"parts[{i}].anchor")
        z = int(p.get("z", 0))
        shapes_raw = p.get("shapes") or {}
        if not isinstance(shapes_raw, dict) or not shapes_raw:
            raise ValueError(f"parts[{i}].shapes 必须是非空对象")
        shapes: dict[str, tuple[str, ...]] = {}
        for sname, rows_ in shapes_raw.items():
            if not (isinstance(rows_, list) and all(isinstance(r, str) for r in rows_)):
                raise ValueError(f"parts[{i}].shapes[{sname}] 必须是字符串数组")
            shapes[str(sname)] = tuple(rows_)
        map_raw = p.get("map") or {}
        if not isinstance(map_raw, dict):
            raise ValueError(f"parts[{i}].map 必须是对象")
        cmap = {str(ch): str(key) for ch, key in map_raw.items()}
        parts.append(Part(id=pid, anchor=anchor, z=z, shapes=shapes, map=cmap))

    poses: list[Pose] = []
    poses_raw = raw.get("poses") or {}
    if not isinstance(poses_raw, dict):
        raise ValueError("poses 必须是对象")
    for name, pd in poses_raw.items():
        if not isinstance(pd, dict):
            raise ValueError(f"poses[{name}] 必须是对象")
        fps = float(pd.get("fps", 0))
        eye = pd.get("eyeLayer", None)
        eye_layer = str(eye) if isinstance(eye, str) and eye else None
        frames_raw = pd.get("frames")
        if not (isinstance(frames_raw, list) and frames_raw):
            raise ValueError(f"poses[{name}].frames 必须是非空数组")
        frames = tuple(_frame_from_dict(f, f"poses[{name}].frames[{i}]") for i, f in enumerate(frames_raw))
        poses.append(Pose(name=str(name), fps=fps, eye_layer=eye_layer, frames=frames))

    return Model(
        cols=cols,
        rows=rows,
        bake_scale=bake_scale,
        palette_name=palette_name,
        parts=tuple(parts),
        poses=tuple(poses),
        fmt=fmt,
        frames=(int(raw["frames"]) if raw.get("frames") is not None else None),
    )


def load_model(path: str | Path) -> Model:
    """读取并返回 :class:`Model`。JSON 结构错误抛 :class:`ValueError`。"""
    raw = json.loads(Path(path).read_text(encoding="utf-8"))
    return model_from_dict(raw)


# ---------------------------------------------------------------------------
# 校验
# ---------------------------------------------------------------------------


def validate_model(model: Model, palette: Palette | None = None) -> list[str]:
    """校验模型；返回错误字符串列表（空列表 = 合法）。不抛异常。"""
    errs: list[str] = []

    if model.fmt != FORMAT:
        errs.append(f"format 必须是 {FORMAT!r}，实际 {model.fmt!r}")
    if model.cols <= 0 or model.rows <= 0:
        errs.append(f"canvas 尺寸非法: {model.cols}×{model.rows}")
    if model.bake_scale < 1:
        errs.append(f"bakeScale 必须 ≥ 1，实际 {model.bake_scale}")
    if model.frames is not None and model.frames < 1:
        # 皮肤级 frames=0 → skin_frames=0 → 每张 sheet 无帧可拼（hconcat 会炸）。
        # 在**校验期**就拦下，别等到烘焙中途才抛。
        errs.append(f"frames 必须 ≥ 1，实际 {model.frames}")
    if not model.parts:
        errs.append("parts 为空")
    if not model.poses:
        errs.append("poses 为空")

    seen_ids: set[str] = set()
    for p in model.parts:
        tag = f"部件 {p.id!r}"
        if p.id in seen_ids:
            errs.append(f"{tag}: id 重复")
        seen_ids.add(p.id)
        if DEFAULT_SHAPE not in p.shapes:
            errs.append(f"{tag}: 缺少默认形状 {DEFAULT_SHAPE!r}")
        if not p.map:
            errs.append(f"{tag}: map 为空")
        for sname, rows in p.shapes.items():
            for r, row in enumerate(rows):
                if len(row) > model.cols:
                    errs.append(f"{tag}.{sname} 第{r}行宽 {len(row)} > canvas.cols {model.cols}")
                for ch in row:
                    if ch == TRANSPARENT_CHAR:
                        continue
                    if ch not in p.map:
                        errs.append(f"{tag}.{sname} 第{r}行字符 {ch!r} 不在 map 中")
                        break
            if len(rows) > model.rows:
                errs.append(f"{tag}.{sname} 行数 {len(rows)} > canvas.rows {model.rows}")
        if palette is not None:
            for ch, key in p.map.items():
                if key not in palette:
                    errs.append(f"{tag}.map[{ch!r}] → 未知调色板 key {key!r}")

    for pose in model.poses:
        ptag = f"姿态 {pose.name!r}"
        if pose.fps < 0:
            errs.append(f"{ptag}: fps 不能为负（{pose.fps}）")
        if not pose.frames:
            errs.append(f"{ptag}: frames 为空")
        for fi, fr in enumerate(pose.frames):
            for part_id in fr.offsets:
                if part_id not in seen_ids:
                    errs.append(f"{ptag}.frames[{fi}].offsets 引用未知部件 {part_id!r}")
            for part_id, sname in fr.shapes.items():
                if part_id not in seen_ids:
                    errs.append(f"{ptag}.frames[{fi}].shapes 引用未知部件 {part_id!r}")
                elif sname not in model.part(part_id).shapes:
                    errs.append(f"{ptag}.frames[{fi}].shapes[{part_id!r}] = 未知形状 {sname!r}")
            for part_id in fr.overrides:
                if part_id not in seen_ids:
                    errs.append(f"{ptag}.frames[{fi}].overrides 引用未知部件 {part_id!r}")

    # 眼球层声明必须能找到承载瞳孔的部件
    layered = [p.name for p in model.poses if p.eye_layer]
    if layered and model.eye_part() is None:
        errs.append(f"姿态 {layered} 声明了 eyeLayer，但没有任何部件映射出 key 'e'（瞳孔）")

    return errs


# ---------------------------------------------------------------------------
# 序列化（round-trip）
# ---------------------------------------------------------------------------


def model_to_dict(model: Model) -> dict:
    """把 :class:`Model` 还原成可写 JSON 的 dict（与载入 schema 对称）。"""
    parts = []
    for p in model.parts:
        parts.append(
            {
                "id": p.id,
                "anchor": [p.anchor[0], p.anchor[1]],
                "z": p.z,
                "shapes": {name: list(rows) for name, rows in p.shapes.items()},
                "map": dict(p.map),
            }
        )
    poses: dict[str, dict] = {}
    for pose in model.poses:
        frames = []
        for fr in pose.frames:
            fd: dict = {}
            if fr.offsets:
                fd["offsets"] = {k: [v[0], v[1]] for k, v in fr.offsets.items()}
            if fr.shapes:
                fd["shapes"] = dict(fr.shapes)
            if fr.overrides:
                fd["overrides"] = {k: dict(v) for k, v in fr.overrides.items()}
            frames.append(fd)
        poses[pose.name] = {"fps": pose.fps, "eyeLayer": pose.eye_layer, "frames": frames}
    return {
        "format": model.fmt,
        "canvas": {"cols": model.cols, "rows": model.rows},
        "bakeScale": model.bake_scale,
        "palette": model.palette_name,
        "parts": parts,
        "poses": poses,
    }


def dumps_model(model: Model) -> str:
    """确定性 JSON 文本（``ensure_ascii=False``、2 空格缩进、末尾换行）。"""
    return json.dumps(model_to_dict(model), ensure_ascii=False, indent=2) + "\n"


def save_model(model: Model, path: str | Path) -> None:
    """序列化写盘（round-trip 用）。"""
    Path(path).write_text(dumps_model(model), encoding="utf-8")
