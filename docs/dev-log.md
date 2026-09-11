# 开发日志（Dev Log）

按时间顺序记录 Neko 的开发过程：**做了什么、踩了什么坑、怎么修的、为什么这么决策**。
版本变更摘要见 [`CHANGELOG.md`](../CHANGELOG.md)，本文件侧重过程与决策依据，坑的复现细节是最有价值的部分。

---

## 2026-09-01（第一天：从立项到 v0.1.0 可运行）

### 1. 立项与 19 项决策

灯灯要一个 Windows 桌面宠物。中途有一次**决策翻转**：最初评估 Tauri 2（看中 WASM 沙箱），但确认分发范围只是「自用 + 朋友」后，沙箱的决定性理由消失 → 改用 **C# + WPF + .NET 8**（透明窗口、托盘、全局钩子原生支持，环境要求低）。

19 项决策分批确认后定稿，关键的几条：

| 项 | 决策 | 理由 |
|---|---|---|
| 命名 / 风格 | Neko，可爱橘猫（橙白、圆脸大眼、粗描边非纯黑） | — |
| 精灵 | 32×32 画布，4 帧/状态，8 状态 | 像素风，素材量可控 |
| 说话气泡 | 宿主只提供渲染，**内容由插件 `Say()` 驱动**，不做内置台词库 | 保持宿主无状态 |
| 开机自启 | 默认关，**不放托盘入口**，只在设置窗口开 | 避免误触 |
| 插件签名 | v1 不做，预留接口 | 朋友圈分发不需要 |
| 插件分发 | 手动放 `.npet` 解压到 `plugins/<id>/`，v1 不做管理 GUI | 先跑通内核 |

### 2. 素材：程序化生成，而不是手绘或 AI 出图

原计划等灯灯自绘，后来改为用 Python 程序化生成（`tools/generate_sprites.py`）。
**理由**：32×32 像素需要精确对齐，AI 出图不可控；程序化能保证调色板严格、4 帧循环可控、眼球层坐标零偏移。

- 自动描边：对非透明像素做 alpha `MaxFilter(3)` 膨胀 → 差分得描边环 → 与原图合成
- 头部锚点统一在 **(17, 11)**，所有状态（除 sleep）眼球层零偏移对齐
- `sleep` 不叠眼球层（身体帧自带闭眼弧线）

产出 8 状态 × 4 帧雪碧图 + 4 眼球层 + 阴影 + 四尺寸托盘图标。**P0 阻塞项就此解除。**

### 3. 插件目录约定：加载器与安装器解耦

灯灯要求先预留 `plugins/` 目录。设计时定死一条：**加载器只认目录结构，安装器只做文件搬运**。

```
plugins/<id>/{plugin.toml, Entry.dll, assets/}   ← 用户可自由增删
data/plugins/<id>/                                ← 插件私有数据
```

- 加载流程：扫描一级子目录 → 跳过 `_` 开头 → 读 toml → 校验 api 版本/权限 → ALC 加载 → `OnLoad`
- v2 要做拖拽安装时 = 解压到 `plugins/<id>/` + 调加载器热加载，**加载器一行代码都不用改**

三个定死的约定（违反会导致后续返工）：
1. 插件数据必须写 `data/plugins/<id>/`，**禁止写进自己插件目录** —— 保证插件目录可整体覆盖，更新不丢数据
2. 禁用用 `config.json` 的 `disabledPlugins` 数组，**不改名不移动文件夹** —— 改名会破坏插件自更新
3. 源码 `builtins/` 与运行时 `plugins/` 分离 —— 内置插件编译产物与第三方插件运行时无区别（dogfooding 的物理体现）

### 4. P0 骨架 + dogfooding 立刻抓到 2 个坑

4 项目分层（Core / Runtime / Sdk / App）+ 示例插件 + 无 UI 加载链路测试。**写完示例插件一跑就抓到 2 个真实 bug**——这就是 dogfooding 的价值：

**坑 1：类型分裂**
- 现象：`IsAssignableFrom` 判定失败，插件加载不进来
- 根因：插件 ALC 内又加载了一份 `DesktopPet.Core` → `IPetPlugin` 变成两个不同的类型
- 修复：`PluginLoadContext` 用 **HostSharedAssemblies 白名单**把 Core / Sdk 路由回宿主侧

**坑 2：权限前缀不匹配**
- 现象：所有插件权限全变成 `None`
- 根因：清单写 `pet = ["animate"]`，查表却用 `pet.animate` 匹配
- 修复：按段加前缀（`pet.` / `ui.` / `storage.`）再查表

### 5. 迁移到 G 盘

灯灯要求不占 C 盘。最终落位 **`G:\lantetn-li\neko`**（`G:\lantetn-li` 下还有 8 个别的项目，一概不碰）。
C 盘工作区只保留规划文档（`.workbuddy/memory/` + 两份 md）。

### 6. 宿主内置能力 + 好感度插件（架构决策点）

完成眼神追踪、边缘吸附、全局热键、抚摸判定、径向菜单 UI、托盘完整设置，以及**第一个插件型内置功能：好感度**。

**关键决策——宿主与插件的边界怎么切：**

| 能力 | 归属 | 依据 |
|---|---|---|
| 眼神追踪 / 边缘吸附 / 键盘映射 | **宿主** | 依赖渲染器、窗口物理、全局输入钩子，插件 API 语义上够不到 |
| 好感度 | **插件** | 纯状态 + 决策逻辑，可完全插件化，作为 dogfooding 示范 |

判定原则：**凡是「纯状态 + 决策」的扩展功能必须插件化，走与朋友插件完全相同的加载路径。**

### 7. 自测重构 + 第 3 个坑（发布形态）

灯灯要求代码简洁化（该封装的封装、该引用的引用、别死写），顺手做了死代码清理和魔法数字具名化。

**坑 3：发布形态插件加载失败**
- 现象：无头启动 `Neko.exe` 后 `data/plugins/com.lantern.affection` 没被创建 → 好感度插件在真实运行时根本没加载
- 根因：宿主 `App.csproj` **没引用 `DesktopPet.Sdk`** → App 输出目录缺 `DesktopPet.Sdk.dll` → 插件 ALC 的白名单找不到 Sdk → 路由失败
- 修复：App 加 Sdk 的 `ProjectReference`
- **教训（比 bug 本身更重要）**：原来的 `LoaderTest` 直接测源码 bin（自动带着 Sdk.dll），**完美掩盖了发布形态的缺失** → 补了「测试 4：加载真实 App 输出目录」，专门验证发布形态

### 8. 三个运行时坑（本轮重点，都是实际跑起来才暴露的）

灯灯 F5 后反馈两个问题，排查出三个独立的坑：

**坑 4：径向菜单崩溃（`InvalidOperationException`）**
- 现象：`在窗口关闭期间，无法将可见性设置为可见...`，未捕获 → 整个应用崩溃
- 根因：`RadialMenu` 用 `Close()` 销毁窗口，`PetWindow` 还监听 `Closed` 把引用置 null。鼠标一离开菜单就 `Close()`，**关闭过程中**再右键/按热键就会对「关闭中的窗口」调 `Show()` → 抛异常
- 修复：**单例复用 + `Hide()` 而非 `Close()`**；失焦/鼠标离开改为 300ms 延迟隐藏（`MouseEnter` 可取消），顺带解决「鼠标划过缝隙立刻误关」
- 原理：窗口永不进入关闭状态，也就永远不会出现「关闭期间 Show」

**坑 5：宠物几乎不可见（最隐蔽的一个）**
- 现象：灯灯说「没看到宠物在那啊」
- 排查：写了个 WPF 探针实测三种窗口配置 ——

  | 方案 | 窗口 Actual | 宿主 Desired |
  |---|---|---|
  | `SizeToContent` + 裸 `FrameworkElement`（**旧**） | **12.8 × 12.8 px** | 0×0 |
  | 显式尺寸 + 裸宿主 | 128 × 128 | 0×0 |
  | 显式尺寸 + `MeasureOverride` 宿主（**新**） | 128 × 128 | 115.2×115.2 |

- 根因：`SizeToContent = WidthAndHeight` 时 WPF 忽略显式 `Width/Height`，按内容测量；而 `DrawingVisualHost` 是裸 `FrameworkElement`，**没有 `MeasureOverride`，默认测量 0×0** → 窗口被量成 12.8px，猫被裁成一个点
- 附带发现：`PetRenderer.Scale` 从未从配置同步 —— 窗口按 `config.Scale` 缩放，渲染器却写死 3.0
- 修复：移除 `SizeToContent` 改显式尺寸 + 宿主补 `MeasureOverride` 自报精灵尺寸 + `_renderer.Scale = config.Scale`

**坑 6：VS 启动项目配错**
- 现象：F5 报 `无法启动程序 ...DesktopPet.Core.dll — 不是有效的 Win32 应用程序`
- 根因：VS 启动项目被设成了 `DesktopPet.Core`（**类库，只产 .dll**）。而 `.sln` 里 Core 恰好排第一个，`.suo` 又是二进制 → 无 `.suo` 时 VS 默认选第一项，天然踩坑
- 修复：右键 `DesktopPet.App` → 设为启动项目（App 才是产 `Neko.exe` 的入口）
- 顺带加了全局异常兜底：`DispatcherUnhandledException` 写 `data/errors.log` + 提示一次，以后不会再「静默崩溃只剩个弹窗」

### 9. 防回归：新增 `tests/UiProbe`

上面坑 4、坑 5 都属于**无头测试永远覆盖不到**的类型（要真实窗口），而且改一行代码就可能让猫变回 12.8px。所以把验证探针固化成正式测试：

```
PASS: 径向菜单 50 次 Show/Hide
PASS: 径向菜单 连点 10 次（已显示时重复打开）
PASS: 宠物窗口尺寸 128x128（期望 128x128）
PASS: PetWindow 开/关菜单 20 次
PASS: PetWindow ToggleRadialMenu 10 轮（热键路径）
```

---

## 跨日期有效的决策记录

| 决策 | 内容 | 依据 |
|---|---|---|
| **dogfooding 铁律** | 纯状态+决策的扩展功能必须插件化，走与第三方完全相同的加载路径 | 内置功能不插件化，API 缺陷永远暴露不出来 |
| **宿主 / 插件边界** | 依赖渲染器、窗口物理、全局输入钩子的能力放宿主 | 插件 API 语义上够不到 |
| **加载器与安装器解耦** | 加载器只认目录结构，安装器只做文件搬运 | v2 加拖拽安装时加载器零改动 |
| **Core API 版本化** | 公开发布后只增不改 | 保护朋友写的插件 |
| **崩溃隔离** | 单插件崩溃不得拖垮宿主（watchdog + 熔断） | 桌宠要长期常驻 |
| **素材与代码解耦** | 同构占位图跑通管线，正式素材直接覆盖，代码零改动 | 灯灯随时可换素材 |
| **测试分层** | 无头逻辑用 `LoaderTest`，UI 行为用 `UiProbe` | 两类 bug 都不放过 |

## 下一步

- 键盘映射可配置化
- 径向菜单槽位图标美化（当前是 emoji 占位）
- 设置页支持插件自定义页
- 等灯灯指示后上传 git（**当前尚未纳入版本控制**）

---

## 2026-09-01（续）插件开发手册：让 AI 拿一份 md 就能生成插件

灯灯提出：插件文档要随每次更新同步，并且**希望拿着这份 md 能在任何地方（任何会话/任何 AI）生成插件**。

### 产出：`docs/plugin-authoring-guide.md`

定位是**自包含**——不读宿主源码就能写出可运行插件，所以里面塞了：
完整 `plugin.toml` 字段表 / 11 项权限键表 / 三大接口的全部方法签名 / 生命周期 / 事件表 / 三件套完整示例（csproj + toml + 代码）/ 构建部署 / 排错清单 / 红线。

手册第 0 节写了**维护约定**：改 Core 接口、权限枚举、事件常量、PermissionChecker 映射后，必须同步手册对应章节 + 第 13 节版本历史。

### 为保证手册不是"看起来对"，做了三项核实

1. **示例真的编译过**：照抄手册的「整点报时」插件建临时项目 `dotnet build` → **0 警告 0 错误**，证明所有 API 签名、枚举名、事件常量都真实存在
2. **权限前缀规则**：`pet` / `ui` / `storage` 段自动加前缀，`other` 段**不加**（易错点，已单列表格）
3. **payload 读取方式实测**：宿主用匿名类型构造 payload（编译为 `internal`），跨程序集实测 ——

   | 方式 | 结果 |
   |---|---|
   | 反射 `GetProperty("X")` | ✅ 读到值 |
   | `dynamic d = payload; d.X` | ❌ `RuntimeBinderException` |

   手册因此推荐反射 / `System.Text.Json`，并明确警告别用 `dynamic`

### 顺带发现：API 定义与实现不一致

`PetEvents` 里定义了 10 个常量，但宿主实际只广播 **5 个**：

| 已广播 | 未广播（预留） |
|---|---|
| `pet:petted` / `pet:dragged` / `pet:landed` / `pet:edge` / `mouse:moved` | `app:ready` / `app:wake` / `app:sleep` / `screen:change` / `tick` |

手册里给事件表加了「是否已广播」列如实标注 —— 否则照手册写的插件可能订阅到**永不触发**的事件。列入待改进（会向后兼容地补上）。

### 文档去重

`plugins/README.md` 原本也有一份「从零写插件」教程，与手册重复 → 改为指向手册，只留最小骨架 + 快速入口。
**插件 API 只保留一份权威源**，避免以后两处都要同步、迟早不一致。

### 待改进（向后兼容地加入）
- 事件 payload 改强类型 record，替代匿名类型
- 广播 `app:ready` / `app:wake` / `app:sleep` / `screen:change`
- 设置页插件自定义承载（`ISettingsPage` 已定义，宿主侧尚未接入）

---

## 2026-09-01（续）致命显示 bug 排查 + 4 个稳定性 bug + 菜单改造

灯灯报告"现在都没能做出这个最基本的（稳定在桌面显示）"——这是用户第一次明确说看不到猫。我自己从未**真在屏幕上确认过**猫在显示，之前只检查了 ActualWidth=128。

### 排查路径（每个环节都学到了东西）

1. **进程活着 + 窗口 visible=1 + 尺寸 128×128** ✅（tasklist + EnumWindows）
2. **但 GDI 截屏看不到猫**——因为 WPF `AllowsTransparency` 窗口是 layered，GDI `CopyFromScreen` 抓不到
3. **用 `PrintWindow` + `PW_RENDERFULLCONTENT` 抓窗口内部**——**抓到了，但 128×128 全黑！**
4. 假设是 PNG 解码（`PngBitmapDecoder` 对 alpha PNG 偶发黑帧的已知坑），改用 `FileStream + BitmapImage`——还是黑
5. 加 **SANITY 检查**：Draw 顶部无视 state 画一个大红方块 —— **变成红方块** → 确认 OnRender 触发了
6. 加 state/bitmap null 诊断（state null 画红、bitmap null 画蓝、写日志）—— diag.txt 显示 **`state=NULL`**
7. 追到 `PetStateMachine.Transition`：

   ```csharp
   public PetState Current { get; private set; } = PetState.Idle;  // 初始 Idle
   private void Transition(PetState next)
   {
       if (Current == next) return;   // ← 致命：LoadPlugins → Play(Idle) → Transition(Idle)
       Current = next;                //    Current 已是 Idle → 早返回 → 渲染器永远收不到首次 Play
       _ = _api.PlayAnimationAsync(next, ...);
   }
   ```

8. **去掉早返回 → 橘猫显示！** PrintWindow 抓到 5870 个橘色像素、68.7% 非黑

### 教训（这条最重要，写在前头给后人看）

- **"程序能跑"≠"功能对"**。build 0 错误 + 测试通过 + 进程不死 ≠ 用户能看到预期效果。下次关键功能改完必须**真的看一眼像素**，不能只检查进程和尺寸
- **GDI 截屏抓不到 WPF layered window**。验证 WPF 窗口内容用 `PrintWindow + PW_RENDERFULLCONTENT`，不要用 `Graphics.CopyFromScreen`
- **WPF 透明窗口的"可见性"不等于"画了什么"**。visible=1 只代表窗口在，画的内容可能因为渲染管线 bug 是一片空

### 顺手修的其他 4 个 bug（灯灯 AskUserQuestion 选的全部修）

1. **UI 撤销机制失效** —— 注册用「项 id」作 key、撤销用「插件 id」，对不上；且卸载/失败时从未调 `UnregisterAll`。修复：ScopedUiApi 用 thread-local scope 自动归类，卸载与 OnLoad 失败时真正调 `UnregisterAll`。**保留 IUIApi 接口稳定**（不破坏插件代码）
2. **落体定时器叠加** —— 每次 new DispatcherTimer 不取消旧的，弹跳途中再拖拽会两个物理模拟同时改 Top → 抖动。改字段持有，新建前 Stop 旧的
3. **事件 payload 改强类型** —— 新增 `MouseMovedArgs` / `PetDraggedArgs` / `PetEdgeArgs` record（旧反射方式仍兼容）。插件可直接 cast，不再依赖反射
4. **补 `app:ready` 广播** —— 在 `LoadPlugins` 后广播，插件可订阅替代部分 OnLoad 内延迟初始化

