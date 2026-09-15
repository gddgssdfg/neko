#!/usr/bin/env python3
"""生成 WiX 4 完整安装源（self-contained NekoSetup.wxs）—— 静态骨架 + 发布目录文件清单。
heat 在 WiX 7 被移除、WiX 4 build 只收单源，所以一步到位生成单个 .wxs。
用法：python gen_setup_wxs.py <publish_dir> <version> <output_wxs>
"""
import os
import sys
import uuid

NS = uuid.UUID("a1b2c3d4-e5f6-7890-1234-567890abcdef")

# ── v0.1.78 P0-1：打包排除规则（显式常量集合，便于维护）─────────────────────────
# 背景：旧实现只排除 .pdb，导致 publish/<ver>/ 下「开发机的运行期数据」被当成 MSI 自有组件打进安装包：
#   - data/**（config.json、host/activity.json、plugins/ 私有数据）
#       → ① 泄露开发机个人数据 ② 安装/升级覆盖用户自己的配置 ③ 卸载残留、与用户数据打架
#   - neko-assets/ 下的开发噪声（_icon_*、*preview*、reference/**、*.bak-*）→ 白送体积
# ⚠️ 打包器不读 .gitignore，必须在此显式排除——否则 data/ 会被静默打进包（本规则即唯一防线）。
EXCLUDE_TOP_DIRS = {"data"}            # publish 根下的整棵目录（开发机运行期数据）
EXCLUDE_DIR_NAMES = {"reference"}      # 任意层级的目录名（开发参考图，非运行时素材）
EXCLUDE_EXACT_FILES = {".env"}         # 精确文件名（密钥，绝不入包）
EXCLUDE_SUFFIXES = (".pdb",)           # 文件后缀（保留原有 .pdb 排除）
EXCLUDE_PREFIXES = ("_icon_",)         # 文件名前缀（拼图标中间产物）
EXCLUDE_CONTAINS = ("preview", ".bak-")  # 文件名含片段（*_preview* / preview.png / *.bak-* 变体）


def is_excluded(rel: str) -> bool:
    """判断相对路径（/ 分隔）是否应从安装包排除；True = 不打包。"""
    parts = rel.split("/")
    name = parts[-1]
    low = name.lower()
    if parts[0] in EXCLUDE_TOP_DIRS:                               # data/**（整棵）
        return True
    if any(p in EXCLUDE_DIR_NAMES for p in parts[:-1]):            # 任意层级目录名命中
        return True
    if name in EXCLUDE_EXACT_FILES:                                # .env
        return True
    if low.endswith(tuple(s.lower() for s in EXCLUDE_SUFFIXES)):   # .pdb
        return True
    if low.startswith(tuple(p.lower() for p in EXCLUDE_PREFIXES)):  # _icon_*
        return True
    if any(frag.lower() in low for frag in EXCLUDE_CONTAINS):      # *preview* / *.bak-*
        return True
    return False


