#!/usr/bin/env python3
"""Neko 本地一键 CI 闸门（无 GitHub Actions，纯本地可跑）。

背景
----
审计 docs/project-audit-2026-09-11.md 第 12 条「无 CI / 无自动化触发」：
回归只能靠人肉 `dotnet run`，是「无留痕回退」的温床。本脚本把发版前必须过的
闸门固化成一条命令，灯灯和后续 Agent 只需：

    python tools/ci.py

闸门（任一步失败立即中止，并打印清晰原因 + 对应日志路径）
--------------------------------------------------------
  1. `dotnet build src/DesktopPet.sln -c Debug` —— 必须 0 警告 0 错误
  2. 单独构建三个测试工程（⚠️ 见下方「陈旧二进制陷阱」）：
       tests/LoaderTest、tests/UiProbe、tests/PluginSmoke
  3. 依次运行测试套件并按固定判据断言：
       · LoaderTest  — 末行 `=== 全部通过 ===` 且退出码 0
       · PluginSmoke — `加载 N/N 成功`（N==N，全量加载）+ `[ALC 未能回收] 0 个`
       · UiProbe     — `=== 全部通过 ===`（含超时兜底与「读到哨兵后主动收尾」）
  4. 打印 `git status --short`（仅提示未提交改动，非闸门）
  5. 结束打印汇总表（各步耗时 + 结果）

⚠️ 陈旧二进制陷阱（本项目真实踩过的坑）
--------------------------------------------------------
仓库根的 `src/DesktopPet.sln` **不包含 `tests/` 下的任何工程**。
如果只构建 sln 就直接运行 `tests/*/bin/**` 里的旧 exe，跑的会是**上一次**留下的
陈旧二进制，测试结果与当前源码无关（假绿 / 假红都可能）。因此本脚本第 2 步
**必须逐个单独构建** tests/LoaderTest、tests/UiProbe、tests/PluginSmoke。

⚠️ 为什么产物进 `bin_ci/`
--------------------------------------------------------
测试（尤其 UiProbe 会开真实 WPF 窗口）可能长期占用输出目录，把常规 `bin/` 锁住，
导致后续 `dotnet build` 报文件占用。故测试工程统一用
`-p:BaseOutputPath=bin_ci/` 隔离（相对路径按各自工程目录解析，互不冲突），
并在 .gitignore 里忽略 `bin_ci*/`。

⚠️ 为什么必须走包装脚本
--------------------------------------------------------
本机 shell 未导出 Windows 已知目录环境变量，直接 `dotnet` 会在 NuGet restore
阶段崩于 `Value cannot be null. (Parameter 'path1')`。故所有 dotnet 调用一律走
`bash /c/Users/Lantern/neko-dotnet-env.sh <args>`（可用环境变量
`NEKO_DOTNET_WRAPPER` 覆盖包装脚本路径）。

退出码
------
    0  全部闸门通过
    1  有闸门失败（已打印原因与日志路径）
"""

from __future__ import annotations

import os
import queue
import re
import shutil
import subprocess
import sys
import threading
import time
from dataclasses import dataclass, field
from pathlib import Path

# ---------------------------------------------------------------------------
# 路径与常量
# ---------------------------------------------------------------------------

REPO_ROOT = Path(__file__).resolve().parent.parent
DOTNET_WRAPPER = os.environ.get(
    "NEKO_DOTNET_WRAPPER", "/c/Users/Lantern/neko-dotnet-env.sh"
)

# 测试工程 → 输出 exe 名（AssemblyName）
TEST_PROJECTS = [
    ("LoaderTest", REPO_ROOT / "tests" / "LoaderTest" / "LoaderTest.csproj", "LoaderTest.exe"),
    ("UiProbe", REPO_ROOT / "tests" / "UiProbe" / "UiProbe.csproj", "UiProbe.exe"),
    ("PluginSmoke", REPO_ROOT / "tests" / "PluginSmoke" / "PluginSmoke.csproj", "PluginSmoke.exe"),
]

# 测试产物 / 日志根目录（均被 .gitignore 的 bin_ci*/ 覆盖）
CI_OUTPUT_DIR = "bin_ci/"          # BaseOutputPath（相对各工程目录）
LOG_DIR = REPO_ROOT / "bin_ci_logs"