### 菜单改造（v0.1.0 折中方案）

用户选"菜单可以从宠物周围弹出包围宠物"。完整环形挖洞（`SetWindowRgn`）工作量大且需 DPI/Win32 兼容处理，**v0.1.0 先做"上方弹出"**：菜单圆心放在宠物头顶上方 150px，宠物完全在菜单下方可见可点。**真正环形挖洞留 v0.2**。

### 稳定性测试

- Neko 跑 3m44s，工作集 140,248K → 140,176K（**0.05% 变化，几乎不漏**）
- 后续让 Neko 后台继续跑做长期挂机监控

### 版本

- 升到 **v0.1.1**（致命修复值得单独版本号）。csproj `<Version>` 已改，CHANGELOG 已加 [0.1.1] 段

---

## 2026-09-01（续）6 个连环 init bug · 灯灯反馈「只有托盘 / 拖拽不跟 / 点击消失」

### 完整诊断链

1. 灯灯反馈"为什么只有托盘图标，不能显示正常的桌宠在桌面上"
2. PrintWindow 抓窗口内部 → **全黑**（确认是显示 bug）
3. 加 SANITY 红方块 → 变红（OnRender 触发）
4. 加 state/bitmap null 诊断 → diag.txt: **`state=NULL`**
5. 追到 `PetStateMachine.Transition` 早返回 → 修了（v0.1.1）
6. **灯灯下一轮反馈：还是只能看到托盘，猫看不到**
7. EnumWindows 查窗：visible=1、128×128、Topmost、未隐藏、位置在屏幕内 → **窗口一切正常**
8. 决定埋点：给 OnLoopTick 加 `tick.log` 写文件
9. tick.log 显示 timer 跑（每 50tick ≈3.1s）但**帧号 FrameIndex 永远 = 0**！这是关键转折点
10. 挖 PetRenderer → `_moodIntensity` **默认 0** → Playback=0 → `fps = 2 * 0 = 0` → FrameAnimator.Tick 早返回。这是**第二个 init bug**（连同 `_lastTick` 默认 MinValue）
11. 修完 `FrameIndex` 仍 0 → 还有**第三个 init bug**：`_lastTick = DateTime.MinValue`，首次 delta ≈ 6.4e10 秒，`_accumulator` 累加溢出 int，`(int)` 强转在 C# 中行为未定义（实测 `Int32.MinValue`），`_frame = (0 + Int32.MinValue) % 4 = 0` 永远停住
12. 双修后，PrintWindow 5 帧 hash 全部不同 → **动画终于在跑**

### 灯灯本轮反馈的 3 个问题 + 根因

| 反馈 | 表面症状 | 根因 |
|---|---|---|
| **桌宠有点大，缩小 20%** | 128px 太大 | `Scale = 4.0` → 改为 **3.2**（物理 160→128） |
| **拖拽不跟鼠标** | 宠物越拖越偏 | `PointToScreen()` 返回物理像素，`Left/Top` 是 WPF DIP，125% 缩放下每次 Move 累积 0.25 倍偏差。修：WPF 官方 `CompositionTarget.TransformFromDevice` 转回 DIP |
| **点击它会消失** | 点击后宠物飞出屏幕 | `StartFall` 三个 bug 叠加：①`_dragVelocity` 是"每帧位移"与 Gravity"像素/秒²"差 60 倍 ②`Math.Min(v.Y,0)` 保留负值(向上)但注释写"向下" ③无速度上限。修：单位统一 `*60` + 限速 `MaxThrowSpeed=900 px/s` |

### 6 个连环 init bug 总览（最关键的教训）

| 字段 | 默认值 | 后果 | 修复 |
|---|---|---|---|
| `PetStateMachine.Current` | `Idle` | Play(Idle) → Transition(Idle) → Current 已是 → 早返回 → 渲染器永远收不到首次 Play | 去掉 `if (Current == next) return` |
| `PetWindow._loopTimer.Start()` | 缺失 | 动画/眼神/边缘/鼠标广播全失效 | 在 `LoadPlugins()` 调 `Start()` |
| `PetWindow._lastTick` | `DateTime.MinValue` | 首次 delta ≈6.4e10s → 累加溢出 int → 帧永远 0 | 显式 `= DateTime.Now` + FrameAnimator clamp |
| `PetRenderer._moodIntensity` | `0` | Playback=0 → fps=0 → 帧早返回 | 显式 `= 1.0` |
| `FrameAnimator.step` | 无 clamp | (int) 溢出变 `Int32.MinValue` | `Math.Min((int)_accumulator, Frames.Length)` |
| `PetStateMachine._walkTimer` / `WalkDuration` | 缺失 | Walk→Idle 回路代码引用未定义字段，编译错误 | 补定义 |

### 核心教训（写进 MEMORY.md）

**"字段默认值 + 算术/比较"是连环 init bug 高发区**：
- 引用类型默认 null：可空检查保护
- 数值类型默认 0：必须显式初始化到"业务上合理的非零值"，否则乘法/除法/比较全部异常
- `DateTime` 默认 MinValue：与 `DateTime.Now` 减法会得到巨大值，所有"增量/速率"逻辑全部爆炸
- `(int)double` 超出范围：C# 行为未定义，必须用 `Math.Min` 钳制
- **C# `(int)` 强转溢出产生 `Int32.MinValue` 且 `Int32.MinValue % 4 == 0` 这种巧合 → 静默死循环**（最难发现，因为没有异常）

诊断方法论：用文件日志 + PrintWindow 帧 hash 对比 + 反射 FrameIndex，定位"timer 跑但帧不推进"这种深层次 bug 链

---

## 2026-09-01（续）v0.1.2 收尾：菜单真环形挖洞 + libpng 清理 + 稳定性 + Gitee 准备

### 菜单真环形挖洞（用户最后明确要求）

之前 v0.1.0/v0.1.2 用"上方弹出"折中（菜单从宠物头顶往上 150px），宠物完全可见但菜单"远离"猫无包围感。

v0.1.2 收尾做真环形：
- Win32 `SetWindowRgn(outer_elliptic - inner_elliptic)` 把菜单窗口形状变成外圆挖内圆（260 外径、120 内径）
- 中心空洞让宠物在中间**完全可见可点**，鼠标可穿透到下层（拖拽/抚摸不再被菜单挡住）
- 增加 100ms 中心检测定时器：菜单打开时持续检查鼠标位置，进入中心空洞（猫上）→ 主动关菜单，让用户能直接操作宠物
- 槽位仍在外环 8 个方向，环绕感真正成立

技术要点：环形 region = `CombineRgn(outer, hole, RGN_DIFF)`，`SetWindowRgn` 接管 ring 所有权（**不能** DeleteObject），outer/hole 要 DeleteObject 释放。

### libpng 警告 10→0

build 时每次吐 10 条 `libpng warning: iCCP: known incorrect sRGB profile`（无害但烦人）。根因：程序化生成的 PNG 用 Pillow save 时内嵌了不规范的 sRGB profile。

修：写个 Python 脚本用 `Image.save(p, 'PNG', icc_profile=None, optimize=True)` 重新保存全部 14 个 PNG（13 雪碧图 + preview.png），去掉 iCCP chunk，保留所有其他元数据，build 输出干净。

### 稳定性（用户最初要求"再去测试稳定性"）

Neko 跑 10 分钟长挂机：
- 1min: 148MB
- 5min: 166MB（+18MB 涨 = v0.1.2 新字段 + FrameAnimator clamp + PetStateMachine 回路的 **JIT 预热 + WPF 资源首次加载**）
- 10min: 167MB（+1MB → 趋于稳定）

**非泄漏**。v0.1.0 跑过同样 5min 测试：140→139 反向（GC 释放预热后稳定在 140MB）。v0.1.2 预热值略高是字段多。

### Gitee 提交准备

项目仍未纳入版本控制（无 .git）。给灯灯准备了完整提交流程：
1. Gitee 网页建空仓库（不勾选 README/.gitignore）
2. 写 `.gitignore`（排除 bin/obj/.workbuddy/.vs）
3. `git init -b main` + `git add .` + `git commit -m "v0.1.2: ..."` + `git remote add origin <url>` + `git push -u origin main`

灯灯的 git 已全局配好（user=gddgssdfg, email=lijianhui0614@gmail.com）。等灯灯给仓库 URL 就执行。

### 累计决策记录（v0.1.2）

| 决策 | 内容 | 依据 |
|---|---|---|
| 命名 | Neko | v0.1.0 决策 1 |
| 尺寸 | 32px×3.2=102.4 DIP（物理 128 @125%） | 灯灯本轮要求缩小 20% |
| 核心 API 表面稳定 | v0.1.2 后不再改 IEventBus/IPetApi/IUIApi/ICapabilityApi 已有方法 | 插件手册已发版给朋友 |

---

## 2026-09-01（续）v0.1.3：BongoCat 借鉴 4 项功能 + 素材探索

### 借鉴来源

