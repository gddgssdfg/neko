"""把 AI 生成的「绿幕 mp4」转成 Neko 皮肤构建管线认识的序列条 png。

背景
----
`build_skin_v2.py` 只认「序列条 png」（横向等分格、每格一帧、透明底），整条管线里
**没有任何 mp4 / ffmpeg 进口**。所以豆包新生成的一批 mp4 素材一个都接不进去。
本工具补上这一环：

    mp4  --ffmpeg 全量抽帧-->  原始帧  --周期感知取帧(见下)-->  挑中的帧  --自适应抠绿幕-->  RGBA  --归一化-->  序列条 png

其中「周期感知取帧」是 **v0.1.93 起**的关键改动：走路这类**周期性**动作绝不能再「把整段
均匀降采样成 8 帧」（那会把 5.04s 的整段动作压进 0.8s = 6.3× 速，"像在放动画"）。正确做法
是先估出**一个步态周期 P**，只在 ``[st, st+P)`` 内均匀取 8 帧 —— 循环无缝、速度自然。
详见 :func:`sample_indices`。无清晰周期的动作（pet/drag/fall）自动回退旧行为，且必须打印原因。

设计要点
--------
* **背景色自适应**：从每一段视频的边框像素里统计「出现频率最高的颜色」作为真实背景色，
  再做「距离键控」。绝不写死 ``#00FF00`` —— 实测同一批素材里 ``walk/pet`` 的背景是
  黄绿 ``(160,234,75)``，``drag/fall/climb`` 才是标准绿 ``(~40,245,30)``；固定阈值
  ``g>200 且 r<80 且 b<80`` 在黄绿上命中率为 0%。本工具对同一段视频内采样、逐视频自适应，
  因此天然同时兼容黄绿与标准绿。
* **只抠「与画布四边连通」的背景**（flood fill），而不是全局色阈。这样猫身上的
  奶白肚皮 / 白爪 / 绿眼等「非背景」区域绝不会被抠穿。
  （教训：全局容差 flood fill 会把奶白肚皮一起吃掉 —— 只有与边缘连通的绿才是背景。）
* **去毛边 / 去绿描边**：键控后对 alpha 做 1px 收缩 + 高斯羽化，并对边缘带做 despill
  （``g = min(g, max(r, b))``，只压绿、不动橙/白），消掉绿幕在抗锯齿边缘留下的绿色描边。
* **逐状态定标到静态图基准**：五段视频镜头远近不同（同一 512 口径下 walk 猫高 347、pet 367、
  drag 418、fall 251），故**每个状态一个系数**（该状态所有帧共用，避免猫「呼吸」）：
  把「中位数内容高」映射到 ≈399px、「中位数离底」映射到 ≈52px —— 与静态 idle(399/56)/
  sit(403/48.5) 对齐。这样 idle→walk 不会「长大 + 下移」。帧间相对起伏（颠簸）原样保留。
  产出仍是与旧 ``{prefix}_seq_*.png`` **完全同构**的等分格序列条 → ``build_skin_v2.py``
  的 ``segment_auto()`` 与缩放逻辑**一行都不用改**。

用法
----
单段（最常用）：:

    python tools/video_to_frames.py \
        --video neko-assets/_airaw/cat_walk.mp4 \
        --out   neko-assets/_airaw/cat_vseq_walk.png \
        --prefix cat --state walk --frames 8

批量（多段一起处理，跨视频统一比例/基线 + 出对照图）：:

    python tools/video_to_frames.py --batch \
        --video neko-assets/_airaw/cat_walk.mp4  --out neko-assets/_airaw/cat_vseq_walk.png \
        --video neko-assets/_airaw/cat_pet.mp4   --out neko-assets/_airaw/cat_vseq_pet.png \
        --video neko-assets/_airaw/cat_drag.mp4  --out neko-assets/_airaw/cat_vseq_drag.png \
        --video neko-assets/_airaw/cat_fall.mp4  --out neko-assets/_airaw/cat_vseq_fall.png \
        --video neko-assets/_airaw/cat_climb.mp4 --out neko-assets/_airaw/cat_vseq_climb.png \
        --frames 8 --contact-sheet _review_vid.png

``--video`` / ``--out`` 按出现顺序一一配对；``--prefix`` 用于给状态命名与打印，
非批量模式下可省（会从 ``--out`` 文件名推断）。
"""

from __future__ import annotations

import argparse
import sys
import subprocess
import tempfile
from dataclasses import dataclass, field
from pathlib import Path
from statistics import median

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parents[1]
RAW = ROOT / "neko-assets" / "_airaw"

# ---------------------------------------------------------------- 常量（易调）
# 抽帧数：与现有 ``{prefix}_seq_*.png`` 的 8 格保持一致。
# ⚠️ 为什么不取 12：skin.json 的帧数受 build_skin_v2.py 的 min 规则约束，
#    而 work 态（无 mp4，仍是旧的 8 格 `cat_seq_work.png`）是当前下限 → 全局帧数=8。
#    若这里取 12，重建时会被截断成「前 8 帧」，反而丢掉视频后 1/3 的运动、造成循环跳变。
#    等 cat_work.mp4 到位、work 也升到 12 格后，把这里一起改成 12 即可（工具已支持）。
DEFAULT_FRAMES = 8

CELL = 512               # 序列条每格边长（与旧素材一致）
# 归一化目标（对齐静态图基准，单位=512 画布 px）
DEFAULT_TARGET_H = 399.0    # 目标「中位数内容高」（静态 idle≈399 / sit≈403）
DEFAULT_TARGET_GAP = 52.0   # 目标「中位数离底」（静态 idle≈56 / sit≈48.5 之间）
# ⚠️ 上面两个只是**兜底**（找不到静态图时用）。**默认改为按前缀自动推导** ——
#    因为它们原本是**只为「猫」标定的数字**，用在菲比上会小 10.5%。
#    实测：猫静态基准 399 / 离底 56；菲比静态基准 **446 / 离底 34.5**。
#    曾因此把 phoebe_pet 做成 399/446 = 89.5% → 切到 pet 会「缩水」（用户可见）。