# 各步超时（秒）：UiProbe / PluginSmoke 较重，给足余量
BUILD_TIMEOUT = 900
RUN_TIMEOUTS = {"LoaderTest": 300, "PluginSmoke": 900, "UiProbe": 900}
SENTINEL_GRACE = 20.0              # 读到哨兵后仍不退出，最多再等这么久就收尾

# MSBuild 汇总行随 dotnet 语言本地化：同时兼容英文与简体中文
#   英文：    0 Warning(s)   /   0 Error(s)
#   简体中文：0 个警告       /   0 个错误
_MSBUILD_WARN_RE = re.compile(r"(\d+)\s+(?:Warning\(s\)|个警告)")
_MSBUILD_ERR_RE = re.compile(r"(\d+)\s+(?:Error\(s\)|个错误)")
_PLUGIN_LOAD_RE = re.compile(r"加载\s+(\d+)\s*/\s*(\d+)\s+成功")
_ALC_LEAK_RE = re.compile(r"\[ALC 未能回收[^\]]*\]\s*(\d+)\s*个")


# ---------------------------------------------------------------------------
# 数据结构
# ---------------------------------------------------------------------------

@dataclass
class StepResult:
    """一步闸门的结果。"""

    name: str
    ok: bool
    duration: float = 0.0
    detail: str = ""
    log_path: Path | None = None
    skipped: bool = False


@dataclass
class RunResult:
    """一次子进程运行的原始结果。"""

    rc: int
    lines: list[str] = field(default_factory=list)
    duration: float = 0.0
    timed_out: bool = False
    log_path: Path | None = None

    @property
    def text(self) -> str:
        return "\n".join(self.lines)

    @property
    def tail(self, n: int = 8) -> str:  # noqa: D401 - convenience
        return "\n".join(self.lines[-n:])


# ---------------------------------------------------------------------------
# 子进程执行
# ---------------------------------------------------------------------------

def _popen_kwargs() -> dict:
    """Popen 公共参数：以**二进制**读取输出，逐行自行解码。

    为什么不直接用 text=True/utf-8：本机 Console 代码页为 GBK(936)，未被显式设为 UTF-8 的
    子进程（如 LoaderTest / UiProbe、以及 MSBuild 转发的 Python 脚本）会把中文按 GBK 写出，
    用 UTF-8 硬解会得到乱码，导致中文哨兵（如 `=== 全部通过 ===`）匹配失败。故此处读原始
    字节，逐行「先试 UTF-8、失败再退 GBK」。详见 _decode。
    """
    return dict(
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        bufsize=0,
    )


def _decode(raw: bytes) -> str:
    """字节行解码：优先 UTF-8，失败退回 GBK（兼容未设 UTF-8 的 Console 子进程）。"""
    try:
        return raw.decode("utf-8")
    except UnicodeDecodeError:
        return raw.decode("gbk", errors="replace")


