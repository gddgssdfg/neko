"""Build reviewed Windows release artifacts. Never uploads or installs anything.

python tools/package_release.py [--publish-dir publish/v0.4.0] [--skip-build]
Requires .NET 8, Pillow for the existing asset build, and WiX 4.0.5.
"""
from __future__ import annotations
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path
from xml.etree import ElementTree

ROOT = Path(__file__).resolve().parents[1]
RUNTIME_VERSION = "8.0.32"
sys.path.insert(0, str(ROOT / "installer"))
from gen_setup_wxs import is_excluded


def version() -> str:
    return ElementTree.parse(ROOT / "src/DesktopPet.App/DesktopPet.App.csproj").findtext(".//Version") or ""


def allowed(relative: Path) -> bool:
    if is_excluded(relative.as_posix()):
        return False
    name = relative.name.lower()
    if any(part.lower() in {".git", "logs", "data", "reference"} for part in relative.parts):
        return False
    if name.startswith(".env") or relative.suffix.lower() in {".key", ".pem", ".pfx", ".p12", ".log", ".pdb"}:
        return False
    return name not in {"config.json", "portable.flag"}


def stage_runtime(source: Path, destination: Path) -> None:
    if destination.resolve().is_relative_to(source.resolve()):
        raise ValueError("Staging destination must be outside publish input")
    if not (source / "Neko.exe").is_file():
        raise ValueError("Publish output does not contain Neko.exe")
    destination.mkdir(parents=True, exist_ok=False)
    for path in source.rglob("*"):
        if path.is_symlink():
            raise ValueError(f"Symlink in publish output: {path.name}")
        relative = path.relative_to(source)
        if path.is_file() and allowed(relative):
            target = destination / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(path, target)
    for name in ("LICENSE", "LICENSE-ASSETS", "THIRD-PARTY-NOTICES.txt", "PRIVACY.md", "SECURITY.md"):
        shutil.copy2(ROOT / name, destination / name)


def build(skip_build: bool, source: Path | None, output: Path | None) -> Path:
    release_version = version()
    result = output or ROOT / "publish" / "release" / f"v{release_version}"
    if result.exists():
        raise FileExistsError(f"Use a new output directory; preserving existing artifacts: {result}")
    source = source or ROOT / "publish" / f"v{release_version}"
    if not skip_build:
        subprocess.run(["dotnet", "publish", str(ROOT / "src/DesktopPet.App/DesktopPet.App.csproj"),
                        "-c", "Release", "-r", "win-x64", "--self-contained", "true", f"-p:RuntimeFrameworkVersion={RUNTIME_VERSION}", "-o", str(source)], cwd=ROOT, check=True)
        commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
        dirty = bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT).strip())
        (source / "BUILD-INFO.json").write_text(json.dumps({"version": release_version, "runtime_version": RUNTIME_VERSION,
            "source_commit": commit, "working_tree_dirty": dirty}, indent=2), encoding="utf-8")
    metadata = json.loads((source / "BUILD-INFO.json").read_text(encoding="utf-8"))
    if metadata.get("runtime_version") != RUNTIME_VERSION or metadata.get("version") != release_version:
        raise ValueError("Prebuilt runtime does not match the pinned application/runtime version")
    runtime = result / "runtime"
    stage_runtime(source, runtime)
    licenses = runtime / "licenses"
    licenses.mkdir()
    nuget = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget" / "packages"))
    for name, package_version in [("microsoft.netcore.app.runtime.win-x64", RUNTIME_VERSION),
            ("microsoft.windowsdesktop.app.runtime.win-x64", RUNTIME_VERSION),
            ("system.drawing.common", "8.0.7"), ("system.security.cryptography.protecteddata", "8.0.0")]:
        package = nuget / name / package_version
        notices = [p for p in package.iterdir() if p.is_file() and ("LICENSE" in p.name.upper() or "NOTICE" in p.name.upper())]
        if not notices:
            raise ValueError(f"Missing runtime/license files for {name} {package_version}")
        for notice in notices:
            alias = {"microsoft.netcore.app.runtime.win-x64": "net8", "microsoft.windowsdesktop.app.runtime.win-x64": "wpf8",
                     "system.drawing.common": "drawing", "system.security.cryptography.protecteddata": "protecteddata"}[name]
            shutil.copy2(notice, licenses / f"{alias}-{package_version}-{notice.name}")
    artifacts = result / "artifacts"
    artifacts.mkdir()
    archive = artifacts / f"Neko-{release_version}-win-x64-portable.zip"
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as bundle:
        for path in sorted(runtime.rglob("*")):
            if path.is_file():
                bundle.write(path, path.relative_to(runtime).as_posix())
        bundle.writestr("portable.flag", "")
    subprocess.run([sys.executable, str(ROOT / "installer/build_setup.py"), release_version, str(runtime)], cwd=ROOT, check=True)
    shutil.copy2(ROOT / "installer" / f"NekoSetup-{release_version}.msi", artifacts)
    lines = []
    for path in sorted(artifacts.iterdir()):
        with path.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        lines.append(f"{digest}  {path.name}")
    (artifacts / "SHA256SUMS.txt").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"Artifacts ready: {artifacts}")
    return artifacts


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--skip-build", action="store_true")
    parser.add_argument("--publish-dir", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    build(args.skip_build, args.publish_dir, args.output)
