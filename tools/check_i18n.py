#!/usr/bin/env python3
"""Neko 双语（i18n）完整性检查。

背景
----
Neko 设置页用内联 helper 做双语：

    private string T(string zh, string en) => _config.Language == "en" ? en : zh;

调用形如 `T("中文", "English")`。由于没有外部语言包，漏翻 / 写反 / 空串都无法被
编译器发现。参考 BongoCat 的 `cmake/CheckLocalization.cmake`（构建期强制文案覆盖），
本脚本在提交前做静态检查。

检查规则
--------
扫描 `src/**/*.cs` 中所有 `T("...", "...")` 调用：

  1. 两个参数都必须非空。
  2. 中文参数应包含中文字符（防「中文位写了英文」的漏翻）。
     - 若**完全不含**中文字符        → 错误（几乎肯定是漏翻）。
     - 若含 ASCII 字母但同时也含中文 → 警告（多为有意混排，如语言选择器
       「中文 / English」；加 `--strict` 可升级为错误）。
  3. 英文参数不应包含中文字符（防「英文位写了中文」的漏翻）→ 错误。

发现问题时以退出码 1 结束。

用法
----
    python tools/check_i18n.py            # 默认：错误致失败，警告只提示
    python tools/check_i18n.py --strict   # 把「中文参数含 ASCII 字母」的警告也当错误

退出码
------
    0  通过（可能有警告）
    1  存在错误
"""

from __future__ import annotations

import argparse
import re
import sys
from dataclasses import dataclass
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
SRC_ROOT = REPO_ROOT / "src"

# 匹配 T("...", "...")（跳过 `T(string zh, string en)` 定义：其首参数不是字符串字面量）
# 组 1 = 第一个字符串字面量（含引号），组 2 = 第二个字符串字面量（含引号）
_T_CALL_RE = re.compile(
    r'(?<![A-Za-z0-9_])T\s*\(\s*("(?:[^"\\]|\\.)*")\s*,\s*("(?:[^"\\]|\\.)*")\s*\)'
)

# C# 简易转义解码表
_ESCAPES = {
    "n": "\n",
    "r": "\r",
    "t": "\t",
    "\\": "\\",
    '"': '"',
    "0": "\0",
    "a": "\a",
    "b": "\b",
    "f": "\f",
    "v": "\v",
}


@dataclass
class Finding:
    """一条检查结果。"""

    path: Path
    line: int
    level: str  # "error" | "warning"
    message: str
    zh: str
    en: str


def decode_cs_literal(literal: str) -> str:
    """把 C# 字符串字面量（含两端引号）解码为实际字符串。

    支持常见转义（\\n \\t \\" \\\\ 等）与 \\uXXXX / \\xXX。
    """
    assert literal and literal[0] == '"' and literal[-1] == '"'
    body = literal[1:-1]
    out: list[str] = []
    i = 0
    n = len(body)
    while i < n:
        ch = body[i]
        if ch != "\\":
            out.append(ch)
            i += 1
            continue

        i += 1
        if i >= n:  # 尾随反斜杠，异常字面量
            break
        esc = body[i]
        i += 1

        if esc == "u" and i + 4 <= n:
            out.append(chr(int(body[i : i + 4], 16)))
            i += 4
        elif esc == "U" and i + 8 <= n:
            out.append(chr(int(body[i : i + 8], 16)))
            i += 8
        elif esc == "x":
            j = i
            while j < n and j < i + 4 and body[j] in "0123456789abcdefABCDEF":
                j += 1
            if j > i:
                out.append(chr(int(body[i:j], 16)))
                i = j
        else:
            out.append(_ESCAPES.get(esc, esc))
    return "".join(out)


def has_cjk(text: str) -> bool:
    """是否含 CJK 字符（中日韩统一表意文字）。"""
    return any(
        "\u4e00" <= ch <= "\u9fff"          # CJK 基本区
        or "\u3400" <= ch <= "\u4dbf"       # 扩展 A
        or "\uf900" <= ch <= "\ufaff"       # 兼容表意文字
        for ch in text
    )