def run_captured(
    cmd: list[str],
    log_path: Path,
    timeout: float,
    sentinel: str | None = None,
    grace: float = SENTINEL_GRACE,
    cwd: Path | None = None,
    echo: bool = False,
) -> RunResult:
    """运行命令并把 stdout/stderr 合并写入日志文件，带超时与哨兵兜底。

    - 输出通过后台线程读入队列，主循环据此同时看守「超时」与「哨兵后收尾」，
      避免子进程打印完后挂住不退出时 readline 永久阻塞。
    - `sentinel` 一旦在输出中出现，最多再等 `grace` 秒；仍不退出则主动终止，
      并以「哨兵已出现」为准交由判据决定成败（UiProbe 正是这种形态）。
    """
    log_path.parent.mkdir(parents=True, exist_ok=True)
    start = time.monotonic()
    proc = subprocess.Popen(cmd, cwd=str(cwd) if cwd else None, **_popen_kwargs())

    out_queue: "queue.Queue[bytes | None]" = queue.Queue()

    def _reader() -> None:
        assert proc.stdout is not None
        # 用 readline() 逐行读（而非 for-iterate，后者有预读缓冲会阻塞流式输出）
        while True:
            raw = proc.stdout.readline()
            if not raw:
                break
            out_queue.put(raw)
        out_queue.put(None)

    reader = threading.Thread(target=_reader, daemon=True)
    reader.start()

    lines: list[str] = []
    deadline = start + timeout
    sentinel_at: float | None = None
    timed_out = False

    with open(log_path, "w", encoding="utf-8", errors="replace") as logf:
        while True:
            try:
                item = out_queue.get(timeout=0.5)
            except queue.Empty:
                item = ""  # 醒来只为检查超时 / 哨兵宽限

            if item is None:
                break  # EOF：子进程输出结束

            if item:
                decoded = _decode(item)
                line = decoded.rstrip("\r\n")
                lines.append(line)
                logf.write(decoded)
                logf.flush()
                if echo:
                    print(line)
                if sentinel and sentinel_at is None and sentinel in line:
                    sentinel_at = time.monotonic()

            now = time.monotonic()
            # 哨兵已出现但进程迟迟不退出 → 主动收尾
            if sentinel_at is not None and now - sentinel_at >= grace:
                if proc.poll() is None:
                    proc.terminate()
                    try:
                        proc.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        proc.kill()
                break
            # 总超时兜底
            if now >= deadline:
                timed_out = True
                if proc.poll() is None:
                    proc.kill()
                break

    try:
        rc = proc.wait(timeout=10)
    except subprocess.TimeoutExpired:
        proc.kill()
        rc = proc.wait()

    return RunResult(
        rc=rc,
        lines=lines,
        duration=time.monotonic() - start,
        timed_out=timed_out,
        log_path=log_path,
    )


def dotnet(args: list[str]) -> list[str]:
    """把 dotnet 参数包装成经过 env 包装脚本的命令。"""
    return ["bash", DOTNET_WRAPPER, *args]


# ---------------------------------------------------------------------------
# 各步骤实现
# ---------------------------------------------------------------------------

def step_build_solution() -> StepResult:
    """步骤 1：构建解决方案，要求 0 警告 0 错误。"""
    name = "build sln (Debug, 0 warn/0 err)"
    log = LOG_DIR / "01_build_sln.log"
    cmd = dotnet(["build", "src/DesktopPet.sln", "-c", "Debug"])
    r = run_captured(cmd, log, timeout=BUILD_TIMEOUT, echo=True)

    warns = _last_int(_MSBUILD_WARN_RE, r.lines)
    errs = _last_int(_MSBUILD_ERR_RE, r.lines)

    if r.timed_out:
        return StepResult(name, False, r.duration, f"构建超时（>{BUILD_TIMEOUT}s）", log)
    if r.rc != 0:
        return StepResult(name, False, r.duration, f"构建失败（退出码 {r.rc}）", log)
    if warns is None or errs is None:
        return StepResult(name, False, r.duration,
                          "未能从构建输出解析警告/错误计数（输出格式异常）", log)
    if errs != 0:
        return StepResult(name, False, r.duration, f"存在 {errs} 个编译错误", log)
    if warns != 0:
        return StepResult(name, False, r.duration, f"存在 {warns} 个编译警告（要求 0 警告）", log)
    return StepResult(name, True, r.duration, "0 警告 0 错误", log)


def step_build_tests() -> StepResult:
    """步骤 2：逐个单独构建三个测试工程（sln 不含 tests/，必须单独构建）。"""
    name = "build tests (LoaderTest/UiProbe/PluginSmoke)"
    duration = 0.0
    for label, csproj, _exe in TEST_PROJECTS:
        log = LOG_DIR / f"02_build_{label}.log"
        cmd = dotnet(["build", str(csproj), "-c", "Debug", f"-p:BaseOutputPath={CI_OUTPUT_DIR}"])
        r = run_captured(cmd, log, timeout=BUILD_TIMEOUT, echo=True)
        duration += r.duration
        if r.timed_out:
            return StepResult(name, False, duration, f"构建 {label} 超时", log)
        if r.rc != 0:
            return StepResult(name, False, duration, f"构建 {label} 失败（退出码 {r.rc}）", log)
    return StepResult(name, True, duration, "3 个测试工程均构建成功", None)


