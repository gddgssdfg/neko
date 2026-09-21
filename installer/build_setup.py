#!/usr/bin/env python3
"""Neko 桌宠一键出包：生成 WiX 4 源 → 编译 MSI。

用法：python build_setup.py <version> [publish_dir]
  例：python build_setup.py 0.1.82
产物：installer/NekoSetup-<version>.msi
依赖：wix.exe 4.0.5（dotnet tool install --global wix --version 4.0.5）

设计约定（v0.1.82 起，见 docs/dev-log.md 附录「2026-09-11 项目审计」批次 B P1-4）：
  · 版本号 **必填**，且必须与 `src/DesktopPet.App/DesktopPet.App.csproj` 的 `<Version>`
    **完全一致** —— 否则直接报错退出。这是史上 v0.1.42 / v0.1.53 版本漂移的根因防线
    （旧写法只把版本当字符串用，打出的包名与实际程序集版本可以不一致，且无人察觉）。
  · 对应的 `publish/v<version>/` 目录必须已存在且含 `Neko.exe`；缺失时给出可执行的
    `dotnet publish` 提示，而不是打出一个半成品包。
  · MSI **不随版本自动生成**（灯灯定的约定）：发版时手动执行本脚本。
"""
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))   # 项目根
INSTALLER = os.path.join(ROOT, "installer")
CSPROJ = os.path.join(ROOT, "src", "DesktopPet.App", "DesktopPet.App.csproj")

# <Version>0.1.82</Version>（容忍前后空白，与 tools/check_version.py 口径一致）
_VERSION_RE = re.compile(r"<Version>\s*([^<\s]+)\s*</Version>")


def read_csproj_version() -> str:
    """读取 csproj 中的 <Version>（单一事实来源）。缺失/空则抛 ValueError。"""
    if not os.path.isfile(CSPROJ):
        raise FileNotFoundError(f"找不到 csproj：{CSPROJ}")
    with open(CSPROJ, "r", encoding="utf-8-sig") as f:
        text = f.read()
    match = _VERSION_RE.search(text)
    if not match:
        raise ValueError(f"在 {CSPROJ} 中找不到 <Version> 元素")
    version = match.group(1).strip()
    if not version:
        raise ValueError(f"{CSPROJ} 的 <Version> 为空")
    return version


def normalize(version: str) -> str:
    """去掉可能的前缀 v/V，便于比较。"""
    return version[1:] if version[:1] in ("v", "V") else version


def err(message: str) -> int:
    """统一错误出口：打印醒目错误并返回退出码 1。"""
    print(f"[build_setup] 错误：{message}", file=sys.stderr)
    return 1


def main() -> int:
    # ---- 1. 参数：版本号必填 ----
    if len(sys.argv) < 2 or not sys.argv[1].strip():
        print("用法: python build_setup.py <version> [publish_dir]", file=sys.stderr)
        print("  <version> 必填，且必须与 src/DesktopPet.App/DesktopPet.App.csproj 的", file=sys.stderr)
        print("  <Version> 完全一致（例：python build_setup.py 0.1.82）。", file=sys.stderr)
        return 1

    version = normalize(sys.argv[1].strip())

    # ---- 2. 版本一致性强校验（防漂移）----
    try:
        csproj_version = normalize(read_csproj_version())
    except (FileNotFoundError, ValueError) as exc:
        return err(f"无法读取 csproj 版本号：{exc}")

    if version != csproj_version:
        return err(
            "版本号不一致，已中止出包。\n"
            f"        传入版本        : {version}\n"
            f"        csproj <Version>: {csproj_version}\n"
            "        请先同步 src/DesktopPet.App/DesktopPet.App.csproj 的 <Version>，"
            "再传入相同版本出包。\n"
            "        （历史上 v0.1.42 / v0.1.53 的安装器漂移即由此而来，故此校验为硬性拦截）"
        )

    # ---- 3. 发布目录校验 ----
    pub = (os.path.abspath(sys.argv[2]) if len(sys.argv) > 2
           else os.path.join(ROOT, "publish", f"v{version}"))
    if not os.path.isdir(pub):
        return err(
            f"发布目录不存在：{pub}\n"
            f"        请先为 v{version} 生成发布产物，例如：\n"
            f"        dotnet publish src/DesktopPet.App/DesktopPet.App.csproj -c Release -o publish/v{version}"
        )
    if not os.path.isfile(os.path.join(pub, "Neko.exe")):
        return err(
            f"发布目录缺少 Neko.exe：{pub}\n"
            "        该目录可能未完成发布或被清理，请重新执行：\n"
            f"        dotnet publish src/DesktopPet.App/DesktopPet.App.csproj -c Release -o publish/v{version}"
        )

    # ---- 4. 生成 self-contained wxs（WiX 4 结构：StandardDirectory/Component/Feature 全在 Package 内！）----
    gen = os.path.join(INSTALLER, "gen_setup_wxs.py")
    wxs = os.path.join(INSTALLER, f"NekoSetup-{version}.wxs")
    r = subprocess.run([sys.executable, gen, pub, version, wxs], cwd=ROOT)
    if r.returncode != 0:
        return err(f"生成 .wxs 失败（gen_setup_wxs.py 退出码 {r.returncode}）")

    # ---- 5. wix build ----
    msi = os.path.join(INSTALLER, f"NekoSetup-{version}.msi")
    wix = os.path.join(os.path.expanduser("~"), ".dotnet", "tools", "wix.exe")
    if not os.path.isfile(wix):
        return err(
            f"找不到 WiX 编译器：{wix}\n"
            "        请先安装：dotnet tool install --global wix --version 4.0.5"
        )
    cmd = [wix, "build", wxs, "-o", msi, "-d", f"SrcDir={pub}\\"]
    print("编译中（170MB 压缩需约 1 分钟）...")
    r = subprocess.run(cmd, cwd=ROOT)
    if r.returncode != 0:
        return err(f"WiX 编译失败（wix.exe 退出码 {r.returncode}）")

    size_mb = os.path.getsize(msi) / 1024 / 1024
    print(f"OK: {msi} ({size_mb:.1f} MB)")
    print(f"安装: msiexec /i \"{msi}\"")
    print(f"卸载: msiexec /x \"{msi}\"")
    return 0


if __name__ == "__main__":
    sys.exit(main())