# 超宽/超高时的策略：
#   error   —— 默认。**大声报错并跳过该状态**（宁可暴露，绝不静默出错）
#   contain —— 按宽度兜底缩放（整只保住，但会比基准矮，属"退而求其次"）
#   clip    —— 旧行为（静默裁掉溢出），仅作兼容保留，会打警告
DEFAULT_ON_OVERFLOW = "error"
OVERFLOW_POLICIES = ("error", "contain", "clip")
# 不越权：让新序列条**不抬高公共画布**。画布高由既有素材（静态 idle 离底 456、
# 旧 climb/work 的「内容高+离底」=463）钉死；新动画里个别翻滚帧的 (内容高+离底) 会更大，
# 若不设上限会把画布从 471 顶到 486 → 帧高 231 变 239（尺寸回归）。故对逐帧
# `内容高+离底` 设上限 463（= 既有素材实测最大值），只裁掉极少数离群帧的余量，不动中位数。
DEFAULT_MAX_ADVANCE = 463

# 键控参数
BORDER_BAND = 6          # 采样背景色用的边框带宽（px）
QUANT = 16               # 背景色众数的颜色量化步长
BG_TOL_SOFT = 36         # 与背景色的切比雪夫距离 <= 此值 → 一定是背景
BG_TOL_HARD = 96         # >= 此值 → 一定是前景；中间线性羽化
ERODE_PX = 1             # alpha 收缩像素（消掉抗锯齿绿边）
FEATHER_SIGMA = 0.7      # 羽化半径（消硬边）

ALPHA_THR = 8            # 与 build_skin_v2.py 一致的 alpha 判定阈值


# ---------------------------------------------------------------- 数据类型

@dataclass
class Cutout:
    """一帧抠图结果（已裁到内容框）。"""

    index: int                 # 在原始视频里的帧序号
    image: Image.Image         # RGBA，已 crop 到 alpha bbox
    raw: Image.Image           # 对应的原始帧（RGB），供对照图用
    bg: tuple[int, int, int]   # 该帧估计到的背景色
    bbox: tuple[int, int, int, int]  # 内容框在原始帧中的位置 (x0,y0,x1,y1)

    @property
    def size(self) -> tuple[int, int]:
        return self.image.size


@dataclass
class Job:
    """一段视频 → 一张序列条。"""

    video: Path
    out: Path
    prefix: str
    state: str
    frames: int = DEFAULT_FRAMES
    cutouts: list[Cutout] = field(default_factory=list)
    bg: tuple[int, int, int] = (0, 0, 0)


# ---------------------------------------------------------------- ffmpeg 抽帧

def _ffmpeg_exe() -> str:
    """拿到可用的 ffmpeg 可执行文件路径（PATH 上没有，用 imageio-ffmpeg 的托管版）。"""
    try:
        from imageio_ffmpeg import get_ffmpeg_exe
    except ImportError as exc:  # pragma: no cover - 环境缺失时才走到
        raise RuntimeError(
            "需要 imageio-ffmpeg：pip install imageio-ffmpeg（或把 ffmpeg 放进 PATH）"
        ) from exc
    try:
        return get_ffmpeg_exe()
    except Exception as exc:  # pragma: no cover
        raise RuntimeError(f"无法定位 ffmpeg：{exc}") from exc


def extract_all_frames(video: Path, workdir: Path) -> list[Path]:
    """用 ffmpeg 把视频无损拆成 PNG 帧序列，返回按序号排好的路径列表。"""
    exe = _ffmpeg_exe()
    workdir.mkdir(parents=True, exist_ok=True)
    pattern = str(workdir / "f_%05d.png")
    cmd = [exe, "-v", "error", "-y", "-i", str(video),
           "-fps_mode", "passthrough", pattern]
    proc = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if proc.returncode != 0:
        raise RuntimeError(
            f"ffmpeg 抽帧失败（{video}）：\n{proc.stderr.decode('utf-8', 'replace')}"
        )
    files = sorted(workdir.glob("f_*.png"))
    if not files:
        raise RuntimeError(f"ffmpeg 没抽出任何帧：{video}")
    return files


# ---------------------------------------------------------------- 周期感知抽帧
# 为什么（血案）：走路是**周期性**动作。旧做法「把整段视频均匀降采样成 8 帧」会把
# 5.04s（121 帧@24fps）的整段动作压进 8 帧 @10fps = 0.8s → **6.3 倍速**，观感"像在放动画"。
# 正确做法是先在**一个步态周期内**均匀取 8 帧：循环无缝、速度自然。
# 实测 cat_walk：周期 35 帧=1.46s，压缩倍速 6.30× → 1.82×，帧间运动量 CV 0.480 → 0.041。
CYCLE_THUMB = 64            # 分析用缩略灰度边长（够快够稳；只影响分析，不影响输出）
CYCLE_LAG_MIN = 4           # 自相关搜索最小 lag（帧）
CYCLE_LAG_MAX = 60          # 最大 lag（帧）
CYCLE_CORR_THRESH = 0.25    # 相关度阈值：最高峰低于此 → 视为"无清晰周期" → 回退


def _frame_signatures(paths: list[Path], size: int = CYCLE_THUMB) -> np.ndarray:
    """把每帧读成 ``size×size`` 灰度缩略（float64），用于运动量 / 周期分析。"""
    sigs = []
    for p in paths:
        g = Image.open(p).convert("L").resize((size, size), Image.BILINEAR)
        sigs.append(np.asarray(g, dtype=np.float64))
    return np.stack(sigs) if sigs else np.zeros((0, size, size))


def _frame_motion(sigs: np.ndarray) -> np.ndarray:
    """逐帧瞬时运动量 ``d[i] = mean|G[i+1] - G[i]|``（长度 = total-1）。"""
    if len(sigs) < 2:
        return np.zeros(0)
    return np.abs(np.diff(sigs, axis=0)).mean(axis=(1, 2))