def has_ascii_letter(text: str) -> bool:
    """是否含 ASCII 字母。"""
    return any(("a" <= ch <= "z") or ("A" <= ch <= "Z") for ch in text)


def line_of(text: str, index: int) -> int:
    """返回字符下标所在行号（1 起）。"""
    return text.count("\n", 0, index) + 1


def check_file(path: Path, strict: bool) -> list[Finding]:
    """检查单个文件，返回全部发现。"""
    try:
        text = path.read_text(encoding="utf-8-sig")
    except (OSError, UnicodeDecodeError):
        return []

    findings: list[Finding] = []
    for match in _T_CALL_RE.finditer(text):
        zh = decode_cs_literal(match.group(1)).strip()
        en = decode_cs_literal(match.group(2)).strip()
        line = line_of(text, match.start())

        # 规则 1：两参数均非空
        if not zh or not en:
            empty = []
            if not zh:
                empty.append("中文")
            if not en:
                empty.append("英文")
            findings.append(
                Finding(path, line, "error",
                        f"T() 的{'/'.join(empty)}参数为空", zh, en)
            )
            continue

        # 规则 3：英文参数不应含中文
        if has_cjk(en):
            findings.append(
                Finding(path, line, "error",
                        "英文参数含中文字符（疑似漏翻/写反）", zh, en)
            )

        # 规则 2：中文参数
        if not has_cjk(zh):
            findings.append(
                Finding(path, line, "error",
                        "中文参数不含中文字符（疑似漏翻为英文）", zh, en)
            )
        elif has_ascii_letter(zh):
            findings.append(
                Finding(path, line, "error" if strict else "warning",
                        "中文参数含 ASCII 字母（疑似混排；如为有意，加 --strict 前请确认）",
                        zh, en)
            )

    return findings


def collect_files(root: Path) -> list[Path]:
    """收集 src 下全部 .cs 文件（排除 bin/obj 等构建产物）。"""
    skip_dirs = {"bin", "obj", ".vs", ".git"}
    files: list[Path] = []
    for path in root.rglob("*.cs"):
        if any(part in skip_dirs for part in path.parts):
            continue
        files.append(path)
    return sorted(files)


def main() -> int:
    parser = argparse.ArgumentParser(description="Neko 双语（i18n）完整性检查")
    parser.add_argument("--strict", action="store_true",
                        help="把「中文参数含 ASCII 字母」的警告也视为错误")
    args = parser.parse_args()

    if not SRC_ROOT.is_dir():
        print(f"[环境错误] 找不到源码目录：{SRC_ROOT}")
        return 2

    files = collect_files(SRC_ROOT)
    print(f"== Neko i18n 检查 ==  扫描 {len(files)} 个 .cs 文件"
          f"{'（严格模式）' if args.strict else ''}")

    all_findings: list[Finding] = []
    for path in files:
        all_findings.extend(check_file(path, args.strict))

    errors = [f for f in all_findings if f.level == "error"]
    warnings = [f for f in all_findings if f.level == "warning"]

    def render(items: list[Finding]) -> None:
        for f in items:
            rel = f.path.relative_to(REPO_ROOT)
            print(f"  {rel}:{f.line}: [{f.level}] {f.message}")
            print(f"      zh = {f.zh!r}")
            print(f"      en = {f.en!r}")

    if errors:
        print(f"\n[错误] {len(errors)} 处：")
        render(errors)
    if warnings:
        print(f"\n[警告] {len(warnings)} 处（不影响通过）：")
        render(warnings)

    if errors:
        print(f"\n[失败] {len(errors)} 个错误，{len(warnings)} 个警告。")
        return 1

    print(f"\n[通过] 无错误（{len(warnings)} 个警告）。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
