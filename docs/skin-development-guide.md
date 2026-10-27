# Neko 皮肤开发标准文档（Skin Development Guide）

当前工具入口（v0.8.0）：[皮肤工作台](skin-workbench.md) 可逐帧检查、校验并打包。`frameWidth/frameHeight` 应声明源素材像素尺寸；宿主逻辑画幅始终为 32×32，柔光皮肤的源图为 128×128。下面保留的历史说明应结合最新 [动作参数与过渡](skin-motion.md) 阅读。

> **本文件是「可执行」标准文档。** 照着它做，就能产出一套**在设置页里出现、能切、能跑动画**的皮肤。
> 每一条「必须 / 禁止」都能追溯到宿主源码或 `docs/dev-log.md` / `CHANGELOG.md` 的真实记录。
>
> | 项 | 值 |
> |---|---|
> | **最后核对日期** | **2026-09-19** |
> | **核对到的宿主版本** | **v0.1.83**（皮肤契约最后变动于 **v0.2.1**：新增「`fps>0` ⇒ 各帧像素必须不同」的**全皮肤**不变量；`pixel`/`soft` 的 `sleep` 由 `1` 改 `0`） |
> | 皮肤发现方式 | 扫 `neko-assets/<id>/skin.json` 自动注册（零改代码） |
> | 相关源码 | `SkinRegistry.cs` / `SpriteAtlas.cs` / `FrameAnimator.cs` / `SettingsWindow.cs` |

---

## 目录（TOC）