def _estimate_period(d: np.ndarray) -> tuple[int | None, float]:
    """对去均值后的运动量序列做自相关，返回 ``(周期 P, 相关度 r)``；无清晰周期时 ``P=None``。

    只取 ``[CYCLE_LAG_MIN, CYCLE_LAG_MAX]`` 内的**最高峰**；峰值相关度 < ``CYCLE_CORR_THRESH``
    视为"无清晰周期"（如 pet/fall 这类平滑、非周期的动作）。
    """
    if len(d) < CYCLE_LAG_MIN + 3:
        return None, 0.0
    x = d - d.mean()
    best_lag, best_r = None, -1.0
    hi = min(CYCLE_LAG_MAX, len(x) - 2)
    for lag in range(CYCLE_LAG_MIN, hi + 1):
        a, b = x[:-lag], x[lag:]
        r = float(np.dot(a, b) / np.sqrt(np.dot(a, a) * np.dot(b, b) + 1e-12))
        if r > best_r:
            best_r, best_lag = r, lag
    if best_lag is None or best_r < CYCLE_CORR_THRESH:
        return None, max(best_r, 0.0)
    return best_lag, best_r


def _selection_metrics(sigs: np.ndarray, idxs: list[int]) -> tuple[float, float]:
    """选帧质量：``(CV, 接缝比)``。

    * ``CV`` = 相邻选帧运动量(标准差/均值)：越小越"帧间均匀"（`0` = 完美匀速）。
    * ``接缝比`` = 末帧→首帧运动量 / 最大相邻帧差：``≤1`` 表示循环处不跳（无损循环）。
    """
    adj = [float(np.abs(sigs[idxs[k + 1]] - sigs[idxs[k]]).mean()) for k in range(len(idxs) - 1)]
    seam = float(np.abs(sigs[idxs[0]] - sigs[idxs[-1]]).mean())
    mean_adj = float(np.mean(adj)) if adj else 0.0
    cv = (float(np.std(adj)) / mean_adj) if mean_adj > 0 else 0.0
    mx = max(adj) if adj else 0.0
    seam_ratio = (seam / mx) if mx > 0 else 0.0
    return cv, seam_ratio


def _best_cycle_window(sigs: np.ndarray, n: int, period: int):
    """在 ``[st, st+period)`` 内均匀取 ``n`` 帧；遍历起点 ``st`` 取 ``score`` 最小者。

    ``score = CV(相邻帧差) + 2 × max(0, 接缝比 − 1)`` —— 既求帧间均匀，又罚"循环处跳变"。
    返回 ``(score, st, idxs, cv, seam_ratio)``；取不到 n 个唯一点时返回 ``None``。
    """
    total = len(sigs)
    best = None
    for st in range(0, total - period + 1):
        idxs = sorted({min(max(0, int(round(st + k * period / n))), total - 1) for k in range(n)})
        if len(idxs) < n:
            continue
        cv, seam_ratio = _selection_metrics(sigs, idxs)
        score = cv + 2.0 * max(0.0, seam_ratio - 1.0)
        if best is None or score < best[0]:
            best = (score, st, idxs, cv, seam_ratio)
    return best


def sample_indices(all_frames: list[Path], n: int) -> list[int]:
    """**周期感知抽帧**：优先在「一个动作周期」内均匀取 ``n`` 帧；无清晰周期则回退整段均匀。

    算法：
      1. 各帧 → 64×64 灰度缩略，算逐帧瞬时运动量 ``d[i] = mean|G[i+1] − G[i]|``。
      2. ``d`` 去均值后做自相关，取 ``lag∈[4,60]`` 的最高峰为周期 ``P``
         （相关度 < ``CYCLE_CORR_THRESH`` → 无清晰周期）。
      3. 有 ``P``：在 ``[st, st+P)`` 内均匀取 n 帧；遍历起点 st 取 ``score`` 最小者。
      4. ``P`` 缺失、或 ``P < n``（周期内取不满）→ **回退旧的整段均匀抽帧**，并打印原因（绝不静默）。

    返回选中帧号（长度 ≤ n）。
    """
    total = len(all_frames)
    if n >= total:
        print(f"    帧数 {total} ≤ 目标 {n} → 全取")
        return list(range(total))

    sigs = _frame_signatures(all_frames)
    d = _frame_motion(sigs)
    uniform = [int(round(i * total / n)) for i in range(n)]
    u_cv, u_sr = _selection_metrics(sigs, uniform)
    u_score = u_cv + 2.0 * max(0.0, u_sr - 1.0)
    u_cmp = (total / 24.0) / (n / 10.0)                 # 整段压进 n 帧@10fps 的倍速

    period, corr = _estimate_period(d)
    if period is None or period < n:
        why = (f"未检出清晰周期（最高相关度 {corr:.3f} < {CYCLE_CORR_THRESH}）"
               if period is None else f"检出 lag={period} 帧但 < 目标帧数 {n}（疑似相邻帧平滑的伪峰），取不满")
        print(f"    {why} → 回退均匀抽帧 {uniform}"
              f"  | CV={u_cv:.3f} 接缝比={u_sr:.2f} 倍速={u_cmp:.2f}x")
        return uniform

    best = _best_cycle_window(sigs, n, period)
    if best is None:
        print(f"    周期 {period} 帧内取不到 {n} 个唯一点 → 回退均匀抽帧 {uniform}")
        return uniform

    _, st, idxs, cv, seam_ratio = best
    cmp_ = (period / 24.0) / (n / 10.0)                 # 一个周期压进 n 帧@10fps 的倍速
    score = cv + 2.0 * max(0.0, seam_ratio - 1.0)

    # 防劣化：周期存在不代表"周期内抽帧更优"。像 pet（蹭头节奏不匀）/ drag（摆动不匀）
    # 这类动作，周期内均匀取帧反而比整段均匀更差（帧间忽快忽慢 + 循环接缝变大）。
    # 与其"为了用周期而用周期"，不如不劣化地回退 —— 与第 4 步"绝不静默出错"同源。
    if score >= u_score:
        print(f"    检出 P={period} 帧(r={corr:.3f})，但周期内抽帧并不更优"
              f"（新 score={score:.3f} ≥ 均匀 score={u_score:.3f}，周期内动作本就不均匀）"
              f" → 回退均匀抽帧 {uniform}  | CV={u_cv:.3f} 接缝比={u_sr:.2f} 倍速={u_cmp:.2f}x")
        return uniform

    print(f"    周期感知：P={period} 帧(r={corr:.3f}) 起点 st={st} → {idxs}")
    print(f"      新(周期内): CV={cv:.3f} 接缝比={seam_ratio:.2f} 倍速={cmp_:.2f}x"
          f"   |   旧(均匀): CV={u_cv:.3f} 接缝比={u_sr:.2f} 倍速={u_cmp:.2f}x")
    return idxs


