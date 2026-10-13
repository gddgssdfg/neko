"""Rebuild a bead palette file from an upstream color list.

Reads a ``_perler.json``-style dataset and writes a ``palette.json`` the toolchain can
consume. The upstream shape is::

    { "colors": [ { "code": "P18", "name": "Black", "hex": "#323234",
                    "rgb": [50,50,52], "sku": "80-..." }, ... ] }

**Pure standard library** — no third-party dependency.

The emitted palette always starts with one **transparent** slot (``hex: null``) so empty
bead cells work; the rest are the (optionally limited) upstream colors with short,
stable keys.

Usage::

    python gen_palette.py --input _perler.json --out palette.json --name my-perler
    python gen_palette.py --input _perler.json --out palette.json --limit 24
"""

from __future__ import annotations

import argparse
import json
import string
from pathlib import Path
from typing import Any

# ---------------------------------------------------------------------------
# 常量
# ---------------------------------------------------------------------------

TRANSPARENT_KEY = "n"
"""透明 / 空珠位槽的 key。"""

KEY_POOL = "".join(
    c for c in (string.ascii_lowercase + string.ascii_uppercase + string.digits)
    if c != TRANSPARENT_KEY
)
"""合法 key 字符池（模型 map 的**值**即调色板 key，用单字符便于阅读）。

**排除**透明槽 key（``n``），避免有色条目与它撞 key。
"""

TRANSPARENT_NAME = "透明（空珠位）"

_HEX_DIGITS = set("0123456789abcdefABCDEF")


# ---------------------------------------------------------------------------
# 解析上游数据集
# ---------------------------------------------------------------------------


def _normalize_hex(value: Any) -> str | None:
    """把 ``"#RRGGBB"`` / ``"RRGGBB"`` 规整为小写 ``"#rrggbb"``；非法返回 None。"""
    if not isinstance(value, str):
        return None
    h = value.strip().lstrip("#")
    if len(h) != 6 or any(c not in _HEX_DIGITS for c in h):
        return None
    return "#" + h.lower()


def load_upstream_colors(path: str | Path) -> list[dict]:
    """从上游 JSON 读取有色条目（跳过无 hex 的项）。任何结构错误抛 ValueError。"""
    raw = json.loads(Path(path).read_text(encoding="utf-8"))
    if isinstance(raw, dict):
        entries = raw.get("colors")
    elif isinstance(raw, list):
        entries = raw
    else:
        raise ValueError("上游数据顶层必须是对象（含 colors 数组）或数组本身")
    if not isinstance(entries, list) or not entries:
        raise ValueError("上游 colors 必须是非空数组")

    colors: list[dict] = []
    seen_hex: set[str] = set()
    for i, e in enumerate(entries):
        if not isinstance(e, dict):
            raise ValueError(f"colors[{i}] 必须是对象")
        hexv = _normalize_hex(e.get("hex"))
        if hexv is None:
            continue  # 无有效颜色 → 跳过（透明槽我们自建）
        if hexv in seen_hex:
            continue  # 去重
        seen_hex.add(hexv)
        code = e.get("code") or e.get("sku")
        colors.append(
            {
                "code": str(code) if code is not None else None,
                "hex": hexv,
                "name": str(e.get("name")) if e.get("name") is not None else None,
            }
        )
    if not colors:
        raise ValueError("上游数据里没有任何带有效 hex 的颜色")
    return colors


# ---------------------------------------------------------------------------
# key 分配
# ---------------------------------------------------------------------------


def assign_keys(count: int, *, pool: str = KEY_POOL) -> list[str]:
    """给 ``count`` 个颜色分配单字符 key。池不足则抛 ValueError。"""
    if count > len(pool):
        raise ValueError(f"需要 {count} 个 key，但字符池只有 {len(pool)} 个")
    return list(pool[:count])


# ---------------------------------------------------------------------------
# 颜色分散度优选（CIELAB 最远点采样）
# ---------------------------------------------------------------------------


def hex_to_rgb(hexv: str) -> tuple[int, int, int]:
    """``"#rrggbb"`` → ``(r, g, b)``。"""
    h = hexv.strip().lstrip("#")
    return (int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16))


def _srgb_to_linear(v: int) -> float:
    c = v / 255.0
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def rgb_to_lab(rgb: tuple[int, int, int]) -> tuple[float, float, float]:
    """sRGB → CIE L*a*b*（D65 白点）。

    与 ``bead_convert.rgb_to_lab`` **逐行同口径**，这样「选调色板」与「量化配色」落在同
    一个感知空间（CIE76 ΔE），选出来的色才有实际分离度。
    """
    r, g, b = (_srgb_to_linear(v) for v in rgb)
    # sRGB → XYZ（D65）
    x = r * 0.4124564 + g * 0.3575761 + b * 0.1804375
    y = r * 0.2126729 + g * 0.7151522 + b * 0.0721750
    z = r * 0.0193339 + g * 0.1191920 + b * 0.9503041
    # 归一到 D65 白点
    x /= 0.95047
    y /= 1.00000
    z /= 1.08883

    def f(t: float) -> float:
        return t ** (1.0 / 3.0) if t > 0.008856 else (7.787 * t + 16.0 / 116.0)

    fx, fy, fz = f(x), f(y), f(z)
    return (116.0 * fy - 16.0, 500.0 * (fx - fy), 200.0 * (fy - fz))


