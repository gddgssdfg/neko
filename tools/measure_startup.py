#!/usr/bin/env python3
"""Neko 冷启动耗时量尺（v0.2.3）。

背景
----
项目的红线之一是「**冷启动 < 2.0s**」，但此前**没有量尺** —— 只能靠感觉说"挺快的"。
本脚本把它变成可复现的数字：**从 CreateProcess 到宠物窗口真正出现**的墙钟时间。

为什么量"窗口出现"而不是别的
----------------------------
- 量"进程启动"太浅：单文件自包含 + WPF 的耗时大头在**运行时装载 / JIT(R2R) / WPF 初始化 / 插件加载**，
  这些都在窗口出现**之前**发生；
- 量"某个日志行"也不准：日志级别默认 warn，Info 行不落盘，且写日志的时刻早于窗口渲染完成；
- 窗口出现 = 用户真正感知到的"猫出来了"，是与红线口径一致的那个点。

用法
----
    python tools/measure_startup.py --exe publish/_a2/baseline/Neko.exe --runs 5
    python tools/measure_startup.py --exe <任意 Neko.exe> --runs 7 --warmup 1

要点 / 坑
--------
- **必须隔离数据根**：默认用临时目录作 `NEKO_DATA_DIR`，绝不碰用户的真实 `%AppData%\\Neko`
  （否则要么污染用户数据，要么被"首次启动引导窗"影响测量）。
- 传 `--dev` 跳过首启引导 —— 否则量到的第一个窗口是**引导窗**而不是宠物窗。
- 每轮跑完**必须杀掉进程**，否则托盘图标与窗口会堆积，后续轮次的窗口枚举会认错目标。
- 先跑一次**预热**（默认 1 次，不计入结果）：首次运行的冷文件缓存会显著偏慢。
- 窗口枚举用 user32 的 `EnumWindows` + `GetWindowThreadProcessId` + `IsWindowVisible`，
  按 PID 精确匹配（而不是按标题猜）。

退出码
------
    0  测量成功
    2  环境问题（exe 不存在 / 一直没出现窗口）
"""

from __future__ import annotations

import argparse
import ctypes
import ctypes.wintypes as wintypes
import os
import statistics
import subprocess
import sys
import tempfile
import time
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent

_user32 = ctypes.WinDLL("user32", use_last_error=True)

WNDENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

#: 轮询间隔（秒）。窗口出现的判定精度不会比它更细。
POLL_INTERVAL = 0.01


def _visible_windows_of(pid: int) -> list[tuple[int, str, tuple[int, int, int, int]]]:
    """返回该 PID 的可见顶层窗口 (hwnd, title, (l,t,r,b))。"""
    found: list[tuple[int, str, tuple[int, int, int, int]]] = []

    def cb(hwnd, _lparam):
        wpid = wintypes.DWORD()
        _user32.GetWindowThreadProcessId(hwnd, ctypes.byref(wpid))
        if wpid.value != pid:
            return True
        if not _user32.IsWindowVisible(hwnd):
            return True
        length = _user32.GetWindowTextLengthW(hwnd)
        buf = ctypes.create_unicode_buffer(length + 1)
        _user32.GetWindowTextW(hwnd, buf, length + 1)
        rect = wintypes.RECT()
        _user32.GetWindowRect(hwnd, ctypes.byref(rect))
        found.append((hwnd, buf.value, (rect.left, rect.top, rect.right, rect.bottom)))
        return True

    _user32.EnumWindows(WNDENUMPROC(cb), 0)
    return found