# ---------------------------------------------------------------- 背景色自适应

def estimate_bg_color(rgb: np.ndarray, band: int = BORDER_BAND) -> tuple[int, int, int]:
    """从视频帧的四边像素估计背景色。

    做法：取四边 ``band`` 像素宽的带，量化到 ``QUANT`` 步长后取**出现频率最高的颜色**
    （众数 bin），再对落在该 bin 内的原始像素求均值 → 稳健、抗噪声、抗角色压边。
    这样黄绿 / 标准绿都能各自测得，无需任何硬编码。
    """
    h, w, _ = rgb.shape
    border = np.concatenate([
        rgb[:band, :, :].reshape(-1, 3),
        rgb[h - band:, :, :].reshape(-1, 3),
        rgb[:, :band, :].reshape(-1, 3),
        rgb[:, w - band:, :].reshape(-1, 3),
    ], axis=0).astype(np.int32)

    q = border // QUANT
    keys = (q[:, 0] << 20) | (q[:, 1] << 10) | q[:, 2]
    values, counts = np.unique(keys, return_counts=True)
    modal = values[int(np.argmax(counts))]
    sel = keys == modal
    mean = border[sel].mean(axis=0)
    return (int(round(mean[0])), int(round(mean[1])), int(round(mean[2])))


# ---------------------------------------------------------------- 连通域 flood fill

def _flood_from_border(candidate: np.ndarray) -> np.ndarray:
    """span 式 flood fill：把与画布四边连通的 ``candidate`` 区域标成 True。

    纯 numpy + 游程栈实现（不依赖 scipy），对 960×960 单帧在毫秒级完成。
    只做「边缘连通」判定，因此角色内部的同色区（如绿眼、白肚皮）不会被误吞。
    """
    h, w = candidate.shape
    filled = np.zeros_like(candidate)
    stack: list[tuple[int, int]] = []

    # 播种：四边所有候选像素
    for x in np.where(candidate[0, :])[0]:
        stack.append((0, int(x)))
    for x in np.where(candidate[h - 1, :])[0]:
        stack.append((h - 1, int(x)))
    for y in np.where(candidate[:, 0])[0]:
        stack.append((int(y), 0))
    for y in np.where(candidate[:, w - 1])[0]:
        stack.append((int(y), w - 1))

    while stack:
        y, x = stack.pop()
        if filled[y, x] or not candidate[y, x]:
            continue
        # 向左右扩展整条游程
        x0 = x
        while x0 > 0 and candidate[y, x0 - 1] and not filled[y, x0 - 1]:
            x0 -= 1
        x1 = x
        while x1 < w - 1 and candidate[y, x1 + 1] and not filled[y, x1 + 1]:
            x1 += 1
        filled[y, x0:x1 + 1] = True
        # 上下相邻行里，把该游程覆盖范围内的「游程起点」入栈
        for ny in (y - 1, y + 1):
            if ny < 0 or ny >= h:
                continue
            row = candidate[ny, x0:x1 + 1] & ~filled[ny, x0:x1 + 1]
            if not row.any():
                continue
            starts = np.where(row & ~np.concatenate(([False], row[:-1])))[0]
            for s in starts:
                stack.append((ny, int(x0 + s)))
    return filled


# ---------------------------------------------------------------- 键控

def key_out_background(frame: Image.Image) -> tuple[Image.Image, tuple[int, int, int], np.ndarray]:
    """对单帧做自适应绿幕键控，返回 ``(RGBA 帧, 背景色, 背景掩膜)``。

    步骤：
      1. 估计背景色（边框众数）。
      2. 候选背景 = 与背景色切比雪夫距离 < ``BG_TOL_HARD`` 的像素。
      3. 只保留「与四边连通」的候选 → 背景掩膜（护住肚皮/白爪/绿眼）。
      4. alpha：背景掩膜处按距离做 ``SOFT..HARD`` 线性羽化，非背景处 255。
      5. despill：背景掩膜带内压绿（``g = min(g, max(r, b))``）。
      6. 1px 收缩 + 高斯羽化，去掉抗锯齿绿边与硬边。
    """
    rgb = np.asarray(frame.convert("RGB")).astype(np.int16)
    bg = estimate_bg_color(rgb)
    bg_arr = np.array(bg, dtype=np.int16)

    dist = np.max(np.abs(rgb - bg_arr), axis=2)          # 切比雪夫距离
    candidate = dist < BG_TOL_HARD
    bg_mask = _flood_from_border(candidate)              # 只抠与边缘连通的部分

    # alpha：背景带内按距离羽化，其余不透明
    ramp = np.clip((dist - BG_TOL_SOFT) / (BG_TOL_HARD - BG_TOL_SOFT), 0.0, 1.0)
    alpha = (ramp * 255.0).astype(np.uint8)
    alpha[~bg_mask] = 255

    out = rgb.astype(np.uint8).copy()
    # despill：只动「背景掩膜」内的绿，橙猫（r≫g）与白肚（r≈g≈b）不受影响
    spill = bg_mask
    if spill.any():
        r = out[..., 0].astype(np.int16)
        g = out[..., 1].astype(np.int16)
        b = out[..., 2].astype(np.int16)
        cap = np.maximum(r, b)
        g2 = np.where(spill & (g > cap), cap, g)
        out[..., 1] = np.clip(g2, 0, 255).astype(np.uint8)

    rgba = np.dstack([out, alpha])
    im = Image.fromarray(rgba, "RGBA")

    if ERODE_PX > 0:                                     # 收缩：去掉 1px 绿描边
        a = im.getchannel("A").filter(ImageFilter.MinFilter(ERODE_PX * 2 + 1))
        im.putalpha(a)
    if FEATHER_SIGMA > 0:                                # 羽化：消硬边
        a = im.getchannel("A").filter(ImageFilter.GaussianBlur(FEATHER_SIGMA))
        im.putalpha(a)
    # 羽化后再补一遍 despill（半透明带），进一步压掉残留绿边
    arr = np.asarray(im).copy()
    band = (arr[..., 3] > 0) & (arr[..., 3] < 255)
    if band.any():
        r = arr[..., 0].astype(np.int16)
        g = arr[..., 1].astype(np.int16)
        b = arr[..., 2].astype(np.int16)
        cap = np.maximum(r, b)
        arr[..., 1] = np.clip(np.where(band & (g > cap), cap, g), 0, 255).astype(np.uint8)
    im = Image.fromarray(arr, "RGBA")
    return im, bg, bg_mask