def select_diverse(colors: list[dict], count: int) -> list[dict]:
    """从上游颜色里挑 ``count`` 个在 **CIELAB 上彼此相距最远**的颜色（贪心最远点采样）。

    动机：``bead_convert.py`` 用 CIELAB 最近邻做量化；若调色板只是「上游前 N 个」，
    很可能挤在同一个色区，量化时大量颜色抢同一颗珠。这里让子集在感知空间内尽量铺开。

    确定性：固定以 ``colors[0]`` 为种子（保留「含上游首个色」的直觉）；并列时取更小
    的原始下标；返回时按原始下标升序，输出稳定、便于人工复核。复杂度 ``O(count · N)``。
    """
    n = len(colors)
    if count >= n:
        return list(colors)

    labs = [rgb_to_lab(hex_to_rgb(c["hex"])) for c in colors]

    def _d2(a: tuple[float, float, float], b: tuple[float, float, float]) -> float:
        return (a[0] - b[0]) ** 2 + (a[1] - b[1]) ** 2 + (a[2] - b[2]) ** 2

    picked = [False] * n
    picked[0] = True
    chosen = [0]
    # min_d2[i]：颜色 i 到「已选集合」的最小距离平方
    min_d2 = [_d2(labs[i], labs[0]) for i in range(n)]
    while len(chosen) < count:
        best_i, best_v = -1, -1.0
        for i in range(n):
            if picked[i]:
                continue
            if min_d2[i] > best_v:  # 严格大于 → 并列取更小下标
                best_v, best_i = min_d2[i], i
        picked[best_i] = True
        chosen.append(best_i)
        for i in range(n):
            if not picked[i]:
                d = _d2(labs[i], labs[best_i])
                if d < min_d2[i]:
                    min_d2[i] = d
    return [colors[i] for i in sorted(chosen)]


def build_palette(
    colors: list[dict],
    *,
    name: str,
    brand: str = "Perler",
    source: str = "",
    license_: str = "",
    status: str = "community",
    status_note: str = "",
    description: str = "",
    limit: int | None = None,
    keys: list[str] | None = None,
) -> dict:
    """构造 palette dict（含透明槽）。

    ``limit`` 非空时按 **CIELAB 分散度**优选 ``limit`` 个色（贪心最远点采样，而非简单
    取上游前缀），与 ``bead_convert`` 的配色空间一致；``limit`` 为空则用全部颜色。
    """
    selected = select_diverse(colors, limit) if limit is not None else colors
    if keys is None:
        keys = assign_keys(len(selected))
    if len(keys) != len(selected):
        raise ValueError(f"提供 {len(keys)} 个 key，但有 {len(selected)} 个颜色")

    out_colors: list[dict] = [
        {
            "key": TRANSPARENT_KEY,
            "code": None,
            "hex": None,
            "name": TRANSPARENT_NAME,
            "role": "空珠位：不放置珠粒",
        }
    ]
    seen: set[str] = {TRANSPARENT_KEY}
    for key, c in zip(keys, selected):
        if key in seen:
            raise ValueError(f"key 重复: {key!r}")
        seen.add(key)
        out_colors.append(
            {
                "key": key,
                "code": c.get("code"),
                "hex": c["hex"],
                "name": c.get("name"),
                "role": None,
            }
        )

    return {
        "name": name,
        "description": description
        or f"由 gen_palette.py 生成 —— {len(selected)} 色（含 1 个透明槽）。",
        "source": source,
        "license": license_,
        "status": status,
        "statusNote": status_note,
        "unit": "bead",
        "brand": brand,
        "colors": out_colors,
    }


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(
        description="从上游 Perler 颜色数据集生成调色板 JSON",
    )
    ap.add_argument("--input", required=True, help="上游 JSON（含 colors 数组）")
    ap.add_argument("--out", required=True, help="输出 palette JSON 路径")
    ap.add_argument("--name", default="perler-custom", help="调色板 name（模型以此引用）")
    ap.add_argument("--brand", default="Perler", help="品牌")
    ap.add_argument("--source", default="", help="来源说明")
    ap.add_argument("--license", dest="license_", default="", help="许可说明")
    ap.add_argument("--status", default="community", help="数据可信度状态")
    ap.add_argument("--status-note", default="", help="状态补充说明")
    ap.add_argument("--description", default="", help="调色板描述")
    ap.add_argument("--limit", type=int, default=None, help="只保留 N 个颜色（按 CIELAB 分散度优选，非前缀）")
    ap.add_argument("--keys", default="auto", help="key 分配策略：auto（默认）")
    args = ap.parse_args(argv)

    if args.keys != "auto":
        raise SystemExit("目前仅支持 --keys auto")
    if args.limit is not None and args.limit < 1:
        raise SystemExit("--limit 必须 ≥ 1")

    colors = load_upstream_colors(args.input)
    palette = build_palette(
        colors,
        name=args.name,
        brand=args.brand,
        source=args.source,
        license_=args.license_,
        status=args.status,
        status_note=args.status_note,
        description=args.description,
        limit=args.limit,
    )

    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(palette, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    n = len(palette["colors"]) - 1
    print(f"已生成调色板 {args.name!r}: {n} 色 + 1 透明槽 → {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
