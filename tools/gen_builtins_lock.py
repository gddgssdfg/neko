#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
gen_builtins_lock.py —— 生成「内置插件信任清单」builtins.lock.json（审计 P0-4 T02 / v0.1.82 接线）

背景
----
宿主 `PluginTrustSet` 会随包读取 exe 同级的 `builtins.lock.json`，为「随包发布的官方内置插件」
开一条受控信任通道：只有 id 在清单内（且清单登记了内容摘要时，现场 `plugin.toml` / 入口 dll 的
SHA-256 与登记值一致）的插件，才被放行其**高风险权限**声明（如 other=["process"]）。
清单缺失 / 损坏 / schema 不符时宿主一律不信任（与旧版逐字节一致，不引入新风险）。

本脚本在构建期扫描**已部署到输出目录**的 `plugins/*/`，为每个内置插件登记：
    · manifestSha256 —— plugin.toml 的 SHA-256（小写 hex）
    · entrySha256    —— 入口 dll（plugin.toml 里 [plugin].entry 指向的文件）的 SHA-256
并写出：
    {"schema": 1, "builtins": [ {"id": ..., "manifestSha256": ..., "entrySha256": ...}, ... ]}

用法
----
    python tools/gen_builtins_lock.py --plugins-dir <部署后的 plugins 目录> [--output <输出路径>]
    （--output 缺省 = <plugins-dir>/../builtins.lock.json，即 exe 同级 / App 输出根）

约定
----
    · 名字以 `_` 开头的目录（如 `_template` 模板）视为非插件，跳过。
    · 缺 plugin.toml 的目录跳过（不是插件）。
    · plugin.toml 解析失败 / 无 id → 跳过并在 stderr 提示，绝不中断（构建期不因个别目录报错而失败）。
    · 输出按 id 排序、2 空格缩进 → 结果对同一输入**可重现**（便于 diff / 入库）。
    退出码：0 = 正常（即使没有任何插件）；1 = 致命错误（plugins 目录不存在等）。
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys


def sha256_hex(path: str) -> str:
    """计算文件的 SHA-256，返回小写 hex（与宿主 PluginTrustSet 的算法一致）。"""
    h = hashlib.sha256()
    with open(path, "rb") as fp:
        for chunk in iter(lambda: fp.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def parse_manifest(path: str) -> dict:
    """
    读取 plugin.toml 的 [plugin] 表。优先用标准库 tomllib（Python 3.11+）；
    退化到极简正则解析（只取 id / entry），保证老 Python 也能跑。
    """
    try:
        import tomllib  # type: ignore

        with open(path, "rb") as fp:
            data = tomllib.load(fp)
        return data.get("plugin", {}) or {}
    except ModuleNotFoundError:
        return _parse_manifest_fallback(path)
    except Exception:
        # 文件存在但 tomllib 解析失败 → 交给调用方按「跳过」处理
        raise


def _parse_manifest_fallback(path: str) -> dict:
    """极简 [plugin] 解析：仅取 id / entry 两个键（老 Python 无 tomllib 时的兜底）。"""
    import re

    result: dict = {}
    in_plugin = False
    with open(path, "r", encoding="utf-8", errors="replace") as fp:
        for raw in fp:
            line = raw.strip()
            if not line or line.startswith("#"):
                continue
            if line.startswith("[") and line.endswith("]"):
                in_plugin = line[1:-1].strip() == "plugin"
                continue
            if not in_plugin:
                continue
            m = re.match(r'^(id|entry)\s*=\s*["\']([^"\']*)["\']', line)
            if m:
                result[m.group(1)] = m.group(2)
    return result


def build_lock(plugins_dir: str) -> dict:
    """扫描 plugins_dir 下每个插件目录，产出锁文件数据模型。"""
    builtins = []
    for name in sorted(os.listdir(plugins_dir)):
        if name.startswith("_"):
            continue  # 模板 / 特殊目录不算内置插件
        sub = os.path.join(plugins_dir, name)
        if not os.path.isdir(sub):
            continue
        manifest = os.path.join(sub, "plugin.toml")
        if not os.path.isfile(manifest):
            continue  # 没有 manifest → 不是插件
        try:
            plugin = parse_manifest(manifest)
        except Exception as ex:  # noqa: BLE001 —— 构建期不因个别目录中断
            print(f"[gen_builtins_lock] 跳过 {name}：plugin.toml 解析失败（{ex}）", file=sys.stderr)
            continue

        pid = (plugin.get("id") or "").strip()
        if not pid:
            print(f"[gen_builtins_lock] 跳过 {name}：plugin.toml 缺 [plugin].id", file=sys.stderr)
            continue

        entry = {"id": pid, "manifestSha256": sha256_hex(manifest)}

        entry_name = (plugin.get("entry") or "").strip()
        if entry_name:
            entry_path = os.path.join(sub, entry_name)
            if os.path.isfile(entry_path):
                entry["entrySha256"] = sha256_hex(entry_path)
            else:
                # 入口 dll 缺失：仍登记 manifest 摘要（可被 id 授权），但提示以便排查
                print(f"[gen_builtins_lock] 提示 {name}：入口 dll 缺失（{entry_name}），仅登记 manifest 摘要",
                      file=sys.stderr)

        builtins.append(entry)

    builtins.sort(key=lambda e: e["id"])
    return {"schema": 1, "builtins": builtins}


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description="生成内置插件信任清单 builtins.lock.json")
    ap.add_argument("--plugins-dir", required=True, help="部署后的 plugins 目录（含各插件子目录）")
    ap.add_argument("--output", default=None,
                    help="输出路径（缺省 = <plugins-dir>/../builtins.lock.json，即 exe 同级）")
    args = ap.parse_args(argv)

    plugins_dir = os.path.abspath(args.plugins_dir)
    if not os.path.isdir(plugins_dir):
        print(f"[gen_builtins_lock] 错误：plugins 目录不存在：{plugins_dir}", file=sys.stderr)
        return 1

    output = args.output or os.path.join(os.path.dirname(plugins_dir), "builtins.lock.json")

    lock = build_lock(plugins_dir)
    with open(output, "w", encoding="utf-8", newline="\n") as fp:
        json.dump(lock, fp, ensure_ascii=False, indent=2)
        fp.write("\n")

    print(f"[gen_builtins_lock] 已写入 {output}（{len(lock['builtins'])} 个内置插件）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