def bbox_of_alpha(img: Image.Image, thr: int = ALPHA_THR):
    """返回 alpha>thr 的内容框（与 build_skin_v2.bbox_of_alpha 同义）。"""
    a = img.getchannel("A").point(lambda v: 255 if v > thr else 0)
    return a.getbbox()


# ---------------------------------------------------------------- 单段处理

def process_job(job: Job, workdir: Path, dump_dir: Path | None) -> None:
    """抽取 --- 采样 --- 键控 --- 裁内容框，结果写进 ``job.cutouts``（尚未归一化）。"""
    all_frames = extract_all_frames(job.video, workdir)
    total = len(all_frames)
    print(f"[{job.state}] {job.video.name}: 共 {total} 帧 @24fps = {total / 24.0:.2f}s")
    idxs = sample_indices(all_frames, job.frames)
    print(f"    抽取 {len(idxs)} 帧 {idxs}")

    cutouts: list[Cutout] = []
    first_bg: tuple[int, int, int] | None = None
    for i in idxs:
        frame = Image.open(all_frames[i]).convert("RGB")
        keyed, bg, _ = key_out_background(frame)
        if first_bg is None:
            first_bg = bg
        bb = bbox_of_alpha(keyed)
        if bb is None:
            print(f"    ⚠️ 第 {i} 帧抠完是空的，跳过")
            continue
        content = keyed.crop(bb)
        cutouts.append(Cutout(index=i, image=content, raw=frame, bg=bg, bbox=bb))
        if dump_dir is not None:
            dump_dir.mkdir(parents=True, exist_ok=True)
            content.save(dump_dir / f"{job.state}_{i:05d}.png")
    job.cutouts = cutouts
    job.bg = first_bg if first_bg is not None else (0, 0, 0)
    print(f"    背景色估计 = {job.bg}")


# ---------------------------------------------------------------- 归一化 + 拼条
#
# 为什么不能「把猫塞满格子」：静态图走的是「按源画布归一化」(`sc = REF_CELL/max(w,h)`),
# 512 画布里猫高 ≈399px（78% 填充）、离底 ≈52px。若序列条把猫塞满整格（≈455px / 离底 7），
# 重建后 idle→walk 会「长大一圈 + 下移 20+px」，是用户可见回归。
#
# 为什么必须**逐状态**定标：五段视频镜头远近不同 —— 同一 512 口径下 walk 猫高 347、
# pet 367、drag 418、fall 251（相对静态基准 399 差 ±35%）。全局一个系数无法同时对齐。
#
# 做法（每状态一个系数，**同一状态内所有帧共用**，避免猫「呼吸」）：
#   1. 尺寸：`s = TARGET_H / median(各帧内容高)`，把「中位数猫高」映射到 TARGET_H。
#      用中位数而非均值，鲁棒于翻滚/伸展的离群帧。
#   2. 基线：`gap_i = 原始离底_i × s + delta`，`delta = TARGET_GAP - median(原始离底) × s`
#      → 让「中位数离底」落在 TARGET_GAP，同时**完整保留帧间相对起伏**（只整体平移）。

def still_baseline(prefix: str, ref_cell: int = CELL) -> tuple[float, float, str] | None:
    """从 ``{prefix}_still_idle.png`` / ``{prefix}_still_sit.png`` 推导**该角色自己的**定标基准。

    为什么必须按角色推导（血案）：静态图走的是「按源画布归一化」，**不同角色的填充率不同** ——
    实测 **猫 399 / 离底 56**，而 **菲比 446 / 离底 34.5**。本工具原先写死 399/52（只为猫标定），
    结果把 ``phoebe_pet`` 做成 399/446 = **89.5%** → 切到 pet 时角色**缩水 10%**（用户可见）。

    返回 ``(目标内容高, 目标离底, 来源说明)``；两张静态图都找不到时返回 ``None``
    （调用方退回 ``DEFAULT_TARGET_H`` / ``DEFAULT_TARGET_GAP`` 兜底并打警告）。
    """
    found: dict[str, tuple[float, float]] = {}
    for st in ("idle", "sit"):
        p = RAW / f"{prefix}_still_{st}.png"
        if not p.is_file():
            continue
        a = np.asarray(Image.open(p).convert("RGBA"))
        al = a[:, :, 3] > 16
        if not al.any():
            continue
        ys, _xs = np.where(al)
        sc = ref_cell / max(a.shape[0], a.shape[1])
        found[st] = ((ys.max() - ys.min() + 1) * sc,
                     (a.shape[0] - 1 - ys.max()) * sc)
    if not found:
        return None
    # 口径必须与既有常量一致，否则会改动已产出的素材（零回归要求）：
    #   · 高度取 **idle**（动画态都是站姿，与 idle 对齐）—— 猫 399 ✓（若取 idle/sit 中位会变成 401，+0.5%）
    #   · 离底取 idle/sit 的**中位数并取整** —— 猫 median(56, 48.5)=52.25 → 52 ✓ 与常量 52 一致
    h = round(found.get("idle", next(iter(found.values())))[0])
    g = round(median([v[1] for v in found.values()]))
    src = "still_idle" + ("+still_sit" if "sit" in found else "")
    return (float(h), float(g), src)