def _find_test_exe(label: str, exe_name: str) -> Path | None:
    """在测试工程的 bin_ci/ 下定位构建出的 exe（兼容 RID 子目录）。"""
    proj_dir = REPO_ROOT / "tests" / label
    root = proj_dir / "bin_ci"
    if not root.is_dir():
        return None
    matches = [p for p in root.rglob(exe_name) if p.is_file()]
    if not matches:
        return None
    # 多个候选时取最近修改的
    matches.sort(key=lambda p: p.stat().st_mtime, reverse=True)
    return matches[0]


def step_run_loadertest() -> StepResult:
    """步骤 3a：LoaderTest —— 末行 `=== 全部通过 ===` 且退出码 0。"""
    name = "run LoaderTest"
    log = LOG_DIR / "03_run_LoaderTest.log"
    exe = _find_test_exe("LoaderTest", "LoaderTest.exe")
    if exe is None:
        return StepResult(name, False, 0.0, "未找到构建产物 LoaderTest.exe（bin_ci/）", log)
    r = run_captured([str(exe)], log, timeout=RUN_TIMEOUTS["LoaderTest"], echo=True)

    last = _last_nonempty(r.lines)
    if r.timed_out:
        return StepResult(name, False, r.duration, "运行超时", log)
    if last != "=== 全部通过 ===":
        return StepResult(name, False, r.duration, f"末行非『全部通过』：{last!r}", log)
    if r.rc != 0:
        return StepResult(name, False, r.duration, f"末行为全部通过但退出码 {r.rc}", log)
    return StepResult(name, True, r.duration, "全部通过（exit 0）", log)


def step_run_pluginsmoke() -> StepResult:
    """步骤 3b：PluginSmoke —— `加载 N/N 成功` 且 `[ALC 未能回收] 0 个`。"""
    name = "run PluginSmoke"
    log = LOG_DIR / "03_run_PluginSmoke.log"
    exe = _find_test_exe("PluginSmoke", "PluginSmoke.exe")
    if exe is None:
        return StepResult(name, False, 0.0, "未找到构建产物 PluginSmoke.exe（bin_ci/）", log)
    # 显式传入仓库根，免依赖 BaseDirectory 上溯
    r = run_captured([str(exe), str(REPO_ROOT)], log,
                     timeout=RUN_TIMEOUTS["PluginSmoke"],
                     sentinel="结论：PASS", echo=True)

    if r.timed_out:
        return StepResult(name, False, r.duration, "运行超时", log)

    m = _PLUGIN_LOAD_RE.search(r.text)
    if not m:
        return StepResult(name, False, r.duration, "未找到『加载 N/M 成功』汇总行", log)
    loaded, total = int(m.group(1)), int(m.group(2))
    if total == 0 or loaded != total:
        return StepResult(name, False, r.duration,
                          f"插件未全量加载：{loaded}/{total} 成功", log)

    leak_m = _ALC_LEAK_RE.search(r.text)
    if not leak_m:
        return StepResult(name, False, r.duration, "未找到『ALC 未能回收』统计行", log)
    leak = int(leak_m.group(1))
    if leak != 0:
        return StepResult(name, False, r.duration, f"有 {leak} 个插件 ALC 未能回收（要求 0）", log)

    if r.rc != 0:
        return StepResult(name, False, r.duration, f"退出码 {r.rc}（期望 0）", log)
    return StepResult(name, True, r.duration, f"{loaded}/{total} 加载成功，ALC 未回收 0 个", log)


def step_run_uiprobe() -> StepResult:
    """步骤 3c：UiProbe —— `=== 全部通过 ===`（含哨兵超时兜底）。"""
    name = "run UiProbe"
    log = LOG_DIR / "03_run_UiProbe.log"
    exe = _find_test_exe("UiProbe", "UiProbe.exe")
    if exe is None:
        return StepResult(name, False, 0.0, "未找到构建产物 UiProbe.exe（bin_ci/）", log)
    r = run_captured([str(exe)], log, timeout=RUN_TIMEOUTS["UiProbe"],
                     sentinel="=== 全部通过 ===", echo=True)

    if _has_failed_sentinel(r.lines):
        return StepResult(name, False, r.duration, "存在失败项（输出『N 项失败』）", log)
    if "=== 全部通过 ===" not in r.text:
        if r.timed_out:
            return StepResult(name, False, r.duration, "运行超时且未打印『全部通过』", log)
        return StepResult(name, False, r.duration, f"未打印『全部通过』（退出码 {r.rc}）", log)

    note = "全部通过"
    if r.timed_out:
        note += "（总超时兜底）"
    elif r.rc not in (0, -15, -9):
        return StepResult(name, False, r.duration, f"已打印全部通过但退出码 {r.rc}", log)
    else:
        note += "（读到哨兵后已确认收尾）"
    return StepResult(name, True, r.duration, note, log)