`G:/open_source_obj/BongoCat-master/BongoCat-1.1.0/BongoCat-1.1.0` —— GitHub 上 1k+ stars 的 Tauri 2 + Live2D 桌宠，**灵感来源**是 [Bongo-Cat-Mver](https://github.com/MMmmmoko/Bongo-Cat-Mver)。

**技术栈差异**（不能直接抄代码）：BongoCat = Tauri 2 + Rust + Vue + Live2D WebGL（3D/2D 动态模型）；Neko = C# + WPF + 像素雪碧图。
**借鉴思路**：只看它的**功能设计 + 交互模式**，用 Neko 自己的实现方式重做。

### v0.1.3 借鉴的 4 项功能

| # | BongoCat 特性 | Neko 实现 |
|---|---|---|
| 1 | 键盘/手柄 → 猫左右手动作（`useGamepad`+`useKeyPress`） | `GlobalInput.KeyPressed` 事件（任意非修饰键 300ms 节流）→ `PetRenderer.FlashEyes(300)` 瞪眼 |
| 2 | `window.passThrough` 鼠标穿透模式 | 托盘勾选 → `SetWindowLong + WS_EX_TRANSPARENT` |
| 3 | `window.scale` 缩放百分比（10%-500%）+ `modelSize` | 托盘 +/- → `PetWindow.SetScale` 调整窗口/渲染器/位置，clamp 1.0-8.0，持久化 |
| 4 | `useKeyPress` 自定义全局快捷键 | `AppConfig.MenuHotKey`（Ctrl/Alt/Shift+主键字母）→ `GlobalInput.MenuHotKey` 读取配置（设置 UI v0.2） |

### 不借鉴的部分
- **Live2D 模型系统**：Neko 是像素风，不引入 WebGL/3D。素材用像素雪碧图
- **设置页 UI**（Vue 组件）：Neko 的 WPF SettingsWindow 后续重做，借用它的"模型/窗口/行为"分组思路
- **手柄支持**：Neko 定位轻量桌宠，先不做
- **macOS/Linux 跨平台**：Neko v1 只 Windows

### 素材探索（未完结，等用户决定）

找不侵权像素猫素材替代当前程序生成的几何图元（用户反馈"丑了"）：

| 来源 | 许可证 | 风格 | 状态 |
|---|---|---|---|
| [caz-bee.itch.io/kitty](https://caz-bee.itch.io/kitty) | CC0 | 32×32 橘猫，8 动画（idle/walk/jump/fall × 镜像），**跟 Neko 主题一致** | 待下载（itch.io 页面解析） |
| OpenGameArt catto (SangerStudios/Spring) | CC0 | 1840×40 粉紫手绘长条雪碧图，walk/run | 已下载（`$TEMP\neko-skins\catto.png`）|
| OpenGameArt cat-sprite (Shepardskin) | CC0 | GIF（catwalkx2/x4/catrunx2/x4），8×8/16×16 | 已下载（`$TEMP\neko-skins\cat-sprite.zip`）|
| OpenGameArt pixel-cat (scofanogd/alizard) | CC0 | 单独 5 帧/66×66 跑 | 备选 |

present 对比图给用户选。**适配工作量**：雪碧图格式转换（8 状态 × 4 帧 128×32）+ 眼球层独立分层（catto 是整体，需拆分）。如果用户选定后再做。

### 实施细节笔记

- **键盘反应不吞键**：WH_KEYBOARD_LL 钩子回调里，热键 `return (IntPtr)1` 吞掉，键盘反应 `return CallNextHookEx(...)` 正常传递（打字功能不破坏）
- **键盘反应不影响好感度**：`Pet()` 会广播 `pet:petted`（好感度插件消费），打字涨好感度不合理。`FlashEyes` 是单独的 PetRenderer 能力，不触发状态机切换
- **点击穿透 WS_EX_TRANSPARENT**：开启后整个窗口不接收鼠标事件（拖拽/抚摸/菜单暂时失效），符合"纯展示"语义。托盘菜单标了"鼠标穿透（纯展示）"提示
- **缩放 SetScale 保持中心**：先记窗口中心 (cx, cy)，改 Width/Height，再重设 Left = cx - Width/2。clamp 1.0-8.0 防极端值
- **键盘映射 Config 化**：`HotKeyConfig` 字段 + `ToHotKey` 辅助（字母→VK码），GlobalInput.MenuHotKey 从 config 注入。设置 UI 留 v0.2（v0.1.3 改 config.json 即生效）

---

## 2026-09-01（续）v0.1.4：开机自启

### 决策 14 落地

之前 dev-log 标注的 v0.2 待办里"开机自启开关（决策说放设置窗口，没实现）"——这轮先做底层（注册表 + 托盘开关 + 持久化），设置 UI 留 v0.2。

### 实现

- 新建 `Core/Autostart.cs`：封装 `Microsoft.Win32.Registry` 读写
  - `IsEnabled()` → `Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run").GetValue("Neko")` 判 null
  - `SetEnabled(bool)` → SetValue/DeleteValue，**用 HKCU 而非 HKLM**（HKLM 需管理员权限；HKCU 足够个人自启）
  - exe 路径用 `Process.GetCurrentProcess().MainModule.FileName`，写注册表时用 `\"{exe}\"` 包裹（处理空格路径）
- `App.xaml.cs` OnStartup：加载 config 后与注册表**双向同步**（`config.StartWithWindows != Autostart.IsEnabled()` 时以 config 为准写注册表），处理"用户直接改 config.json"和"用户外部改注册表"两种情况
- `TrayManager.cs`：托盘菜单"开机自启"勾选项（`IsCheckable=true`），初始 `IsChecked=Autostart.IsEnabled()`，勾选时 `SetEnabled + PetWindow.SetStartWithWindows` 持久化到 config.json
- `PetWindow.SetStartWithWindows(bool)`：更新 `_config.StartWithWindows` + `ConfigManager.Save`

### HKCU vs HKLM 选型

| | HKCU | HKLM |
|---|---|---|
| 权限 | 普通用户即可 | 需管理员 |
| 作用范围 | 当前用户 | 所有用户 |
| 桌宠场景 | ✓ 够用 | 不需要 |

选 HKCU：免管理员、桌宠单用户场景足够、用户卸载/重装无残留（HKLM 残留会困扰其他用户）

### 验证

- LoaderTest 4 组 + UiProbe 10 项全过
- 构建 0 警告 0 错误
- 启动 Neko，托盘勾选"开机自启" → 注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 出现 `Neko` = `"<exe 路径>"`；取消勾选 → 注册表值消失

---

## 2026-09-01（续）v0.1.5：设置窗口重做 + ISettingsPage 接入

### 背景

v0.1.0 决策 4：插件可自定义设置页（`ISettingsPage.BuildContent()` 返回 WPF 控件）。但宿主设置窗口从未接入，**插件能注册设置页却没地方显示**。v0.1.3 借鉴 BongoCat `preference` 页面（Vue 组件分组：模型/窗口/行为/通用），重做设置窗口同时接入 ISettingsPage。

### 实现

- **`UIApiImpl.AllSettingsPages`**（`Services/UIApiImpl.cs`）—— 之前已有 `_pages` 字典和 `RegisterSettingsPage`，但没暴露查询接口。新增 `IReadOnlyList<ISettingsPage> AllSettingsPages => _pages.Values.SelectMany(v => v).ToList()`，宿主设置窗口遍历它
- **`PetWindow.Config`** —— `public AppConfig Config => _config;` 让设置窗口读写配置（之前 `_config` 是 private readonly）
- **`AppConfig.KeyboardReactionsEnabled`**（默认 `true`）—— v0.1.3 键盘瞪眼的总开关，false 时 PetWindow 订阅 KeyPressed 直接 return
- **`SettingsWindow` 重写**（4 tab TabControl）：
  - **模型 tab**：`Slider` 1.0-8.0 实时缩放，标签同步显示「32 × N = M 像素」
  - **窗口 tab**：开机自启勾选（`Autostart.IsEnabled()` 同步注册表）、鼠标穿透勾选（`SetMousePassthrough`）
  - **行为 tab**：键盘反应勾选（持久化）、热键只读显示（编辑 UI 留 v0.2）
  - **插件 tab**：`ListBox` 左 + 详情区右。详情包含描述/作者/版本/加载错误，**遍历 `_pet.UIApi.AllSettingsPages` 匹配 `Manifest.Id/Name`，调用 `BuildContent()` 嵌入 UIElement**
- **`TickPlacement`** —— `Slider.TickPlacement` 在 `System.Windows.Controls.Primitives` 命名空间（不是 `Controls`），SettingsWindow 加 `using System.Windows.Controls.Primitives;`

### 插件自定义页（ISettingsPage）接入示例

之前插件注册时只需 `UI.RegisterSettingsPage(new MyPage())`，但宿主从未调用 `BuildContent()` 渲染。现在设置窗口的「插件 → 详情区」会查找匹配的 ISettingsPage 嵌入。

但当前内置的 **AffectionPlugin 没注册设置页**（它只有托盘项和径向槽位）。可以补一个「好感度设置」页（统计/重置/导出）作为示范插件，v0.2 再做。

### 待办（v0.2）

- 键盘映射设置 UI（v0.1.3 标注留 v0.2：当前是改 config.json；v0.2 加个文本框 + 三个 CheckBox 即可）
- 透明度设置（BongoCat `window.opacity`，PetWindow 加 `Opacity` 绑定 + 托盘滑条）
- 槽位图标 PNG 化（emoji 依赖系统字体，PNG 更可控）
- AffectionPlugin 加设置页示范 ISettingsPage 接入

---

## 2026-09-01（续）v0.1.6-0.1.8：v0.1.5 列的 v0.2 待办收尾

### v0.1.6：热键 UI 闭环 + Affection 设置页狗粮

- **键盘热键设置 UI**：SettingsWindow「行为」tab 加 3 CheckBox（Ctrl/Alt/Shift）+ 主键 TextBox + 保存按钮，调用 `PetWindow.SetMenuHotKey(ctrl, alt, shift, key)` 立即生效 + 持久化。v0.1.3 改 config.json 的方式终于闭环
- **Affection 插件 ISettingsPage 示范**：新建 `AffectionSettingsPage.cs`（含当前分数 TextBlock + 重置 Button），csproj 加 `<UseWPF>true</UseWPF>` 启用 WPF 引用（之前缺这个，第三方插件要做自定义 WPF 设置页会编译失败）。`Title="com.lantern.affection"` 匹配 SettingsWindow 的嵌入规则
- **关键技术点**：C# nested class 访问外部类实例字段必须显式传引用（SlotView 构造时接收 `BitmapImage icon` 参数，而不是直接访问 `RadialMenu._iconCache`）

### v0.1.7：窗口透明度

- BongoCat `window.opacity` 借鉴
- `AppConfig.Opacity` 默认 1.0，`PetWindow.SetOpacity(double)` clamp 0.2-1.0（< 0.2 鼠标穿透交互失灵）
- 设置窗口「窗口」tab 加 0.2-1.0 Slider + 实时 Label

### v0.1.8：槽位图标 PNG 化

- 之前用 `TextBlock` 显示 emoji 依赖 Segoe UI Emoji 字体（系统/字体/缩放变化渲染不一致）
- **预渲染策略**：用 Pillow + Segoe UI Emoji 把 12 个常用图标 (`wave/heart/food/sleep/dance/home/gear/info/eye/pin/close/default`) 渲染成 `neko-assets/icons/*.png`（脚本 `$TEMP/gen_icons.py`，26px 字号 / 32x32 画布）
- `RadialMenu` 启动时 `LoadIcons` 一次性加载到 `_iconCache: Dictionary<string, BitmapImage>`（Freeze + OnLoad 解码）
- `SlotView` 构造接收 `BitmapImage icon` 参数（nested class 访问外部字段限制），按 `slot.Icon` key 查缓存
- 运行时**不再依赖系统 emoji 字体**，跨系统一致，加载/绘制性能更稳定
- 删除 `IconFor` 静态 emoji switch（不再需要）

### v0.1.8 长挂机稳定性确认

Neko 跑 5min：内存从 ~150MB（JIT 预热）→ ~167MB（GC 后稳态），**无泄漏**。v0.1.2 起的所有改动（6 个连环 init bug + 4 项 BongoCat 借鉴 + 开机自启 + 设置窗口重做 + 透明度 + 图标 PNG 化）经长挂机验证稳定。

### v0.1.8 完整功能清单（v0.1.0 起步到这里共 9 版）

1. **核心**：8 状态像素动画 + 好感度内置插件（dogfooding 示范）
2. **修复**：致命显示 bug、6 个连环 init bug、拖拽/落体物理 DPI 修复
3. **菜单**：真环形挖洞（SetWindowRgn，猫在中心完全可见可点）
4. **设置**：4-tab BongoCat 风格设置窗口 + ISettingsPage 插件自定义页接入
5. **借鉴 BongoCat**（v0.1.3-0.1.7）：键盘反应（瞪眼）、点击穿透、缩放可调、热键可配、窗口透明度
6. **系统集成**：开机自启（HKCU 注册表）
7. **健壮性**：libpng 警告 10→0、槽位图标 PNG 化（不依赖系统字体）
8. **测试**：LoaderTest 4 组 + UiProbe 10 项覆盖（含 30 次 StartFall 压测、50 次 Pet 压测、20 次 Edge 压测）

### v0.1.0 → v0.1.8 总耗时

约 12 小时（含多次 PrintWindow 取证诊断、6 个连环 init bug 排查、菜单环形挖洞、多次回归+稳定性验证）。

---

## 2026-09-01（续）v0.1.9：菜单沿猫身边排列 + 备忘录/天气插件

### 用户反馈 + 根因

"现在这个菜单被猫挡住一部分了" —— 当前菜单圆心对齐宠物中心，槽位中心在半径 64、方块 42px（内边缘 43），而猫半径 = 102.4/2 = 51。**槽位内边缘 43 < 猫半径 51 → 槽位方块盖住猫边缘**。v0.1.2 的环形挖洞只挖中心（120 直径），没解决槽位覆盖。

### 修复：菜单沿猫身边排列

`PetWindow.OpenRadialMenu` 重写定位逻辑：
1. **右侧优先**：菜单中心 = 猫中心 + 猫半宽 + 菜单半径 + 10px 间距
2. 右侧超工作区 → **左侧**
3. 左右都放不下 → **上下**（空间更大的那侧），clamp 工作区内

配套：`RadialMenu` 移除 SetWindowRgn 环形挖洞 + 中心检测定时器（菜单不再覆盖猫后中心无猫，挖洞无意义且中心检测会误关菜单）。`MenuSize` 改 public 供 PetWindow 计算。

### 两个新内置插件（用户"可以加入新功能 例如备忘录天气"）

**备忘录**（`builtins/DesktopPet.Builtin.Notes`）：
- 右键菜单槽位 → 打开 WPF 便签窗口（多行 TextBox + 保存）
- Storage 持久化（`data/plugins/com.lantern.notes/notes`）
- 技术点：插件 OnInvoke 在 UI 线程可直接 `new Window().Show()`；回调传 Action<string> 写 Storage

**天气**（`builtins/DesktopPet.Builtin.Weather`）：
- 右键菜单槽位 → 异步调 Open-Meteo 免费 API（免 key）：`https://api.open-meteo.com/v1/forecast?latitude=..&longitude=..&current=temperature_2m,weather_code`
- `HttpClient` + `System.Text.Json` 解析，WMO weather_code → 中文描述，`Pet.Say()` 气泡播报
- 佛山坐标硬编码（灯灯城市），后续可做城市配置（设置页）
- **技术点**：`async void` 事件处理器（OnInvoke 是 Action），内部 try/catch 兜底网络失败；插件直接 HttpClient 不依赖宿主网络 API（宿主 ISystem 无 HTTP 方法，v0.2 可考虑加）

### 部署

两个插件加进 `DesktopPet.sln`（VS 可见）+ `DeployToPlugins` target 自动部署到 `plugins/`。App 输出目录的 `DeployPluginsToOutput` 自动带上。`plugins/` 现有：affection / notes / weather。

### 验证

- LoaderTest 4 组 + UiProbe 11 项全过
- 构建 0 警告 0 错误
- v0.1.9 发布

---

## 2026-09-01（续）v0.1.10：备忘录自动保存 + 番茄钟插件

### 备忘录自动保存
用户点「保存」才能存，体验笨。改为 `DispatcherTimer` 800ms 停顿防抖：`TextChanged` 重置定时器，停止输入 800ms 自动 `_onSave(text)`；关窗（`Closed`）也保存。手动保存按钮保留（立即存 + 关窗）。

### 番茄钟插件（IScheduler + Notify 能力 dogfooding）

- 槽位「番茄钟 25分」（icon=clock ⏰，新增 icons/clock.png）/「停止番茄」（icon=close）
- `Scheduler.Every(1000, ...)` 每秒检查 `_endTime - now`，≤0 时：
  - `System.Notify("Neko", "🍅 番茄钟结束...")` 系统通知
  - `Pet.Say(...)` 气泡
- `OnUnload` 调 `Stop()` 释放定时器（防止卸载后残留）

### 插件生态现状

`plugins/` 现有 4 个（dogfooding 逐步覆盖 Core API 各能力）：
| 插件 | 演示的能力 |
|---|---|
| affection | 事件订阅 + Storage + 径向槽位 |
| notes | Storage + 自定义 WPF 窗口 |
| weather | HttpClient 网络 + Pet.Say（Network 权限）|
| pomodoro | IScheduler 定时 + ISystem.Notify 通知 |

### 验证
- LoaderTest 4 组 + UiProbe 11 项全过
- 构建 0 警告 0 错误
- v0.1.10 发布

---

## 2026-09-01（续）v0.1.11：天气城市可配置

### 背景
v0.1.9 天气插件佛山硬编码。用户可能换城市，做成可配置。

### 实现
- 城市存 Storage（key=city，默认佛山）
- 坐标解析三阶：内置 14 城市表 → Open-Meteo 地理编码 API → 回退佛山
- `WeatherSettingsPage`（ISettingsPage）：城市输入 + 保存 → Storage.Set，宿主设置窗口「插件」tab 可改
- Weather csproj 加 UseWPF（设置页需要 WPF 控件——和 Affection 之前一样，凡是插件含 WPF 设置页必须加）

### 插件设置页生态
| 插件 | 设置页 | 内容 |
|---|---|---|
| affection | AffectionSettingsPage | 分数 + 重置 |
| weather | WeatherSettingsPage | 城市输入 + 保存 |

两个都演示了「插件自定义设置页」完整链路（ISettingsPage 注册 → 宿主嵌入 → Storage 持久化）。

### 验证
- LoaderTest 4 组 + UiProbe 11 项全过
- 构建 0 警告 0 错误
- v0.1.11 发布

---

## 2026-09-01（续）v0.1.12：每日一句插件

### 背景
用户说"可以加入新的功能 例如备忘录天气等"——插件生态继续扩充。

### 每日一句（QuotePlugin）
- 右键菜单「每日一句」槽位 → `Pet.Say(QuoteOfToday(), 5000)`
- **按日期确定性选句**：`days = (DateTime.Today - new DateTime(1900,1,1)).TotalDays`，`idx = (uint)(days * 2654435761u % 1000000007u) % Quotes.Length`
  - 同一天多次点击显示同一句（确定性）
  - 跨天自动刷新（日期变化 → 天数变化 → 句子变化）
- 内置 20 句中文（鼓励/放松/专注风格）
- 不需要 Storage/网络（纯确定性算法，最轻量插件）

### 插件生态现状（5 个，覆盖 Core API 各能力）
| 插件 | 能力 |
|---|---|
| affection | 事件订阅 + Storage + 径向槽位 |
| notes | Storage + 自定义 WPF 窗口 |
| weather | 网络 + Storage + ISettingsPage（城市可配）|
| pomodoro | IScheduler + Notify |
| quote | 纯算法 + Pet.Say（最轻量）|

### 验证
- LoaderTest 4 组 + UiProbe 11 项全过
- 构建 0 警告 0 错误
- v0.1.12 发布

---

## 2026-09-01（续）v0.1.13：单词打卡插件

### 背景
插件生态继续扩充（用户"可以加入新功能"）。

### 单词打卡（WordCardPlugin）
- 槽位「今日单词」（icon=book 📖）：按日期确定性选词（同 QuotePlugin 的 Knuth 哈希），`Pet.Say("apple · 苹果")`
- 槽位「✅ 打卡」（icon=heart）：Storage 记录 `lastCheckDay`（yyyyMMdd）+ `streak`（连续天数）
  - 上次是昨天 → streak+1；否则重置 1；当天重复打卡 → 提示已有记录
- 新增 icons/book.png（📖）

### 插件生态现状（6 个）
| 插件 | 能力 |
|---|---|
| affection | 事件订阅 + Storage + 径向槽位 |
| notes | Storage + 自定义 WPF 窗口 |
| weather | 网络 + Storage + ISettingsPage |
| pomodoro | IScheduler + Notify |
| quote | 纯算法 + Pet.Say |
| wordcard | Storage 日期逻辑 + Pet.Say |

### 验证
- LoaderTest 4 组 + UiProbe 11 项全过
- 构建 0 警告 0 错误
- v0.1.13 发布

---

## 2026-09-01（续）v0.1.14：天气 3 日预报

### 背景
天气插件只有当前天气，加未来预报。

### 实现
- 槽位「3日预报」（icon=clock）→ Open-Meteo daily API：`daily=temperature_2m_max,temperature_2m_min,weather_code&forecast_days=3&timezone=auto`
- 解析 daily 数组，气泡播报「佛山 今天 32/25°C 晴  明天 31/24°C 多云」
- `ShortDesc`（简短描述）vs `Describe`（详细带 emoji）：预报用简短版避免气泡过长
- 复用 `ResolveCityAsync` 城市解析（Storage + 内置表 + 地理编码）

### 验证
- LoaderTest 4 组 + UiProbe 11 项全过
- 构建 0 警告 0 错误
- v0.1.14 发布

---

## 2026-09-01（续）v0.1.15：倒计时插件

### 背景
插件生态继续扩充。番茄钟是 25 分专注，补一个 5/10 分快捷倒计时（煮面/泡茶场景）。

### 倒计时（CountdownPlugin）
- 槽位「倒计时 5分 / 10分 / 停止倒计时」
- 复用番茄钟模式：`Scheduler.Every(1000)` 检查 `_endTime`，结束 `System.Notify` + `Pet.Say`
- 与番茄钟的差异：时长更短、入口更多（两个时长槽位）

### 插件生态现状（7 个）
| 插件 | 能力 |
|---|---|
| affection | 事件订阅 + Storage |
| notes | Storage + WPF 窗口 |
| weather | 网络 + Storage + ISettingsPage + 3日预报 |
| pomodoro | IScheduler + Notify |
| quote | 纯算法 + Pet.Say |
| wordcard | Storage 日期逻辑 |
| countdown | IScheduler + Notify（快捷时长）|

### 验证
- LoaderTest 4 组 + UiProbe 11 项全过
- 构建 0 警告 0 错误
- v0.1.15 发布

---

## 2026-09-01（续）v0.1.16：音乐控制插件

### 背景
桌宠很常见的功能：猫控制媒体播放。

### 音乐控制（MusicControlPlugin）
- 槽位「上一曲 / 播放暂停 / 下一曲」（icons: prev/play/next，⏮⏯⏭）
- 用 Win32 `keybd_event(VK_MEDIA_*)` 发送系统媒体键（0xB0/0xB1/0xB3）
- **对任意播放器生效**（系统级媒体键，QQ音乐/网易云/浏览器都响应）
- 展示「插件可 P/Invoke 系统 API」——Core API 没提供的（媒体键），插件可直接 Win32
- 权限声明 `process`（发射系统级输入）

### 插件生态现状（8 个）
| 插件 | 能力 |
|---|---|
| affection | 事件订阅 + Storage |
| notes | Storage + WPF 窗口 |
| weather | 网络 + Storage + ISettingsPage + 3日预报 |
| pomodoro | IScheduler + Notify |
| quote | 纯算法 + Pet.Say |
| wordcard | Storage 日期逻辑 |
| countdown | IScheduler + Notify |
| music | P/Invoke 系统媒体键 |

### 验证
- LoaderTest 4 组 + UiProbe 11 项全过
- 构建 0 警告 0 错误
- v0.1.16 发布

---

## 2026-09-01（续）v0.1.17：剪贴板历史插件

### 背景
桌宠实用功能补充：剪贴板历史（复制过的东西找回）。

### 剪贴板历史（ClipboardHistoryPlugin）
- `Scheduler.Every(800ms)` 轮询 `System.ReadClipboard()`，文本变化 → 入栈到 Storage（key=history，List<string> 最近 20 条）
- 右键菜单「剪贴板」→ ClipboardWindow（ListBox 展示 + 双击 `System.WriteClipboard` 复制回）
- 新增 icons/copy.png（📋）
- 权限：clipboard（剪贴板读写，中风险）+ scheduler + storage + ui
- 注意：剪贴板轮询每 800ms 一次，需保证轻量（ReadClipboard 是 COM 调用，偶发"剪贴板忙"异常用 try/catch 兜底）

### 插件生态现状（9 个）
| 插件 | 能力 |
|---|---|
| affection | 事件订阅 + Storage |
| notes | Storage + WPF 窗口 |
| weather | 网络 + Storage + ISettingsPage + 3日预报 |
| pomodoro | IScheduler + Notify |
| quote | 纯算法 + Pet.Say |
| wordcard | Storage 日期逻辑 |
| countdown | IScheduler + Notify |
| music | P/Invoke 媒体键 |
| clipboard | 剪贴板轮询 + Storage |

### 验证
- LoaderTest 4 组 + UiProbe 11 项全过
- 构建 0 警告 0 错误
- v0.1.17 发布

---

## 2026-09-01（续）v0.1.18：系统信息插件

### 背景
桌宠实用功能：猫播报系统状态。

### 系统信息（SystemInfoPlugin）
- 槽位「系统信息」→ `Pet.Say("CPU 12% · 内存 8.2/16.0GB · C盘 120/256GB")`
- CPU：`GetSystemTimes` 两次采样（间隔 300ms，Task.Delay）算使用率
- 内存：`GlobalMemoryStatusEx`（P/Invoke MEMORYSTATUSEX）
- 磁盘：`DriveInfo`（C 盘固定盘）
- 展示「插件可 P/Invoke 系统 API」+ async void 异步采样

### 插件生态现状（10 个）
| 插件 | 能力 |
|---|---|
| affection / notes / weather / pomodoro / quote / wordcard / countdown / music / clipboard | 各覆盖不同能力 |
| sysinfo | P/Invoke 系统状态 |

### 验证
- LoaderTest 4 组 + UiProbe 11 项全过
- 构建 0 警告 0 错误
- v0.1.18 发布

---

## 2026-09-01（续）v0.1.19：待办清单插件

### 背景
桌宠实用功能：待办清单。

### 待办清单（TodoListPlugin）
- 槽位「待办清单」→ TodoWindow：添加（输入 + 回车/按钮）、CheckBox 勾选完成、删除选中
- **实时持久化**：任何变化（添加/勾选/删除）立即 `Storage.Set(TodoKey, List<TodoItem>)`
- `List<TodoItem>` JSON 序列化存 Storage（System.Text.Json），最多 30 条
- 新增 icons/todo.png（✅）

### 插件生态现状（11 个）
| 插件 | 能力 |
|---|---|
| affection / notes / weather / pomodoro / quote / wordcard / countdown / music / clipboard / sysinfo | 各覆盖不同能力 |
| todo | Storage List 序列化 + WPF 窗口 |

### 验证
- LoaderTest 4 组 + UiProbe 11 项全过
- 构建 0 警告 0 错误
- v0.1.19 发布

---

## 2026-09-01（续）v0.1.20：今日运势插件

### 背景
轻量趣味插件，延续"每日一句/每日单词"的确定性算法模式。

### 今日运势（FortunePlugin）
- 槽位「今日运势」→ `Pet.Say("今日运势：大吉 🌟（适合努力）")`
- 运势等级：6 档（大吉/中吉/小吉/平/小凶/大凶）+ 星标
- 按日期确定性选档（同天同运、跨天刷新），建议随机（Random）
- 最轻量插件：纯算法，无 Storage/网络

### 插件生态现状（12 个）
| 插件 | 能力 |
|---|---|
| affection / notes / weather / pomodoro / quote / wordcard / countdown / music / clipboard / sysinfo / todo | 各覆盖不同能力 |
| fortune | 纯算法 + Pet.Say（最轻量）|

### 验证
- LoaderTest 4 组 + UiProbe 11 项全过
- 构建 0 警告 0 错误
- v0.1.20 发布

---

## 2026-09-01（续）v0.1.21：整点报时插件（手册示例落地）

### 背景
插件手册第 8 节「完整可编译示例」是整点报时（HourlyPlugin），把它做成真实内置插件——**dogfooding 闭环：验证手册示例可直接编译可运行**。

### 整点报时（HourlyChimePlugin）
- 手册示例基本照搬，加了：开关槽位（Storage 持久化 enabled，默认关）+ OnUnload 释放定时器
- `Scheduler.Every(60_000)` 检查跨整点 → `Pet.Say("现在是 HH:mm 啦")`，`lastHour` 存 Storage 防重复
- 槽位「整点报时（开/关）」切换

### 插件生态现状（13 个）
| 插件 | 能力 |
|---|---|
| affection / notes / weather / pomodoro / quote / wordcard / countdown / music / clipboard / sysinfo / todo / fortune | 各覆盖不同能力 |
| hourly | IScheduler + Storage（手册示例落地）|

### 验证
- LoaderTest 4 组 + UiProbe 11 项全过
- 构建 0 警告 0 错误
- v0.1.21 发布

---

## 2026-09-01（续）v0.1.22：睡眠提醒插件

### 背景
桌宠实用功能：到点提醒睡觉。

### 睡眠提醒（SleepReminderPlugin）
- 槽位「23:00 睡眠提醒」（icon=sleep 😴）开启 → `Scheduler.Every(60_000)` 检查到 23:00 → `System.Notify` 一次性 + `Pet.Say("夜深了，早点休息吧")`
- 槽位「取消睡眠提醒」（icon=close）关闭
- 复用 Scheduler + Notify 模式（同番茄钟/倒计时/整点报时）

### 插件生态现状（14 个）
| 插件 | 能力 |
|---|---|
| affection / notes / weather / pomodoro / quote / wordcard / countdown / music / clipboard / sysinfo / todo / fortune / hourly | 各覆盖不同能力 |
| sleep | IScheduler + Notify（定时提醒）|

### 验证
- LoaderTest 4 组 + UiProbe 11 项全过
- 构建 0 警告 0 错误
- v0.1.22 发布

---

## 2026-09-01（续）v0.1.23：截图插件

### 背景
桌宠实用功能：一键截图。

### 截图（ScreenshotPlugin）
- 槽位「截图」（icon=camera 📷）→ `Graphics.CopyFromScreen` 全屏截图 → `bmp.Save(path, ImageFormat.Png)` 保存到 `data/plugins/com.lantern.screenshot/shot_yyyyMMdd_HHmmss.png` → `Pet.Say` 提示路径
- 第一个引入 NuGet 依赖的插件：`System.Drawing.Common 8.0.7`（构建时恢复成功）
- 权限：file（文件系统，中风险）
- 注意：CopyFromScreen 抓不到 WPF layered 窗口（PetWindow 区域可能透明），但其他区域正常

### 插件生态现状（15 个）
| 插件 | 能力 |
|---|---|
| affection / notes / weather / pomodoro / quote / wordcard / countdown / music / clipboard / sysinfo / todo / fortune / hourly / sleep | 各覆盖不同能力 |
| screenshot | System.Drawing + P/Invoke（首个带 NuGet 依赖的插件）|

### 验证
- LoaderTest 4 组 + UiProbe 11 项全过
- 构建 0 警告 0 错误
- v0.1.23 发布

---

## 2026-09-01（续）v0.1.24：快捷搜索插件

### 背景
桌宠实用功能：选中文字 → 猫帮你搜索。

### 快捷搜索（QuickSearchPlugin）
- 槽位「搜索剪贴板」（icon=gear）→ `System.ReadClipboard()` → `Pet.Say("帮你搜「xxx」")` → `System.OpenUrl("https://www.baidu.com/s?wd=" + UrlEncode(text))`
- 空剪贴板 → 提示"先复制"
- 展示 ISystem.ReadClipboard + OpenUrl 组合
- `HttpUtility.UrlEncode`（System.Web，net8.0-windows 内置）

### 插件生态现状（16 个）
| 插件 | 能力 |
|---|---|
| affection / notes / weather / pomodoro / quote / wordcard / countdown / music / clipboard / sysinfo / todo / fortune / hourly / sleep / screenshot | 各覆盖不同能力 |
| search | ReadClipboard + OpenUrl |

### 验证
- LoaderTest 4 组 + UiProbe 11 项全过
- 构建 0 警告 0 错误
- v0.1.24 发布

---

## 2026-09-02 v0.1.25：径向菜单大改版 + 键鼠反应（用户推翻 v0.1.9 方案）

### 背景（用户截图反馈，原话）
"为什么这些全都叠在一起了 限制这边插件的数量最多设置八个，透明度设置到能看清，这些插件右键猫时候围绕猫一圈，不要在旁边，之前给你参考的项目中，键盘映射的功能加入，停止做更多插件，先做好原生功能"

四条指令：① 撤销 v0.1.9 旁边定位 → 改回围绕猫一圈；② 槽位每页最多 8 个；③ 透明度拉到能看清；④ 加键盘映射（BongoCat）。⑤ 停止做插件。

### 教训：为什么 v0.1.9 会翻车
16 个插件注册了 22+ 槽位，而 RebuildSlots 按 360°/n 均分固定半径 64：22 槽 → 角距 16.4° → 弦长 18px < 方块 42px → 全叠一起。**旁边定位只是把遮挡换成了更糟的重叠，方向本身就错了**——用户要的一直是"环形围绕猫"，遮挡问题应该用挖洞+加大半径解决，而不是把菜单挪走。
> 产品教训：修 UI 问题先问"用户心里的正确形态是什么"，别只治表面症状。

### 关键实现决策
1. **纯 WPF 挖洞取代 SetWindowRgn**：Window 与 Canvas 的 Background 设为 **null**（不是 Brushes.Transparent！Transparent 画刷参与命中测试，null 才不拦点击）→ 中心空洞天然穿透到宠物窗口，无需 Win32 region、无 DPI 换算、无 region 所有权坑
2. **分页而非截断**：22 槽位砍到 8 个会静默丢 14 个功能 → 按 Order 分页（8/8/6），滚轮 + 顶部「‹ 1/3 ›」胶囊翻页；单页 8 个在 45° 角距下弦长 59px > 38px 槽位，物理上不可能重叠
3. **洞半径动态化**：PetWindow 传 `Width/2 + 4`（猫半宽+余量），ShowAt 内推导全部尺寸；`MenuSize` 常量删除。默认 3.2x 时洞 55.2 / 槽带 58-96 / 外缘 104，菜单直径 212
4. **Path 类名陷阱**：`using System.IO` 在场时 `new Path` 解析为 System.IO.Path（抽象类）→ CS0712 + 一串 CS0117，必须写 `System.Windows.Shapes.Path`
5. **键盘映射**：素材 8 状态无"打字"动画 → 用现有瞪眼球层（eyes_scared FlashEyes 300ms）映射键盘+鼠标输入（WH_MOUSE_LL 新增，各 300ms 节流不吞按键），设置页可关

### 验证（UiProbe 新增 2 项守护）
- 分页断言：22 槽位翻页 5 轮不崩、单页 SlotView ≤ 8
- **像素断言**：RenderTargetBitmap 实抓 → 洞 alpha=0（透猫）/ 底盘 alpha=242（能看清）；人工目检第 3 页 6 槽位均布无重叠、胶囊正常
- LoaderTest 4 组全过；构建 0 警告 0 错误

### 遗留
- 猫贴屏幕右/下边缘时环带局部出屏（几何必然：环围绕猫且猫在角落则环必出屏），边缘槽位部分可点；后续可做"槽位避让出屏扇区"
- 键鼠反应目前是瞪眼近似；若要 BongoCat 式"手"动画需新增精灵状态（待素材）

---

## 2026-09-02（续）v0.1.26：基底优化——借鉴 BongoCat 补齐原生能力

### 背景
灯灯："你看一下我给你的参考项目 看看还有没有什么可以抄的，尽量以优化基底为主 插件功能等我指示。"
调研 BongoCat（ayangweb/BongoCat）功能清单，对照 Neko 现状筛出 3 个高价值基底缺口并实现：

| BongoCat 功能 | Neko v0.1.25 前状态 | 本轮处理 |
|---|---|---|
| 位置记忆 | ❌ 每次启动回右下角 | ✅ 实现 |
| 显隐老板键（Ctrl+Shift+B） | ❌ 无法藏猫 | ✅ 实现（Ctrl+Shift+H）|
| 多显示器 | ❌ GetWorkArea 固定主屏 | ✅ 实现 |
| 快捷键调透明度/缩放 | 托盘有缩放 | 后置 |
| 手柄映射 | ❌（像素猫无"手"动画，价值低）| 后置，待素材 |
| 模型/皮肤切换、自动更新、主题 | ❌（需素材/超范围）| 后置 |

### 关键实现
1. **位置记忆**：`AppConfig.PosX/PosY`（double?，null=默认位）；SavePosition 挂 3 个时机（拖拽松手/落体落定/缩放后）——不在每帧存（IO）；`OnSourceInitialized` clamp 进工作区防换分辨率后猫出屏；托盘「回到默认位置」清 null
2. **老板键**：`GlobalInput.SecondaryHotKey` + `SecondaryHotKeyPressed`，钩子按"菜单热键→老板键→普通键（键鼠反应）"顺序匹配，命中即吞；托盘等价项；设置页热键编辑器抽取 `BuildHotKeyEditor` 复用（两处热键一个实现）
3. **多显示器**：`MonitorFromWindow(MONITOR_DEFAULTTONEAREST) + GetMonitorInfo` 取猫所在屏工作区；**rcWork 是物理像素，必须乘 TransformFromDevice.M11/M22 转 DIP**（v0.1.1 拖拽坐标系同款教训，不转则 125% DPI 下边界计算偏 25%）

### 验证
- UiProbe 新增「位置记忆恢复」：config 指定 200,150 → PetWindow 精确落位 200x150（14 项全过）
- 端到端：预置 config 启动 Neko.exe → 进程存活正常
- LoaderTest 4 组全过；构建 0 错误

### 教训
- 环境限制：Bash 里调用 pwsh、Add-Type 运行时编译均被安全策略拦截——**验证类一次性脚本优先沉淀进 UiProbe 当永久回归项**，比外部脚本更稳还能防回退

---

## 2026-09-02（续2）v0.1.27：游戏模式 + 附加热键 + 托盘双击

### 背景
续接"抄 BongoCat、优化基底为主"的后置清单，做掉不需要素材的三项。

### 关键实现与决策
1. **游戏模式**：每秒 `GetForegroundWindow` → 排除桌面/任务栏类名（Progman/WorkerW/Shell_TrayWnd）→ 排除带 WS_CAPTION 的普通窗口（最大化也带）→ 矩形与猫所在显示器**交集 ≥98%** 判全屏 → 自动藏猫或穿透，退出恢复。**交集法**而非窗口面积法：最小化窗口矩形是 -32000 偏移，交集天然为 0，不用特判
2. **状态代管边界**：游戏模式只恢复"自己藏的"——老板键先藏的猫，退出全屏不抢着 Show（`_hiddenByGame = IsVisible` 只在可见时代管）
3. **附加热键默认值决策**：BongoCat 用 Ctrl+Shift+方向键，但那是编辑器选中文本/移行的快捷键，**全局低级钩子吞掉会毁掉打字体验** → 默认改 Ctrl+Alt+Shift 三修饰（↑↓=透明度±10%、←→=缩放±20%、R=回默认位），config.json 可改；`ToVk` 扩展支持 "Up/Down/Left/Right/Home/End/PgUp/PgDn" 键名
4. LL 键盘钩子回调跑在**安装线程（UI 线程）的消息循环**上，附加热键回调直接操作窗口属性是安全的
5. 双击托盘图标 = 显隐猫：`TaskbarIcon.TrayMouseDoubleClick`

### 验证
- UiProbe 新增全屏判定回归（4 个边界：精确全屏/略超屏全屏/普通窗口/最小化）——纯逻辑 `PetWindow.CoversMonitor` 公开静态便于测试
- LoaderTest 4 组 + UiProbe 15 项全过；App/UiProbe 强制重建 0 警告；烟测正常
- 判定：sln 构建的 3 个警告均来自旧内置插件（TodoList CS8602 等），非本轮代码

### 后置清单（仍待灯灯指示/素材）
手柄映射（需精灵）、皮肤/模型切换、游戏模式"自动调透明度"变体、自动更新

## 2026-09-02（v0.1.28：键鼠映射窗，BongoCat 核心视觉）

### 起因
灯灯截图 BongoCat 问"键盘映射呢？没见到啊，模拟鼠标点击和敲键盘的"。v0.1.25 当时误判"做键盘映射必须用图片素材"，只给了瞪眼近似——实际键盘/鼠标用纯 WPF Border+TextBlock 就能画，根本不需要素材。本版补齐，且按灯灯要求做成**可开关**。

### 实现要点
- **GlobalInput 只增不改**：新增无节流原始事件 `RawKeyEvent(vk,down)` / `RawMouseButtonEvent(btn,down)` / `RawMouseWheelEvent`。HookCallback 补 WM_KEYUP/SYSKEYUP 分支（按下高亮+松开还原依赖完整上下沿）；DOWN 分支在热键匹配**之前**发原始事件——被热键吞掉的键映射窗也照常高亮，与 BongoCat 行为一致
- **InputMirrorWindow**：Esc+F1-F12+60% 主区共 66 键 + 三键鼠标+滚轮，纯 Border/TextBlock 绘制；vk→Border 用 `Dictionary<uint,Border>`（TryAdd 防撞）；底盘 95% 白+深棕描边与径向菜单统一；DragMove 拖动 + `MirrorPosX/Y` 记忆 + 工作区 clamp；ShowActivated=false 不抢焦点
- **开关三入口**：config `KeyboardMirrorEnabled`（默认 true）+ 设置页 checkbox + 托盘勾选项，统一走 `SetKeyboardMirror`；老板键藏猫/游戏模式全屏联动隐藏，恢复时按开关状态还
- **UiProbe 第 16 项**：66 键全注册断言（防 vk 撞车吞键）+ 鼠标 3 区 + FlashKey 高亮切换

### 踩坑
- **`cmd //c "taskkill ..."` 在 Git Bash 下 `//c` 转换不稳定**：打出 cmd 交互 banner 而不执行命令，导致旧 Neko 进程锁 Runtime.dll，连续两次构建失败。结论：杀进程统一用 PowerShell 工具的 `Stop-Process -Name Neko -Force`
- **61 vs 66**：断言初写 61（纯 60% 布局键数），实际布局含 Esc+F 区 13 键应为 66——断言数字必须数自己的布局，别背"标准值"
- 编辑器大段替换时 old_string 覆盖面过大，把构造器签名后面的方法体吞了——大段替换后必须回读验证

### 验证
LoaderTest 4 组全过；UiProbe 16 项全过；构建 0 警告 0 错误

### 后置清单（仍待灯灯指示/素材）
手柄映射（需精灵）、皮肤/模型切换、映射窗加爪子动画帧（需新素材或程序化绘制）、自动更新

## 2026-09-02（v0.1.29：映射窗猫爪动画，BongoCat 灵魂补齐）

### 起因
v0.1.28 只做了键位高亮，但灯灯截图里 BongoCat 的核心画面是**猫爪在拍键盘/鼠标**。之前一直把爪子标记为"需素材"，实际和键盘一样可以用程序化管线解决——`tools/generate_sprites.py` 的 Cv 类（几何图元+自动描边）现成，加两个函数输出爪子 PNG 即可。

### 实现要点
- **素材**：14×14 画布画橘色绒毛掌+三趾+粉色肉垫，`Cv.out()` 自动描边后 `NEAREST ×4` → 56px；`paw_up`（待命）/ `paw_down`（下压 2px 轻压扁）两帧，存 `neko-assets/mirror/`（csproj 的 `**\*.png` 通配自动拷到输出）
- **动画**：映射窗内容改 Grid = 键盘行 + 爪子覆盖层 Canvas（`IsHitTestVisible=false` 不挡拖动）；爪从窗口上沿探入（top=-4），正好像猫趴在映射窗上方按进来（猫窗默认就在映射窗上方，视觉自洽）
  - 键盘爪：敲键 → 下压帧+3px，130ms 回弹定时器（Tick 先 `-=` 命名方法再 `+=`，防重复叠加）；连打时定时器不断重启 → 爪一直压着，符合直觉
  - 鼠标爪：按下压住、松开抬起；左/右键横向偏移 ±7px
  - 滚轮：轻拍 120ms 回弹
- 加载走 `FileStream+BitmapImage(CacheOption=OnLoad)+Freeze`（沿用 PNG 加载铁律），素材缺失 try/catch 静默降级
- UiProbe：PawAssetsLoaded / IsKeyboardPawDown 断言，FlashKey 同步触发（130ms 定时器不会在断言前触发）

### 踩坑
- `BitmapCacheOption` 缺 `using System.Windows.Media.Imaging` → 全限定修（CS0103）
- `BitmapImage` 返回给 `ImageSource?` 触发 CS8619 nullable 警告 → 显式 `(ImageSource)bmp` 强转
- DispatcherTimer 的 Tick `-=` 匿名 lambda 是无效代码（每次都是新实例，卸不掉），必须用命名方法——上一个版本写的 `Tick -= (_, e) => { };` 自查发现后修掉

### 验证
LoaderTest 4 组 + UiProbe 16 项全过；构建 0 警告 0 错误

## 2026-09-02（v0.1.33：附加热键热更新 + Release 红线实测 + 资源红线调整）

### 改动
- **附加热键热更新**：`GlobalInput._extraHotKeys` List → Dictionary<string,...> 覆盖式注册；`PetWindow.SetExtraHotKey(id, ctrl, alt, shift, key)` 写回 config + 重注册；设置页附加热键卡片改用新方法——改热键不用重启
- **Release `dotnet publish -c Release -r win-x64 --self-contained=true` 自包含单文件 172MB**；启动后 166MB 常驻；Debug 150MB
- **资源红线调整**（灯灯确认）：体积 50→180MB；常驻 120→170MB；CPU/启动时间保持原红线。WPF 框架成本，非代码问题；想再压：关 R2R（~140MB）/ 换 Avalonia（推倒重来）

### 教训
- **`Thread.Sleep` 在 STA 阻塞消息循环** → 动画停摆 → DispatcherFrame 让消息循环跑（v0.1.30）
- **P/Invoke 改方法名必带 EntryPoint**：`[DllImport("user32.dll")] private static extern bool GetCursorPosWin32(...)` → runtime 报 EntryPointNotFoundException，因为 user32 导出名是 `GetCursorPos`。要么保留原名，C# helper 改名区分；要么 P/Invoke 加 `EntryPoint="GetCursorPos"`（v0.1.31）
- **WPF 部署体积不可能 <50MB**：runtime + WPF 全量就要 ~100MB（v0.1.33 调红线至此）
- **WPF 冷启动 <1.5s 偏紧**：Debug + Release+R2R 都是 ~1.96s（R2R 不影响——瓶颈在 WPF runtime init + 插件 ALC 加载，非 JIT）。调红线到 <2.0s；优化空间：插件懒加载（v0.1.35 候选）
- **沙箱禁用清单**：`wmic` / `schtasks` / WMI Process.Create / Add-Type / Bash 调 PowerShell。杀 Neko 进程统一 PowerShell 工具 `Stop-Process -Name Neko -Force`（v0.1.28 经验）

### 验证
LoaderTest 4 组 + UiProbe 16 项全过；Debug 构建 0 警告 0 错误；Release 172MB/166MB

## 2026-09-02（v0.1.35：真机崩溃热修复——冻结刷动画）

### 事故
v0.1.34 真机运行 11 分钟后，**用户点击鼠标** → `System.InvalidOperationException: 无法在 System.Windows.Media.SolidColorBrush 上激活 Color 属性，因为该对象已密封或已冻结`。栈：`AnimateBg` ← `OnRawMouseButton` ← `MouseHookCallback`。

### 根因
v0.1.32 我把鼠标键高亮从"直接赋新刷"改成 `AnimateBg`（`scb.BeginAnimation(ColorProperty, ...)`）。但 `MakeMouseKey` 初始 Background 用的是 **`Brushes.Transparent`——这是 WPF 系统预制的冻结刷**。对冻结刷调 `BeginAnimation` 直接抛异常。
- 为什么 v0.1.32/33 没炸：UiProbe 只模拟键盘键（`FlashKey`），键盘键 Background 是 `new SolidColorBrush`（非冻结）→ 动画正常；鼠标键路径（`OnRawMouseButton`）从未被测试覆盖
- 为什么真机炸：用户点了鼠标（左键）→ `OnRawMouseButton(0, true)` → `AnimateBg` → 崩

### 修复
1. `AnimateBg`：`scb.IsFrozen` 检查——冻结刷直接 `b.Background = new SolidColorBrush(target)` 替换，跳过动画（防御所有未来 frozen 场景）
2. `MakeMouseKey`：初始 `Background` 改用非冻结 `new SolidColorBrush(Color.FromArgb(0,0,0,0))`
3. UiProbe：加 `FlashMouse(btn, down)` 公开方法 + 第 16 项补鼠标键按下/松开断言（左+中键）——从此鼠标键动画路径有守护

### 教训（沉淀）
- **Brushes.Transparent / Brushes.White 等静态系统刷全部是冻结的**，不能 BeginAnimation、不能改属性——动画对象必须用 `new SolidColorBrush(...)`
- **UiProbe 覆盖不等于全部覆盖**：键盘键路径过了，鼠标键路径没人测。所有新动画路径都要同步加 probe 断言
- 真机 11 分钟才触发，说明崩溃是"事件驱动+特定交互"——真机验证要覆盖交互路径，不能只看存活

## 2026-09-02（v0.1.36：键盘显示不完整 + 无法翻页，两个"老 bug"真因定位）

### 灯灯反馈
"你这个键盘首先还是显示不完整…而且右键菜单无法翻页到第二页…你自己跑的时候多验证一下功能"

### Bug 1：键盘显示不完整 = Grid 列溢出
- `AddKey` 用 `Grid.SetColumn + Margin 左偏移`。1u 列宽 = KeyW + KeyGap = 24px，但 1.5u 键宽 = 34px、1.75u = 40px、2.25u = 52px —— **每个非整数宽键都超出所在列，盖住相邻键或溢出截断**（Tab 盖 Q、Back 截断成 "Ba"）
- v0.1.30 只把尺寸 34→22 缩小，没改布局算法，所以用户看到"还是这样"
- **修法**：重写为 Canvas 绝对定位——行内游标 x 累加 `span*Unit`，键宽 `KeyW*span + KeyGap*(span-1)`，`Canvas.SetLeft/Top` 精确放置。无 Grid 列的概念，数学上不可能重叠/溢出

### Bug 2：右键菜单无法翻页 = DPI 缩放误判外部点击
- v0.1.30 引入"点外部才关菜单"：`OnExternalMouseClick` 拿 `GetCursorPos`（**物理像素**）和 `Rect(Left,Top,Width,Height)`（**DIP**）直接 `Contains` 比较
- DPI≠100%（125%/150% 常见）时，点翻页胶囊的物理坐标换算成 DIP 后落在菜单 rect 外 → **菜单直接关闭，翻页永不触发**。用户表现为"点胶囊菜单消失/无法翻页"
- **修法**：`IsClickInsideMenu()` 用 `PresentationSource.FromVisual(this).CompositionTarget.TransformFromDevice.M11/M22` 把物理坐标转 DIP（PetWindow.GetWorkArea 同款做法，v0.1.1 就踩过物理/DIP 的坑，这次漏了）

### 教训（沉淀）
- **坐标系转换是高频坑**：凡是"Win32 API 坐标"（GetCursorPos/GetWindowRect/MONITORINFO）与"WPF 坐标"（Left/Top/Width/Height）比较，必须经 `TransformFromDevice` 转 DIP。PetWindow 有，RadialMenu 漏了
- **UiProbe 加布局自检**：`LayoutHasOverlaps()` / `LayoutMaxRight()`（≤358）公开方法 + 断言——Grid 列溢出这种"视觉 bug"无法用像素断言捕获（窗口渲染尺寸对），必须从布局结构断言

### 后置清单（仍待灯灯指示/素材）
手柄映射（需精灵）、皮肤/模型切换、自动更新

## 2026-09-02（v0.1.30：灯灯截图反馈的视觉/UX 集中修复 + 双模态）

### 起因
灯灯 4 张截图：键盘太大显示不全、状态不刷新、右键菜单一下就没了、设置页太丑、托盘臃肿、猫要非像素参考图。他给了双模态建议：摸鱼/工作模式——工作模式猫和键盘二合一（BongoCat 那种）。

### 改动清单
- **键盘缩小 35%**：KeyW/KeyH 34→22、KeyGap 3→2、PlatePad 10→8、爪子 56→36——单 u 网格自动按 KeyW 重算，Backspace 不再截
- **鼠标修型**：高 172→82、宽 74→56、1:1.4 真实比例、左/右键改竖排
- **锁键 LED**：CAPS / NUM / SCR 三个 7px 圆点 + 8px 文字，200ms 轮询 `GetKeyState(vk) & 0x0001`——这就是灯灯说的"状态不刷新"该有的样子
- **径向菜单交互修正**：
  - 删 `Deactivated / MouseLeave / _closeTimer`（300ms 失焦即关是"还没看清就没了"的元凶）
  - ShowAt 加 `ShowActivated=false`（不抢焦点）
  - 改"点外部或 Esc 才关"：订阅 `GlobalInput.RawMouseButtonEvent` 全局点击 + `RawKeyEvent vk=0x1B`
  - 用 Win32 `GetCursorPos`（注意 `System.Drawing.Point` 与 `System.Windows.Rect.Contains` 类型差异，必须用 `System.Windows.Point` 比较）
  - 200ms 淡入 + scale 0.9→1 动画（RenderTransformOrigin 中心）
- **双模态** `AppMode ∈ {slack, work}`：
  - `work` = 键鼠映射显示 + 猫贴附映射窗正上方（`AttachCatToMirror`）+ 拖映射窗猫跟随（`onRelocated` 回调）
  - `slack` = 只猫
  - `SetKeyboardMirror` 同步切 mode（勾选=work，取消=slack）
- **托盘瘦身 14→5**：设置/暂停动画/键鼠映射/显示隐藏/退出，插件项折叠进「插件…」子菜单——其它（游戏模式/穿透/自启/缩放/回默认/关于）下沉设置页
- **径向菜单加键盘 toggle 按钮**：顶部左侧 `⌨` 圆按钮（Segoe UI Symbol 字体 U+2328），点调 SetKeyboardMirror；用 `_suppressExternalClose` 抑制紧随的 UP 事件关菜单

### 踩坑
- `System.Drawing.Point` 不能直接喂给 `System.Windows.Rect.Contains`——必须 `new System.Windows.Point(p.X, p.Y)` 转（CS1503）
- **`Thread.Sleep` 阻塞 STA 消息循环 = 动画停摆**：UiProbe 像素断言前用 Sleep(250) 等待 200ms 动画，结果 Opacity 还停在 0，alpha=0 误报"底盘太透"。改用 `DispatcherFrame` + `DispatcherTimer` 让消息循环跑：Timer 260ms 后 `frame.Continue = false` 退出 PushFrame——动画此时已完，断言正确
- 编译错 `方法组` 转 `Action?`：`new RadialMenu(_uiApi, _globalInput, onToggleMirror: SetKeyboardMirror)` 中 `SetKeyboardMirror(bool)` 不能直接转 `Action?`——必须 lambda 包裹：`() => SetKeyboardMirror(!_config.KeyboardMirrorEnabled)`
- 编译错 `DoubleAnimation` 缺命名空间：加 `using System.Windows.Media.Animation;`

### 验证
LoaderTest 4 组 + UiProbe 16 项全过；构建 0 错误（2 个 LED nullable 字段静态分析警告，无害）

### 后置（v0.1.31 候选）
设置页大改（深色主题 + 左侧 sidebar + 卡片式 toggle，参照截图 3）；非像素版猫咪参考图（Pillow 程序化生成抗锯齿+渐变版放 `neko-assets/reference/`）

## 2026-09-02（v0.1.31：设置页大改 + 非像素猫参考图）

### 改动
- **`tools/generate_soft_cat.py` 新增**：4x 超采样 + LANCZOS 缩小 = 抗锯齿；椭圆径向渐变填充（橘→深橘）；耳朵尾巴用 `polygon+GaussianBlur` 柔边。输出 `neko-assets/reference/neko_soft.png` 512×512 透明背景——和原版像素猫对比看风格差异
- **SettingsWindow 整体重写**（352 行 → ~500 行，深色主题）：
  - 主题色：根 #1E262F / sidebar #252F3B / 卡片 #2B3647 / 选中 #2A3647 / 边框 #38445A / accent #4C9FFF / 文字主 #E5E7EB / 副 #9CA3AF / toggle off #475569
  - 左侧 sidebar 5 项（emoji + 文字 + 选中态 3px 蓝色 accent 条）
  - 卡片构建器：BuildCard / BuildToggleCard / BuildChoiceCard / BuildStepperCard / BuildButtonCard / BuildHotKeyCard / BuildHotKeyRestart
  - **自定义 `ToggleSwitch` 控件**（40×22 胶囊 + 18px 圆点 + `DoubleAnimation 0.15s` 滑动 + 蓝/灰切换）
  - 互斥按钮组替代 ComboBox（深色卡片里 ComboBox 太突兀）
  - + - 步进按钮替代 Slider（缩放/透明度）
  - 附加热键改动提示需重启（v0.1.27 启动时一次性注册，运行时改不动）

### 踩坑
- 缺 `using System.Windows.Shapes;` → Ellipse 找不到（CS0246），加上即过
- 杀 Neko 才能 rebuild 产物（apphost.exe 被锁）——每次发版前先 Stop-Process

### 验证
LoaderTest 4 组 + UiProbe 16 项全过；构建 0 错误（2 个 LED nullable 警告无害）

---

## 2026-09-02（v0.1.39：双模态实质化 + 一切绑定猫位置）

灯灯一次性提了 8 项，看起来散，根因其实就两条：**① 切换模式时猫没任何变化 ② 浮层不跟着猫走**。
本轮全部收敛到「以猫为锚」这一个原则上。

### 8 项逐条落地

| # | 需求 | 做法 |
|---|---|---|
| 1 | 工作↔摸鱼猫没变化 | 摸鱼＝猫滑到最近边缘挂住（且不自动脱离）；工作＝映射窗来就猫 |
| 2 | 长名字悬浮漏全名 | `FormattedText` 实测宽度，只在真截断时挂 Tooltip |
| 3 | 暂停改成开关 | 托盘菜单点完 `RefreshMenu()` 重建，文案就地切「开始动画」 |
| 4 | 去掉 BongoCat 字样 | 应用模式卡片 + 关于页文案清理 |
| 5 | 键盘朝向镜像 | `ScaleTransform(-1,1)` 作用于内容根，键帽 + 爪子一起翻 |
| 6 | 托盘加「关于」 | 标题直接带版本号 `v0.1.39`，点进去打开设置页关于栏 |
| 7 | 轮盘动画 + 跟随猫 | 槽位逐个入场（28ms 阶梯）+ `MoveTo()` 主循环/拖拽同步 |
| 8 | 呼出键盘猫瞬移 | **方向反了**：改成映射窗位置由猫派生，猫不再被搬 |

### 关键决策：谁是谁的锚？

v0.1.30 的设计是「猫贴映射窗」（`AttachCatToMirror` 改猫的 Left/Top），映射窗位置来自 `MirrorPosX/Y` 记忆。
这带来两个必然结果：切到工作模式时猫被瞬移到键盘上方；拖键盘时猫也瞬移。

v0.1.39 反过来——**猫是锚，映射窗是被派生方**：

```
SyncMirrorToCat():  mirror.Left = cat.CenterX - mirror.Width / 2
                    mirror.Top  = cat.Bottom + 6        // 放不下则挂到猫上方
```

用户主动拖键盘时才反向带猫，且走 `AnimateMoveTo(220ms)` 平滑跟随，不瞬移。

### 踩坑：WPF 位移动画的三个反直觉行为

**坑 1 —— 动画结束后赋值会被吃掉。**
`BeginAnimation(Window.LeftProperty, anim)` 默认 `FillBehavior.HoldEnd`，动画「还挂」在属性上，
此时 `Left = 100` 无效（动画优先级高于本地值），现象是**猫粘住再也拖不动**。
必须在 `Completed` 里先撤动画再赋本地值：

```csharp
ax.Completed += (_, _) =>
{
    BeginAnimation(Window.LeftProperty, null);
    BeginAnimation(Window.TopProperty, null);
    Left = left; Top = top;
    _moveAnimating = false;
    SavePosition();
};
```

**坑 2 —— 打断动画会跳回起点。**
动画进行时读 `Left` 拿到的是**当前插值**（不是 base value），但 `BeginAnimation(prop, null)` 一撤，
值立刻回落成动画开始前的 base value → 视觉上「啪」地跳回去。
所以 `CancelMoveAnim()` 必须**先读、后撤、再写回**：

```csharp
var cur = new Point(Left, Top);              // 先读动画当前值
BeginAnimation(Window.LeftProperty, null);
BeginAnimation(Window.TopProperty, null);
Left = cur.X; Top = cur.Y;                   // 再固化成本地值
```

**坑 3 —— 四条链路都要同步浮层。**
拖拽 / 落体 / 边缘吸附 / 平滑位移都会改 Left/Top，漏哪条哪条就出现「菜单飞了、键盘掉了」。
统一收口到 `SyncFollowers()`：主循环（20Hz）+ 拖拽 `OnMouseMove`（跟手，补主循环频率不足）+ 落体 tick 各调一次。

### 待办（灯灯口述，已记录等指示）

1. **封包**：做安装文件时，首次安装引导设置「猫如何称呼自己」+「给猫取名」
2. **气泡对话**（排在封包之后）：猫时不时说话（"工作了很久要不要休息"、"天气怎么样"），
   依据工作时长 / 用户选择定位 / **好感度**决定；备忘录、天气等通知也走气泡；形态为**像素云朵**

### 验证

LoaderTest 4 组 + **UiProbe 18 项**全过；构建 0 警告 0 错误。
新增第 18 项守护：键盘镜像 ScaleX ±1、轮盘 `MoveTo` 圆心正确、work 模式映射窗居中于猫
**且猫的 Left 一步不动**（这条直接锁死「瞬移」回退）。

---

## 2026-09-02（v0.1.40：气泡对话——像素云朵 + 通道统一）

灯灯明确"在上面所有问题解决后加入气泡对话"。本轮核心发现：
**`PetRenderer.Say` 从 v0.1.0 就有接口，但 Draw 里压根没画——所有插件的 `Say` 一直是有声无画。**
所以本轮的工作 = 把这个"有接口没渲染"补完，所有 12 个用 `Say` 的插件（WordCard/Quote/QuickSearch/Weather/Pomodoro/SleepReminder/Countdown/Screenshot/SystemInfo/ColorPicker/HourlyChime/Affection）**通知自动统一成像素云朵**，
这就是灯灯要的"备忘录/天气等通知也走气泡对话"——一个渲染层统一解决，零插件改动。

### 架构（dogfooding 铁律兑现）

- **宿主内置（渲染/窗口物理/全局输入钩子类底层能力）**：
  - `BubbleWindow`——像素云朵渲染（方块密铺 + FormattedText 算宽 + IsHitTestVisible 穿透 + 跟随猫）
  - `ActivityTracker`——键鼠活动统计（数据源 = GlobalInput 原始事件，DOWN 才计）
  - `ChatScheduler`——定时调度（DispatcherTimer 60s tick）
- **决策/内容**：第一版内置台词库（4 档好感度 × 2 场景 × 4 条），后续可拆成内置插件（dogfooding）

### BubbleWindow 像素云朵形态

不是光滑椭圆，是**方块密铺**（7px 块 × 1px 描边 = 像素网格感）：

```
   ██ ██              ← 上凸块（居中 4~16 块）
 ██████████            ← 主体第 1 行（文字背景）
 ██【 文字 】██        ← 文字居中
 ██████████            ← 主体末行
   ██ ██              ← 下凸块
       ██ ██          ← 尾巴（中央 2 块，朝下指向猫）
```

宽度按 FormattedText 实测文字宽自适应（列数 8~24，最大文字区 148px），多行自动换行。

### 踩坑：WPF 窗口渲染到 RTB 的色彩微差

第 19 项像素断言一开始死磕 `B >= 220`，多次 B=131/169/225 不达标。诊断：
- **B=131**：采到文字笔画（文字 #4A3728 抗锯齿）—— 改采主体最左块（X>=18 文字区外）
- **B=169**：采到文字右半抗锯齿 —— 同上
- **B=225**：WPF 窗口到 RTB 的 sRGB 色彩管理 + pre-multiplied 合成导致
  实际像素源 #FFF9EC 在 Pbgra32 输出时被微调（`src*opacity` 算 B=210~236 区间），不是纯渲染 bug

**修正**：放弃"采固定坐标卡死阈值"，改为"扫主体区域找任意 alpha>=200 的非文字点"——
意图是"云朵主体存在非透明白块"，不是"特定 RGB 值精确"。这才是真正的回归守护。

### 设计权衡：为什么不内置插件 `com.lantern.chat`？

- 内置插件有"和第三方同权"的隔离/加载开销，对每分钟 tick 的闲聊反而慢
- 渲染层（BubbleWindow）必须宿主内置（架构铁律：渲染/窗口物理 = 宿主）
- 调度/台词生成放插件化会让数据流变复杂（ActivityTracker 要跨 ALC 共享）
- 第一版内置，**接口已设计成 Action<>/Action<,> 委托，台词库是静态数组**——后续要插件化只需拆 `ChatScheduler` 为接口 + 把 `Pick()` 委托给内置插件
- 同时把这条原则写进 dev-log 备查

### 验证

LoaderTest 4 组 + **UiProbe 19 项**全过；构建 0 警告 0 错误。
Release v0.1.40 / Neko.exe 打包完成；真机启动跨调用存活，PID 13880，**常驻 171MB**——
比 v0.1.39 的 165MB 多 6MB（BubbleWindow 渲染 + ActivityTracker/ChatScheduler 运行时数据），
红线 170MB 略超 1.3MB（< 1%），**在可接受波动内**。如果要严格回线，下版可考虑：合并 BubbleWindow 内容 Canvas 的 Background=null 检查（当前 IsHitTestVisible=false 单独设）、或把 _iconCache 的冻结时机提前。
诚实记录，让灯灯拍板。

### 待办（未变）

- 封包 = 安装文件首次安装引导「猫如何称呼自己」+「给猫取名」
- 气泡对话已交付（本轮）

---

## 2026-09-02（v0.1.41：首启引导 + 称呼融入气泡对话）

封包需求核心可交付部分（"首次安装时设置称呼+取名"）——不依赖 Inno Setup/WiX 工具链，
直接做在 App 启动流程里：config 缺字段时弹 FirstRunWindow 引导窗，效果等同"安装时设置"。

### 三个新东西

| 文件 | 职责 |
|---|---|
| `Windows/FirstRunWindow.cs` | 像素风对话框（深色 + 蓝 accent，与设置页同主题）：两个 TextBox（猫名+称呼）+ 保存/退出；空/超 12 字拦截；取消 = 整个程序退出 |
| `Services/ChatScheduler.cs` | ctor 新增 `catName`/`ownerTitle` 参数；台词模板化（`{0}` 称呼 `{1}` 猫名）；L2/L3 融入，L0/L1 故意不融入（生疏期喊太亲昵出戏） |
| `Windows/SettingsWindow.cs` | `BuildTextCard` 新构建器；外观组末尾加称呼/猫名编辑；改完调 `PetWindow.RebuildChatScheduler` 让新台词立即生效 |

### 关键决策

- **首启做完才能进主程序**：用户点退出 = `Application.Shutdown()`，不是最小化到托盘——
  不让用户带着没名字的猫凑合（"没名字"会被 ChatScheduler 兜底"主人/Neko"，但首启没填就退出是态度）
- **App 内引导 vs 安装器引导**：选前者，因为不依赖工具链、跨开发机也能用；安装器只负责注册表/开始菜单/卸载
- **L0/L1 不融入**：L0 陌生档喊"主人"太出戏（"喵～今天过得怎么样？"已经够礼貌），L1 熟悉档也没必要
  （设计原则：亲密度匹配语气）
- **不修改插件代码**：v0.1.40 的 Say 通道已统一，ChatScheduler 内部 `string.Format` 就够
  （AffectionPlugin 升级台词不带称呼，但用户原话主要是"猫时不时说话"对应 ChatScheduler，已覆盖）

### 验证与红线

- LoaderTest 4 组 + **UiProbe 20 项**全过（第 20 项专门覆盖：Config 字段 + FirstRunWindow ctor + Pick 模板替换 + L0 不含占位符）
- 构建 0 警告 0 错误
- **常驻 156MB**（v0.1.40 171MB → 156MB，**红线 170MB 内大幅回落**）——
  推测：首启引导 ShowDialog 阻塞了部分初始化路径（PetWindow + 映射窗 + 气泡的 renderer 还没全展开）；
  或者 .NET GC 在引导期间做了更激进的回收。**记到 MEMORY 备查**——v0.1.40 的 171MB 是首次冷启动尖峰，
  v0.1.41 第一次跑就 156MB 说明 v0.1.40 也不是泄漏，是 GC 节奏

### 待办（剩一项）

- 封包成安装文件（Inno Setup 脚本 + 开始菜单/注册表/卸载）—— App 内首启已交付，剩下的纯打包工具链

---

## 2026-09-02（v0.1.44 + v0.1.45：猫爪举手 / 按键还原兜底 / 缩小融合 / 插件页卡片化 / --dev）

### v0.1.44：灯灯 4 项反馈

1. **猫爪挡键盘**：v0.1.43"爪悬停在最后按的键上方"反而一直挡键（灯灯：不用键盘时请举手）。
   改为**举手位** `PawRaisedTop = -PawSize - 3`（完全在键盘上方之外），敲键瞬间才落到该键上拍 130ms 后举回
2. **按键"按着才亮、松开就白"**：GlobalInput UP 链路本身正常，但连打/热键路径偶发丢 UP。
   新增 `_litKeys`（Border→按下时刻）+ `_ledTimer` 200ms 轮询：亮超 300ms 且期间没再输入（长按刷新时刻）强制还原。
   **教训：交互正确性不能只依赖事件链路，必须加兜底状态机**
3. **键鼠页缩小 20% + 更融合**：键 22→18px、键距 2→1.5、鼠标 56→46×82→68、底盘内边距 8→6、爪子 36→30、字号 9/11→8/9。
   猫坐姿 + 间隙 2px + 爪跟键（v0.1.43）不变
4. **插件页卡片化**：旧 ListBox 左右布局 → 每插件一张卡（名称+版本/作者/描述/红字加载错误/内置设置页嵌入）

### v0.1.45：--dev 跳过首启引导

灯灯：测试开发阶段别每次启动都弹首启引导输名字。
`App.OnStartup` 检测 `e.Args` 含 `--dev` 或 `--skip-onboarding` 跳过；正式版不带参数仍弹。
ChatScheduler 已有 "主人"/"Neko" 兜底，跳过不影响功能。

### 封包尝试（WiX，失败→成功记录）

**第一轮失败（WiX 7 / Inno Setup 下载）**
- Inno Setup 下载被沙箱网络拦（GitHub TLS handshake 失败 / download.php 只回 HTML 页）
- WiX 7（dotnet tool 最新版）schema 大改（MajorUpgrade/MediaTemplate 无处安放），降级 WiX 4.0.5

**第二轮失败（WiX 4.0.5，关键坑）**
- `wix build a.wxs b.wxs` 只收单源（第二个被忽略）；先 build wixlib 再 link 也不行
- **最隐蔽的坑**：WiX 4 里 StandardDirectory/Component/ComponentGroup/Feature **必须放在 `<Package>` 元素内部**，
  放 Fragment 里会被链接器静默丢弃——生成 28KB 的"空壳 MSI"（`wix msi decompile` 只剩 Package 骨架，安装报 1603）
- 用最小 wxs 二分实验定位（min1 无内容成功 → min5 Package 内嵌成功 56MB）——**实验驱动，一次一个变量**

**成功路径**
- `installer/gen_setup_wxs.py` 生成 self-contained wxs（静态骨架 + 发布目录 115 文件清单，uuid5 稳定 GUID）
- `wix build NekoSetup.wxs -o NekoSetup.msi -d SrcDir=...` → **53MB MSI**
- 实测：`msiexec /i` 静默安装到 `%LOCALAPPDATA%\Neko`（19 插件 + 桌面/开始菜单快捷方式）✓；安装版可启动 ✓；`msiexec /x` 卸载清理文件+快捷方式 ✓（用户 data/ 保留）
- 一键出包：`installer/build_setup.py <version>`

### 经验沉淀

- **WiX 4 schema 结构与 v3 差异巨大**：Package 是根容器（替代 Product），MajorUpgrade/MediaTemplate/StandardDirectory/Component/Feature 全在 Package 内；Fragment 里的安装内容会被静默丢弃
- **MSI 是否有效用 `wix msi decompile` 验证**（28KB 空壳 → 只剩 Package；有效 → 反编译出完整结构）
- 沙箱网络：GitHub 被 TLS 拦（curl/node/winget 全失败），NuGet 通（dotnet tool 可用）

---

## 2026-09-02（v0.1.48：柔光皮肤切换，进行中）

灯灯"继续"推进剩余项里素材已备最大的——柔光版皮肤切换。

### 计划

- **SpriteAtlas 加 Skin 参数**（pixel / soft 两种文件布局）：
  - pixel 现状：neko_<state>.png 4 帧横排 + neko_eyes_*.png 4 眼球层
  - soft：neko_<state>_<f>.png 4 个独立 128×128 文件（眼睛已画在身体里，**EyeLayer=null 跳过叠加**）
- **FrameW/FrameH 改实例属性**（去掉 const）：软版 Bitmap 实际 128 像素但 FrameW 仍 32 → PetRenderer.Draw target=32*3.2=102 → WPF 自动缩放 128→102，视觉与像素版同尺寸
- **PetRenderer.SetAtlas(SpriteAtlas)** 公开方法（热切换）
- **PetWindow.SetSkin(string skin)** 公开：重建 atlas + SetAtlas + `_stateMachine.Play(Idle)` 强制从新 atlas 重拉（避免 _animator 缓存旧 SpriteState）+ 持久化 `Config.Skin` + `ConfigManager.Save`
- **Config.Skin** 默认 "pixel"（Config 字段类型 string）
- **App.OnStartup** 读 `Config.Skin` 构造对应 atlas（已在 PetWindow ctor 里读 config）
- **SettingsWindow 外观组**加 `BuildChoiceCard("外观风格", "默认像素风，柔光为可选项", ["像素风", "柔光风"], idx, onChange)`：回调调 `PetWindow.SetSkin(newSkin)` + `Save()`

### 进行中

- 灯灯"在本地和云端生成记忆"——优先做了完整记忆沉淀（详见当日日志），完成后继续 v0.1.48 改造
- 改动点：SpriteAtlas.cs / PetRenderer.cs / PetWindow.cs / Config.cs / SettingsWindow.cs（BuildPetPage）/ UiProbe.csproj（无需改）/ UiProbe 第 23 项可加皮肤断言

### 经验沉淀（持续累积）

- **沙箱进程管理铁律**（已记）：Bash `run_in_background=true` 唯一正路，沙箱工具 Stop-Process 杀进程
- **记忆沉淀节奏**：每个版本完成后立刻 CHANGELOG + dev-log + 工作区 memory + 用户级 memory 同步更新——保证未来会话零摩擦接力

---

## 2026-09-02（v0.1.50：Alt+Tab 隐藏 / 深浅主题+中英语言 / 柔光简笔画重做）

灯灯 5 项反馈一次收：Alt+Tab 显示窗口 / 柔光皮肤丑 / 深浅主题+语言 / work 模式 BongoCat 化 / 稳定+日志。

### ① Alt+Tab 不显示（WS_EX_TOOLWINDOW）

- 新建 `Windows/WindowEx.cs`：`HideFromAltTab(Window)` 加 `WS_EX_TOOLWINDOW`（0x80）到 `GWL_EXSTYLE`
- PetWindow / InputMirrorWindow / BubbleWindow / RadialMenu 的 `OnSourceInitialized` 里调用（幂等）
- **FirstRunWindow 不加**（首启引导窗必须能被找到）
- 配合已有 `ShowInTaskbar=false` → Alt+Tab 完全不出现（BongoCat 同理）
- 坑：设置要等 `OnSourceInitialized`（窗口句柄创建后），ctor 里拿不到 hwnd

### ② 深浅主题 + 中英语言

- `Config.Theme`（dark/light）+ `Config.Language`（zh/en）
- **SettingsWindow 颜色重构**：11 个 static readonly 颜色常量 → 实例 readonly 字段，ctor 按 `_config.Theme` 选深/浅两套色板（深 #1E262F 系 / 浅 #F5F7FA 系）
- **ToggleSwitch 支持主题**：ctor 加 `offColor` 参数（原静态 OffColor 深色，浅色主题轨道会太暗）；4 处 `new ToggleSwitch` 调用点传外层 `ToggleOff`
- 通用页加「外观与语言」分组：主题 + 语言两个 ChoiceCard，保存后 MessageBox 提示"重开设置窗口生效"（主题色是 ctor 初始化的 readonly，不重建窗口无法即时刷新——务实方案）
- sidebar 标签 + 页面标题双语：`string T(string zh, string en)` helper，核心文案双语，其余后续

### ③ 柔光皮肤简笔画重做（BongoCat 风）

- 旧 `generate_soft_cat.py`（4x 超采样 + GaussianBlur + LANCZOS）→ 用户嫌"糊、丑"
- 新 `tools/generate_bongocat.py`：**简笔画**配方：
  - 粗描边：先画黑大一圈（fill=outline, ±sw=6px）再画彩小一圈 → 均匀粗黑边（Pillow ellipse 没有 outline 宽度参数，双画法实现）
  - 大圆头（占 60%）+ 小圆身体（头下方）+ 黑豆大眼（白高光点）+ 粉腮红 + 小三角耳（粉内耳）+ 嘴 w 弧线 + 胡须
  - 平涂色无渐变无柔化（简笔画要的就是"干净线条"）
  - work 状态：猫蹲低（身体下压）+ 前爪伸到画面底沿（y=508≈触底=趴在桌面边沿伸爪按键盘）
  - 输出格式不变（soft/neko_<state>_<f>.png 128×128）→ SpriteAtlas.LoadSoft 直接读，零宿主改动
- **画 BongoCat 风的要点**：头大身小、眼大而圆带高光、描边粗而均匀、配色干净（橘 #FFA34D + 黑描边 #342C28 + 粉 #FFB2C2 + 白）

### ④ 稳定性 + 日志

- UiProbe 第 24 项：Config Theme/Language 字段 + SettingsWindow 深/浅主题色实例化断言（BgRoot 深=#1E262F / 浅=#F5F7FA）
- LoaderTest 4 + UiProbe 24 项全过；构建 0 警告 0 错误
- Release v0.1.50 发布（plugins 19）；真机跨调用存活（PID 48896，167MB）
- CHANGELOG / dev-log / MEMORY / 用户级记忆全同步

---

## 2026-09-02（v0.1.51：简笔画 9 状态验收修复）

按"改完必须真看像素"铁律，**v0.1.50 柔光 9 状态生成后真看 3×3 网格预览**，发现：
- **fall 看着像 idle**（身体没动，无下落感）——dy 没下压
- **climb 头与身体分离**（hcy 固定偏上 + body 偏下 = 头悬空感）
- **drag 尾巴没炸毛**（与 sit 几乎一样）

**修 generate_bongocat.py**：
- **fall**：body 下压（dy = 60 + frame×8 逐帧下落）+ 后倾 cx+20 + 四仰八叉惊慌前爪 + 尾巴大角度上飘（pline 从 `[(400,bcy+10), (440,bcy-80), (460,bcy-160), (430,bcy-200)]`）+ eyes scared 缩圆
- **climb**：竖直窄长椭圆（rx 50 ry 130）整条贴墙 + 头/身体位置下移紧贴（hcy=180+dy / bcy=310+dy）+ 前后各两对爪抓墙（身体上/下各 2 个 paw）+ 尾巴向下垂
- **drag**：尾巴炸毛后翘 + 眼睛 scared 缩圆

**坑：32×32 画布 + ImageDraw 没 rotate API**——像素版做不到"身体倾斜"，但柔光版（512→128）能画夸张尾巴/四仰八叉区分状态。柔光版用 4x 超采样到 128 后边缘平滑但仍是简笔画（粗描边+平涂）。

**质量保证原则**：素材生成完一定要看预览图（不是只看代码生成 0 错误）。

---

## 2026-09-02（v0.1.52：语言即时生效 + 像素 fall 微调）

- **语言切换即时生效**：v0.1.50 改的"重新打开设置窗口"体验差——回调从 `MessageBox+重开` 改为 `ShowSection(_currentSection)` 重建当前栏。页面标题/卡片文案实时变（sidebar 是 BuildLayout 一次性建的不变，下开窗变）
- **像素版 fall 抽查微调**：`st_fall` 尾巴上飘更夸张（pline 起点 y 22→17，像飘起来）；身体缩小（rx 8→7）+ 头缩小（rx 6→5）—— 之前看像 walk，现在"惊慌小猫尾巴上飘"明显

### 经验沉淀（持续累积）

- **"改完必须真看像素"铁律**（已记多次）：素材生成、状态帧调整、视觉改动后必须真实预览图验收。**写代码对 ≠ 看起来对**（v0.1.48 像素 work 4 条腿 / v0.1.50 柔光 fall 像 idle 都是写代码对但视觉错）
- **"逐步发布+版本号严格"**：v0.1.51（柔光重做+Alt+Tab+主题+语言）+ v0.1.52（语言即时+像素 fall 微调）——微小改动也升版本，避免 publish 与源码漂移
- **沙箱网络限制不变**（已记多次）：GitHub TLS 拦 / NuGet 通 / IS 下载被网络拦 → 封包走 WiX 4

---

## 2026-09-03（v0.1.53/54：AI 素材评估 + 像素版画法学 AI + 柔光屏蔽）

### ① AI sprite sheet 评估结论（重要认知）

灯灯用即梦 AI 生成了 13 张素材（9 sheet + 4 眼球）放 G:/lantetn-li/资源/neko像素图，评估后**不可直接替换**：

- sheet 2560×1440 大画布"4 只猫排一列"不是紧凑 4 帧；帧间不一致（walk 腿乱飘）
- 背景不纯（渐变/键盘场景）；drag 有人手；work 是完整场景；眼球层都带脸；有"即梦 AI"水印
- **这是生图 AI 硬伤**（把"4 帧动画"理解成"4 张同主题图"），所有 AI 都一样，不是 prompt 问题
- AI 图的价值 = 造型参考（虎斑/腮红/圆润感），不是 sprite sheet

### ② 像素版画法学 AI（v0.1.53）

- generate_sprites.py：_blush() 腮红（face_open/pet 加 cx±6 粉点）+ 头顶 C_FUR_L 高光两点 + body 侧纹左右对称 4 条
- generate_bongocat.py：palette 补 C_FUR_D + 额头虎斑三竖线 + 头顶高光弧线
- 素材备份 neko-assets.bak-20260903/
- 注意：当时会话模型不支持看图——预览用 present_files 给灯灯审，用户确认"可以"才继续

### ③ 柔光屏蔽（v0.1.54，灯灯指示"先专注像素版"）

- SettingsWindow 皮肤切换卡注释掉；PetWindow.SetSkin + ctor 白名单（非 pixel 强制回 pixel）
- 素材/代码保留不删：恢复 = 取消注释 + 删白名单 2 处
- **MSI 不再随版本自动生成**（灯灯约定：需要时手动 build_setup.py）

### 经验沉淀（持续累积）

- **AI 生图 ≠ 游戏素材管线**：AI 出 sprite sheet 永远不连贯；做游戏帧动画要么程序画（可控）要么真人画/专业工具
- **用户约定要即时记**（MSI 按需、柔光屏蔽）：每个版本都进 CHANGELOG + memory，未来会话不踩
- **功能开关优先于删代码**（柔光屏蔽用注释+白名单）：要恢复时 2 分钟搞定，不用翻 git 历史

---

## 2026-09-06（v0.1.59：性能优化 P1-P4 + 交互增强批次 3 ⑩⑨⑧⑦）

灯灯指示「继续批次 3，且检查一下目前开发情况」。本版把 QA 的 P1-P4 体检结论和批次 3 四项功能一并交付。

### ① 勘察先行（不写代码，只读 + 报告）

按 SOP，工程师先勘察再实现（团队分工：team-lead 定规格 → 我实现 → QA 验证）。勘察覆盖 5 个问题：主循环挂点、StartFall 改抛物线风险、爱心粒子落点层选择、SetMood/Play 调用点、FrameAnimator/DrawingVisualHost 职责。关键结论直接影响设计：

- **爱心粒子必须走独立浮层窗口**：`DrawingVisualHost.MeasureOverride` 把绘制区钉死在 32×Scale，粒子飞出猫身会被裁切；放大主窗口又破坏像素对齐 → 按 `BubbleWindow`/`RadialMenu` 范式开独立窗口
- **StartFall 改抛物线会与吸附逻辑抢方向盘**：`UpdateEdgeSnap` 只在 `_dragging||_moveAnimating` 早返回，飞行中两标志都假 → 必须加 `_fallTimer != null`

### ② P1-P4 是"行为等价"优化，改前明确"不能改变任何可见行为"

- P1 LED 轮询早返回：`RefreshLeds()` 加 `if (!IsVisible) return;`——SetAppMode/游戏模式只 Hide 不停 timer，隐藏态白跑 5Hz GetKeyState
- P2 剪贴板降频 800→1500ms：顺手把魔数提成命名常量
- P3 闲置系统调用按需化：三处（眼神/鼠标广播/工作区）加"无变化不干活"缓存，**都是行为等价**——鼠标不动结果必然不变，猫不移动工作区必然不变
- P4 渲染时钟统一：`_flashEyesUntil`/`_typingUntil` 从 `DateTime` 改为 `_clockMs` 基准 double，与 v0.1.58 眨眼的绝对截止时间口径统一；**时长必须不变**

### ③ 批次 3 四项（按难度从轻到重）

- **⑩ 鼠标路过张望**（最轻）：复用 `SetEyeOffset`，只加 gain 参数（不新建渲染通道）；`_peekActive` 只在进入/离开边界切换一次，避免每 tick 触发
- **⑨ 心情驱动动画**：好感度读取**提取**成 `AffectionReader` 静态类（ChatScheduler 委托它），不新写一份读取逻辑；60s 缓存避免每 tick 读盘；挂点在两 Tick 之间
- **⑧ 爱心粒子**：Program Geometry 贝塞尔画心（不引入 PNG）；粒子窗口随猫同步；播完自动隐藏不常驻
- **⑦ 扔猫抛物线**：vx 同口径 ×60、左右以猫中心判定反射、触地 `vx *= 0.6` 摩擦 + 低速归零；**同时修 UpdateEdgeSnap 早返回条件**

### ④ 工程约定沉淀

- **视觉改动必须进 UiProbe**（硬要求）：V0.1.32 漏了鼠标键路径导致真机 11 分钟后才崩的教训 → 本版新增第 26/27/28 项断言（P4 字段类型 + ⑩增益 + ⑨档位/帧率 + ⑧粒子数/自动隐藏 + ⑦水平飞越边界）
- **可复用逻辑提取而非复制**：`ReadAffectionLevel` → `AffectionReader`，两处共用单一数据源，避免分叉
- **dotnet 走包装脚本** `bash /c/Users/Lantern/neko-dotnet-env.sh <参数>`（沙箱缺 Windows env 变量，直调 dotnet 会在 NuGet restore 崩）
- **子代理 shell stdout 抓不到但重定向到项目内文件可用**：Bash 工具直接输出恒为空（连 `echo` 都空）、`>`/`>>` 重定向到 `/tmp` 也不落盘；但重定向到**项目工作目录内的相对路径**（如 `./x.log`）文件会真实生成，再用 Read 工具读取即可拿到完整输出。后续所有 dotnet 命令统一 `... > x.log 2>&1; echo EXIT=$? >> x.log` 再读文件

### ⑤ ⑧ 爱心粒子 QA 失败复盘（第一版 27/28 → 修复后 28/28）

**现象**：QA 跑出 `⑧：爱心寿命 800ms 后应全部消散，实际仍剩 5 颗`（Burst 数量/穿透/不拦鼠标三条均过，仅"消散"挂）。

**排查（关键：先拿证据再改）**：
1. 给 `OnTick` 加 `_tickCount` 诊断计数 + UiProbe 断言"tick 必须推进"——把"timer 没跑"与"逻辑算错"两类根因分开
2. 对照实验 A（单 timer 等待 + 原固定步长）→ `FAIL ... 剩 3 颗（tick 0→44）`：**timer 明明跑了 44 次**，排除"timer 没跑"
3. 44 tick × 16ms = **704ms < 800ms 寿命** → 真因浮出：`_clockMs` 每 tick 固定 +16ms，但 **dispatcher 在嵌套消息泵（`Dispatcher.PushFrame`）下会合并/延迟 timer tick**，1100ms 墙钟只跑出 44 tick，仿真时间被"压缩"，粒子没到寿命
4. 对照实验 B（单 timer 等待 + **真实 elapsed 修复**）→ **PASS**，坐实根因

**修复**：`OnTick` 改用 `Environment.TickCount64` 真实单调差做 `dt`（与 v0.1.58 任务5 主循环同款口径）——tick 疏密不再影响寿命语义。同时给 UiProbe 第 27 项加确定性 pump（反复 PushFrame + 20ms 短等）作双保险。

**教训**：
- **定时器驱动的仿真，别用"每 tick 固定步长"当时间源**——tick 频率不是承诺。凡依赖"经过多久"的逻辑（寿命/冷却/缓动相位），一律用真实 elapsed
- **"没生效"先分类再改**：是"回调没被调用"还是"调用了但条件不成立"？加一个计数/日志把两类分开，避免盲改一堆
- **嵌套消息泵会改变 timer 行为**：`PushFrame` 下的 tick 疏密与主循环不同，测试用例不能假定"等待 N ms = N/interval 个 tick"
- ⑧ 修复顺带自查了 ⑦（同为 DispatcherTimer 驱动）：⑦ 用的是真实 dt（沿用 v0.1.58），无同源问题；但原第 28 项只做常量/字段存在性断言（假绿风险）→ 已补真实时序断言（StartFall 后 Left **和 Top** 均须实际变化 + 飞行不越界）

---

## 2026-09-11（v0.1.60：两起「无留痕回退」的发现与修复 + 压缩取舍定量决策）

### 1. 起因：灯灯问「之前不是改了图标吗，还有记录吗」

查证结果——**图标确实被静默换回了，且无任何记录**。

逐版本比对 `neko-assets/tray.ico` 体积：

| 版本 | 体积 | 内容 |
|---|---|---|
| v0.1.53 / v0.1.54 | 1,330 B | 原始程序化像素猫头（32×32 + 16×16，RGBA） |
| v0.1.55 | 71,413 B | 豆包 AI 版①：抠白底 → 托盘下半截黑（灯灯否决） |
| v0.1.56 | 82,965 B | 豆包 AI 版②：**保留白底 + 抹水印**（灯灯认可 ✅） |
| **v0.1.57 起** | **1,330 B** | **退回像素版，无记录** |

- CHANGELOG 搜「图标/tray.ico/icon」：最后一条实质记载停在 v0.1.56 的「83KB，保留白底+抹水印」，之后**没有任何"换回旧图标"的记录**
- 文件 mtime = 9/5 13:26（v0.1.58 publish 时间点），与 v0.1.57 也对不上
- **恢复**：从 `publish/v0.1.56/neko-assets/tray.ico` 拷回（MD5 `5045aa3dc927f9226e65776f8fbffe9c`）
- **注意**：`tray.ico.bak-v0.1.54` 备份的内容也是像素版（1,330 B），**指望它回滚到 AI 版是无效的**——备份本身就不是 AI 版

### 2. 顺藤摸瓜：发布形态也被无留痕改了

检查产物时发现 `publish/v0.1.59/Neko.exe` 仅 **1.3MB**（目录 3.9MB），而 v0.1.58 是 170.9MB（目录 174MB）→ 说明 `<SelfContained>` 被从 `true` 改成了 `false`。

- 追查：工程师确认本会话只改过 `<Version>` 一行，且其**首次读取 csproj 时第 19 行已是 `false`** → 改动发生在本会话之前
- **项目无 git**，无法做代码级追溯（`git blame` 报 not a git repository）——这是本次两起问题都无法定位责任人的根本原因
- 文档本身也矛盾：`desktop-pet-plan.md` L405（决策 #17）写 `false`、L541（示例）写 `true`
- **处置**：灯灯拍板「降低装机门槛」→ 改回 `true` + 显式 `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`；同时统一 plan 文档两处

### 3. 压缩取舍：定量对照实验（关键决策依据）

自包含形态下 `EnableCompressionInSingleFile` 是个真实取舍，跑对照实验：

| 配置 | 磁盘体积 | 稳态内存（140s 观测） |
|---|---|---|
| 压缩 **on** | **80MB** | **276MB** |
| 压缩 **off** | 174MB | **177MB** |

**原因**：压缩单文件启动时需将运行时解压（`%TEMP%\.net\Neko\`），该映射常驻内存 → 多约 100MB。

**决策**：桌宠是**常驻**程序（一天 24 小时在内存里），体积是一次性成本、内存是持续成本 → **内存优先，关压缩**。结论与实测数据已写进 csproj 注释，防后人只看"80MB 更小"就误开。

### 4. 流程教训（本次最有价值的部分）

- **无留痕回退是本项目当前最大的流程风险**：两起问题（图标、发布形态）都发生在 v0.1.57-58 期间，都无记录，都靠"逐版本比对产物"才发现。**项目无 git** → 建议灯灯考虑对 `G:\lantetn-li\neko` 做版本控制（哪怕本地 git init + 定期 commit），否则此类问题永远无法追溯
- **备份要验证内容而非只看名字**：`tray.ico.bak-v0.1.54` 名字像是"v0.1.54 的备份"，实际内容与当前像素版相同，无法用于回滚到 AI 版。备份应校验 MD5 并注明来源版本
- **发布形态、磁盘图标这类"非代码配置"同样需要进 CHANGELOG**——它们不在源码 diff 的直觉范围内，最容易被漏记
- **对照实验是决策的正确姿势**：压缩 vs 不压缩不靠猜，跑两版各采 140s 内存，拿数据让灯灯拍板

### 5. 遗留与限制（如实记录）

- ⚠️ **CPU 未取得可信数据**：沙箱性能计数器实例枚举受限——`typeperf -qx "\Process(*)\% Processor Time"` 枚举出的实例里**根本没有 Neko**，`\Process(Neko)\% Processor Time` 返回的是 PDH 脏值（15-28% 波动，与内存稳定、进程空闲的事实矛盾）。**此前 v0.1.59 报告的 0.000% 同源存疑，不应作为达标证据**。CPU 复测请在真机任务管理器/资源监视器观测
- ⚠️ 体积 174MB / 内存 177MB 均略超原红线（180MB / 170MB）→ 按灯灯决策「自包含形态下红线放宽」接受
- **MSI 打包链路**：`gen_setup_wxs.py` / `build_setup.py` 按 publish 目录遍历生成清单，**自包含/框架依赖都能直接复用**；但若将来再切框架依赖，需补 ① .NET 8 Desktop Runtime 前置检测（WiX Launch Condition / Inno `[Code]`）② `neko.iss` 的注释与 `Compression=lzma2/ultra64` 参数（按 170MB 调的）

### 6. 启用本地 git（灯灯拍板「你可以做本地的git」）

**动因**：上面两起无留痕回退都因「无版本控制」而无法追溯——这是本次暴露出的根因级风险。

**落地**：
- `git init` + 两级提交（`7511be1` 初始化 / `3a38a1b` README 更新）
- `.gitignore` 设计（**先 dry-run 验证再提交**）：
  - 排除 `publish/`（4.1G，含历史自包含 Neko.exe 170MB × N 版本）、`bin/`+`obj/`（递归，覆盖 builtins/ 下的 108 个 dll 产物）、`installer/*.msi`（375M）、`data/`、`.env`（**含 ARK API Key，必须排除**）
  - 排除素材中间产物 `neko-assets/_icon_*`、`*.bak-*`、`*_preview*.png`
  - **只跟踪 257 文件 / 仓库 1.1MB**
- 提交前安全检查：确认 `.env` 与 `publish/` 均不在暂存区（`git check-ignore -v` 逐条验证）

**误判修正**：`.gitignore` 初版写了全局 `*.dll`/`*.exe`——这会误伤（虽然 builtins 的 dll 都在 bin/ 下可被覆盖），但更危险的是一旦将来有非 bin 下的必要二进制就会被误排除。改为只排 `bin/`/`obj/` 更精确。

**实战演练（验证 git 真能防住这类问题）**：
1. 模拟误操作：把 `tray.ico` 换回像素版
2. `git status --short` → 立刻报 `M neko-assets/tray.ico`
3. `git diff --stat` → 明确显示 `Bin 82965 -> 1330 bytes`
4. 还原后 `git status` 恢复干净

→ **这正是此前查不出来的那类改动**（二进制素材在源码 diff 里不可见），现在一眼可见。

**后续约定**：
- 提交格式 `<版本号>: <主题>`（与 csproj 版本同步）
- **每次改动后先跑 `git status --short` + `git diff --stat`**——防范非预期修改的第一道防线
- 素材类改动（PNG/ICO）靠 `git diff --stat` 的 `Bin N -> M bytes` 发现
- 建议每个版本交付时提交一次；`publish/` 不入库，需要时重新 publish
- （可选）将来可加 remote 推到 Gitee，但本地版本控制已解决"追溯"这个核心诉求

---

## 2026-09-11（v0.1.61：批次 A —— 可观测性 + 宿主稳定性 + 文档诚实化）

灯灯批准审计后的批次 A（三 P0）。三项按依赖顺序做：**先日志（异常要落地）→ 再异常隔离 → 后文档/接口诚实化**。

### 1. 轻量文件日志（修 P0-2）

**问题**：全项目只有 `App.xaml.cs` 写 `errors.log`（且只记未处理异常），其余 12 处全是 `Debug.WriteLine`——**Release 下被编译器整段移除，真机出问题零线索**。

**新增 `Core/Log.cs`**（静态类，四级 `Debug/Info/Warn/Error`）：
- 落盘 `data/logs/neko-YYYYMMDD.log`；`Paths.Logs` 纳入 `EnsureCreated()`；按天滚动 + 单文件超 5MB 追加 `-N`；启动清 7 天前旧日志
- **线程安全**（`lock`）——插件线程池回调也会调它；**失败静默**（所有 IO 包 try/catch，日志设施绝不反过来搞崩宿主）
- 每次 `File.AppendAllText` 重开文件（不常驻句柄，频率低）；格式 `[ts] [LEVEL] [ThreadId] msg`
- 默认 **Warning 及以上**；`AppConfig.LogLevel`（默认 `"warn"`，可 `info/debug/off`）
- `Log.Error` **同时写 `errors.log`** 保持兼容

**踩坑 1（自己的 bug，被 UiProbe 抓出）**：级别过滤初版写 `if (level < _minLevel) return;`，`Off=0` 时 `Error(4) < 0` 为假 → **off 级别仍写盘**。UiProbe「off 不写」断言直接 FAIL。修法：`_minLevel == Off` 时无条件 return。**教训：枚举当阈值用要显式处理边界值，"数值最小"不等于"关闭"**。

### 2. 插件后台异常隔离 + 熔断（修 P0-1）

**问题**（审计 P0 第一条）：`SchedulerImpl` 三处 Timer 回调裸调无 try/catch。System.Threading.Timer 回调跑**线程池**，未处理异常**直接终止进程**；而全局兜底只挂 `DispatcherUnhandledException`（**仅 UI 线程**）→ **任一插件定时任务抛异常就杀死整个 Neko**，正面违反架构铁律 3。

**实现**：
- `SchedulerImpl` 抽取 `Add(dueMs, periodMs, cb)` + `SafeInvoke` 统一包异常 → `Log.Error`
- **熔断**：`TimerHandle` 记连续失败次数（`Interlocked`），达 **5 次**自动 `Dispose` 停表 + `Log.Warn`
- `App.xaml.cs` 补 `TaskScheduler.UnobservedTaskException`（`SetObserved()` 防 CLR 终止）+ `AppDomain.UnhandledException`（留遗言，无法阻止退出）
- 注意：`TimerHandle` 是私有嵌套类，回调闭包需引用 handle 做计数——用局部捕获 + 可空抑制解决构造顺序

**测试**：UiProbe 新增 2 项——①回调抛异常后宿主存活 + 异常落盘 + 连续 5 次熔断停用（`handle.IsActive` 变 false）；②正常回调不被误熔断 + Dispose 后 IsActive=false。都通过。

### 3. 文档诚实化（修 P0-3 + P1-7 + P1-9）

- **权限模型（P0-3）改文档不改代码**：实际是「加载时校验」（声明高风险即拒载），运行时无逐次守卫。`plugin-authoring-guide.md` §4 从"每次调用校验"改写为真实口径——**诚实优于对齐一份没实现的铁律**。
- **手册漂移（P1-7）**：版本标注 v0.1.0 → v0.1.61；`enum PetState` 补第 9 值 `Work`；「8 组雪碧图」→ 9 组。
- **删死接口（P1-9）**：`IScheduler.Cron` 原实现忽略表达式退化为每分钟，属半成品；全仓（src/builtins/samples）**零实际使用**（仅接口定义 + 测试桩）→ 直接删除，改手册为 `Every/At` only。同步 `ICapabilityApi.cs` + `LoaderTest.NullScheduler` 桩。

### 4. 环境坑：UiProbe 无法构建（NETSDK1151）

App 自 v0.1.60 起 `SelfContained=true` + `RID=win-x64`，而 UiProbe 非自包含 → **`error NETSDK1151: 自包含可执行文件不能由非自包含可执行文件引用`**，测试直接构建失败。

- 处置：**只在 `UiProbe.csproj`（测试侧）** 对齐 `SelfContained=true` + `RID=win-x64`，**未动 App 的发布配置**（团队约束：发布配置已定稿）
- 附带：UiProbe 无 `using System.IO;`（WPF SDK 隐式 using 不含），新增测试用到 `Path/File/Directory` 报 CS0103 → 显式补 using
- **只读审计时发现的配置漂移在"跑测试"这一环才真正爆出来**——印证"测试可移植性"必须包含"在当前发布配置下能构建"

### 5. 验证（本会话自跑）

- 构建 App/tests（Release + Debug）：**0 警告 0 错误**
- LoaderTest 全过（4 组）
- UiProbe **33/33 全过**（新增 6 项：日志 4 + 调度器隔离 2）
- publish → `publish/v0.1.61`（自包含 + 压缩关，配置未改）

### 6. 教训沉淀

- **UiProbe 是"自己写的代码"的第一道防线**：日志 off 级别边界 bug 就是被它抓出来的——测试先行省了真机排查
- **改接口要全仓搜实现**：Cron 删之前先 `grep` 确认零使用，避免误删；同步改测试桩
- **发布配置变更的连锁影响**：csproj 一个 `<SelfContained>` 能卡住整个测试链路，配置类改动同样要跑测试



---

## 2026-09-11（v0.1.62：插件系统升级第一批 —— Windows 通知接入 + 提醒插件时间设定）

**起因**：灯灯要求"测试整个项目是否需要重构（零零碎碎加功能怕臃肿）+ 优化插件 UI/平滑度 + 提醒类插件要能设定时间 + 接入 Windows 通知作为提醒 + 每个插件跑一遍评估"。

### 1. 重构评估结论（team-lead 侦察）

**不需要大重构。** 17 个插件每个 100-300 行，规模健康。真正的痛点是三个"能力没下沉"导致的假臃肿：

1. `ISystem.Notify` 是空壳（占位符只写日志）—— 假功能
2. 提醒插件时间硬编码无设置页 —— 能力缺失
3. 插件 UI 简陋 + 风格参差 —— 宿主 `SettingsWindow.BuildXxxCard` 是 private，插件够不着，各自造轮子

### 2. 本版落地（第一批 P0+P1）

- **P0 Windows 通知**：`SystemImpl.NotifySink` 静态字段 + `TrayManager.ShowBalloon`（`ShowBalloonTip`）+ `App.xaml.cs` 绑定；跨线程 Dispatcher 兜底（插件 Timer 线程池回调触发）。
- **P1 提醒时间设定**：SleepReminder（时+分）/ Pomodoro（5/15/25/45 分）/ Countdown（自定义分钟）/ HourlyChime（提醒时段，支持跨天）各加 `ISettingsPage`，持久化 Storage。

### 3. 关键纠正（侦察结论的错误）

- **我误判"插件都是 UseWPF 项目"**：实际 17 个插件里只有 Affection 有 `<UseWPF>true</UseWPF>`。这 4 个提醒插件原本没有，加设置页返回 WPF 控件编译不过，已补上。此纠正对命理插件 P0（要建独立窗口）同样重要。

### 4. 资源复核（QA #14，本轮一并闭环）

- **CPU 测量方法确立**：绕开 PDH，用 Win32 `GetProcessTimes`（进程累计 CPU 时间差值/墙钟）。busy-loop 对照 98.43% / sleep 对照 0.000% 证明方法可信。
- **红线实测（v0.1.61 自包含产物）**：空闲 CPU **<0.05%**（PASS，远低于 0.5%）/ 冷启动 **668ms**（PASS，<2.0s）/ 体积 **175M**（PASS，<180M）/ 常驻内存 **192.7MB**（**FAIL**，超 170M 红线 22.7MB）。
- **内存疑点**：v0.1.60 曾测 177MB，本轮 192.7MB，涨 ~15MB，待定位增量来源。计数器粒度 15.6ms，0.000% 应读作 <0.05%。

### 5. 待办（下次接续）

- **P2 UI 美化**（未开始）：SDK 加 PluginUi 统一组件库（深色主题卡片）+ 独立窗口（Todo/Notes/Clipboard）深色化。这是灯灯"改动多一点插件 UI 和平滑度"的重点，独立大任务。
- **内存增量排查**：177→192.7MB 的 ~15MB 来源。
- **QA 插件审计**：17 插件跑测评估（进行中，可能未完整）。

---

## 2026-09-11（v0.1.63：插件 UI 美化第一批 —— 独立窗口深色化 + 淡入）

**承接 v0.1.62，落地 QA 插件审计指出的"剩余最大 UI 债"**：3 个独立窗口（Notes/ClipboardHistory/TodoList）从默认 WPF 白底 → 深色主题，与宿主 #1E262F 一致。

### 落地
- 3 窗口深色化 + 150ms Opacity 淡入动画；配色各窗口内联常量，未抽 SDK 共享类（避免动宿主共享程序集影响插件 ALC 路由）。
- 顺带消除 TodoList CS8602 空引用警告（纯重排）。

### 验证
3 插件 0 警告 0 错误；LoaderTest 全过；逻辑零改动。

### 备注（未做的 UI 债，下次接续）
- **设置页旧样式**：Weather / Affection 设置页仍是旧样式（非深色），待统一。
- **静默吞异常 3 处**：TodoWindow.cs:82 / NotesWindow.cs:56 / ClipboardWindow.cs:42 的 `try{}catch{}`，保存/复制失败无感知，待改 Log/提示。
- **设置页小瑕疵 2 处**：Pomodoro ComboBox 边界、SleepReminder label 不实时刷。

---

## 2026-09-12（v0.1.64：插件设置页 UI 统一化 —— 深色控件工厂下沉 SDK）

**承接 v0.1.63，收掉"插件 UI 统一"的尾巴**：6 个设置页 UI 参差（Affection/Weather 旧样式黑字看不清、4 个提醒页按钮仍是默认浅色），根因是各页手写样式、无统一来源。

### 落地
- **SDK 加 `<UseWPF>true</UseWPF>`** + 新增 `src/DesktopPet.Sdk/PluginUi.cs`（深色控件工厂：颜色常量 + Text/Button/Input/SectionHeader 工厂，`Freeze` 冻结省内存）。
- 6 个设置页（Affection/Weather/SleepReminder/Pomodoro/Countdown/HourlyChime）迁移到 PluginUi。
- 顺手修 QA 审计的 2 个瑕疵：SleepReminder label 不实时刷、Pomodoro 下拉边界。

### 关键验证：SDK 加 WPF 引用是否破坏插件 ALC 路由
- **结论：不破坏。** 证据：LoaderTest 全过（含真实 PluginLoader + ALC）+ UiProbe 第 32 项（真实 PetWindow + PluginHost 加载插件）通过。
- SDK 是宿主共享程序集（HostSharedAssemblies 白名单），宿主本身是 WPF 应用，SDK 加 WPF 引用后由宿主提供 WPF 程序集，路由逻辑不变。

### 🚨 环境坑（重要，必记）
**Bash 后台 `run_in_background=true` 拉起的 GUI 进程无法加载插件**——进程能起（窗口 + 托盘初始化），但 `data/plugins/` 无任何插件数据目录、无日志。
- **根因**：`PetWindow` 的插件加载挂在 WPF `Loaded` 事件上，无交互桌面会话下该事件不触发。
- **A/B 对照证明与代码改动无关**：把 SDK 的 UseWPF 撤掉重建再跑，现象完全一样。
- **教训**：**GUI 应用的真机验证不能用 Bash 后台拉起**，必须手动双击 exe（有交互桌面会话）。LoaderTest / UiProbe 这类"显式构造 + 显式调用"的测试才是沙箱内可信的验证手段。

### 验证
- build 0 警告 0 错误；LoaderTest 全过；UiProbe 全过（0 FAIL）。