def _measure_once(exe: Path, data_dir: Path, timeout: float, extra_args: list[str]) -> tuple[float, str]:
    """跑一次，返回 (到窗口出现的秒数, 窗口描述)。超时抛 TimeoutError。"""
    env = dict(os.environ)
    env["NEKO_DATA_DIR"] = str(data_dir)          # 隔离：不碰真实数据根
    env.setdefault("APPDATA", os.path.expandvars(r"%APPDATA%"))

    t0 = time.perf_counter()
    proc = subprocess.Popen(
        [str(exe), *extra_args],
        cwd=str(exe.parent),                       # 单文件自包含：工作目录无所谓，但与真机一致更稳
        env=env,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    try:
        deadline = t0 + timeout
        while time.perf_counter() < deadline:
            if proc.poll() is not None:
                raise RuntimeError(f"进程在窗口出现前就退出了（exit={proc.returncode}）")
            wins = _visible_windows_of(proc.pid)
            if wins:
                elapsed = time.perf_counter() - t0
                # 优先报"像宠物窗"的那个（有尺寸的），否则报第一个
                hwnd, title, rect = max(wins, key=lambda w: (w[2][2] - w[2][0]) * (w[2][3] - w[2][1]))
                w, h = rect[2] - rect[0], rect[3] - rect[1]
                return elapsed, f"0x{hwnd:X} {w}x{h} '{title or '(无标题)'}'"
            time.sleep(POLL_INTERVAL)
        raise TimeoutError(f"{timeout}s 内没出现任何可见窗口")
    finally:
        # 必须杀干净：否则托盘图标/窗口堆积，污染后续轮次
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait(timeout=5)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", required=True, help="要测量的 Neko.exe 路径")
    ap.add_argument("--runs", type=int, default=5, help="计入结果的轮数（默认 5）")
    ap.add_argument("--warmup", type=int, default=1, help="预热轮数，不计入结果（默认 1）")
    ap.add_argument("--timeout", type=float, default=30.0, help="单轮超时秒数")
    ap.add_argument("--args", default="--dev", help="传给 Neko 的额外参数（默认 --dev 跳过首启引导）")
    args = ap.parse_args()

    exe = Path(args.exe)
    if not exe.is_absolute():
        exe = (REPO_ROOT / exe).resolve()
    if not exe.is_file():
        print(f"[环境错误] 找不到 exe：{exe}", file=sys.stderr)
        return 2

    extra = args.args.split() if args.args else []
    # 数据根隔离到临时目录（用绝对路径；同一次测量的所有轮次共用一个，模拟"非首次安装"的稳态）
    scratch = Path(tempfile.mkdtemp(prefix="neko-startup-"))
    print(f"测量对象 : {exe}")
    print(f"  大小   : {exe.stat().st_size / 1048576:.1f} MB")
    print(f"  数据根 : {scratch}（隔离，不碰真实 %AppData%\\Neko）")
    print(f"  参数   : {' '.join(extra) or '(无)'}  预热 {args.warmup} 轮 + 计分 {args.runs} 轮")

    # 预热时**不删数据根**：让"非首次启动"成为稳态条件（首启引导已被 --dev 跳过）
    for i in range(args.warmup):
        try:
            secs, desc = _measure_once(exe, scratch, args.timeout, extra)
            print(f"  [预热 {i + 1}/{args.warmup}] {secs:.3f}s  {desc}")
        except Exception as exc:                     # noqa: BLE001 - 预热失败也要能继续看到原因
            print(f"  [预热 {i + 1}/{args.warmup}] 失败：{exc}", file=sys.stderr)
            return 2

    times: list[float] = []
    for i in range(args.runs):
        try:
            secs, desc = _measure_once(exe, scratch, args.timeout, extra)
        except Exception as exc:                     # noqa: BLE001
            print(f"  [第 {i + 1} 轮] 失败：{exc}", file=sys.stderr)
            return 2
        times.append(secs)
        print(f"  [第 {i + 1}/{args.runs} 轮] {secs:.3f}s  {desc}")

    lo, hi = min(times), max(times)
    med = statistics.median(times)
    print("\n" + "=" * 58)
    print(f" 冷启动（进程创建 → 宠物窗口出现）")
    print(f"   最小 {lo:.3f}s   中位 {med:.3f}s   最大 {hi:.3f}s   均值 {statistics.fmean(times):.3f}s")
    print(f"   红线 < 2.000s ⇒ " + ("通过 ✅" if med < 2.0 else "未通过 ❌"))
    print("=" * 58)
    return 0


if __name__ == "__main__":
    sys.exit(main())