def step_git_status() -> StepResult:
    """步骤 4：打印 git status --short（信息项，非闸门）。"""
    name = "git status --short"
    log = LOG_DIR / "04_git_status.log"
    r = run_captured(["git", "status", "--short"], log, timeout=30)
    if r.rc != 0:
        return StepResult(name, True, r.duration, "（git 不可用，跳过）", log)
    body = "\n".join(r.lines).strip()
    detail = "工作区干净" if not body else f"{len([l for l in r.lines if l.strip()])} 项未提交改动"
    print("\n---- git status --short ----")
    print(body if body else "(干净)")
    print("----------------------------")
    return StepResult(name, True, r.duration, detail, log)


# ---------------------------------------------------------------------------
# 小工具
# ---------------------------------------------------------------------------

def _last_int(regex: re.Pattern[str], lines: list[str]) -> int | None:
    """取最后一次匹配到的整数（MSBuild 汇总行在末尾）。"""
    found: int | None = None
    for line in lines:
        m = regex.search(line)
        if m:
            found = int(m.group(1))
    return found


def _last_nonempty(lines: list[str]) -> str:
    for line in reversed(lines):
        if line.strip():
            return line.strip()
    return ""


def _has_failed_sentinel(lines: list[str]) -> bool:
    return any(re.search(r"===\s*\d+\s*项失败\s*===", ln) for ln in lines)


def _print_summary(results: list[StepResult]) -> None:
    """打印汇总表（含跳过项）。"""
    print("\n" + "=" * 74)
    print(" Neko 本地 CI 汇总")
    print("=" * 74)
    width = max(len(r.name) for r in results)
    for r in results:
        if r.skipped:
            status = "SKIP"
        else:
            status = "PASS" if r.ok else "FAIL"
        print(f" [{status}] {r.name:<{width}}  {r.duration:6.1f}s  {r.detail}")
    print("=" * 74)
    if any(r.log_path for r in results):
        print(f" 日志目录：{LOG_DIR}")
    ok = all(r.ok or r.skipped for r in results)
    print(" 结论：" + ("全部闸门通过 ✅" if ok else "存在失败闸门 ❌"))
    print("=" * 74)


def _fail_banner(step: StepResult) -> None:
    print("\n" + "!" * 74)
    print(f" 闸门失败：{step.name}")
    print(f" 原因    ：{step.detail}")
    if step.log_path:
        print(f" 日志    ：{step.log_path}")
    print("!" * 74)


# ---------------------------------------------------------------------------
# 主流程
# ---------------------------------------------------------------------------

def main() -> int:
    print("Neko 本地 CI 开始")
    print(f"  仓库根        : {REPO_ROOT}")
    print(f"  dotnet 包装器 : {DOTNET_WRAPPER}")

    if shutil.which("bash") is None:
        print("[环境错误] 找不到 bash（dotnet 包装脚本需要它）。", file=sys.stderr)
        return 1

    LOG_DIR.mkdir(parents=True, exist_ok=True)

    steps = [
        step_build_solution,
        step_build_tests,
        step_run_loadertest,
        step_run_pluginsmoke,
        step_run_uiprobe,
        step_git_status,
    ]

    results: list[StepResult] = []
    failed = False
    for fn in steps:
        r = fn()
        results.append(r)
        if not r.ok:
            _fail_banner(r)
            failed = True
            break

    if failed:
        # 未执行到的步骤标记为跳过
        done = len(results)
        for fn in steps[done:]:
            # 用函数名推导一个可读标签（无需执行）
            results.append(StepResult(fn.__doc__.split("：", 1)[-1].split("。")[0]
                                      if fn.__doc__ else fn.__name__,
                                      ok=False, skipped=True, detail="前序闸门失败，未执行"))

    _print_summary(results)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
