#!/usr/bin/env python3
"""Neko 桌宠一键出包：生成 WiX 4 源 → 编译 MSI。
用法：python build_setup.py <version> [publish_dir]
  例：python build_setup.py 0.1.45
产物：installer/NekoSetup-<version>.msi
依赖：wix.exe 4.0.5（dotnet tool install --global wix --version 4.0.5）
"""
import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))   # 项目根
INSTALLER = os.path.join(ROOT, "installer")


def main():
    if len(sys.argv) < 2:
        print("用法: python build_setup.py <version> [publish_dir]")
        return 1
    version = sys.argv[1]
    pub = os.path.abspath(sys.argv[2]) if len(sys.argv) > 2 else os.path.join(ROOT, "publish", f"v{version}")
    if not os.path.exists(os.path.join(pub, "Neko.exe")):
        print(f"发布目录不存在或缺少 Neko.exe: {pub}")
        return 1

    # 1) 生成 self-contained wxs（WiX 4 结构：StandardDirectory/Component/Feature 全在 Package 内！）
    gen = os.path.join(INSTALLER, "gen_setup_wxs.py")
    wxs = os.path.join(INSTALLER, f"NekoSetup-{version}.wxs")
    r = subprocess.run([sys.executable, gen, pub, version, wxs], cwd=ROOT)
    if r.returncode != 0:
        return 1

    # 2) wix build
    msi = os.path.join(INSTALLER, f"NekoSetup-{version}.msi")
    wix = os.path.join(os.path.expanduser("~"), ".dotnet", "tools", "wix.exe")
    cmd = [wix, "build", wxs, "-o", msi, "-d", f"SrcDir={pub}\\"]
    print("编译中（170MB 压缩需约 1 分钟）...")
    r = subprocess.run(cmd, cwd=ROOT)
    if r.returncode != 0:
        return 1

    size_mb = os.path.getsize(msi) / 1024 / 1024
    print(f"OK: {msi} ({size_mb:.1f} MB)")
    print(f"安装: msiexec /i \"{msi}\"")
    print(f"卸载: msiexec /x \"{msi}\"")
    return 0


if __name__ == "__main__":
    sys.exit(main())
