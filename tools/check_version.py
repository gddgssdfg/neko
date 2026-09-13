#!/usr/bin/env python3
"""Neko 版本号一致性检查。

用途
----
Neko 的版本号定义在 `src/DesktopPet.App/DesktopPet.App.csproj` 的 `<Version>`，
但历史上还散落在 `CHANGELOG.md`、README、git tag 等多处，靠人肉同步导致过漂移。
本脚本把「单一事实来源」落地为可自动化的检查：

  1. `src/DesktopPet.App/DesktopPet.App.csproj` 的 `<Version>`
  2. `CHANGELOG.md` 中**首个**版本标题 `## [x.y.z]`
  3. 当前 HEAD 上的 git tag（若有；没有则跳过，不算失败）

三者（实际是前两者 + 可选 tag）不一致时打印差异并以退出码 1 结束。

用法
----
    python tools/check_version.py

退出码
------
    0  一致（或 tag 不存在、被跳过）
    1  不一致
    2  文件缺失 / 无法解析（环境问题）

设计说明
--------
- 纯标准库，无第三方依赖。
- tag 检查为「尽力而为」：git 不可用或 HEAD 无 tag 时只提示、不失败，
  以免在开发机上频繁误报。
"""

from __future__ import annotations

import re
import subprocess
import sys
from pathlib import Path

# ---------------------------------------------------------------------------
# 路径与正则
# ---------------------------------------------------------------------------

REPO_ROOT = Path(__file__).resolve().parent.parent
CSPROJ = REPO_ROOT / "src" / "DesktopPet.App" / "DesktopPet.App.csproj"
CHANGELOG = REPO_ROOT / "CHANGELOG.md"

# <Version>0.1.73</Version>（容忍前后空白）
_CSPROJ_VERSION_RE = re.compile(r"<Version>\s*([^<\s]+)\s*</Version>")

# CHANGELOG 首个版本标题：## [0.1.73] - 2026-09-12
_CHANGELOG_VERSION_RE = re.compile(r"^##\s*\[([^\]]+)\]", re.MULTILINE)

# 宽松的语义化版本校验：major.minor.patch（允许预发布后缀）
_SEMVER_RE = re.compile(r"^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.\-]+)?$")


def _read_text(path: Path) -> str:
    """按 UTF-8 读取文本（容忍 BOM）。"""
    return path.read_text(encoding="utf-8-sig")


def read_csproj_version() -> str:
    """读取 csproj 中的 <Version>。缺失或空则抛 ValueError。"""
    if not CSPROJ.is_file():
        raise FileNotFoundError(f"找不到 csproj：{CSPROJ}")
    text = _read_text(CSPROJ)
    match = _CSPROJ_VERSION_RE.search(text)
    if not match:
        raise ValueError(f"在 {CSPROJ} 中找不到 <Version> 元素")
    version = match.group(1).strip()
    if not version:
        raise ValueError(f"{CSPROJ} 的 <Version> 为空")
    return version


def read_changelog_version() -> str:
    """读取 CHANGELOG 首个 ## [x.y.z] 标题。缺失则抛 ValueError。"""
    if not CHANGELOG.is_file():
        raise FileNotFoundError(f"找不到 CHANGELOG：{CHANGELOG}")
    text = _read_text(CHANGELOG)
    match = _CHANGELOG_VERSION_RE.search(text)
    if not match:
        raise ValueError(f"在 {CHANGELOG} 中找不到形如 '## [x.y.z]' 的版本标题")
    return match.group(1).strip()


def read_head_tags() -> list[str] | None:
    """返回指向 HEAD 的 git tag 列表；git 不可用或无 tag 时返回 None。

    None 表示「无法检查 / 不适用」，调用方应视作跳过而非失败。
    """
    try:
        result = subprocess.run(
            ["git", "tag", "--points-at", "HEAD"],
            cwd=str(REPO_ROOT),
            capture_output=True,
            text=True,
            timeout=10,
        )
    except (OSError, subprocess.SubprocessError):
        return None

    if result.returncode != 0:
        # 非 git 仓库 / git 报错：跳过
        return None

    tags = [line.strip() for line in result.stdout.splitlines() if line.strip()]
    return tags or None


def normalize(version: str) -> str:
    """去掉可能的前缀 v/V，便于比较。"""
    return version[1:] if version[:1] in ("v", "V") else version


def main() -> int:
    print("== Neko 版本一致性检查 ==")

    # ---- 1. 读取两处权威来源 ----
    try:
        csproj_version = read_csproj_version()
        changelog_version = read_changelog_version()
    except (FileNotFoundError, ValueError) as exc:
        print(f"[环境错误] {exc}")
        return 2

    print(f"  csproj <Version>      : {csproj_version}")
    print(f"  CHANGELOG 首个版本    : {changelog_version}")

    errors: list[str] = []

    # ---- 2. 格式校验 ----
    for label, value in (("csproj", csproj_version), ("CHANGELOG", changelog_version)):
        if not _SEMVER_RE.match(normalize(value)):
            errors.append(f"{label} 的版本号 '{value}' 不是合法的 x.y.z 语义化版本")

    # ---- 3. 两处一致性 ----
    if normalize(csproj_version) != normalize(changelog_version):
        errors.append(
            "csproj 与 CHANGELOG 版本不一致："
            f"csproj={csproj_version} ≠ CHANGELOG={changelog_version}；"
            "改版本号时请同步更新 CHANGELOG.md 顶部条目"
        )

    # ---- 4. 可选：git tag ----
    tags = read_head_tags()
    if tags is None:
        print("  HEAD 上的 git tag    : （无 / 非 git 仓库，跳过）")
    else:
        print(f"  HEAD 上的 git tag    : {', '.join(tags)}")
        expected = normalize(csproj_version)
        if not any(normalize(tag) == expected for tag in tags):
            errors.append(
                "HEAD 上的 git tag 与版本号不匹配："
                f"tag={tags} vs 期望 v{expected}"
            )

    # ---- 5. 汇总 ----
    if errors:
        print("\n[不一致] 发现以下问题：")
        for i, message in enumerate(errors, 1):
            print(f"  {i}. {message}")
        return 1

    print("\n[通过] csproj / CHANGELOG / git tag 版本一致。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