def stills_max_footprint(prefix: str, ref_cell: int = CELL) -> float | None:
    """该角色静态图的「内容高 + 离底」最大值 —— 即它天然的**纵向占位**。

    用途：给 ``max_advance`` 定上限，保证新序列条**不把公共画布顶高**（否则帧高会变、尺寸回归）。

    为什么也要按角色推导：``DEFAULT_MAX_ADVANCE = 463`` 同样是**只为猫标定**的。
    公式 ``max(静态图 h+gap) + 8`` **能精确复现猫的 463**（cat: max(455, 451.5)+8 = 463 ✓），
    但对菲比会算成 492 —— 用 463 去卡菲比，会把 pet 的离底从 28 硬削到 9（实测），
    导致 pet 比 sit 低 6px。故改为按角色推导。
    """
    vals: list[float] = []
    for st in ("idle", "sit"):
        p = RAW / f"{prefix}_still_{st}.png"
        if not p.is_file():
            continue
        a = np.asarray(Image.open(p).convert("RGBA"))
        al = a[:, :, 3] > 16
        if not al.any():
            continue
        ys, _xs = np.where(al)
        sc = ref_cell / max(a.shape[0], a.shape[1])
        vals.append((ys.max() - ys.min() + 1) * sc + (a.shape[0] - 1 - ys.max()) * sc)
    return max(vals) if vals else None