def main():
    src = os.path.abspath(sys.argv[1])
    version = sys.argv[2]
    out = os.path.abspath(sys.argv[3])

    rels = []
    excluded = 0
    for root, dirs, files in os.walk(src):
        for f in sorted(files):
            rel = os.path.relpath(os.path.join(root, f), src).replace("\\", "/")
            if is_excluded(rel):
                excluded += 1
                continue
            rels.append(rel)

    def d_id(path):
        return "Dir_" + "".join(c if c.isalnum() else "_" for c in path)

    L = []
    L.append('<?xml version="1.0" encoding="utf-8"?>')
    L.append('<!-- Neko 桌宠安装包（WiX 4，自动生成：python gen_setup_wxs.py <publish_dir> <version> <out.wxs>） -->')
    L.append('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
    L.append(f'  <Package Name="Neko 桌宠" Manufacturer="灯灯" Version="{version}"')
    L.append('           UpgradeCode="A1B2C3D4-E5F6-7890-1234-567890ABCDEF" Scope="perUser">')
    L.append('    <MajorUpgrade DowngradeErrorMessage="已安装更高版本，无法降级。" />')
    L.append('    <MediaTemplate EmbedCab="yes" CompressionLevel="high" />')
    L.append('')
    L.append('    <!-- 注意：WiX 4 的 StandardDirectory/Component/Feature 必须放 Package 内（不是 Fragment！） -->')
    L.append('    <!-- 目录布局：%LOCALAPPDATA%\\Neko（文件主体）+ 桌面 + 开始菜单 -->')
    L.append('    <StandardDirectory Id="LocalAppDataFolder">')
    L.append('      <Directory Id="INSTALLFOLDER" Name="Neko" />')
    L.append('    </StandardDirectory>')
    L.append('    <StandardDirectory Id="DesktopFolder" />')
    L.append('    <StandardDirectory Id="ProgramMenuFolder">')
    L.append('      <Directory Id="NekoProgramMenu" Name="Neko 桌宠" />')
    L.append('    </StandardDirectory>')

    # 文件目录树（挂 INSTALLFOLDER 下）
    def build(path):
        dirs, files = {}, []
        for rel in rels:
            if not rel.startswith(path):
                continue
            rest = rel[len(path):].lstrip("/")
            if "/" in rest:
                sub = rest.split("/", 1)[0]
                dirs.setdefault(sub, f"{path}/{sub}" if path else sub)
            else:
                files.append(rel)
        return dirs, files

    L.append('')
    L.append('    <DirectoryRef Id="INSTALLFOLDER">')
    def emit_dirs(path, indent):
        dirs, _ = build(path)
        for name, subpath in sorted(dirs.items()):
            L.append(f"{' ' * indent}<Directory Id=\"{d_id(subpath)}\" Name=\"{name}\">")
            emit_dirs(subpath, indent + 2)
            L.append(f"{' ' * indent}</Directory>")
    emit_dirs("", 6)
    L.append('    </DirectoryRef>')

    # 快捷方式 / 自启组件
    L.append('')
    L.append('    <!-- 桌面快捷方式 -->')
    L.append('    <Component Id="CmpDesktopShortcut" Directory="DesktopFolder" Guid="A1B2C3D4-E5F6-7890-1234-567890ABD001">')
    L.append('      <Shortcut Id="DesktopShortcut" Name="Neko 桌宠" Description="Neko 桌宠"')
    L.append('                Target="[INSTALLFOLDER]Neko.exe" WorkingDirectory="INSTALLFOLDER" />')
    L.append('      <RemoveFolder Id="RemoveDesktopShortcut" Directory="DesktopFolder" On="uninstall" />')
    L.append('      <RegistryValue Root="HKCU" Key="Software\\Neko" Name="DesktopShortcut" Type="integer" Value="1" KeyPath="yes" />')
    L.append('    </Component>')
    L.append('    <!-- 开始菜单快捷方式 -->')
    L.append('    <Component Id="CmpStartMenuShortcut" Directory="NekoProgramMenu" Guid="A1B2C3D4-E5F6-7890-1234-567890ABD002">')
    L.append('      <Shortcut Id="StartMenuShortcut" Name="Neko 桌宠" Description="Neko 桌宠"')
    L.append('                Target="[INSTALLFOLDER]Neko.exe" WorkingDirectory="INSTALLFOLDER" />')
    L.append('      <RemoveFolder Id="RemoveStartMenuDir" Directory="NekoProgramMenu" On="uninstall" />')
    L.append('      <RegistryValue Root="HKCU" Key="Software\\Neko" Name="StartMenuShortcut" Type="integer" Value="1" KeyPath="yes" />')
    L.append('    </Component>')
    L.append('    <!-- 开机自启（默认不装，Feature Level=2） -->')
    L.append('    <Component Id="CmpAutoStart" Directory="INSTALLFOLDER" Guid="A1B2C3D4-E5F6-7890-1234-567890ABD003">')
    L.append('      <RegistryValue Root="HKCU" Key="Software\\Microsoft\\Windows\\CurrentVersion\\Run"')
    L.append('                     Name="NekoDeskPet" Value="&quot;[INSTALLFOLDER]Neko.exe&quot;" Type="string" KeyPath="yes" />')
    L.append('    </Component>')

    # 文件组件
    L.append('')
    L.append('    <ComponentGroup Id="AppFiles">')
    for rel in rels:
        parts = rel.split("/")
        parent = "INSTALLFOLDER" if len(parts) == 1 else d_id("/".join(parts[:-1]))
        cid = "Cmp_" + "".join(c if c.isalnum() else "_" for c in rel)
        guid = str(uuid.uuid5(NS, rel)).upper()
        L.append(f'      <Component Id="{cid}" Directory="{parent}" Guid="{guid}">')
        L.append(f'        <File Source="$(var.SrcDir){rel}" KeyPath="yes" />')
        L.append('      </Component>')
    L.append('    </ComponentGroup>')

    # Feature
    L.append('')
    L.append('    <Feature Id="MainFeature" Title="Neko 桌宠" Description="程序文件 + 桌面/开始菜单快捷方式" Level="1">')
    L.append('      <ComponentGroupRef Id="AppFiles" />')
    L.append('      <ComponentRef Id="CmpDesktopShortcut" />')
    L.append('      <ComponentRef Id="CmpStartMenuShortcut" />')
    L.append('    </Feature>')
    L.append('    <Feature Id="AutoStartFeature" Title="开机自启动" Description="登录 Windows 时自动运行 Neko" Level="2">')
    L.append('      <ComponentRef Id="CmpAutoStart" />')
    L.append('    </Feature>')
    L.append('  </Package>')
    L.append('</Wix>')

    with open(out, "w", encoding="utf-8") as f:
        f.write("\n".join(L) + "\n")
    print(f"OK: {len(rels)} files -> {out}（已排除 {excluded} 个 data/噪声文件）")


if __name__ == "__main__":
    main()