1. [皮肤就是一个目录](#1-皮肤就是一个目录)
2. [skin.json 字段表（含校验规则）](#2-skinjson-字段表)
3. [🔴 `fps` 语义（最重要）](#3-fps-语义最重要)
4. [9 个状态清单与用途](#4-9-个状态清单与用途)
5. [帧图布局：宽度必须 = frameWidth × frames](#5-帧图布局)
6. [尺寸与对齐：frameWidth / frameHeight / recommendedScale](#6-尺寸与对齐)
7. [眼球层（眼神追踪）](#7-眼球层眼神追踪)
8. [thumb.png（设置页缩略图）](#8-thumbpng设置页缩略图)
9. [素材生成管线工具](#9-素材生成管线工具)
10. [构建与发布自动收集](#10-构建与发布自动收集v027)
11. [AI 生图提示词要点](#11-ai-生图提示词要点)
12. [常见错误速查表](#12-常见错误速查表)
13. [附录 A：皮肤-实现一致性核对](#附录-a皮肤-实现一致性核对)
14. [附录 B：自绘素材规格（手工 / Aseprite）](#附录-b自绘素材规格手工--aseprite)

---

## 1. 皮肤就是一个目录

宿主启动 / 打开设置页时，会扫 `neko-assets/` 下的**每个一级子目录**，只要目录里有 `skin.json`
就当成一套皮肤注册：

```
<Neko 程序根>/
└── neko-assets/
    ├── pixel/                 ← 内置默认皮肤（id 排最前）
    │   ├── skin.json
    │   ├── neko_idle.png      ← 每个状态一张「横排 N 帧」雪碧图
    │   ├── neko_walk.png
    │   ├── …（其余状态）
    │   ├── neko_eyes_open.png ← 眼球层（可选）
    │   ├── neko_shadow.png    ← 影子层（可选，hasShadow=true 时加载）
    │   └── thumb.png          ← 设置页缩略图
    ├── cat_pixel/
    └── 你自己的皮肤 id/
        └── skin.json + 帧图 + thumb.png
```

**规则（源码 `SkinRegistry.Scan`）：**

| 规则 | 依据 |
|---|---|
| 没有 `skin.json` 的子目录**不是皮肤**（跳过，如 `icons/`、`mirror/`、`reference/`） | `if (!File.Exists(manifest)) continue;` |
| 任何解析失败（JSON 损坏 / 字段非法）→ **跳过该皮肤 + `Log.Warn`，绝不抛** | `TryLoad` 返回 null → `Log.Warn("[皮肤] 跳过非法皮肤目录…")` |
| 一个都没扫到 → 回退**内置兜底** `pixel`（硬编码规格），保证永不崩 | `BuiltinFallback()` |
| 排序：默认皮肤 `pixel` 排最前，其余按 id 字典序 | `Scan` 末尾的 `Sort` |

### 1.1 最小可用皮肤（完整例子）

**目录：** `neko-assets/mycat/`

**`skin.json`（4 帧静止皮肤，最省事）：**

```json
{
  "id": "mycat",
  "displayName": "我的猫",
  "layout": "sheet",
  "frameWidth": 32,
  "frameHeight": 32,
  "frames": 4,
  "hasShadow": false,
  "eyeLayers": [],
  "recommendedScale": 1.0,
  "states": [
    { "name": "idle",  "fps": 0, "eyeLayer": null },
    { "name": "walk",  "fps": 8, "eyeLayer": null },
    { "name": "sit",   "fps": 0, "eyeLayer": null },
    { "name": "sleep", "fps": 0, "eyeLayer": null },
    { "name": "pet",   "fps": 6, "eyeLayer": null },
    { "name": "drag",  "fps": 6, "eyeLayer": null },
    { "name": "fall",  "fps": 8, "eyeLayer": null },
    { "name": "climb", "fps": 6, "eyeLayer": null },
    { "name": "work",  "fps": 6, "eyeLayer": null }
  ]
}
```

**帧图（每状态一张）：**

- `neko_idle.png` … `neko_work.png` —— 每张是**横排 4 帧**的图，因此画布必须是 `32×4 = 128` 宽、`32` 高。
- `thumb.png` —— 96×96 缩略图（可稍后用 `tools/gen_skin_previews.py` 生成）。

**图怎么画（最省事路线）：** 只画**一张 32×32 的猫**，然后 9 个状态都用这张（或各画一张 128×32、四帧相同）。
配合 `fps = 0`（静止）+ 程序侧的呼吸/眨眼，效果反而最好（见 §11）。

**做完三件事，皮肤就能用：**

1. 把 `mycat/` 放进 `neko-assets/`；
2. 若这是**内置/发布**皮肤：放入源码 `neko-assets/<id>/`，由共用运行素材规则自动收集（见 §10）；
3. `thumb.png` 用 `gen_skin_previews.py` 生成（见 §9）。

> 用户自制皮肤放到**运行时**的 `<Neko程序根>/neko-assets/<id>/` 即可，无需改任何工程
> ——`SkinRegistry` 零改码发现（v0.1.75 解锁 UGC 的目标）。

---

## 2. skin.json 字段表

> 解析与校验逐行摘录自 `src/DesktopPet.App/Core/SkinRegistry.cs` 的 `TryLoad`。

| 字段 | 类型 | 默认值 | 校验 / 说明 |
|---|---|---|---|
| `id` | string | 目录名 | 空则用目录名。= 目录名（用于持久化 `AppConfig.Skin`） |
| `displayName` | string | = id | 设置页显示名 |
| `layout` | string | `"sheet"` | `"files"`（忽略大小写）→ 独立帧文件；**其余任何值 → 按 `sheet` 处理** |
| `frameWidth` | int | `32` | **必须 > 0**，否则整张皮肤判非法（`return null`） |
| `frameHeight` | int | `32` | **必须 > 0**，同上 |
| `frames` | int | `4` | **必须 > 0**，同上；这是**全状态统一的帧数** |
| `hasShadow` | bool | `false` | true 时尝试加载 `neko_shadow.png`（缺失则跳过，不报错） |
| `pixelArt` | bool | `true` | v0.2.4 新增。true 保留最近邻像素缩放；手绘/高清插画可声明 false 使用平滑缩放。切换皮肤即时生效。 |
| `recommendedScale` | double | `0` | 仅接受 **1.0–8.0**；缺失 / NaN / 越界 / 负数 → 落回 0（**不影响皮肤可用性**，只打 `Log.Warn`） |
| `eyeLayers` | string[] | `[]` | 眼球层 key 列表；按 key 尝试加载 `neko_<key>.png`（缺失则跳过） |
| `states` | array | — | 见下；**空数组 → 整张皮肤判非法** |
| `states[].name` | string | — | **必填**；空 → 跳过该条状态 |
| `states[].fps` | double | `0` | 见 §3；**`< 0` 或 NaN → 整张皮肤判非法**；`0` = 静止 |
| `states[].eyeLayer` | string? | null | 关联 `eyeLayers` 里的某个 key；null/空 = 不叠加 |

**「整张皮肤判非法」（`TryLoad` 返回 null → 被跳过）的三个条件：**

```csharp
if (frameWidth <= 0 || frameHeight <= 0 || frames <= 0) return null;   // ① 尺寸/帧数非法
if (fps < 0 || double.IsNaN(fps)) return null;                          // ② 某状态 fps 非法
if (states.Count == 0) return null;                                     // ③ 没有任何可用状态
```

> ⚠️ 注意：**`fps == 0` 不是非法**（v0.1.82 修正，见 §3）。历史版本曾把 `fps <= 0` 判为非法，
> 导致 4 套新皮肤在设置页里**根本不显示**。

---

## 3. 🔴 `fps` 语义（最重要）

`fps` 来自 `skin.json` 的 `states[].fps`，是**唯一权威**（v0.1.76 起，已移除会被静默覆盖的 `anim.json`）。

引擎侧：`src/DesktopPet.App/Renderer/FrameAnimator.cs` 的 `Tick`：

```csharp
var fps = _state.Fps * Playback;
if (fps <= 0) return false;      // ← 0 或负 → 帧永不推进（静止）
```

| `fps` | 引擎行为 | 建议用途 |
|---|---|---|
| **`> 0`** | **动画**：按帧率循环推进 `0→1→…→frames-1→0` | walk / pet / drag / fall / climb / work |
| **`= 0`** | **静止**：`Tick` 直接 `return false`，**帧永不推进**（永远画第 0 帧） | idle / sit / sleep（待机/坐/睡） |
| **`< 0` 或 NaN** | **整张皮肤判为损坏、直接跳过**（设置页里根本不出现） | 禁止 |

> 🔴 **不变量（v0.2.1 起由 UiProbe 全局强制）**：**凡 `fps > 0` 的状态，其各帧像素必须真的不一样**。
> 一张 N 帧全同的图却标 `fps > 0` = 「静止帧冒充动画」：引擎在做无意义的每秒重绘，而用户视觉上什么都没发生。
> 断言覆盖**全部 9 套皮肤**（含 `layout: files` 的逐帧目录），报错会点名到 `皮肤.状态`。
> 反过来说 —— **状态确实是静止的，就该写 `fps: 0`**；这不是偷懒，而是与引擎语义一致的**诚实声明**（见上表）。
> 历史案例：`pixel.sleep` / `soft.sleep` 曾标 `fps: 1` 而 4 帧完全相同，v0.2.1 改为 `0`（视觉零变化）。

> **实测（dev-log v0.1.82）**：用 `fps=0` 表达「猫大部分时间不动」，配合只在「帧真的推进」时重绘，
> 闲置 CPU 增量 **0.000s（0.00%）**（slack 模式）。这是性能上的直接收益。

**历史坑（务必记住）**：一度把 `fps <= 0` 当作「非法 → 整张皮肤损坏」，
结果 4 套写了 `fps:0` 的新皮肤**全被静默跳过**，症状是「设置页里根本没有这几套皮肤」，
日志里只有一行难懂的 `[皮肤] 跳过非法皮肤目录`。**校验规则与引擎语义不一致**是隐蔽 bug 的典型来源。

---

## 4. 9 个状态清单与用途

引擎状态枚举 `PetState`（`src/DesktopPet.Core/Enums.cs`）共 9 个，与 `skin.json` 的 `states[].name` 对应：

| 状态 | 建议用途 | 引擎在什么交互下切到它 |
|---|---|---|
| `idle` | 待机 | 默认状态；空闲随机/交互复归 |
| `walk` | 走动 | 待机时每 tick 约 **2%** 概率触发；走约 **2 秒**后回 `idle` |
| `sit` | 坐下 | 空闲累计 **30 秒**后 |
| `sleep` | 睡觉 | 空闲累计 **5 分钟**后 |
| `pet` | 被抚摸 | 鼠标抚摸猫时 |
| `drag` | 被拖拽 | 鼠标按住拖动时 |
| `fall` | 自由落体 | 拖拽松手、未被吸附时下落 |
| `climb` | 贴边悬挂 | 靠近屏幕边缘贴边时 |
| `work` | 工作模式 | 键盘映射窗 / BongoCat 场景（猫伏低前探） |

> 依据：`src/DesktopPet.App/Behavior/PetStateMachine.cs`（空闲行为 + 阈值
> `SitAfter=30s` / `SleepAfter=5min` / `WalkDuration=2s` / 随机走动 `_rng.NextDouble() < 0.02`），
> 以及 `PetWindow` / 输入层对 `pet/drag/fall/climb/work` 的驱动。
>
> ⚠️ **不必 9 个全做**：缺某状态的 PNG → 该状态被跳过（`SpriteAtlas` `complete=false → continue`），
> 不报错、不影响其他状态。但**提供的状态必须帧数一致**（见 §5）。

---

## 5. 帧图布局

### 5.1 横排 N 帧；`sheet 宽度必须 = frameWidth × frames`

引擎按固定几何切帧（`SpriteAtlas.LoadFromInfo`）：

```csharp
// sheet 布局：一张 neko_<state>.png，横向切 frames 帧
Bitmap = Crop(sheet, f * info.FrameWidth, 0, info.FrameWidth, info.FrameHeight);
```

→ 第 `f` 帧的裁剪矩形是 `(f×frameWidth, 0, frameWidth, frameHeight)`。
**只要 sheet 宽度 ≠ frameWidth × frames，就会出现错位或切到画布外。**

**怎么算（公式 + 示例）：**

```
sheet 宽度 = frameWidth × frames
sheet 高度 = frameHeight
```

| 皮肤 | frameWidth | frames | sheet 宽 | sheet 高 | 备注 |
|---|---|---|---|---|---|
| `pixel`（内置） | 32 | 4 | **128** | 32 | 像素风 |
| `pixelai` | 64 | 4 | **256** | 64 | 64px 像素 |
| `cat_pixel` | 64 | 8 | **512** | 64 | 8 帧真动画 |
| `hd` | 256 | 4 | **1024** | 252 | 高清，recommendedScale=4 |
| `cat_hd` | 256 | 8 | **2048** | 231 | 高清 8 帧 |

### 5.2 🔴 所有状态必须帧数一致

`frames` 是**全皮肤统一**的一个数（`SkinInfo.Frames`），引擎对**每个状态**都按它切帧：

> `build_skin_v2.py` 注释原文：**「引擎按 skin.json 的 frames 从每张图固定切 N 帧；
> 各状态帧数不一致会切到画布外（空白帧闪烁）。」**

**规则：** 每个提供的 `neko_<state>.png` 宽度都必须 = `frameWidth × frames`。
静态状态若只画 1 帧，就把这 1 帧**重复铺满** `frames` 帧（或直接用 `fps=0` 只画第 0 帧，
其余帧内容相同即可）——**但画布仍要给足 `frames × frameWidth` 的宽度**。

### 5.3 `layout: "files"`（独立帧文件）

`soft` 皮肤用的是另一种布局：每状态每帧一张：

```
neko_<state>_<index>.png        # 如 neko_idle_0.png … neko_idle_3.png
```

对应 `skin.json` 里 `"layout": "files"`。宿主按 `neko_{state}_{f}.png` 逐帧加载，
**缺任一帧 → 该状态被跳过**（`complete=false`）。

---

## 6. 尺寸与对齐

### 6.1 `frameWidth / frameHeight` 是「每帧像素尺寸」

- 它们是**源图**里单帧的像素宽高，**只用于把 sheet 切成帧**。
- ⚠️ 宿主**绘制逻辑尺寸常量**是 `SpriteAtlas.FrameW = FrameH = 32`（编译期常量），
  **不随皮肤变化**。窗口的初始尺寸 / 绘制目标 Rect / 眼球层 Rect / 影子定位 / 贴底边界等
  几何计算全部基于 `32`，皮肤源位图由 WPF 在 `Draw` 时自动缩放到 `32 × Scale`。
- **推论**：`frameWidth/frameHeight` 写多少，只影响「怎么切帧」；**显示多大由 `Scale` 决定**。

### 6.2 `recommendedScale` 的作用

- 语义：`> 0` 表示**作者建议宿主在用户切换到本皮肤时把缩放套用到该值**；
  `<= 0`（默认）表示未声明，**宿主不得改用户缩放**。
- 取值：仅 **1.0–8.0**；缺失 / NaN / 越界 / 负数 → 落回 0（不影响皮肤可用性）。
- 套用时机：**「切换到该皮肤」与「重复选择该皮肤」两种**（v0.1.82 修正）；
  用户手动改过缩放后再点该皮肤，会被拉回作者推荐值。
- 未声明的皮肤（如 `pixel` / `soft`）切换时**不改变用户缩放**。

**高清皮 vs 像素皮的做法：**

| 类型 | 做法 |
|---|---|
| **像素皮**（pixel / pixelai / cat_pixel / phoebe_pixel） | 32/64px 源，声明 `recommendedScale: 4.0`（或省略由用户自己开缩放）。像素放大用 **NEAREST** 保硬边 |
| **高清皮**（hd / cat_hd / phoebe_hd） | 源图下采样到 **单帧 ≤ 256px**（`--max-frame 256`），声明 `recommendedScale: 4.0`，开箱即 1:1 |

> **内存动机（v0.1.81）**：hd 皮肤曾是 `677×668 × 4 帧 × 9 状态 × 4B ≈ 62MiB` 常驻，
> 把进程内存推到 245MB（超 <200MB 红线）。下采样到 256px 后降到 ~9MiB，**省 ~53MiB**。
> → **建议单帧 ≤ 256px**。

### 6.3 对齐：统一画布 + 底边对齐（避免切换状态时「猫跳一下」）

- 引擎绘制时按**固定尺寸**画，各状态图若「猫在画布里的位置/大小」不一致，切状态时会**跳一下**。
- 正确做法（`build_skin_v2.py` 的做法）：
  - 取所有状态所有帧的**最大内容宽 / 最大离底 / 最大总高**，建一块**公共画布**（含 `PAD=4` 留白）；
  - 每帧按「**水平居中 + 底边对齐**」贴进公共画布；
  - **保留帧间相对位移**（逐帧重新居中会把动作抹平）。
- 手工画图时同理：**所有状态用同一画布尺寸，猫的脚底落在同一条基线上**。

---

## 7. 眼球层（眼神追踪）

眼球层让猫的瞳孔跟随鼠标移动（渲染层反馈）。

### 7.1 命名与放置

- 文件名：`neko_<key>.png`，其中 `<key>` 在 `skin.json` 的 `eyeLayers` 里声明，例如
  `eyeLayers: ["eyes_open", "eyes_blink", "eyes_scared"]` → `neko_eyes_open.png` 等。
- **必须是整帧尺寸**（= `frameWidth × frameHeight`），与身体帧同位图尺寸，才能**精确叠加**。
- 放在皮肤目录里。
- 关联：`states[].eyeLayer` 指向某个 key，表示该状态叠加哪个眼球层；`null` = 不叠加
  （如 `sleep` 本就闭眼，或眼睛已画在身体里）。

`pixel` 的实例：`eyeLayers` 列 5 个 key，状态里 `idle/…` 用 `eyes_open`、
`pet` 用 `eyes_pet`、`drag/fall` 用 `eyes_scared`、`sleep` 用 `null`。

> 眼球层**按需探测文件是否存在**：声明了 key 但文件缺失 → 该层跳过（不报错）。

### 7.2 🔴 铁律：眼球层必须来自「同一张画的闭眼版」

- **现象（真实翻车）**：眼球层从 `*_base_3x3.png` 与 `*_closed_3x3.png` 差分抠出，但 idle 帧
  现在来自**单独的 still 图**；实测两张画**不是同一张**（橘猫 idle 差 42.7 / sit 56.0；菲比 60~78）
  → 瞳孔贴上去会**错位成漂浮色斑**。
- **根因**：跨素材（不同生成批次）差分出来的瞳孔位置与脸的画法不对应。
- **正确做法**：为每个要眼神追踪的静态状态，**补一张「同一张画的闭眼版」**
  （如 `{cat,phoebe}_still_{idle,sit}_closed.png`），差分才能对齐。
- 附带校验：`sleep` / `pet` 这类本就闭眼/眯眼的状态，差分出来的瞳孔是畸形的
  （实测 112×14、15×32）→ `build_skin_v2.py` 已加**离群校验**（小于中位数 0.35 倍就剔除）。

> 正确抠法（`extract_pupil_layer`）：**只取瞳孔**（眼白留在身体上，瞳孔在其内部平移），
> 而不是取整只眼——取整只眼需要 inpaint 抹掉脸上的眼睛，一平移就露出色块缺口。

---

## 8. thumb.png（设置页缩略图）

- 尺寸：**96×96**，**底对齐**（各皮肤看起来基座一致）；像素风（帧 ≤64px）用 **NEAREST** 放大保硬边，
  高清皮用 LANCZOS。
- 位置：`neko-assets/<id>/thumb.png`。
- 用途：设置页「外观风格」列表每个皮肤一行显示该缩略图（`SettingsWindow.BuildSkinPicker` →
  `LoadSkinPreview`）；**缺失 → 显示占位文字，不影响选择**。

> 缩略图请使用 `thumb.png`。共用的 `build/RuntimeAssets.props` 只收集运行时命名的文件，并排除 preview 与 .bak-*；预览图和备份不会进入输出目录。

---

## 9. 素材生成管线工具

> ⚠️ 这些脚本是**仓库内工具**，源码里 `ROOT` / `RAW` 路径**硬编码**为本机仓库路径
> （如 `G:\lantetn-li\neko`、`neko-assets/_airaw`）。**换机器需先改脚本里的路径常量**
> （或按其参数传入）。Python 用
> `C:/Users/Lantern/.workbuddy/binaries/python/versions/3.13.12/python.exe`。

### 9.1 `tools/analyze_assets.py` —— 批量素材体检

**用途**：把「看起来对不对」变成可读数字。回答四个问题：① 有没有透明背景 ② 画布多大、
按期望网格切出来每格多大 ③ 每格内容尺寸/位置（判断猫的大小是否一致、有没有跑偏/多余道具）
④ 内容是否**贴边**（贴边 = 被裁切，模型画超出格子了）。

```bash
# 体检 _airaw 下所有已知素材（按脚本内置 LAYOUT 清单）
"…/python.exe" tools/analyze_assets.py
# 或指定单张图 + 网格
"…/python.exe" tools/analyze_assets.py neko-assets/_airaw/cat_base_3x3.png --grid 3x3
```

输出含：不透明像素占比（≥99.5% → 可能没透明背景需抠图）、每格内容包围盒、
内容尺寸一致性（离散度 <12% 算一致）、贴边格子数。

### 9.2 `tools/find_grid.py` —— 判定是否「真分成独立格子」

**用途**：图像模型常把「N 格序列条」画成一整张连续长图（身体跨过格线），硬切会把猫切成碎片。
本工具沿 X / Y 做 alpha 投影数「孤岛」，并做**格线穿越检测**。

```bash
# 扫 _airaw 下所有序列条（期望 6×1）与九宫格（3×3）
"…/python.exe" tools/find_grid.py --all
# 或指定单张图
"…/python.exe" tools/find_grid.py neko-assets/_airaw/cat_seq_walk.png --cols 6 --rows 1
```

判据：`孤岛数 == 期望格数` → ✅ 有真正间隙，可安全硬切；`== 1` → ❌ 连成一片，硬切必碎；
格线穿越检测报出「有内容穿过的格线号」。

### 9.3 `tools/build_skin_v2.py` —— 静态图 + 序列条 → 皮肤

**用途**：把豆包素材组装成皮肤（v2：待机/坐/睡用**静态图**，其余用**序列条**）。
关键策略：**先试等分并校验**（无空格 + 内容高离散 <20%），不合格才退回孤岛切；
**跨画布归一**（序列条每格 512 / 静态图整张 1024 → 按画布比缩放）；**调色板全皮肤共享**。

```bash
# 像素皮肤
"…/python.exe" tools/build_skin_v2.py --prefix cat    --mode pixel --id cat_pixel --name "像素猫（动态）"
# 高清皮肤
"…/python.exe" tools/build_skin_v2.py --prefix cat    --mode hd    --id cat_hd    --name "高清猫（动态）"
"…/python.exe" tools/build_skin_v2.py --prefix phoebe --mode hd    --id phoebe_hd --name "菲比（高清）"

# 关键可选参数（v0.1.81 起）
#   --max-frame 256            高清模式长边上限（默认 256，超限 LANCZOS 下采样）
#   --recommended-scale 4.0    写进 skin.json 的 recommendedScale（默认 4.0）
#   --no-eyes                  跳过眼球层提取
```

**输出**：`neko-assets/<id>/` 下的 `neko_<state>.png`（每状态一张 `frames×frameWidth` 的 sheet）、
可选 `neko_eyes_<state>.png`、以及 `skin.json`。

### 9.4 `tools/gen_skin_previews.py` —— 生成 `thumb.png`

**用途**：为每套皮肤生成设置页缩略图 96×96、底对齐。

```bash
# 全部皮肤
"…/python.exe" tools/gen_skin_previews.py
# 指定皮肤
"…/python.exe" tools/gen_skin_previews.py cat_pixel
```

> 🔴 **换素材后必须重跑本脚本**：`thumb.png` 取自 `neko_idle.png` 的第 0 帧，
> 素材变了不重跑 → 缩略图与实物不符。

---

## 10. 构建与发布自动收集（v0.2.7）

宿主、UiProbe 与 ReliabilityProbe 共用 `build/RuntimeAssets.props`，新增随包皮肤无需再逐个修改 csproj。将目录放在 `neko-assets/<id>/`，提供 `skin.json`、`neko_*.png` 和可选 `thumb.png` 后正常构建即可。

- 按资产根目录保留子目录路径；`skin.json` 的 id 必须等于目录名。
- 只收集运行时命名的清单、帧图、眼层、阴影与缩略图；排除 reference、下划线临时目录、预览、备份与模型源文件。
- 图标、键盘爪、UI 资源和托盘图标由同一规则单独收集；发布时保留为单文件 EXE 外的真实文件。
- 验证目录结构应使用全新的 BaseOutputPath，避免旧产物掩盖漏复制。不要删除用户数据目录。
- 用户接入运行时皮肤仍直接放在程序旁的 `neko-assets/<id>/`；设置页「刷新皮肤」可重新扫描并加载当前皮肤，无需重启。
- 状态名、眼层名不能包含路径字符；状态不能重名，fps 必须为有限非负数，frames 上限 256。未知 layout 会明确记录错误。
- 损坏的非待机状态会被跳过，损坏眼层/阴影会被忽略；待机 idle 不可用时回退默认 pixel，并把实际选择写入配置。缺省皮肤素材也不存在时仅能保活并写日志，不能凭空恢复缺失美术。

---

## 11. AI 生图提示词要点

> 这一段的价值在于「**告诉后来者哪条路已经被证伪**」，省下重复试错。

### 11.1 ❌ 「横向 N 格序列条」这条路走不通

- 实测相邻帧差异（`v1 6 格`：9~34%；`v2 8 格`，prompt 明确写了「相邻格位移 ≤1/12 身高」：**13~42%**）。
- **根因**：图像模型每画一格都会**重新构形**，做不到帧间连贯 —— **这不是 prompt 问题，是图像模型的固有限制**。
  （三次尝试后放弃。）
- 副作用证据：v2 素材是按 512 **等分**排的（硬切没问题）；v1 素材位置会漂移，按格线切会切出「半只猫」。

### 11.2 ✅ 推荐路线：图生视频 + 抽帧

```
静态图 → 图生视频（3 秒循环动作）→ 抽帧 → 真·逐帧
```

**三条硬约束（缺一不可）：**

1. **镜头必须固定**（不能推拉摇移）；
2. **角色不能位移 / 缩放**（保持在画面同一位置、同一大小）；
3. **背景纯绿 `#00FF00`**（视频无 alpha，靠色键抠背景）。

### 11.3 ✅✅ 静态待机（效果反而最好）

只画 **1 张图**，配合 `fps = 0`（静止）+ 程序侧呼吸/眨眼，**效果最好**，且省内存、省 CPU。

### 11.4 抠白底要用 flood-fill

模型交付常见白底/浅底。抠图**必须 flood-fill**（只抠与图像**边缘连通**的背景）——
全局容差会把**奶白肚皮一起抠掉**（第一版就这么翻车的）。

### 11.5 像素皮调色板必须「全皮肤共享」

逐帧各自 `quantize` → 每帧调色板不同 → 同一块毛色在帧间跳变（**动画发闪**）。
实测逐帧量化产生 **728 色**；改成「所有状态 × 帧拼一张长图统一定调色板再套用」后降到 **22~23 色**。

---

## 12. 常见错误速查表

| 症状 | 原因 | 解法 |
|---|---|---|
| **设置页里看不到我的皮肤** | `skin.json` 字段非法 → 整张皮肤被跳过：`frameWidth/Height/frames <= 0`、某状态 `fps < 0` / NaN、`states` 为空 | 检查三项合法性；看日志 `[皮肤] 跳过非法皮肤目录` |
| 同上（且 fps 写的是 0） | 误以为 `fps=0` 非法 | `fps=0` **合法**（静止）；只有 `<0` / NaN 才非法（见 §3） |
| 同上（内置皮肤，源码正常） | 未按运行时命名或尚未重新构建 | 检查共用运行素材规则与全新输出目录（§10） |
| 同上（UGC，素材正常） | 目录里没有 `skin.json`，或放错位置 | 确认目录在 `neko-assets/<id>/` 且**确实有 `skin.json`** |
| **动画闪烁/发闪** | 逐帧各自量化调色板（像素皮） | 调色板**全皮肤共享**（拼一张长图统一定调色板） |
| **切换状态时猫跳一下** | 各状态未做统一画布 / 底边对齐 | 统一画布尺寸 + 底边对齐 + 保留帧间相对位移（§6.3） |
| **动画有空白帧闪烁** | 各状态帧数不一致（引擎按 `frames` 固定切帧，切到画布外） | 所有状态帧数统一；sheet 宽 = `frameWidth × frames`（§5） |
| **某个状态显示空白/错位** | sheet 宽 ≠ `frameWidth × frames` | 按公式重排（§5.1） |
| **切到新皮肤还是很小 / 尺寸不对** | 未声明 `recommendedScale`（高清皮），或声明值越界（>8 落 0） | 高清皮声明 `recommendedScale: 4.0`（1.0–8.0 内） |
| **切到皮肤后缩放被改回** | 这是 v0.1.82 的**预期行为**（推荐缩放套用时机 = 切换 + 重复选择） | 若不想被改，去掉 `recommendedScale` |
| **眼神追踪：瞳孔变成漂浮色斑** | 眼球层跨素材差分（不同批次画不一致） | 眼球层必须来自**同一张画的闭眼版**（§7.2） |
| **眼神追踪：眼睛完全不动** | 静止皮肤（`fps=0`）+ 渲染未在瞳孔偏移时重绘 | 引擎侧已修（`SetEyeOffset` 变化时触发重绘）；确认你的 `eyeLayer` 有正确关联 |
| **设置页缩略图空白** | `thumb.png` 缺失，或被命名成 `preview.png` 遭运行素材规则排除 | 命名 `thumb.png`；用 `gen_skin_previews.py` 生成（§8/§9.4） |
| **换素材后缩略图还是旧的** | 没重跑 `gen_skin_previews.py` | 换素材后**必须**重跑（§9.4） |
| **打包进安装包后缩略图/皮肤丢失** | `LinkBase` 丢了子目录名 → 素材被摊平 | 检查共用规则与输出目录结构（§10） |
| **"UiProbe 全过"但其实素材没生效** | bin 里有上一版嵌套残留 → 假绿 | 改用全新的 BaseOutputPath 验证，不依赖旧 bin 残留（§10） |

---

## 附录 A：皮肤-实现一致性核对

> 逐字段阅读 v0.1.82 源码后的结论。

### 已核实一致

| 项 | 结论 |
|---|---|
| `skin.json` 字段名 / 默认值 / 校验 | 与 `SkinRegistry.TryLoad` 一致（含 `recommendedScale` 1.0–8.0 与 `fps` 校验） |
| `fps` 语义（>0 动画 / 0 静止 / <0·NaN 整张非法） | 与 `FrameAnimator.Tick` + `SkinRegistry` 一致 |
| sheet 切帧公式 `(f×frameWidth, 0, frameWidth, frameHeight)` | 与 `SpriteAtlas.LoadFromInfo` 一致 |
| `layout: sheet/files` 两种布局 | 一致 |
| 绘制常量 `FrameW/FrameH = 32` 不随皮肤变化 | 一致（`SpriteAtlas` 注释明确） |
| 眼球层命名 `neko_<key>.png` / 整帧尺寸 / `states[].eyeLayer` 关联 | 一致 |
| `thumb.png` 96×96、`preview*` / `*.bak-*` 被排除 | 一致（宿主与测试共用 RuntimeAssets.props） |
| 皮肤发现 = 扫目录 + `skin.json`；非法跳过 + `Log.Warn`；兜底 `pixel` | 与 `SkinRegistry.Scan` 一致 |

### 需注意 / 待确认项

1. **`eyeLayer` 的「整帧尺寸」要求来自实践约定，而非 `SkinRegistry` 的显式校验**。
   源码只按 `neko_<key>.png` 路径尝试加载（`if (File.Exists(p))`），**不校验尺寸**；
   「必须整帧尺寸」是渲染叠加能对位的**前提**（尺寸不符 → 叠加错位）。属**约定**，请遵守。
2. **「所有状态帧数必须一致」是引擎几何推论 + dev-log 记录，非显式校验**。
   `SkinRegistry` 不校验各 sheet 的实际帧数，只有 `frames` 一个全局值；不一致会在渲染期表现为
   切帧越界/空白帧（`build_skin_v2.py` 已按此统一帧数）。属**必须遵守的约定**。
3. **`tools/*.py` 的 `ROOT` / `RAW` 路径硬编码为本机仓库路径**（`G:\lantetn-li\neko`）。
   换机器/AI Agent 使用前需改脚本常量（或按脚本支持的参数传入）。
4. **`SpriteAtlas` 的注释里提到「柔光版源位图是 128 像素」**，与当前 `soft/skin.json` 写的
   `frameWidth: 32` 并存——`frameWidth/frameHeight` 只用于切帧，`soft` 是 `layout: files`
   逐帧文件（尺寸即单帧），不受 sheet 切帧影响。此处以**实际 `skin.json` 为准**。

---

## 附录 B：自绘素材规格（手工 / Aseprite）

> 本附录是原《像素猫素材规格书》的精华，给**手工自绘**像素皮的人参考。**程序化 / AI 生成**路线见 §9 / §11；两边的**尺寸/帧数/命名约定一致**，可互换。

### B.1 总览

| 项目 | 规格 |
|---|---|
| 单帧画布 | **32 × 32 px**（像素风经典配比） |
| 屏幕显示 | 放大 **整数倍**（如 3×、4×）；**保持像素锐利，禁止非整数缩放 / 抗锯齿** |
| 每状态帧数 | **4 帧**循环（= `skin.json` 的 `frames`；全状态统一） |
| 图片格式 | **PNG-32**（带 alpha 通道） |
| 缩放算法 | 最近邻（Nearest Neighbor）——导出时**不要**缩放，交给宿主 |
| 主色调 | 橙白（可爱橘猫） |

### B.2 调色板（固定 12 色，便于后续换装配色）

| 用途 | 色值 | 说明 |
|---|---|---|
| 描边（主） | `#3A2A20` | 深棕黑外轮廓。**不要用纯黑**，纯黑在桌面上太生硬 |
| 毛色·亮部 | `#F7B872` | 橙猫主色，受光面 |
| 毛色·中间调 | `#EFA055` | 橙猫主体色，面积最大 |
| 毛色·暗部 | `#C97A38` | 阴影、条纹、层次 |
| 毛色·最深 | `#A85F26` | 条纹暗部、耳内边缘 |
| 白色·亮部 | `#FFF8F0` | 肚子、爪子、嘴周、胸口 |
| 白色·暗部 | `#E8DCCB` | 白色区域的阴影 |
| 眼睛·瞳孔 | `#2A1E18` | 眼球主体 |
| 眼睛·高光 | `#FFFFFF` | 瞳孔高光点（1–2 px，点睛之笔） |
| 鼻子/嘴 | `#E88B8B` | 粉色 |
| 肉垫 | `#F2A0A0` | 粉色，比鼻子稍亮 |
| 舌头 | `#D9738C` | 张嘴/呼噜噜时 |

> 条纹建议：橘猫虎斑用「暗部色」画 2–3 道，额头画「M」形纹、尾巴画环纹。**不要满身都是**，会显脏。
> **调色板必须全皮肤共享**（逐帧各自量化会导致帧间毛色跳变/发闪，见 §11.5）。

### B.3 朝向处理 —— 只画一个方向，宿主镜像

- 走路需要左右两个方向，但**不要画两套**：雪碧图统一画**朝右**，宿主向左走时用
  `ScaleTransform(-1, 1)` **水平翻转**。
- 镜像后若有不对称元素（刘海分缝、尾巴卷向）会左右颠倒——这是像素游戏通用做法，可接受。

### B.4 各状态动作要点（8 状态；第 9 个 `work` 见 §4）

> ⚠️ 下表 fps 是**立项期的默认建议值**；现行以各皮肤 `skin.json` 的 `states[].fps` 为**唯一权威**（§3）。

| 状态 | 建议 fps | 动作要点 |
|---|---|---|
| `idle` 待机 | 2 | 四脚站立，尾巴缓慢摆动；帧差极小（身体上下 1px 呼吸 + 尾巴位置）。呼吸感来自「慢」，**不要画成抖动** |
| `walk` 行走 | 8 | 经典对角步态：帧1 左前+右后抬起 → 帧2 落地 → 帧3 右前+左后抬起 → 帧4 落地；身体上下起伏 1px |
| `sit` 坐下 | 2 | 前腿直立、后腿盘坐、尾巴绕到身前；帧差=尾巴尖轻摆 + 偶尔耳抖 |
| `sleep` 睡觉 | 1 | 蜷缩成团，眼睛闭合成下弯弧线（**画进身体帧，不叠眼球层**）；帧差=身体缓慢起伏 1px |
| `pet` 被抚摸 | 10 | 头部微仰、眯成弯月眼、尾巴快摆、身体轻微挤压（squash）——**要画得明显愉悦** |
| `drag` 被拖拽 | 6 | 四肢悬空乱蹬、尾巴炸毛、眼睛瞪大（宿主会加轻微旋转） |
| `fall` 落下 | 12 | 四肢张开呈「飞」姿、尾巴上飘；帧间形变加大体现加速度 |
| `climb` 悬挂 | 4 | 前爪抓屏缘、身体下垂；尾巴慢摆、后腿偶尔蹬一下 |

### B.5 导出设置

**Aseprite**：`File → Export Sprite Sheet` → Layout **Horizontal Strip**；Constraints `Frames: All`；**取消勾选** `Merge Duplicates`（会打乱帧序）；Output `Scale: 1`（**不要**在导出时缩放）。**LibreSprite** 同理（`File → Export → Export Sprite Sheet`）。

**托盘图标 `tray.ico`**：用猫头特写（只画头部+耳朵）；需 **16/32/48/256** 四尺寸；**16×16 要重画简化版**（不能靠缩放，会糊成一团）。导出：
```bash
magick convert neko_head.png -define icon:auto-resize=256,48,32,16 tray.ico
```

### B.6 质量检查清单（画完逐条自检）

- [ ] 所有 PNG 都是 **32 的整数倍宽度**（如 128×32 的 sheet，或 32×32 单帧）
- [ ] 没有抗锯齿边缘（放大 400% 检查，边缘应是硬像素块）
- [ ] 所有帧的**脚底在同一水平线**（否则走路会「飘」）
- [ ] 眼球层与身体层坐标对齐（叠加测试：两层叠一起严丝合缝）
- [ ] `sleep` 帧自带闭眼、**不含**眼球层位置
- [ ] 描边闭合，没有断线
- [ ] 调色板只用 B.2 的 12 色
- [ ] 文件命名完全一致（**大小写敏感**）
- [ ] 所有状态的 sheet 宽 = `frameWidth × frames`

---

*文档版本：v2.0（合并像素素材规格书）｜ 基于宿主 v0.1.83 源码与 dev-log/CHANGELOG 撰写。*

## v0.2.9：逐帧眼层与动作朝向

状态可选 `eyeOffsets`，例如四帧使用 `[{"x":0,"y":0},{"x":0,"y":1},{"x":0,"y":0},{"x":0,"y":1}]`。数组长度必须等于全局 `frames`；坐标以原始帧像素为单位，必须有限且绝对值不超过对应帧尺寸。偏移同时作用于眼层及眼眶遮罩，预览工具使用首帧偏移。省略时保持原有固定眼层。

`walk` 可声明 `"facing":"left"` 或 `"facing":"right"`，表示原画朝向，宿主在反向移动时镜像整帧，并保持视线朝向正确。省略或使用 `"none"` 时不镜像。此规则替代附录 B.3 的旧朝向约定。

可选状态 `land` 在物理下落实际结束后播放一次，然后回到待机；应让最后一帧自然衔接 `idle`。缺少此状态的旧皮肤保持原有行为。默认 pixel 现在包含十个状态，每状态四帧；睡眠仍为静态。

## v0.3.2：眼神跟随边界

追踪位移与动作锚点分开处理。追踪默认最多水平 0.65、垂直 0.35 个逻辑像素（宿主 32×32 坐标；原生 pixel 等于源像素），斜向使用椭圆范围；有素材安全范围时再取更小值。缺少闭合眼眶层不会放开位移。闭眼、半闭和抚摸眯眼固定在脸部；eyeOffsets 仍用于逐帧点头、下蹲，不能用加大追踪位移补偿锚点错位。