def harmonize_and_strip(jobs: list[Job], cell: int, target_h: float, target_gap: float,
                        delta_override: float | None = None,
                        max_advance: int | None | str = "auto",
                        auto_baseline: bool = True,
                        on_overflow: str = DEFAULT_ON_OVERFLOW) -> list[str]:
    """逐状态定标（尺寸+基线）后，把各帧底对齐放进 ``cell×cell`` 格位、拼成序列条。

    返回 **问题清单**（空 = 全部正常）。调用方据此决定退出码 —— 绝不静默成功。
    """
    problems: list[str] = []
    for job in jobs:
        if not job.cutouts:
            continue
        hs = [c.bbox[3] - c.bbox[1] for c in job.cutouts]          # 各帧内容高（原始帧 px）
        ms = [c.raw.height - c.bbox[3] for c in job.cutouts]       # 各帧原始离底
        med_h = median(hs)

        # ---- 定标目标：**按角色自动推导**（原先写死的 399/52 只为猫标定，菲比会小 10%）----
        th, tg, src = target_h, target_gap, "常量兜底"
        if auto_baseline:
            bl = still_baseline(job.prefix, cell)
            if bl is not None:
                th, tg, src = bl
            else:
                problems.append(
                    f"[{job.prefix}/{job.state}] 找不到 {job.prefix}_still_*.png → "
                    f"退回常量 {target_h:.0f}/{target_gap:.0f}（可能与本皮肤其他状态不对齐）")

        scale = th / med_h
        med_gap = median(ms)
        delta = (tg - med_gap * scale) if delta_override is None else delta_override

        # ---- 🔴 溢出保护（本轮新增）：原先 `x = max(0,(cell-w)//2)` + paste 会**静默裁掉**超出格子的部分 ----
        raw_ws = [c.image.width for c in job.cutouts]
        raw_hs = [c.image.height for c in job.cutouts]
        max_w = max(round(w * scale) for w in raw_ws)
        max_h = max(round(h * scale) for h in raw_hs)
        if max_w > cell or max_h > cell:
            r_max = max(raw_ws[i] / raw_hs[i] for i in range(len(raw_ws)))
            msg = (f"[{job.prefix}/{job.state}] 内容超出 {cell} 格：缩放后最大 {max_w}x{max_h}"
                   f"（源内容宽高比最大 {r_max:.2f}；按高度定标要求 ≤ {cell / th:.2f}）")
            if on_overflow == "error":
                problems.append(msg + " → **已跳过**（素材需重做；或显式加 --on-overflow contain/clip）")
                continue
            if on_overflow == "contain":
                scale = min(scale, cell / max(raw_ws), cell / max(raw_hs))
                delta = (tg - med_gap * scale) if delta_override is None else delta_override
                problems.append(msg + f" → contain: 缩到 scale={scale:.3f}（整只保住，但比基准矮）")
            else:  # clip：旧行为，仅兼容
                problems.append(msg + " → clip: **静默裁掉溢出**（不推荐）")

        # ---- 逐帧 (内容高+离底) 上限：**按角色推导**，保证不把公共画布顶高 ----
        # "auto" = max(该角色静态图 h+gap) + 8（该公式能精确复现猫的 463）
        cap = max_advance
        cap_src = "显式指定"
        if cap == "auto":
            fp = stills_max_footprint(job.prefix, cell)
            cap = round(fp + 8) if fp is not None else DEFAULT_MAX_ADVANCE
            cap_src = f"静态图{cap}" if fp is not None else f"常量兜底{cap}"

        cells: list[Image.Image] = []
        capped = 0
        used_h: list[int] = []
        used_gap: list[int] = []
        for cut in job.cutouts:
            w = max(1, round(cut.image.width * scale))
            h = max(1, round(cut.image.height * scale))
            gap = round((cut.raw.height - cut.bbox[3]) * scale + delta)
            if cap is not None and h + gap > cap:
                gap = cap - h                   # 只裁离群帧余量，不改中位数
                capped += 1
            gap = max(0, min(gap, cell - h))
            used_h.append(h)
            used_gap.append(gap)
            content = cut.image.resize((w, h), Image.LANCZOS)
            canvas = Image.new("RGBA", (cell, cell), (0, 0, 0, 0))
            x = max(0, (cell - w) // 2)
            y = max(0, cell - gap - h)
            # ⚠️ 不带 mask 粘贴：`paste(img, xy, img)` 在透明底上会把 alpha 做
            # dst*(1-m)+a*m，低 alpha 的抗锯齿边缘被平方后趋 0 → bbox 缩水。
            canvas.paste(content, (x, y))
            cells.append(canvas)

        strip = Image.new("RGBA", (cell * len(cells), cell), (0, 0, 0, 0))
        for i, c in enumerate(cells):
            strip.paste(c, (i * cell, 0))
        job.out.parent.mkdir(parents=True, exist_ok=True)
        strip.save(job.out)

        print(f"[{job.state}] scale={scale:.3f} delta={delta:+.1f} "
              f"内容高(格内px)={sorted(used_h)} 离底={sorted(used_gap)} "
              f"{'(裁余量 %d 帧)' % capped if capped else ''} → {job.out.name} "
              f"{len(cells)}格×{cell}px   基准={th:.0f}/{tg:.0f}（来自 {src}）")
    return problems


# ---------------------------------------------------------------- 对照图（QA）

def _checkerboard(size: tuple[int, int], sq: int = 12) -> Image.Image:
    im = Image.new("RGB", size, (222, 222, 222))
    d = ImageDraw.Draw(im)
    for y in range(0, size[1], sq):
        for x in range(0, size[0], sq):
            if ((x // sq) + (y // sq)) % 2:
                d.rectangle([x, y, x + sq - 1, y + sq - 1], fill=(168, 168, 168))
    return im


def _on_checker(img: Image.Image, box: tuple[int, int]) -> Image.Image:
    bg = _checkerboard(box)
    bg.paste(img, ((box[0] - img.width) // 2, (box[1] - img.height) // 2), img)
    return bg


def make_contact_sheet(jobs: list[Job], path: Path, n_samples: int = 3,
                       panel: int = 220, zoom: int = 4) -> None:
    """出「人眼验收」对照图：每个状态取 3 帧，每帧三联 ``原始 | 抠图(棋盘底) | 边缘放大4×``。"""
    try:
        from PIL import ImageFont
        font = ImageFont.load_default()
    except Exception:  # pragma: no cover
        font = None

    rows = []
    for job in jobs:
        cuts = job.cutouts
        if not cuts:
            continue
        picks = [cuts[0], cuts[len(cuts) // 2], cuts[-1]][:n_samples]
        row = Image.new("RGB", (panel * 3 * len(picks), panel + 22), (255, 255, 255))
        d = ImageDraw.Draw(row)
        d.text((4, 4), f"{job.state}  bg={job.bg}  frames={len(cuts)}", fill=(0, 0, 0), font=font)
        for k, cut in enumerate(picks):
            base_x = k * panel * 3
            # 1) 原始帧
            raw = cut.raw.copy()
            raw.thumbnail((panel, panel), Image.LANCZOS)
            p1 = Image.new("RGB", (panel, panel), (255, 255, 255))
            p1.paste(raw, ((panel - raw.width) // 2, (panel - raw.height) // 2))
            # 2) 抠图（棋盘底）
            cut_disp = cut.image.copy()
            cut_disp.thumbnail((panel - 8, panel - 8), Image.LANCZOS)
            p2 = _on_checker(cut_disp, (panel, panel))
            # 3) 边缘放大 4×（取内容框一角附近的小片）
            src = cut.image
            patch_w = min(src.width, max(24, panel // zoom))
            patch_h = min(src.height, max(24, panel // zoom))
            # 取右上角（最容易出现绿边/下垂边缘的位置之一）
            px0 = max(0, src.width - patch_w)
            py0 = 0
            patch = src.crop((px0, py0, px0 + patch_w, py0 + patch_h))
            patch = patch.resize((panel, panel), Image.NEAREST)
            p3 = _on_checker(patch, (panel, panel))
            for idx, p in enumerate((p1, p2, p3)):
                row.paste(p, (base_x + idx * panel, 22))
            d.text((base_x + 2, panel + 6),
                   f"f{cut.index} | raw | cutout | edge×{zoom}", fill=(0, 0, 0), font=font)
        rows.append(row)

    if not rows:
        print("（对照图：无数据）")
        return
    W = max(r.width for r in rows)
    H = sum(r.height for r in rows)
    sheet = Image.new("RGB", (W, H), (255, 255, 255))
    y = 0
    for r in rows:
        sheet.paste(r, (0, y))
        y += r.height
    path.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(path)
    print(f"对照图写出 {path}  ({sheet.size})")


# ---------------------------------------------------------------- CLI

def _infer_prefix_state(out: Path) -> tuple[str, str]:
    """从输出文件名 ``{prefix}_vseq_{state}.png`` 推断 prefix / state。"""
    stem = out.stem
    if "_vseq_" in stem:
        pre, st = stem.split("_vseq_", 1)
        return pre, st
    if "_seq_" in stem:
        pre, st = stem.split("_seq_", 1)
        return pre, st
    return stem, stem


def build_argparser() -> argparse.ArgumentParser:
    ap = argparse.ArgumentParser(
        description="mp4（绿幕）→ 自适应抠图 → Neko 序列条 png",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    ap.add_argument("--video", action="append", default=[], metavar="PATH",
                    help="输入 mp4（可重复；按顺序与 --out 配对）")
    ap.add_argument("--out", action="append", default=[], metavar="PATH",
                    help="输出序列条 png（可重复；按顺序与 --video 配对）")
    ap.add_argument("--batch", action="store_true",
                    help="批量模式：一次处理多段（本工具本就逐状态定标，此开关仅作语义标记）")
    ap.add_argument("--frames", type=int, default=DEFAULT_FRAMES,
                    help=f"每段抽取的帧数（默认 {DEFAULT_FRAMES}）")
    ap.add_argument("--prefix", default=None, help="皮肤前缀（如 cat）；缺省从 --out 推断")
    ap.add_argument("--state", default=None, help="状态名（如 walk）；缺省从 --out 推断")
    ap.add_argument("--cell", type=int, default=CELL, help=f"序列条每格边长（默认 {CELL}）")
    ap.add_argument("--target-height", type=float, default=None,
                    help=f"目标中位数内容高（512 画布 px）。**缺省按 <prefix>_still_*.png 自动推导**"
                         f"——不同角色填充率不同（实测 猫 399 / 菲比 446）；找不到静态图时退回 {DEFAULT_TARGET_H:.0f}")
    ap.add_argument("--target-gap", type=float, default=None,
                    help=f"目标中位数离底（512 画布 px）。缺省同上自动推导；兜底 {DEFAULT_TARGET_GAP:.0f}")
    ap.add_argument("--no-auto-baseline", action="store_true",
                    help="禁用「按静态图自动推导基准」，改用 --target-height/--target-gap（或常量兜底）")
    ap.add_argument("--on-overflow", choices=OVERFLOW_POLICIES, default=DEFAULT_ON_OVERFLOW,
                    help=f"内容缩放后超出格子时的策略（默认 {DEFAULT_ON_OVERFLOW}：大声报错并跳过该状态；"
                         f"contain=按宽度兜底缩；clip=旧行为静默裁）")
    ap.add_argument("--gap-delta", type=float, default=None,
                    help="手工覆盖基线平移量 delta（px）；缺省按 --target-gap 反解")
    ap.add_argument("--max-advance", default="auto",
                    help=f"逐帧 (内容高+离底) 上限，防止新条抬高公共画布。"
                         f"**缺省 auto** = max(该角色静态图 h+gap) + 8（该公式精确复现猫的 463，"
                         f"菲比为 492）；显式传数字覆盖；传 0 = 不限制")
    ap.add_argument("--contact-sheet", default=None, metavar="PATH",
                    help="输出「原始|抠图|边缘放大」对照图")
    ap.add_argument("--dump-dir", default=None, metavar="DIR",
                    help="把每帧抠图单独 dump 到此目录（调试用）")
    ap.add_argument("--dry-run", action="store_true", help="只测背景色/尺寸，不写序列条")
    return ap


def main(argv: list[str] | None = None) -> int:
    args = build_argparser().parse_args(argv)

    if len(args.video) != len(args.out) or not args.video:
        print("❌ 需要成对的 --video / --out（可重复）", file=sys.stderr)
        return 2

    jobs: list[Job] = []
    for v, o in zip(args.video, args.out):
        vp, op = Path(v), Path(o)
        if not vp.is_file():
            print(f"❌ 找不到视频 {vp}", file=sys.stderr)
            return 2
        pre, st = args.prefix, args.state
        if pre is None or st is None:
            ip, ist = _infer_prefix_state(op)
            pre = pre or ip
            st = st or ist
        jobs.append(Job(video=vp, out=op, prefix=pre, state=st, frames=args.frames))

    dump_dir = Path(args.dump_dir) if args.dump_dir else None

    with tempfile.TemporaryDirectory(prefix="neko_v2f_") as td:
        tmp = Path(td)
        for ji, job in enumerate(jobs):
            process_job(job, tmp / f"job{ji}", dump_dir)

    all_cuts = [c for j in jobs for c in j.cutouts]
    if not all_cuts:
        print("❌ 全部帧都被抠空了，检查素材/阈值", file=sys.stderr)
        return 1

    print("\n=== 逐状态定标（尺寸/基线）===")
    auto = not args.no_auto_baseline
    th_fb = args.target_height if args.target_height is not None else DEFAULT_TARGET_H
    tg_fb = args.target_gap if args.target_gap is not None else DEFAULT_TARGET_GAP

    def _resolve(prefix: str) -> tuple[float, float, str]:
        if auto:
            bl = still_baseline(prefix, args.cell)
            if bl is not None:
                return bl
        return (th_fb, tg_fb, "常量兜底")

    if args.dry_run:
        # 只报参数，不写条
        for job in jobs:
            hs = [c.bbox[3] - c.bbox[1] for c in job.cutouts]
            ms = [c.raw.height - c.bbox[3] for c in job.cutouts]
            if not hs:
                continue
            th, tg, src = _resolve(job.prefix)
            s = th / median(hs)
            d = tg - median(ms) * s
            raw_ws = [c.image.width for c in job.cutouts]
            raw_hs = [c.image.height for c in job.cutouts]
            mw = max(round(w * s) for w in raw_ws)
            flag = "  🔴 超出格子（会报错跳过）" if mw > args.cell else ""
            print(f"[{job.state}] scale={s:.3f} delta={d:+.1f} "
                  f"medH={median(hs):.0f}→{median(hs)*s:.0f} medGap={median(ms)*s+d:.0f} "
                  f"最大宽={mw}/{args.cell} 基准={th:.0f}/{tg:.0f}（来自 {src}）{flag}")
        print("（--dry-run：不写序列条）")
        return 0

    problems = harmonize_and_strip(jobs, args.cell, th_fb, tg_fb,
                                   args.gap_delta, args.max_advance or None,
                                   auto_baseline=auto, on_overflow=args.on_overflow)

    if args.contact_sheet:
        make_contact_sheet(jobs, Path(args.contact_sheet))

    if problems:
        print("\n⚠️ 问题清单：", file=sys.stderr)
        for p in problems:
            print("  - " + p, file=sys.stderr)
        skipped = [p for p in problems if "已跳过" in p]
        if skipped:
            print(f"\n❌ 有 {len(skipped)} 个状态因内容超出 {args.cell} 格被**跳过未产出**。\n"
                  f"   这些素材不能用（会被裁）—— 请重做，或显式加 --on-overflow contain（缩到装下，但会比基准矮）。",
                  file=sys.stderr)
            return 3

    print("\n✅ 完成")
    return 0


if __name__ == "__main__":
    sys.exit(main())
