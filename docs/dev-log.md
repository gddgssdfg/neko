# 开发日志（Dev Log）

按时间顺序记录 Neko 的开发过程：**做了什么、踩了什么坑、怎么修的、为什么这么决策**。
版本变更摘要见 [`CHANGELOG.md`](../CHANGELOG.md)；本文件侧重**过程、坑的复现细节、决策依据**——**踩坑与教训是最有价值的部分，一律保留**。

> 📌 **2026-09-19 精简说明**：早期（v0.1.0–v0.1.78）逐版本流水已**按主题归并压缩**（细节见 CHANGELOG）；**v0.1.79+ 的过程记录与全部「坑 / 教训 / 决策」原样保留**。历史项目审计见文末**附录**。

---

## 〇、版本大事记（时间线摘要）

| 版本 | 主题 |
|---|---|
| v0.1.0 | 立项（C#+WPF 决策翻转）+ 4 层骨架 + 示例插件（dogfooding 抓到 2 坑） |
| v0.1.1–0.1.2 | 致命显示 bug 定位 + 6 个连环 init bug + 菜单真环形挖洞 + libpng 清理 |
| v0.1.3–0.1.8 | 借鉴 BongoCat（键盘反应/穿透/缩放/透明度/热键）+ 设置窗口重做 + 图标 PNG 化 |
| v0.1.9–0.1.24 | 插件生态铺开（notes/weather/pomodoro/…/search 共 16 个） |
| v0.1.25–0.1.29 | 径向菜单大改版（分页 + 纯 WPF 挖洞）+ 键鼠映射窗 + 猫爪动画 |
| v0.1.30–0.1.54 | 双模态（摸鱼/工作）+ 气泡对话 + 首启引导 + 柔光皮肤 + 深浅主题/中英 + WiX 打包 + 本地 git |
| v0.1.59–0.1.61 | 性能优化 P1–P4 + 交互批次3 + **批次 A 可观测性/稳定性/文档诚实化** |
| v0.1.62–0.1.64 | Windows 通知接入 + 插件 UI 统一（SDK `PluginUi`） |
| v0.1.65–0.1.69 | **命理插件 com.lantern.bazi**（P0–P5：八字 + AI 咨询 + MBTI） |
| v0.1.70–0.1.77 | 径向菜单可配置 + 键盘窗 UI + 人格定制 + BongoCat 借鉴(P0/P1) + 皮肤 UGC + 自动更新 |
| v0.1.78–0.1.80 | MSI 排除开发数据 + 素材白名单 + 信任通道内核 + 权限细分 |
| v0.1.81 | hd 皮肤尺寸自适应（`recommendedScale`）+ 素材降采样省 ~53MB |
| v0.1.82 | **大修**：插件全丢的两 P0 + 设置页打磨 + AI 素材管线 v1→v3 |
| v0.1.83 | 插件日志通道 `ISystem.Log`（批次 B 裁决） |

---

## 一、立项奠基期（2026-09-01 ~ 09-03，v0.1.0–v0.1.54）

### 1.1 立项与 19 项决策

灯灯要一个 Windows 桌面宠物。中途有一次**决策翻转**：最初评估 Tauri 2（看中 WASM 沙箱），确认分发范围只是「自用+朋友」后沙箱的决定性理由消失 → 改用 **C# + WPF + .NET 8**。19 项决策定稿（详见 [`architecture.md`](architecture.md) 附录 A）。落位 **`G:\lantetn-li\neko`**（不占 C 盘）。

### 1.2 三条定死的约定（违反会返工）

1. 插件数据必须写 `data/plugins/<id>/`，**禁止写进自己插件目录**——保证插件目录可整体覆盖，更新不丢数据。
2. 禁用用 `config.json` 的 `DisablePlugins` 数组（键名 **PascalCase**——`ConfigManager` 未设命名策略），**不改名不移动文件夹**（改名会破坏插件自更新）。
3. 源码 `builtins/` 与运行时 `plugins/` 分离——内置插件产物与第三方运行时无区别（dogfooding 的物理体现）。

### 1.3 dogfooding 立刻抓到 2 个坑（也是 `plugin-development-guide.md` 的踩坑 8/9）

- **坑 1 类型分裂**：插件 ALC 内又加载一份 `DesktopPet.Core` → `IPetPlugin` 成两个类型。修：`PluginLoadContext` 用 **`HostSharedAssemblies` 白名单**把 Core/Sdk 路由回宿主侧。
- **坑 2 权限前缀不匹配**：清单写 `pet = ["animate"]`，查表却用 `pet.animate`。修：按段加前缀（`pet.`/`ui.`/`storage.`），`other` 段不加。
- **坑 3 发布形态加载失败（比 bug 更重要）**：宿主 `App.csproj` 没引用 `Sdk` → 输出目录缺 `DesktopPet.Sdk.dll` → 插件 ALC 白名单找不到 Sdk。**教训**：原 `LoaderTest` 直接测源码 bin（自带 Sdk.dll），**完美掩盖发布形态缺失** → 补「加载真实 App 输出目录」测试。

### 1.4 三个运行时坑（都是跑起来才暴露）

- **坑 4 径向菜单崩溃**：用 `Close()` 销毁 + 监听 `Closed` 置 null，鼠标离开即关，关闭中再 Show → `InvalidOperationException`。修：**单例复用 + `Hide()` 而非 `Close()`** + 延迟隐藏。
- **坑 5 宠物几乎不可见（最隐蔽）**：`SizeToContent=WidthAndHeight` 时 WPF 忽略显式 `Width/Height` 按内容测量，而 `DrawingVisualHost` 是裸 `FrameworkElement`，**没有 `MeasureOverride` → 默认测量 0×0** → 窗口量成 12.8px。修：移除 `SizeToContent` + 宿主补 `MeasureOverride` 自报尺寸 + `_renderer.Scale = config.Scale`。
- **坑 6 VS 启动项目配错**：`.sln` 里 Core 排第一、无 `.suo` 时 VS 默认选第一项 → 报「Core.dll 不是有效 Win32 应用」。修：设 `DesktopPet.App` 为启动项目。

### 1.5 🔴 致命显示 bug + 6 个连环 init bug（最关键的教训）

**排查路径**：进程活着/窗口 visible/尺寸 128 ✅ → 但 GDI 截屏看不到（WPF layered 窗口 GDI 抓不到）→ 改 `PrintWindow + PW_RENDERFULLCONTENT` **抓到但全黑** → 加 SANITY 红方块（变红，证明 OnRender 触发）→ 加 state/bitmap null 诊断 → **`diag.txt: state=NULL`** → 追到 `PetStateMachine.Transition` 的**早返回**。

```csharp
public PetState Current { get; private set; } = PetState.Idle;   // 初始 Idle
private void Transition(PetState next)
{
    if (Current == next) return;   // ← 致命：LoadPlugins → Play(Idle) → Transition(Idle) 早返回
    Current = next;                //    渲染器永远收不到首次 Play
    _ = _api.PlayAnimationAsync(next, ...);
}
```

**6 个连环 init bug 总览（"字段默认值 + 算术/比较"是连环 init bug 高发区）：**

| 字段 | 默认值 | 后果 | 修复 |
|---|---|---|---|
| `PetStateMachine.Current` | `Idle` | Play(Idle)→Transition(Idle) 早返回，渲染器收不到首次 Play | 去掉 `if (Current == next) return` |
| `PetWindow._loopTimer.Start()` | 缺失 | 动画/眼神/边缘/鼠标广播全失效 | 在 `LoadPlugins()` 调 `Start()` |
| `PetWindow._lastTick` | `DateTime.MinValue` | 首次 delta ≈6.4e10s → 累加溢出 int → 帧永远 0 | 显式 `= DateTime.Now` + clamp |
| `PetRenderer._moodIntensity` | `0` | Playback=0 → fps=0 → 帧早返回 | 显式 `= 1.0` |
| `FrameAnimator.step` | 无 clamp | `(int)` 溢出变 `Int32.MinValue` | `Math.Min((int)_accumulator, Frames.Length)` |
| `PetStateMachine._walkTimer/WalkDuration` | 缺失 | 编译错误 | 补定义 |

**核心教训**：
- **"程序能跑"≠"功能对"**：build 0 错误 + 测试通过 + 进程不死 ≠ 用户能看到效果。**关键功能改完必须真看像素**。
- **GDI 截屏抓不到 WPF layered window**：验证窗口内容用 `PrintWindow + PW_RENDERFULLCONTENT`，不要用 `Graphics.CopyFromScreen`。
- **引用类型默认 null**（可空检查保护）/ **数值类型默认 0**（必须显式初始化到业务合理值）/ **`DateTime` 默认 MinValue**（增量/速率逻辑全爆炸）/ **`(int)double` 超范围行为未定义**（必须 clamp）。**`Int32.MinValue % 4 == 0` 这种巧合 → 静默死循环，最难发现（无异常）**。

### 1.6 拖拽/落体物理与坐标系坑

- **拖拽不跟鼠标**：`PointToScreen()` 返回**物理像素**，`Left/Top` 是 **DIP**，125% 缩放下每次 Move 累积 0.25 倍偏差。修：`CompositionTarget.TransformFromDevice` 转回 DIP。
- **点击它会消失**：`StartFall` 三 bug 叠加（每帧位移 vs 像素/秒² 差 60 倍、`Math.Min(v.Y,0)` 语义反、无速度上限）。修：单位统一 `*60` + 限速 `MaxThrowSpeed=900`。
- **🔴 高频坑**：凡「Win32 API 坐标」（`GetCursorPos`/`GetWindowRect`/`MONITORINFO`）与「WPF 坐标」（`Left/Top/Width/Height`）比较，**必须经 `TransformFromDevice` 转 DIP**（v0.1.1 踩过，v0.1.36 又在 `RadialMenu` 漏了 → 点翻页胶囊菜单直接关）。

### 1.7 其他奠基期教训

- **`Thread.Sleep` 在 STA 阻塞消息循环** → 动画停摆；测试里等动画用 `DispatcherFrame` + `DispatcherTimer`（让消息循环跑）。
- **P/Invoke 改方法名必带 `EntryPoint`**：`GetCursorPosWin32` → `EntryPointNotFoundException`（user32 导出名是 `GetCursorPos`）。
- **`System.Drawing.Point` 不能直接喂 `System.Windows.Rect.Contains`**，需转 `System.Windows.Point`。
- **`Path` 类名陷阱**：`using System.IO` 在场时 `new Path` 解析为 `System.IO.Path`（抽象类）→ CS0712；WPF 形状必须写 `System.Windows.Shapes.Path`。
- **`Brushes.Transparent` 等系统静态刷全是冻结的**，对冻结刷调 `BeginAnimation` 直接抛「对象已密封/冻结」→ 动画对象必须用 `new SolidColorBrush(...)`。（v0.1.35 真机 11 分钟后点击鼠标才崩；**UiProbe 只测了键盘键路径，鼠标键路径没覆盖** → 教训：**新动画路径都要同步加 probe 断言**。）
- **菜单大改教训（v0.1.25）**：v0.1.9「菜单挪到猫旁边」是方向错误——16 插件 22 槽位按 360°/n 均分半径 64，角距 16.4° 弦长 18px < 方块 42px 全叠一起。**修 UI 先问「用户心里的正确形态是什么」，别只治表面症状**（用户要的一直是「环形围绕猫」，遮挡该用挖洞+加大半径解决）。
- **纯 WPF 挖洞**：Window 与 Canvas 的 `Background` 设 **null**（不是 `Brushes.Transparent`——Transparent 参与命中测试，null 才不拦点击）。
- **分页而非截断**：22 槽位砍到 8 会静默丢 14 个功能 → 按 Order 分页（8/8/6）。
- **WPF 位移动画的三个反直觉行为（v0.1.39）**：① `BeginAnimation` 默认 `FillBehavior.HoldEnd`，动画「还挂」在属性上 → 结束后赋本地值无效（猫粘住拖不动），须在 `Completed` 里先撤动画再赋本地值；② 动画进行时 `BeginAnimation(prop, null)` 一撤值立刻回落成 base → 视觉跳回，须**先读当前值、后撤、再写回**；③ 拖拽/落体/边缘吸附/平滑位移**四条链路都会改 Left/Top，漏哪条哪条浮层就飞**，统一收口 `SyncFollowers()`。
- **双模态「谁是谁的锚」（v0.1.39）**：`work` 模式改为「**猫是锚**，映射窗由猫派生」（`SyncMirrorToCat`），而非 v0.1.30 的「猫贴映射窗」——后者切模式时猫会瞬移。
- **交互正确性不能只依赖事件链路**：连打/热键路径偶发丢 UP → 加 `_litKeys` + 200ms 轮询兜底强制还原。
- **气泡对话（v0.1.40）**：`PetRenderer.Say` 从 v0.1.0 就有接口，**但 Draw 里压根没画**——所有插件 `Say` 一直「有声无画」。补 `BubbleWindow`（像素云朵=方块密铺、`IsHitTestVisible=false` 穿透、跟随猫），**一个渲染层统一解决，零插件改动**。像素断言教训：**放弃「采固定坐标卡死阈值」，改「扫主体区域找任意合规点」**（WPF 窗口到 RTB 有 sRGB/premultiplied 微差，卡死 RGB 值会假失败）。
- **Alt+Tab 隐藏**：加 `WS_EX_TOOLWINDOW`，必须在 `OnSourceInitialized`（ctor 拿不到 hwnd）。
- **首启引导（v0.1.41）**：config 缺字段时弹 `FirstRunWindow`（App 内引导，不依赖安装器工具链）；用户点退出 = `Application.Shutdown()`。
- **文件命名陷阱**：缩略图最初叫 `preview.png` → 被 csproj `<None Remove="..\..\neko-assets\**\*preview*" />`（早期排除临时预览图的规则）**一并排除、打不进输出目录**。**教训：往 `neko-assets` 放新素材前先看 csproj 的 `None Remove` 规则**（`.bak-*` 同理）。
- **WPF 窗口 `Topmost` 规则**：被 Topmost 窗口拥有的窗口自身也是 Topmost（桌宠是 Topmost → 设置窗永远压所有窗口）。最终方案：**设置窗不设 `Owner`**，桌宠 `Topmost` 不动（用 `WS_EX_TOPMOST` 位验证，不靠肉眼）。
- **编译小坑**：方法组 `SetKeyboardMirror(bool)` 不能直接转 `Action?`，须 lambda 包裹。
- **WiX 4 打包**（v0.1.44）：`StandardDirectory/Component/ComponentGroup/Feature` **必须放在 `<Package>` 元素内部**，放 Fragment 里会被链接器**静默丢弃** → 生成 28KB「空壳 MSI」。用最小 wxs 二分实验定位（min1 无内容成功 → min5 Package 内嵌成功 56MB）。**MSI 是否有效用 `wix msi decompile` 验证**（空壳只剩 Package 骨架）。沙箱网络：GitHub 被 TLS 拦，NuGet 通。
- **AI 生图序列条走不通（v0.1.53）**：图像模型把「4 帧动画」理解成「4 张同主题图」，每格重新构形 → 帧间不连贯（实测相邻帧差异 9~42%）。**这是生图 AI 硬伤，不是 prompt 问题**。→ 推荐**图生视频 + 抽帧**（三条硬约束：镜头固定 / 角色不位移缩放 / 背景纯绿 #00FF00）。**抠白底必须 flood-fill**（只抠与边缘连通的背景，全局容差会抠掉奶白肚皮）。
- **功能开关优先于删代码**（柔光屏蔽用注释+白名单）：要恢复时 2 分钟搞定，不用翻 git 历史。

---

## 二、性能与交互批次（2026-09-06 ~ 09-11，v0.1.59–v0.1.61）

### 2.1 v0.1.59 性能优化 P1–P4 + 交互批次3

- **P1–P4 是「行为等价」优化**，改前明确「不能改变任何可见行为」（LED 轮询早返回 / 剪贴板降频 800→1500ms / 闲置系统调用按需化缓存 / 渲染时钟统一到 `_clockMs`）。
- **⑧ 爱心粒子 QA 失败复盘（最值钱的一条）**：`⑧：爱心寿命 800ms 后应全部消散，实际仍剩 5 颗`。**排查先拿证据**：① 加 `_tickCount` 诊断把「timer 没跑」与「逻辑错」分开；② 对照实验 A：单 timer 等待 → `FAIL 剩 3 颗（tick 0→44）`（timer 跑了 44 次，排除「没跑」）；③ **44 tick × 16ms = 704ms < 800ms 寿命** → 真因：`_clockMs` 每 tick 固定 +16ms，但 **dispatcher 在嵌套消息泵（`Dispatcher.PushFrame`）下会合并/延迟 timer tick**，仿真时间被压缩；④ 实验 B 用真实 elapsed 修复 → PASS。
  - **教训**：**定时器驱动的仿真，别用「每 tick 固定步长」当时间源**——用 `Environment.TickCount64` 真实单调差做 `dt`。
  - **"没生效"先分类再改**：是「回调没被调用」还是「调用了但条件不成立」？加计数/日志把两类分开。
  - **视觉改动必须进 UiProbe**（硬要求）：v0.1.32 漏鼠标键路径导致真机 11 分钟后才崩 → 本版新增多项断言。
- **⑨ 心情驱动动画**：把好感度读取**提取**成 `AffectionReader` 静态类（可复用逻辑提取而非复制）。
- **⑧ 爱心粒子必须走独立浮层窗口**：`DrawingVisualHost.MeasureOverride` 把绘制区钉死在 32×Scale，粒子飞出猫身会被裁切。
- **⑦ 抛物线会与吸附逻辑抢方向盘**：`UpdateEdgeSnap` 只在 `_dragging||_moveAnimating` 早返回，飞行中两标志都假 → 必须加 `_fallTimer != null`。

### 2.2 v0.1.60 两起「无留痕回退」+ 压缩取舍

- **图标被静默换回且无记录**：逐版本比对 `tray.ico` 体积（v0.1.53/54=1330B 像素版 → v0.1.55/56=豆包 AI 版 → **v0.1.57 起退回 1330B 无记录**）。恢复自 `publish/v0.1.56/`。**备份要验证内容而非只看名字**（`.bak-v0.1.54` 实际内容也是像素版，无法用于回滚）。
- **发布形态也被无留痕改**：`publish/v0.1.59/Neko.exe` 仅 1.3MB（v0.1.58 是 170.9MB）→ `<SelfContained>` 被从 true 改成 false。**项目当时无 git → 无法追溯责任人**（这是两次问题都查不出人的根因）。
- **压缩取舍对照实验**：压缩 on = 80MB/稳态 276MB；off = 174MB/177MB。**原因**：压缩单文件启动需解压运行时到 `%TEMP%`，映射常驻内存。**决策**：桌宠常驻 → **内存优先，关压缩**。
- **流程教训**：① **无留痕回退是最大的流程风险** → 引入本地 git（见下）；② **发布形态、磁盘图标这类「非代码配置」同样要进 CHANGELOG**；③ **对照实验是决策的正确姿势**（不靠猜）。
- **启用本地 git（灯灯拍板）**：`git init` + `.gitignore`（**先 dry-run 验证再提交**）排除 `publish/`(4.1G)、`bin/`+`obj/`、`installer/*.msi`(375M)、`data/`、`.env`（**含 API Key，必须排除**）、素材中间产物；**只跟踪 257 文件 / 仓库 1.1MB**。**误判修正**：`.gitignore` 初版写全局 `*.dll`/`*.exe` 会误伤，改只排 `bin/`/`obj/` 更精确。**实战演练**：模拟把 `tray.ico` 换回 → `git status` 立刻报 `M`、`git diff --stat` 显示 `Bin 82965 -> 1330 bytes` → 一眼可见。
  - **约定**：提交格式 `<版本号>: <主题>`；**每次改动后先跑 `git status --short` + `git diff --stat`**；素材类改动靠 `Bin N -> M bytes` 发现。

### 2.3 v0.1.61 批次 A（可观测性 + 稳定性 + 文档诚实化）

- **轻量文件日志（`Core/Log.cs`）**：`data/logs/neko-YYYYMMDD.log`，按天 + 5MB 滚动、保留 7 天；线程安全（`lock`）；**失败静默**（IO 全 try/catch，日志设施绝不反过来搞崩宿主）；默认 Warning+；`AppConfig.LogLevel`；`Log.Error` 兼容写 `errors.log`。
  - **踩坑（自己的 bug，被 UiProbe 抓出）**：级别过滤初版 `if (level < _minLevel) return;`，`Off=0` 时 `Error(4) < 0` 为假 → **off 级别仍写盘**。修：`_minLevel == Off` 时无条件 return。**枚举当阈值用要显式处理边界值，"数值最小"不等于"关闭"**。
- **插件后台异常隔离 + 熔断（修架构铁律3）**：`SchedulerImpl` 三处 Timer 回调裸调无 try/catch（`System.Threading.Timer` 回调跑线程池，未处理异常**直接终止进程**；全局兜底只挂 `DispatcherUnhandledException` **仅 UI 线程**）。修：统一 `SafeInvoke` 包异常 + **熔断**（`TimerHandle` 记连续失败，达 5 次自动停表）+ `App.xaml.cs` 补 `TaskScheduler.UnobservedTaskException` + `AppDomain.UnhandledException`。
- **文档诚实化**：权限模型「改文档不改代码」（真实是**加载时校验**，运行时无逐次守卫——**诚实优于对齐一份没实现的铁律**）；删死接口 `IScheduler.Cron`（原实现忽略表达式退化为每分钟，属半成品，且全仓零使用）。
- **环境坑 NETSDK1151**：App 自 v0.1.60 起 `SelfContained=true`+`RID=win-x64`，而 UiProbe 非自包含 → **自包含可执行文件不能由非自包含可执行文件引用** → 只在 `UiProbe.csproj`（测试侧）对齐，**未动 App 发布配置**。**教训：发布配置变更的连锁影响要跑测试才暴露**。
- **环境坑（重要，必记）**：**Bash 后台 `run_in_background=true` 拉起的 GUI 进程无法加载插件**——`PetWindow` 插件加载挂在 WPF `Loaded`，无交互桌面会话下不触发。**GUI 应用真机验证不能用 Bash 后台拉起**，必须双击 exe。

---

## 三、插件系统升级 / 命理插件（2026-09-11 ~ 09-17，v0.1.62–0.1.77）

### 3.1 v0.1.62–0.1.64 插件 UI 与能力下沉

- **重构评估结论：不需要大重构**（17 插件每个 100–300 行，规模健康）。真痛点是三个「能力没下沉」导致的假臃肿：`ISystem.Notify` 是空壳、提醒插件时间硬编码、插件各自造 UI 轮子。
- **我误判「插件都是 UseWPF 项目」**：实际只有 Affection 有 → 4 个提醒插件加设置页编译不过，已补 `<UseWPF>true</UseWPF>`。
- **SDK 加 `<UseWPF>` + 新增 `PluginUi.cs`（深色控件工厂，`Freeze` 冻结省内存）**；验证**不破坏插件 ALC 路由**（SDK 是共享程序集白名单，宿主本身是 WPF 应用，由宿主提供 WPF 程序集）。

### 3.2 v0.1.65–0.1.69 命理插件（lunar + AI）

- **第三方依赖 ALC 解析（关键）**：`AssemblyDependencyResolver(entryDll)` **只读插件目录里的 `.deps.json`**（靠它把 `lunar` 映射到 `lunar.dll`）→ **只拷 dll 不拷 deps.json 会解析失败**。修：csproj `<GenerateDependencyFile>true</GenerateDependencyFile>` + `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>`，`DeployToPlugins` 拷 `$(OutDir)*.dll` + `.deps.json` + `plugin.toml`。**决定性验证**：LoaderTest 新增「测试5」，**刻意不引用 Bazi/lunar 项目**（否则 lunar 进默认上下文，ALC 失败也能「蒙混过关」）。
- **lunar API 语义坑（实测纠正）**：`Yun.StartYear/StartMonth/StartDay` **不是公历日期，是「起运岁数差」**（起运公历日期在 `Yun.StartSolar`）——第一版误用当公历年，自检暴露 `8-00-20` 才纠正。**「自检要带真实用例」的价值**。
- **`EightChar.*WuXing` 是「干+支」两字**（如「土土」）不是单字五行 → 须 `ElementOfGan(DayGan)` 取单字。`Lunar.DayYi/DayJi/...` 是 `List<string>` 而非 `string[]`（CS0029）。
- **DPAPI 需 NuGet 包**：`System.Security.Cryptography.ProtectedData` 在 .NET 8 不在 BCL，须显式引用；实测对 net8.0-windows **不产出需随插件部署的 dll**。API Key 加密语义：内存明文、落盘密文、解密失败返回 null、加密失败降级明文。
- **内存实测方法**（判 `maxMemoryMB`）：P/Invoke `GetProcessMemoryInfo` 读 `WorkingSetSize`；**采样前双重 `GC.Collect()` + `WaitForPendingFinalizers()`**，采 3 次取最小值；用**差值**而非绝对占用。实测插件增量 ~15–17MB > 16MB → **`maxMemoryMB` 保留 32（不回调）**，诚实保留安全边际，不为交差硬编数字。

### 3.3 v0.1.70–0.1.77 打磨与 UGC

- **v0.1.70 槽位修复**：Order 16 冲突（MusicControl 与 Countdown 撞号）→ 音乐控制挪到 [2,3,4]；两个「今日运势」重名 → Fortune 改「抽个签」。
- **v0.1.71 径向菜单可配置**（设计见 [`architecture.md`](architecture.md) §8）：宿主侧覆盖层，不改 Core API。
- **v0.1.72 键盘窗 UI + 踩坑（重要）**：`PawRaisedTop` 一度从 `const` 改实例字段 → **UiProbe 回归 NRE**（layout pass 触发 null 访问）。**WPF 里能保持 const 就别改实例字段**。定位手法：`git stash` 整体 revert → 逐项回退二分。
- **v0.1.73 人格定制**：题库 24→93 题（**自己中文重写，不照抄**规避版权）；默认关；入口显隐联动（QA 断言发现遗漏）。
- **v0.1.74 BongoCat 借鉴 P0**：**修了一个真 bug**——`ConfigManager.Load()` 解析失败返回默认值 → **后续任何 Save 都会用默认值永久覆盖用户配置**。改为把损坏文件改名保留 `.rejected-<时间戳>`。配置原子写（tmp → `File.Replace`）；未知字段透传（`[JsonExtensionData]`）。产出 `PRIVACY.md` / `SECURITY.md` / `tools/check_version.py` / `tools/check_i18n.py`。（BongoCat 是 AGPL-3.0，**只借设计不抄码**。）
- **v0.1.75 皮肤目录可扩展（解锁 UGC）**：`SkinRegistry.Scan` 扫 `neko-assets/*/skin.json` 自动发现 → **用户自制皮肤零改代码**。QA 独立验证：9 状态元数据与旧硬编码**零差异**；15 张 png 与迁移前**字节级 hash 逐个相等**（纯 move）。
- **v0.1.76 修 UGC 作者陷阱（QA 挖出）**：`anim.json` 的 fps 会**静默覆盖** `skin.json`（仅渲染层）→ **用户改 skin.json 的 fps 完全无效**，直接废掉 UGC 目标。修：`skin.json` 成为 fps 唯一权威（移除 `ReadAnimFpsOverrides` + 删 `anim.json` + 断言锁死）。
- **v0.1.77 自动更新·阶段1（Gitee）**：只「检查→提示→打开下载页」，不做静默安装（需代码签名，否则被 SmartScreen 拦）。**规格 bug（工程师发现）**：Gitee releases 响应**没有 `html_url` 字段**（照 GitHub schema 写的）→ 改为按仓库坐标自行拼发布页 URL。Gitee 仓库踩坑：转公开需 `PATCH`（**必须带 `name` 字段**）+ **完成 2FA/绑定第三方**；仓库当前仍私有 → 匿名 API 读不到 → 自动更新端到端待仓库公开。沙箱实测 Gitee 比 GitHub 稳。
- **v0.1.78 仓库策略修正 + sln 遗漏（真 bug）**：`plugins/*.dll` **非确定性构建**（源码不变、重建内容微变，反复 M）→ `.gitignore` 加 `/plugins/com.lantern.*/`（**clone 后需先构建才有插件**）。**⚠️ sln 遗漏**：`DesktopPet.Builtin.Bazi` 创建时**未加入 `.sln`** → `build sln` 不构建/部署它，clone 后命理插件会**静默缺失**。修：`dotnet sln add`。**新建插件项目后必须 `dotnet sln add`**。

---

## 四、v0.1.81：hd 皮肤尺寸自适应 + 素材降采样省 53MB

- **根因**：`SpriteAtlas.FrameW/FrameH = 32` 是**编译期常量**，被窗口尺寸/绘制 Rect/眼球层/影子/贴底/落体边界等一大票几何计算引用；`skin.json` 的 `frameWidth/Height` **只用来切帧，不参与尺寸计算**。→ 任何皮肤源图都被缩进 `32 × Scale` 逻辑框。附带内存问题：hd 位图常驻 677×668×4帧×9状态×4B ≈ **62MiB** → 进程推到 245MB。
- **决策：加「推荐缩放」旁路，而不是改 `FrameW` 常量**（改常量回归面太大）。skin.json 新增可选 `recommendedScale`（1.0–8.0，缺省=不声明），宿主在**用户切换皮肤时**套用（复用 `SetScale`）。**只加在 hd 上**，pixel/soft 不声明 → 行为与旧版一致。
- **素材侧**：`ai_sheet_to_skin.py` 加 `--max-frame`（默认 256，LANCZOS）/ `--recommended-scale`。⚠️ 下采样时**受限边要精确置为 max-frame**（`677*(256/677)=255.999…` 截成 255 会与断言期望 256 冲突）。hd 677×668 → 256×252，位图 62MiB → ~8.9MiB，**省 ~53MiB**。
- **设置页一致性坑**：`BuildStepperCard` 显示值**构建时固定**；皮肤回调 `SetSkin` 可能改 `_config.Scale` → 「缩放倍率」卡片显示值与真实值不一致。修：`BuildStepperCard` 返回 `(Border, Action refresh)`，`refresh` 从 `() => _config.Scale` **重读**（既刷文案，也把步进器内部基准拉回，否则之后按 ± 从旧值起算）。

---

## 五、v0.1.82 大修：插件全丢的两 P0 + 设置页打磨 + AI 素材管线 v1→v3

### 5.1 起点：灯灯问「插件那边现在为啥都没了」

排查很久——**卡住的不是技术难点，是「看不见」**。

- **先排除假象**（都验证过、全是好的）：`plugins/` 19 目录、`builtins/` 18 工程、输出目录 19 目录**每个都含 DLL+plugin.toml**；`data/config.json` 的 `DisabledPlugins` 空、`RadialMenu.MaxVisible=0`；`git status` 无删除；LoaderTest 全过。
- **转折点：把「看不见」变成「看得见」**：① 读源码发现径向菜单 `窗口高==宽 ⇔ 单页 ⇔ 总槽位 ≤8`，实测 250×250 正方形 → 槽位 ≤8；`all.Count==0` 时不画中心洞 → 实测实心盘面 → **槽位 = 0**；② **给 `PluginHost` 补错误日志（`ReportErrors`）**——此前 `_errors` 只收集不落盘 → **一跑就打印真因**：`Dependency resolution failed ... Affection.dll with error code -2147450750`。
- **两个 P0**：
  1. **`SelfContained` 泄漏到构建期**：对照实验一次定案——同一份代码 `-p:SelfContained=false` 重建 → 12 插件全加载成功；不改 → 全失败。→ 换 `PublishSelfContained`。**连带**：`UiProbe.csproj` 的 `SelfContained=true` 必须同步去掉（否则 NETSDK1150）——这个连带**改了源码触发重建才暴露**（之前「解决方案 0/0」有缓存成分）。
  2. **在 `Loaded` 回调里再挂 `Loaded`**：`LoadDeferred()` 永不执行（WPF 不在事件触发中调用新挂处理器、且 `Loaded` 不重放）。发布版同样中招（实跑 `publish/v0.1.73`：`data/plugins/` 只有 11 个，缺 6 个延迟插件）。
- **🔴 环境坑 DPI 125%**：本机物理 2560×1440 / 逻辑 2048×1152。**非 DPI-aware 的 Python 进程**：`GetWindowRect/GetSystemMetrics` 返回逻辑坐标，而 Pillow `ImageGrab` 抓物理像素 → 差 1.25 倍，我前几轮把菜单判读错了。**解法**：进程启动先 `shcore.SetProcessDpiAwareness(2)`。工具 `tools/probe_menu_count.py`。

### 5.2 设置页一轮打磨

| 反馈 | 根因 | 修法 |
|---|---|---|
| 选项按钮被拉成方块、下面留白 | `BuildCard` 的 `Grid` 子元素默认 `Stretch`，行高 = max(左文字,右控件) | `control.VerticalAlignment = Center`（通用修复）+ `BuildWideCard` |
| 皮肤标签挤成一团 | 8 套皮肤用 WrapPanel | `BuildSkinPicker`：一行一个 + 48px 缩略图 |
| 设置窗永远压所有窗口 | WPF 规则：被 Topmost 窗口拥有的窗口自身也是 Topmost | 见 §1.7 最终方案（设置窗不设 Owner） |

（命名坑、高 DPI `UseLayoutRounding` 见 §1.7。）

### 5.3 AI 素材管线 v1→v3（详见 §1.7 的「走不通」结论与 flood-fill）

- **眼球层不能跨素材差分**：从 `*_base_3x3.png` 与 `*_closed_3x3.png` 差分抠瞳孔，但 idle 帧来自**单独的 still 图**；实测两张画**不是同一张**（橘猫 idle 差 42.7 / sit 56.0）→ 瞳孔错位成漂浮色斑。**结论：眼球层必须来自「同一张画的闭眼版」**。
- **调色板必须全皮肤共享**：逐帧各自 `quantize` → 同一块毛色帧间跳变（**动画发闪**）；实测逐帧量化 728 色 → 拼长图统一定调色板后 22~23 色。

### 5.4 静态态 `fps = 0` + 一次命中的 P0

- 落实「猫大部分时间不动」：待机/坐/睡用静态图 + `fps=0`（`FrameAnimator.Tick` 里 `if (fps <= 0) return false` 正好是「静止」语义）。
- **新写的 UiProbe 断言第一次运行就抓到 P0**：`SkinRegistry.TryLoad` 里有 `if (fps <= 0) return null;`（"fps 非法 → 整张皮肤损坏"）→ **4 套新皮肤全被跳过**，设置页根本不显示，日志只有一行 `[皮肤] 跳过非法皮肤目录`。修：只拦 `fps < 0 || NaN`。
  - **症状是「设置页少几项」而非崩溃，肉眼看极易漏**；断言把「皮肤是否真的被注册」变成可判定命题。**校验规则与引擎语义不一致是隐蔽 bug 的常见来源**。

### 5.5 UiProbe 健壮性 + 沙箱坑

- **不退出**：打印完结果后进程一直活着（QA 曾观察 19 分钟不退出），测试创建过真实 WPF 窗口留下前台线程 → 加 `Console.Out.Flush()` + `Environment.Exit` 强制退出。
- **stdout 块缓冲**：重定向到文件时输出被缓冲，进程不退出就看不到结尾 → `Console.SetOut(new StreamWriter(...) { AutoFlush = true })`。
- **沙箱进程视图不一致（未解决）**：卡住的 UiProbe 用 `taskkill`/`Stop-Process` 报成功但进程仍在，且**锁着输出目录的 Neko.dll**。**绕法**：`-p:BaseOutputPath=bin_alt/` 换输出目录构建。
- **子代理 shell stdout 抓不到，但重定向到项目内文件可用**：Bash 工具直接输出恒为空（连 `echo` 都空），`>`/`>>` 重定向到 `/tmp` 也不落盘；但重定向到**项目工作目录内的相对路径**会真实生成，再用 Read 读取。所有 dotnet 命令统一 `... > x.log 2>&1; echo EXIT=$? >> x.log` 再读文件。

### 5.6 置顶策略返工 + 流畅度打磨

- **置顶返工**：最初「打开设置期间临时把桌宠 `Topmost=false`」解决了设置页压窗口，**但代价是桌宠会被别的窗口盖住**（与「桌宠要置顶」冲突）→ 改成**设置窗不设 `Owner`，桌宠 `Topmost` 不动**。验证靠读 `WS_EX_TOPMOST`(0x08) 位，不靠肉眼。同时补深色滚动条（用功能测试兜底：发滚轮事件比对内容区截图前后差异）。
- **流畅度**：先看渲染链路——结论渲染侧本来就好的（`PetRenderer.Tick` 只在帧真推进/眨眼相位变化时重绘）。**但查出一个我自己引入的 P0**：`SetEyeOffset()` 只存值不触发重绘，而 `fps=0` 静止皮肤下动画永不推进 → **眼神追踪算了但永远不画**（**不报错、日志干净**）。
  - **验证手法（最有价值的经验）**：构造**能证伪的最小场景**——把 pixel 皮肤待机临时改成 `fps=0`，制造「静止+有眼球层」；鼠标从屏幕左移到右，比对渲染帧差异：**1135 像素变化(4.43%)，变化区域中心在窗口 (54%,32%) = 眼睛位置** ✓（只在输出目录改 skin.json，仓库零污染，验完还原）。
  - **闲置 CPU 实测**（`GetProcessTimes` 绕开沙箱不可用的性能计数器）：slack 30s **0.000s(0.00%)**、work 0.016s(0.05%)。
  - **同值短路**：`SyncFollowers` 每 tick 给浮层赋 `Left/Top`，WPF `Window.Left/Top` **不做同值判断**（赋值即 `SetWindowPos`）→ 加 `MoveWindowIfNeeded()`。**实测收益在噪声内，如实记录，不当作性能提升宣传**。

### 5.7 插件打磨（18 插件审计 + `PluginSmoke`）

- **最值钱的 P0-1**：插件定时器回调跑**线程池**，而 `PetApiImpl.Say` 无封送直连有线程亲和性的气泡窗口 → 抛异常被吞 → **"到点系统通知弹了、猫的气泡不弹"**（4 个插件踩这条）。
  - **TDD 案例**：先用 `Dispatcher.Invoke`（阻塞）封送，断言「从线程池调 Say 应弹气泡」→ **直接失败「Pet.Say 在 3 秒内未返回——封送疑似死锁」**（测试主线程在跑断言、没泵消息）。失败点出真设计问题：`Say/SetMood/PlayAnimation` 都是**发后不理**，用阻塞封送会让线程池傻等、**多插件同时定时会线程池饥饿**。改 `BeginInvoke` 后通过。
  - **另一个设计决定**：Dispatcher 由 `PetWindow` **显式注入**，而非查 `Application.Current`（后者在 UiProbe/LoaderTest 无 App 场景是 null → 封送**静默退化成直接调用** → 跨线程 bug 在测试里根本测不出）。
- **P0-3（Bazi ALC 不回收）按审计假设做实验，结果证伪**：把 `static readonly HttpClient` 改成可释放单例 + OnUnload Dispose，复测 **ALC 仍是 N** → 不是 HttpClient。**如实写进 CHANGELOG，不含糊**（详见 §六）。

### 5.8 新增工具（可复用）

`tools/capture_pet.py`（PrintWindow 抓 WPF 分层窗口）/ `probe_menu_count.py`（DPI-aware 菜单槽位判读）/ `analyze_assets.py`（素材体检）/ `find_grid.py`（孤岛判定 + 格线穿越）/ `analyze_ai_sheet.py`（真假像素画）/ `build_skin_v2.py`（静态图+序列条→皮肤）/ `gen_skin_previews.py`（`thumb.png`）/ `Neko.exe --settings[=栏目]`（开设置页并定位栏目，好截图）。

---

## 六、2026-09-18：插件深度打磨（ALC 真凶二分 + 标准文档）

### 6.1 一个「假设被证伪两次才找到真凶」的排查过程

- **起点**：Bazi 插件卸载后 ALC 收不掉，每次泄漏 ~9MB。
- **假设 A**（`static readonly HttpClient` 钉类型）→ **实验证伪**（改可释放单例后 ALC 仍 N）。
- **假设 B**（`lunar.dll` 静态缓存）→ **实验证伪**（`--selftest lunar` = Y）。
- **真正定位 = 二分 6 项 OnLoad 自检**（`PluginSmoke --selftest <清单>`）：`none/lunar/fortune/api/mbti/prompt` 全 Y，**只有 `client` 是 N**。
- **再往机理缩（`--probe`）**：

  | 做法 | ALC |
  |---|---|
  | 只 `GetType()`+`Activator.CreateInstance()`（不序列化） | **Y** → 排除「反射建对象」本身 |
  | 新建 `JsonSerializerOptions` | **N** |
  | 换 `DefaultJsonTypeInfoResolver` | **N** |
  | `Serialize(List<string>)`（BCL 泛型对照） | **Y** → 证明 BCL 类型安全 |
  | **`Utf8JsonWriter` 手写** | **Y** ✅ |

  **「换 options / 换 resolver 都无效」是关键信息** → 钉根在 STJ **反射路径的进程级共享状态**，任何「options 级」改法都够不着，只有绕开反射才行。
- **真凶**：`DeepSeekClient.BuildRequestBody()` 用 `JsonSerializer.Serialize(匿名类型)` 序列化**插件程序集内定义的类型**。
- **教训**：**「改对了但没解决问题」必须如实记录**（第一次修完写「已证伪，保留该改动」而不是含糊写「已修复」——否则没人继续挖，真凶会一直留着）。**「假设被证伪」本身是有价值的结论**，它把搜索空间缩小了一层。

### 6.2 测试覆盖的路径 ≠ 真实使用的路径（第二课）

工程师修完两个调用点后自测全绿（`--only bazi` → Y），但 **QA 用单变量探针证伪了「修复彻底」**：

| 探针 case | ALC |
|---|---|
| `buildbody` / `chatappend` / `sdkload`（已修） | Y |
| **`profileload` / `profilesave`（`BaziProfile`）** | **N** ← 漏修 |
| **`todolistsave`（`List<TodoItem>`）** | **N** ← 漏修 |

**为什么冒烟没抓到**：`PluginSmoke --only bazi` 只跑 OnLoad，而 OnLoad 读不到档案 → `BaziProfile.Load` 提前 return → **压根没碰到 STJ**。杀伤力还大：`CheckBroadcast` 由 `Every(60s)` 驱动、**每 tick 都调 `BaziProfile.Load`** ⇒ 用户一旦存过档案，60 秒内 ALC 必被重新钉住。
→ **可复用的检查法**：一个修复「改在哪个函数」不重要，重要的是**「哪些真实使用路径会走到它」**。只跑初始化路径的冒烟，天然测不到「运行期才会建立的状态」。

### 6.3 探针设计：机制探针 vs 真实路径探针

QA 最初的 `todolistsave` 是**机制探针**（直接反射序列化插件类型）——它**永远返回 N**，无论插件怎么改，**不能当回归判据**。修正：**走插件真实 Save/Load 路径**的 case（→Y）+ **保留一个机制对照 case**（→N）。**一 N 一 Y 才构成有效回归门**：N 证明探针有敏感度（不是恒 Y 假绿），Y 证明修复真生效。

### 6.4 取证不要依赖被验证方的自述

QA 复验没直接相信工程师说「我改好了」，而是：核对被测 DLL 的 **mtime 晚于源码 mtime** + 用 `grep -a` 在**二进制**里搜新生成的符号名（`BaziJsonContext`/`TodoJsonContext`）→ 确认「测的确实是修复后的代码」。**成本极低、价值极高**——避免「测了旧二进制却以为验证通过」（本项目此前就踩过 `sln` 不含 `tests/` 导致跑到陈旧断言的坑）。

### 6.5 「设计好了但从未接线」是一类隐蔽 bug

`PluginTrustSet`（内置信任清单）机制**完整实现**（格式/摘要校验/兜底/测试全有），**但生产代码 `PluginHost` 从来只是 `new PluginLoader(pluginsRoot)`——没传信任集** ⇒ 运行时信任集恒为 `Empty` ⇒ 任何声明 `file`/`process` 的插件都被一律拒载。目前没暴露只因为当前没有内置插件声明高危权限（已细分成 `file.own`/`media`）。
**教训**：**「机制存在」≠「机制生效」**。测试覆盖了机制，但**没有任何测试断言「生产代码真的把清单传进去了」**——「单测绿 + 生产死」的经典缺口（v0.1.82 已接线）。

### 6.6 标准开发文档（本轮的另一个主要交付）

产出 `docs/plugin-development-guide.md` 与 `docs/skin-development-guide.md` + `templates/plugin-skeleton/`。

- **流程教训**：文档初稿里「踩坑 2」的根因还写着**上一轮的推测**（"疑似 lunar.dll 静态缓存"），而那时真凶已用实验钉死 → 我核验时发现并打回重写。**写「避坑文档」时，若某条坑的根因还在演进中，要么等结论稳定再写，要么显式标注「本文写作时的结论/后续已更新」**；**在标准文档里写错根因，比不写更糟**——它会主动把后来者引到错误方向。

---

## 七、v0.1.87（2026-09-23）：AI 素材接入链路（mp4 进口 + 尺寸/基线对齐）

灯灯用豆包生成的首批新素材到场（8 张单图 + 3 段新 mp4）。**复核时发现管线有个从没实现过的缺口**，另有两处教训。

### 7.1 🔴 缺口：管线**根本不读 mp4**（文档承诺过，代码从没做过）

`tools/build_skin_v2.py` 的动画态只读 `{prefix}_seq_{st}.png`（序列条）。
而提示词文档 §8 白纸黑字写着「mp4 → 抽帧 → 抠绿幕 → 裁内容框 → 对齐 → 真动画帧」——
**这条链路从来没被实现过**。灯灯按文档生成的 mp4，一个都进不去。

> **教训**：**「写进文档的能力」必须在同一次交付里被实现和验证**。
> 否则文档就是一张空头支票，而且它会**主动诱导使用者往错的方向投入**（灯灯白生成了 3 段视频）。
> 与 6.6 的教训同源：**文档里的每条"我们支持 X"，都要能指到真实的代码与验证证据。**

**补齐**：新增 `tools/video_to_frames.py`（mp4 → 均匀抽帧 → 抠绿幕 → 裁内容框 → 序列条），
`build_skin_v2.py` 加 10 行「优先读 `{prefix}_vseq_{st}.png`、缺则回退旧 `{prefix}_seq_{st}.png`」。
ffmpeg 走 `imageio-ffmpeg` 托管二进制（沙箱没有 ffmpeg，pip 可达 → 装 Python 包绕开）。

### 7.2 🔴 隐藏坑：**背景绿不统一**，固定阈值会整批漏

| 视频 | 背景 RGB |
|---|---|
| `cat_walk` / `cat_pet` | **黄绿 (160,234,75)** |
| `cat_drag` / `cat_fall` / `cat_climb` | **标准绿 (40,245,30)** |

用固定阈值 `g>200 且 r<80 且 b<80` 判绿幕 → **walk/pet 命中 0%**（r=160 超了）。
→ 键控**必须逐视频自适应采样背景色**（取四边框带像素的众数），**绝不能写死 `#00FF00`**。

### 7.3 🔴 隐藏坑：**每段 AI 视频的「镜头远近」可以差 1.75 倍**

同一批素材，按同一口径量（相对静图基准 399）：
`fall 0.63×` / `walk 0.86×` / `pet 0.92×` / `drag 1.05×` / `climb 1.10×`。

工程师第一版按「把猫**撑满** 512 格子」归一化（猫高 455/512=89%、离底 7），
而静图基准是 **78% 填充、离底 112** → 结果 **动画态比 idle 大 13% 且低 23px**：
```
状态    旧离底  新离底   旧猫高  新猫高
idle    29.0   29.0    196.0   196.0  ✓ 基准
walk    28.4    6.0    195.1   222.0  ✗   ← idle→walk = 「长大一圈 + 下坠 23px」
drag    39.1    6.0    173.9   222.0  ✗
```
**修法**：**逐状态统一缩放**（度量用逐帧内容高度的**中位数**，映射到 399；**同状态所有帧共用同一系数**，
否则猫会「呼吸」）+ **统一基线**（`gap×系数+delta`，使 median 离底 ≈52，**保留帧间起伏**只整体平移）。
修后 9 状态 median 离底 24~31、猫高 194.5~198（idle 29/196）✓

> **教训 1**：**「各状态大小/基线是否一致」不能靠肉眼**。量两个数就够：
> **最终帧里每个状态的（内容高度, 内容离底像素）中位数**；差 >10% / >5px 就是回归。
> 再出一张**对齐图**（画 idle 的脚底线当红线、逐状态标 Δ）——一眼看穿。
> **教训 2**：**AI 图生视频的"镜头远近"是必然的坑**（模型不保证尺度）。素材侧难约束时，
> **管线侧按状态统一缩放**是可接受的补救（终帧只 231px，放大 1.58× 反而净缩小，无画质损失）。

### 7.4 素材侧：两段视频被判不可用 / 半可用

- ❌ **`cat_climb.mp4` 猫被画幅底边平切** → 整段报废，回退旧 seq。
  判据不是"看图觉得怪"，而是数数：**底部 3 行的前景像素数 = 602~658**（猫爪只会是几十个，
  数百像素的**直边**就是"平切"）。**这比看图可靠。**
- ✅ 其余 4 段（walk/pet/drag/fall）可用。
- 提示词文档已把「三条硬约束」升级为**五条**，新增：
  **4. 角色必须完整在画幅内（尤其底部不能切）** · **5. 各段镜头远近要一致（猫占画幅高 ~70%）**，
  并在 §3.4 的 prompt 里加了「【取景】」段。

### 7.5 复核结论

主理人**独立复量**（不听汇报）：9 状态 median 离底/猫高全部落在验收带内 ✓；
**额外查了工程师没提的一条** —— 「有没有帧被画布切」（全部 0/8，他说的"裁 3 帧余量"没切到猫）✓；
`neko_climb.png` 与 HEAD md5 逐字节一致 ✓；尺寸零回归（cat_hd 256×231×8、cat_pixel 64×64×8）✓；
`cat_pixel` 的 idle/sit/sleep/work 字节变化经**画面对照确认**为「共享调色板重算」（alpha 逐字节同、视觉几乎无差）✓；
`ci.py` 六闸门由主理人**再跑一遍**全绿 ✓。

---

## 八、v0.1.88（2026-09-24）：高清皮肤瞳孔跑出眼眶

灯灯实机：「现在高清版本的眼神追踪 眼睛已经超出眼眶了 看着太恐怖」

### 8.1 根因是**偏移量锚错了参照物**（不是素材问题）
`PetRenderer` 把眼球层平移 `_eyeOffset × Scale` **屏幕像素**，而绘制框恒为 `32×Scale`
（`SpriteAtlas.FrameW` 恒为 32 常量）。换算到**源位图像素**后位移 = `2 × frameWidth / 32` ——
👉 **皮肤源图越细，位移越大**：`pixel`(32) ±2px → `cat_pixel`(64) ±4px → **`cat_hd`(256) ±16px（8 倍）**。
叠加第二个事实：**这只猫的瞳孔几乎填满眼白**（实测 `cat_hd/idle` 瞳 29×29 vs 眶 34×32，白圈仅 2~5px）
→ 整颗甩出眼眶。
实测「瞳孔掉出眼眶」占比：**cat_hd 59.7% / phoebe_hd 61.0% / cat_pixel 63.9% / phoebe_pixel 64.3%**。

### 8.2 修法：限幅 + 遮罩裁剪
1. **安全位移**（源像素）：瞳孔层连通块 → 丢噪声块（面积 < 最大块 10%；实测有 2×3 / 1×1 碎块，
   用 min 会被带偏）→ 联合 bbox 的**较小边 × 0.18**。新增 `SourcePixelsPerLogical` 做换算。
2. **眼眶遮罩**：以该状态闭合层 `{eyeLayer}_closed` 的 alpha 作 `PushOpacityMask` 裁掉超界部分
   —— 瞳孔滑到边界自然被切（真猫斜视时虹膜也被眼睑遮挡）。
3. **旧皮肤零影响**：门控「该层存在 `{key}_closed`」；内置 `pixel` 的眼球层是 `eyes_open/_half/_blink/_pet/_scared`
   （**无 `_closed`**）→ 走原路径、逐像素不变。

### 8.3 ⚠️ 两个 WPF / 工具链坑（可复用）
- 🔴 **`PushOpacityMask` 的 `Viewport` 必须用绝对坐标**（`BrushMappingMode.Absolute` + `Viewport=(0,0,W,H)`）。
  用默认的**相对象**模式时，遮罩会**跟着被绘制的矩形一起平移** → 等于没裁（瞳孔带着遮罩一起跑）。
- 🔴 **`tools/ci.py` 不构建 `samples/SamplePlugin`**，而 `LoaderTest` 要靠它部署示例插件
  → **SOP §7「涉及素材/打包的改动先清 bin 再测」一旦执行，LoaderTest 必挂 `DirectoryNotFoundException`**
  （本轮清 bin 出包后真实踩到）。已补 `AUX_PROJECTS` 把测试的**间接依赖**一起构建
  （注意用默认 `bin/Debug` 输出，不是测试工程的 `bin_ci/`）。

### 8.4 方法论
- **「元素是否越界」要选对参照物**：第一版我用「闭合层当眼眶」算 `0.45×(眶−瞳)`，
  结果 `cat_hd/idle` 算出 0.9 源px（几乎不能动）—— 因为白圈本来就只有 2~5px。
  改用「瞳孔自身尺寸 × 系数」更稳，**再叠加遮罩裁剪**才彻底。
  单靠限幅永远在"太紧没效果 / 太松还越界"之间摇摆；**裁剪是唯一能兜住的做法**。
- **连通块统计要用鲁棒量**（去噪 + 联合 bbox），别用 min —— 差分掩膜必然带碎块。

---

## 九、v0.1.89（2026-09-25）：高清皮肤卡顿 —— 先测后修，两个假设被数据推翻

灯灯：「猫替换了高清版本的会有点卡顿」

### 9.1 🔴 根因是**我自己上一版加的遮罩**，不是重采样
`PetRenderer` 的眼球裁剪用 `PushOpacityMask`（v0.1.88 为修「瞳孔跑出眼眶」而加）。
它会**另开一个中间表面**做 alpha 混合 —— 代价与**绘制面积**正相关，而当时是按**整画幅**（cat_hd 128×128）推的。

实测（离屏基准，3000 iters）：
| 场景 | 修复前 | 修复后 |
|---|---|---|
| `cat_hd` 单帧 Draw | 837 µs | **363 µs（−57%）** |
| 遮罩净开销（ON−OFF） | **+354 µs（占 42%）** | **+53 µs（−85%）** |
| 微基准：眼睛绘制 遮罩全画幅 → 裁到眼 bbox | 588 → | **89 µs（6.6×）** |

### 9.2 ❌ 被数据推翻的两个假设（**先测再改的价值**）
- **「每帧重采样贵」**：256×231→128×128 只占 **~8 µs**（WPF 侧已有缩放缓存，耗时由光栅化面积主导）
  → 「预缩放缓存」这条路**实测无收益，没做**。若当初直接按猜去写，就是白写一堆代码。
- **「`BitmapScalingMode` 有影响」**：工程师测得四种模式无差别；**我复测却发现 NearestNeighbor 快 ~1.6×**
  （392 vs 625/628），疑似并发负载干扰了那组数据 → **留作待复核项**，不作结论。

### 9.3 修法：把遮罩的绘制区裁到眼睛
`PushClip` 到「眼球层非透明像素 bbox + 偏移 + 2px 余量」。
- **安全的理由**：bbox 外全是 alpha=0，裁掉不改变任何输出像素 → **逐像素一致**（md5 逐字节证明）
- **有效的理由**：中间表面从整画幅缩到眼睛区（cat_hd 眼睛源 81×29 → 目标约 44×20）

### 9.4 方法论（本轮最值钱的一条）
- **「性能直觉」几乎全是错的**：我给的三个候选（重采样 / 缩放模式 / 循环频率）**没有一个**是主因；
  真凶是我上一版亲自加的那行遮罩。**唯一可靠的做法是把每个候选都量一遍**。
- **裁到包围盒 = 只裁掉透明像素**：这是"零风险提速"的通用手法 ——
  **不改变输出**（可用 md5 证明）+ **缩小光栅化/中间表面面积**。遇到「大画幅里只有一小块有内容」就该想到它。
- **性能验收要用两个独立证据**：单帧 Draw 耗时（离屏、可复现）+ 交互期 CPU（`GetProcessTimes`）。
  本例两个方向一致（−57% / −66%），互相印证。

---

## 十、v0.1.90（2026-09-25）：设置窗口切页卡顿 —— 缓存路线 + 「失效面」的代价

灯灯：「设置菜单处还不够流畅」

### 10.1 根因
**每次切页都整页重建 UI 树**（`ShowSection` 直接换 `Content`，无缓存）→ 首建 pet 29.2ms / plugin 70.2ms，
**切走再切回仍全量重建**。而我原本的头号嫌疑（缩略图同步解码）**被实测证伪**（8 套合计仅 ~5.5ms）。

### 10.2 🔴 本轮最重要的教训：**缓存 = 把「惰性重建」换成「显式失效」，失效面必须扫穷尽**

缓存实装后，QA 用**真窗口真点击的对抗性验证**挖出 **3 处漏失效**（显示层陈旧）：
径向上限步进器 / 插件启停漏失效 radial / pet 签名漏 `Scale`+`Opacity`。工程师又 grep 出 2 处同类。
- **改造前**：这些场景靠"每次重建"自然地自愈 → 漏了也看不出来
- **改造后**：**漏一个就 stale**，而且**静默**（没有报错，用户只觉得"显示不对"）
→ **凡是要引入结果缓存的改动，必须同时交付一张「失效面全枚举表」**（改状态入口 × 影响页 × 触发什么失效），
  让它从"靠记性"变成"可核对"。这是本轮最值钱的一条流程经验。

### 10.3 另一条经验：**「为插件生态预留」不等于「顺手优化」**
最后那处残留（第三方插件运行期改径向槽位/标签）：
- **就内置插件而言**现有方案完全够用（实测空闲 3s 事件触发 **0** 次；`HourlyChime` 的定时器从不碰槽位）
- **但**本项目立身之本就是第三方插件生态 → 这类形态**必然出现**，且对现有方案是**静默 stale**
→ 判定为**已知的正确性缺口**，批准扩边界到宿主层（`UIApiImpl` 发 internal 事件 + 设置窗订阅/退订）。
**判据不是"内置插件会不会这么做"，而是"生态里会不会有人这么做"。**

### 10.4 具体修法与效果
缓存整页 + 签名判活 + 5 处失效接线 + 滚动条样式静态化（`XamlReader.Parse` **20 次 → 1 次**）+
宿主层径向变更事件。切回页 **2.6~4.9×** 提速（pet 29.2→6.7ms / plugin 70.2→14.4ms）。

### 10.5 被否决的方案（同样是数据说话）
- **插件页虚拟化**：`ItemsControl + VirtualizingStackPanel` 实测 **23.6ms ≈ 基线** —— 因为卡片元素
  **在进入列表之前就已构造完毕**，虚拟化只延后"视觉实现"、不延后"元素构造"。**故放弃**。
- **预缩放缓存帧位图**（v0.1.89 那轮）：实测无性能收益（重采样仅 ~8µs），只省内存 → 未采用。

### 10.6 环境坑（本轮亲历，已进 SOP 级别）
- 🔴 **残留测试进程会锁住 `bin/` 里的 dll** → `MSB3026` 文件锁警告 → **ci 闸门因"非 0 警告"失败**。
  真因是上一次跑挂的 `PluginSmoke.exe`（PID 50616）没退出。**排查顺序：先 `taskkill` 测试进程，再怀疑代码。**
  （与已知的 `MSB4166`/`MSB3491` 并发 flake 是**两回事**：后者重试即过，前者必须杀进程。）
- 并发跑多个 teammate 构建 → `MSB4166 子节点过早退出`，重试即过。

---

## 十一、v0.1.91（2026-10-02）：启动优化 + 管线「只为猫标定」的三个常量

### 11.1 启动优化：真凶不在我猜的地方（第四次押错）
QA 分阶段实测（Release 单文件 1242ms）：`ShowMirror` **365ms(30%)** 最大头、
窗口实现 175ms、CLR/WPF 引导 173ms、chat 112ms —— 而**我怀疑的「18 插件 + `builtins.lock` 校验」只有 66ms(5%)**。
> **答案早就写在 `CHANGELOG` 里**（v0.1.33 记着「瓶颈在 WPF 引导 + 双窗口创建 + SpriteAtlas」）——
> **我没去翻。下次遇到性能问题，先查项目自己的历史结论，再动手测。**

**并行的第二次押错**：我以为径向菜单的「整页重建」是瓶颈（照搬设置页的结论），
实测重建**只 1.1ms**（设置页 40ms）——**「同一个模式」不等于「同一个量级」**。

### 11.2 🔴 P1 的收益被高估 6 倍 —— 一次"验证过的解释"
QA 预估 P1（把 `ShowMirror` 移出关键路径）值 **365ms**，实测**只有 63ms**。根因：
那 365ms 的大头是**「进程内首个 WPF 窗口的全局初始化」**。改动前镜像窗是进程第一个窗口、吃掉了它；
改动后成本**挪到猫窗的 `Show→首帧`（+171ms）**。`ctor −352 + 171 ≈ −181` vs A/B 实测 −176 → **自洽**。
> **经验（值得记一辈子）：「把工作挪出关键路径」只在"它不是一次性全局初始化"时才等于"省掉"。**
> 全局初始化（CLR/WPF 引导、**首个窗口**、GPU 合成器首启）**逃不掉，只能选在哪个窗口付**。
> 所以评估这类优化前，先问一句：**这份工作是一次性的全局初始化，还是可重复的活？**

### 11.3 🔴 血案：管线里三个常量是「只为猫标定」的
第二批素材（`cat_work` / `phoebe_walk` / `phoebe_pet`）到位后复核，量出**猫与菲比的静态基准不同**：

| | 内容高 | 离底 |
|---|---|---|
| 猫 | 399 | 56 |
| **菲比** | **446** | **34.5** |

而 `video_to_frames.py` 里 `TARGET_H=399 / TARGET_GAP=52 / MAX_ADVANCE=463` **全是按猫标定的**：
- 前两个 → `phoebe_pet` 被做成 **89.5%** → **切到 pet 缩水 10%**（用户可见）
- `MAX_ADVANCE=463` → 把菲比的 `pet` 离底从 28 **硬削到 9**（比 sit 低 6px）

**修法**：三个常量全部改为**按角色从 `{prefix}_still_*.png` 推导**。
`MAX_ADVANCE` 的公式 `max(静态图 h+gap) + 8` **精确复现猫的 463**（= max(455, 451.5)+8）→ **公式可信**。
**零回归验证**：新公式重跑猫的 4 个序列条，**逐字节相同** ✓（这一步不能省，否则改的是所有人的素材）

> **教训：凡是"写死的对齐常量"，都要问一句「这是为谁标的？」**
> 本项目的角色不止一个（猫/菲比），**基准必须按角色取，且要能从素材自动推导**。

### 11.4 🔴 静默裁切：一个会一路流到用户电脑的 bug
原实现 `x = max(0,(cell-w)//2)` + `paste` —— 内容比格子宽时 **PIL 静默裁掉溢出、不报错**。
`cat_work.mp4`（猫 820×484、宽高比 **1.69**）就中招：**源视频里猫是完整的**，但格子塞不下 → 被裁。
> **若不查，这段废素材会一路进皮肤、进安装包、进用户电脑，而且没有任何报错。**
> 现默认 `--on-overflow error`：**报错 + 跳过 + 非零退出码**（可选 contain/clip）。
>
> **通用原则：凡是"数据放不下"的情况，宁可大声失败，也别静默截断。**
> 静默截断的 bug 成本 = 你发现它的时间 × 它流出去的版本数。

### 11.5 方法论收获（本轮最值钱的两条）
1. **"验证过的解释" > "看起来合理的解释"**：11.2 那个反转让工程师把「ctor 降了但 Show→首帧升了」也对上账，
   数据自洽才敢下结论。
2. **回归验证要"逐字节复现既有产物"**：改了共用管线后，**先证明它对已有素材零影响**（11.3 的 4 个 md5），
   再拿它去处理新素材。否则你不知道自己是在修 bug 还是在制造 bug。

---

## 十二、v0.1.92（2026-10-02）：走路「空地踏步」—— 一个被掩盖了很久的 bug

灯灯：「那个走路的动画 我觉得非常不好看……**一走感觉像在放动画，很蠢**」

### 12.1 这不是审美问题，是 bug
- **Walk 零位移**：全工程 `Left =` 只在 `Drag.cs` / `EdgeSnap.cs`；`AnimateMoveTo` 只被吸附与镜像调用
  → **猫进 Walk 后腿播 2 秒，窗口一动不动**。
- **触发过频**：`_rng < 0.02` **每 tick** 判定，闲置 100ms/tick → **20%/秒 ≈ 每 5 秒一次**。
- **单次 2 秒** → 三者叠加 = **约 40% 时间在踏步**。

### 12.2 ⚠️ 一个「被掩盖」的旧隐患（本轮最值得记的一条）
`Tick` 顶部有「闲置 ≥ `SitAfter`(30s) 就坐下」的判断，而 `_idleTimer` **在走路期间也累加**。
因为走动间隔（40~60s）**大于** `SitAfter`，猫**几乎总是"坐着起身就走"** —— 此刻 `_idleTimer` 已 ≥30s，
若不在进入 Walk 时清零，**下一个 tick 就会 Walk→Sit**，走动只活 ≈50ms。
> **旧版因为零位移，这个 bug 根本看不出来**（原地踏步 50ms 和 2s 在观感上没差别）。
> **教训：一个"看起来无害的 bug"（没位移的动画）会掩盖它下游的另一个 bug（状态被立刻打断）。
> 修好前者之后，必须重新回归后者的路径。** —— 工程师这次就主动补上了 `_idleTimer = TimeSpan.Zero`。

### 12.3 修法要点
- **真位移**：`Left += v×dt` 匀速（`WalkSpeed = 50 px/s`，附推导：8 帧@10fps=0.8s/循环，1 循环=2 步、步长 20px）
  —— ⚠️ **刻意不复用 `AnimateMoveTo`**：那是「280ms 缓动 + BackEase overshoot」，用于吸附/镜像的短距跳跃；
  **走路的语义是"持续匀速"，混用会让猫一窜一窜。**
- **降频用倒计时而非概率**：`_rng < p` 会随 tick 率（50/100ms 两档）与负载漂移，
  而且**把概率按错的 tick 率算就完全跑偏**（旧代码 2% × 10tick/s = 20%/s 就是这么来的）。
  倒计时与 tick 率无关，目标 40~60s 可验证。
- **位移量随机**（时长 1.6~3.0s）→ 更像活物，而不是"每次固定走 2 秒"。

### 12.4 方法论
1. **「观感差」先当 bug 查，别急着改美术**。本例的"很蠢"= 零位移 + 40% 占比，全是可量的逻辑问题。
2. **改完一个 bug 要回归它掩盖的那个**（12.2）。
3. **别拿"看起来能用"的现成函数凑语义不对的场景**（12.3 的 `AnimateMoveTo`）。

---

## 十三、v0.1.93（2026-10-02）：抽帧按「步态周期」—— AI 视频动画 6.3 倍速的根因

### 13.1 灯灯的第二层抱怨：「太快 / 像在放动画」
v0.1.92 修掉了「零位移」，但腿仍然明显快。实测：**把 5.04s 的动作压进 0.8s 播放 = 6.3 倍速**。

### 13.2 🔴 根因：**「均匀抽帧」把周期性动作抽成了乱序**
- 源 `cat_walk.mp4`：121 帧 @24fps = 5.04s，**自相关测出步态周期 ≈ 35 帧（1.46s）**，全片 ~3.5 周期
- 而 `sample_indices` 是**把整段均匀降采样成 8 帧** → `[0,17,34,51,69,86,103,120]`
  → **8 帧跨了 3.5 个周期** → 帧间运动量 `7.71 / 2.38 / 0.61 …`（**CV 1.130**）
- 同时 5.04s 塞进 0.8s → **6.3 倍速**

> **通用教训：对「周期性动作」（走/跑/摆动）绝不能用「整段均匀抽帧」** ——
> 均匀抽帧的步长与动作周期**无关**，两者一旦不成整数倍就产生**拍频（aliasing）**：
> 有的帧跨很大一步、有的几乎不动，且整体速度 = 片段时长/播放时长。
> **正确做法：先测周期，再在一个周期内均匀取帧**（此时循环天然无缝、帧间等步长）。

### 13.3 修法要点
- 自相关找周期（lag∈[4,60]，相关度阈值 0.25）；`P<n` 视为伪峰
- 周期内均匀取 n 帧 + **遍历起点取 score 最小**（`CV + 2×max(0, 接缝比−1)`）
- 🔴 **防劣化护栏（工程师主动加的，超出我的要求）**：`score ≥ 均匀方案` 就**回退均匀**。
  理由：`pet`（蹭头）/`drag`（摆动）**不是匀速周期运动**，硬套周期反而帧间忽快忽慢 + 接缝变大。
  → **"为了用周期而用周期"是劣化**。实测 `drag`/`phoebe_pet` 正是如此，被护栏拦下回退。
- **回退路径与旧公式逐字节等价**（用 SHA256 实证：只有 walk 变，其余 4 个 vseq 与备份一致）

### 13.4 效果
`cat_walk`：CV **0.480→0.041**、倍速 **6.30×→1.82×**；
端到端（最终皮肤条）`cat_hd/walk` CV **0.349→0.096**、`cat_pixel/walk` **0.339→0.087**。

### 13.5 方法论
1. **量"周期性"要用自相关，不能靠肉眼**：我一开始只觉得"帧间差 3.4 倍不均"，**自相关一跑才知道周期是 35 帧**，
   从而算出"6.3 倍速"这个更严重的结论。
2. **给算法加"不比原来差"的护栏**：新算法一旦可能劣化，就让它**自己判断并回退**，
   而不是假设"新的一定更好"。这条护栏本轮真拦下了 2 个状态。
3. **回退路径要能"逐字节复现旧行为"**：改公共管线时，这是唯一能证明"没改坏"的硬证据。

---

## 十四、v0.1.94（2026-10-03）：同一个抱怨，两轮完全不同的根因 —— 从「可修的 bug」到「素材的物理极限」

### 14.1 灯灯的原话一直没变：「走路像在放动画，很蠢」
这句话触发了**两轮**排查，**根因却完全不同**——这是本节最值得记的事：

| 轮次 | 版本 | 表现 | 根因 | 性质 |
|---|---|---|---|---|
| 第一轮 | v0.1.92 | 腿在动，身体不动 | **逻辑 bug**：Walk 零位移（`Transition` 被 `_idleTimer` 立刻打断 / 纯原地踏步） | ✅ **可修** |
| 第二轮 | v0.1.93 | 位移有了，腿**太快** | 抽帧把 5.04s 压进 0.8s = **6.3 倍速** | ✅ **可修** |
| 第三轮 | v0.1.94 | 修完 fps/位移后，仍**像滑行** | **素材本身腿摆无视觉幅度** | 🔴 **不可修** |

> **教训 1：同一个用户抱怨可能对应完全不同的根因。** 不要因为"上一轮修好了"就认为这一类抱怨已解决；
> 每一轮都要**重新定位根因**。灯灯说"继续"，就真有两层、三层的独立问题等着。

### 14.2 🔴 根因量化：素材的腿摆幅度，实测近乎为零
- `cat_walk` / `pixel_walk`：**脚掌相对身体位移仅 0.8~1.1 源 px**、腿区逐帧变化 **8~14%**
- 对比 `phoebe`：**37~41%**
- 三套猫皮肤（`pixel`/`cat_pixel`/`cat_hd`）的走路都是**正面啾比、腿被身体挡死** → 腿摆**没有可感知幅度**
- → 一旦施加真位移，观感必然是**滑行**。**这不是 fps 或 speed 能调的——是素材的物理极限。**

> **教训 2：调参数救不回素材。** fps / WalkSpeed 只能让"腿速与移速"一致（零打滑），
> **无法凭空造出素材里不存在的腿摆幅度**。零打滑 ≠ 不滑行：素材没幅度时，"零打滑的滑行"依然是滑行。
> 该认素材极限时就要认，别无限调参。

### 14.3 🔴 排查盲区：**默认皮肤是 `pixel`，我一直在调 `cat_hd` 根本不生效**
第二轮（v0.1.93）我在真机验证时把配置 `Skin` 临时设成了 `cat_hd` 才测到效果，
**但用户（灯灯）机器上 `DefaultSkinId = "pixel"`** ——
我调的 `cat_hd` 参数、重建的 AI 皮肤，**用户根本没看到**。

> **教训 3：验证前先确认"用户实际在用哪套皮肤/哪条路径"。** 
> 否则会出现"测试全绿、用户仍抱怨"的经典错位。**真机验证必须用生产默认配置**，
> 临时改配置只为测某套皮肤时，务必明确标注并还原（本轮已还原）。

### 14.4 处置：默认关闭，而非删代码
灯灯拍板"别走路了"。落地为**软关**：
- 新增 `AppConfig.WalkEnabled`（**默认 false**）；
- `PetStateMachine.WalkEnabled` 热改属性；倒计时块门控 + **关闭时立刻收尾回 Idle**；
- **代码一行不删**（`BeginWalk`/`EndWalk`/`ReverseWalk`/`WalkVelocityX` 全保留），
  C 档参数（`walk.fps=7` + `WalkSpeed=72`，零打滑比 1.003）原样保留 → 改配置即恢复。

> **教训 4：功能下线优先"软关 + 可恢复"，而不是删代码。** 用户口味会变、素材将来可能换更好的，
> 删掉容易、恢复难（要重新写+重测）。加一个默认关的开关，成本最低、可逆性最好。

### 14.5 方法论
1. **每轮重新定位根因**，不做"问题应该已经解决了"的假设（教训 1）。
2. **区分"可修"与"不可修"**：逻辑 bug 可修；素材物理极限要**如实上报并认账**，而不是继续调参（教训 2）。
3. **真机验证用生产默认路径**（教训 3）。
4. **下线用软关**（教训 4）。

---

## 十五、v0.1.95（2026-10-03）：架构健康审计（C 档）——「只增不改」修 2 个 P1 订阅泄漏 + 资源收口

来源：一次**全项目架构与代码健康审计**（9 个面：契约诚实性 / 静默失败 / 资源生命周期成对性 / 线程与 WPF 亲和性 / 状态机与时间逻辑 / 死代码 / 配置与持久化 / 权限与信任 / 构建与生成器一致性）。审计报告见 `_audit/architecture-health-audit-v0.1.94.md`。本轮只落地其中**风险最低、收益明确**的 C 档。

### 15.1 五个补丁（一句话版）
| 补丁 | 文件 | 缺陷类别 | 修法 |
|---|---|---|---|
| A | `Windows/InputMirrorWindow.cs` | P1 订阅泄漏 | 滚轮还原回调由「匿名 lambda `+=`」改为「命名方法 `-=` 再 `+=`」 |
| B | `Windows/RadialMenu.cs` | P1 订阅泄漏 | `ShowAt` 幂等订阅；`HideMenu` 退订提到早返回**之前** |
| C | `Windows/PetWindow.Plugins.cs` | P2 契约不符 | `Emit(app:ready)` 从「末尾同步」挪进 `LoadDeferred()` **之后** |
| D | `Runtime/PluginLoader.cs` | P2 调用次数不一致 | 新增幂等 `LoadedPlugin.Unload()`；`Dispose` 收口为调它 |
| E | `Services/{PluginHost,SchedulerImpl}.cs` | P2 资源未收口 | 宿主 `_schedulers` 持有每插件调度器；禁用/退出 `Dispose()` 停全部定时器 |

### 15.2 🔴 核心教训：**「同一函数两种写法并存」是找泄漏最快的自检技巧**
P1-1（滚轮定时器）和 P1-2（径向菜单钩子）是**同一类 bug**：**「订阅了却从不/不配对退订」**。它们不是靠通读全项目找出来的，而是靠**同一个（或相邻的）函数里存在两种写法**：
- `OnRawMouseWheel` 里 `_wheelTimer.Tick += 匿名 lambda`（**错**），而**紧挨着**的 `_wheelPawTimer.Tick -= OnWheelPawTimerTick; += ...`（**对**）——同一函数、同一模式、两种写法，**对比即见异味**。
- `RadialMenu` 的「右键入口」无 `IsVisible` 守卫、而「热键入口」有守卫；`ShowAt` 无条件 `+=`、而 `HideMenu` 只 `-=` 一次。

> **可复用技巧**：审「订阅/退订」时，别逐个文件读，而是**把所有 `+=` / `-=` 配对列在一起**，盯「同函数内写法不一致」和「`+=` 多于 `-=`」两处，命中率极高。
> 同理适用于 `new Timer(...)` / `Dispose()`、`Attach` / `Detach`、`AddHandler` / `RemoveHandler`。

### 15.3 教训：`System.Threading.Timer` 会**永久 root** 其回调指向的一切（含插件 ALC）
P2-1 的机理：`PluginHost.BuildContext` 里 `new SchedulerImpl()` **没人持有**，插件禁用时自然没人 `Dispose()` → `System.Threading.Timer` 被运行时定时器队列**永久 root** → root 住回调委托 → 指向插件 ALC → **插件卸载后回调仍在跑、ALC 回收不了**。

> **结论**：**凡「创建即注册进全局资源、但局部变量随后丢弃」的对象，都是 ALC / 内存泄漏的高危模式**。修法是**让宿主持有所有权**（`_schedulers` 字典）+ **提供幂等 `Dispose`**。
> ⚠️ 这条也揭示了一个**测试盲区**：`PluginSmoke` 的「[ALC 未能回收] 0 个」是在**假调度器**下跑的，**不含真实 `SchedulerImpl` 定时器**，因此它绿**不代表**这条修好了——真实器修必须靠真实 `SchedulerImpl` 的新断言（见 15.5）。

### 15.4 教训：注释里的「契约」要能被测试守住
P2-2：`ICapabilityApi` 文档写「订阅 `AppReady` 可替代 `OnLoad` 做延迟初始化」，但 `Emit` 实际在 `LoadDeferred` **之前**就同步发出 → **6 个延迟插件永远收不到**。这不是注释笔误，是**行为与契约相反**。
> **教训**：文档承诺的时序/次数（「恰好一次」「之后」「可替代 OnLoad」）必须落成**断言**，否则注释会随时间被行为越过。本轮补的 **G3** 就是这条契约的守护。

### 15.5 新增断言（UiProbe +4 条，改前必红）
- **G2a**：镜像窗滚轮连按 1000 次 → `DispatcherTimer.Tick` 订阅数恒 **1**（改前 ~1001）。
- **G2b**：径向菜单重复 `ShowAt` 6 次 → 全局钩子订阅数恒 **1**；`HideMenu` 后 **0**（改前 6 / 残留 5）。
- **G1a**：`SchedulerImpl.Dispose()` 停掉**未单独 Dispose** 的定时器（回调冻结、幂等）。
- **G1b**：真实 `PluginHost.DisablePlugin` → 该插件调度器被移除、定时器清零；`EnablePlugin` 还原。
- **G3**：`app:ready` 恰好 **1** 次，且触发时已加载插件数 == 全部加载完成数（含 6 个 deferred）。
- ⚠️ **已知缺口（显式声明）**：G1 **未**覆盖「禁用后插件 ALC 真被 GC 回收」端到端形态（需 stub 插件 dll 夹具）；该缺口与 F1/F2 提案同源，已在断言内留注释。

### 15.6 边界与纪律（本轮刻意**不做**的）
- **不改 Core 公共签名**：审计里 Core 是纯契约层，不能引 WPF 类型。
- **F1**（事件按 scope 自动收口）/ **F2**（插件窗口禁用时关闭）：Core 无法承载 WPF 生命周期 → **只出设计提案，不落地**。
- **不做 P3 清理**、**未**把 `check_version` / `check_i18n` 纳入 `tools/ci.py`（属 D 档，留待后续）。
- **只在隔离 worktree（`fix/v0.1.95-arch`）落地**：主树正被 QA 跑 v0.1.94 对抗回归，严禁互相污染。
- 「只增不改」：插件生态（18 个内置）**零改动**，`IScheduler` 公共签名不变（`SchedulerImpl` 只是**新增** `IDisposable` 实现）。

### 15.7 方法论
1. **审计先出「可拍板的补丁方案」，再动手**——每条都带 `文件:行`、改前/改后片段、风险等级、**是否破契约**。
2. **按风险分档（C/D）**，一批只做一档，做完六闸门全绿再谈下一档。
3. **断言优先覆盖「行为契约」**（次数/时序/配平），而非内部实现细节。
4. **找不到夹具就如实标注缺口**，不用假调度器/近似断言"冒充"覆盖。

---

## 十六、v0.1.96（2026-10-13）：第 9 套「拼豆」皮肤 —— 构建期烘焙 vs 运行时渲染，以及提炼 skill 时暴露的三类"文档比实现满"

主题：把「拼豆（Perler bead）」这一新美术风格做成**第 9 套皮肤**，并把产出它的工具链 `tools/bead/` 沉淀成可复用的构建期烘焙器。默认皮肤**仍是 `pixel`**，`bead` 只是"可被选到"。

### 16.1 决策：为什么是「构建期烘焙」而不是「运行时程序化渲染」

两条路都能让猫变成拼豆样子：

| 方案 | 做法 | 代价 |
|---|---|---|
| **A. 运行时程序化渲染** | 引擎里保留「网格 + 调色板 + 模型」三件套，`Draw` 时按网格实时拼珠 | `SpriteAtlas` / `PetRenderer` / `Core` 都要加"拼豆模式"分支；每帧多一层解释执行；Skin 契约被污染成"有的皮肤是位图、有的是模型" |
| **B. 构建期烘焙（采用）** | 离线把模型烘成**标准** `neko_<state>.png` + `skin.json` | 产物就是既有格式；**运行时一行不用改** |

选 B 的决定性理由：**Neko 的皮肤管线本来就是「数据驱动」的** —— `SkinRegistry.Scan` 只扫 `<skin>/skin.json`，`SpriteAtlas` 只认 `neko_<state>.png` / `neko_<eye>.png` / `neko_shadow.png`。既然产物能被这条既有管线直接吃下去，就**没有任何理由**把解释器搬进渲染循环。

> **结果**：本轮 `SpriteAtlas` / `PetRenderer` / `Core` **一行未动**；新增皮肤 = 在 `neko-assets/` 放一个目录 + 在两个 csproj 加一行白名单。这与 v0.1.74 定下的「新增皮肤宿主零改动」原则一致。

### 16.2 决策：为什么调色板只用 16 色

- **美术理由**：拼豆的观感来自"**少而干净**"——格子大、颜色少，才有颗粒感。色值一多就退化成"低分辨率 PNG"，失去拼豆的味道。
- **数据理由**：**Perler 官方不发布 RGB**。市面上的色值都是社区对**实物拼豆**的采样（`craft-color-codes` ← `maxcleme/beadcolors`），**license MIT、status=community**。既然是近似值，堆数量没有意义；16 色（含 1 透明槽）足够覆盖橘猫的描边/主体/高光/暗部/眼白/粉色系。
- **工程理由**：烘焙器用**全整数**网格 + `palette.json` 的 key 映射，色板越小，`bead_convert` 的 CIELAB 最近邻越稳定、越可复现。
- 诚实标注：`palette.json` 里带 `status: community` + `statusNote`，明确写"色值为社区采样、非官方"，避免使用者当成色度真值。

### 16.3 踩坑：`REPO_ROOT = parents[2]` 这种路径耦合，平时不炸，提炼时才炸

`tools/bead/` 最早是为**本仓库**写的，脚本里默认「自己住在 `tools/bead/`，所以仓库根 = `Path(__file__).parents[2]`」。在仓库里跑一切正常 —— **直到要把同一批脚本搬进 skill**：skill 的目录层级完全不同，`parents[2]` 指到别处，脚本要么崩要么读错文件。

- **修法**：**去掉隐式路径推导**，改成**显式 CLI 参数**（`--model` / `--assets` / `--palette`）+ **CWD 相对**。
- **教训**：**"靠文件位置猜项目结构"的脚本是不可移植的**。凡是脚本里出现 `parents[N]` / `..\\..` 这类**向上数目录**的写法，都是"以后搬不动"的伏笔。**在写第一行时就用显式参数**，成本几乎为零。
- **现状复核**：`tools/bead/` 与 skill 内脚本现已**无** `parents[` / `REPO_ROOT`（已核实）。

### 16.4 🔴 踩坑：文档把能力写得比实现满 —— 抓到 3 例，**均为真缺陷**（第 2 例我初稿曾误判为"不成立"，史实见下）

这是本项目的老毛病（见 §十五 的 `app:ready` 契约）。本轮在提炼 skill、交叉复核时又抓到三处"**文案承诺 > 代码实现**"：

| # | 文档说 | 实际 | 处置 |
|---|---|---|---|
| 1 | `bead-sprite-baker/SKILL.md` 称 **"eight checks"** | `bead_verify.py` 只有 **6** 条（A1–A6：烘焙确定性 / 调色板合规 / 硬边 alpha / 眼睛同源对齐 / 基线稳定 / 帧数一致） | 已在 S1 修复轮收敛为 **"six checks"**；**现状复核**：`SKILL.md:136` 文本与代码的 6 条**一致** |
| 2 | `palette.md` 承诺 **CIELAB 优选**，而（原）代码只取**上游前缀 N 个** | **确系真缺陷**：原实现是 `colors[:limit]`（前缀）、文档却承诺 CIELAB（术语："说谎的契约"）；**前轮 `fix-s1` 已修** —— 取"**实现而非改文档**"，补 `select_diverse`（CIELAB 贪心最远点采样），`--limit` 由"取前 N"改为"分散度优选" | **已被前轮 `fix-s1`（P2-6）修复**（非"不成立"）。⚠️ 我初稿看到的"文档与实现一致"是**修复后**状态 → 属**时序误读**，此处纠正 |
| 3 | `UiProbe` 皮肤契约断言注释称"**让拼豆骨骼/烘焙回归也被这层网罩住**" | 该断言**只查结构与元数据**（帧数 / fps 表 / 帧尺寸 / `thumb`），**从不比较帧间差异** | **本轮已修正注释**（见 16.5），改为如实描述其覆盖面与盲区 |

- **教训**：**"报了但没写进交付物 = 未来用的人看不到"**；反过来，**"写进交付物但没核实 = 永久谎报"**。两者都要防。
- **可用于决策的判断**：文案里的**数量词**（"eight checks"）、**能力词**（"优选"、"覆盖回归"、"保证"）是最容易漂移的地方，**改文档时优先复核这些词**。
- ⚠️ **对第 2 条的纠偏（史实）**：`palette.md` 属 skill（S1）产物、不在本仓库。**史实**：原 `gen_palette.py` 的 `--limit` 取**前缀**（`colors[:limit]`），与 `palette.md` 承诺的 CIELAB 不符 —— **真缺陷**；它已在**本轮之前**由 `fix-s1`（P2-6）修复（改为**实现** CIELAB）。我复核时看到的"一致"是**修复后**状态，故**不能**据此判"不成立"。依据：`_eval/fix-s1/fix-s1-report.md`（P2-6）。

### 16.5 🔴 踩坑：测试盲区 —— 皮肤契约断言**查不出"静止帧冒充动画"**

`UiProbe` 的「动画皮肤契约」断言（v0.1.82 第 31 项）做的是**结构与元数据**校验：

```
frames == 8 · idle|sit|sleep 必须 fps=0 · 其余 fps>0 · 9 态各 8 帧
每帧尺寸 == 皮肤自声明的 FrameWidth×FrameHeight · thumb.png == 96×96
```

它**没有任何一条**去比较"帧与帧是否真的不同"。于是——

**实测 `neko-assets/bead/` 每态唯一帧数（对 8 帧逐帧取 md5）**：

| 状态 | 唯一帧数 | 说明 |
|---|---|---|
| `idle` / `sit` / `sleep` | **1** | 静止态（`fps=0`），1 帧**正确** |
| **`walk`** | **1** | ⚠️ **标了 `fps=7` 但 8 帧全同一张图** —— 断言仍判 **PASS** |
| `pet` / `drag` / `fall` / `climb` / `work` | **2** | A-B-A-B 交替的**占位**；且其 "A" 帧与 `walk` 的单帧**完全相同** |

- **后果**：一张"8 帧全同一张图"的 sheet，本断言**完全罩不住**；这正是 `bead` 的 walk 现状。
- **教训**：**"帧数对不对" ≠ "动画对不对"**。契约断言若只覆盖"形状"（计数/尺寸/元数据），就**必须显式写清它不覆盖"内容"**——否则注释会诱导使用者以为已覆盖（这正是 16.4 第 3 条）。
- **本轮处置**：**只改注释、不加断言**（加"帧去重"断言会让 `bead` 立刻变红，且可能连带打红其他皮肤——留到 T04 动画补齐后再加）。
- **已知边界（写进 CHANGELOG）**：`pet`/`drag`/`fall`/`climb`/`work` 为 2 帧占位、`walk` 素材层单帧；`bead` 动画待 **T04** 补齐。

### 16.6 方法论

1. **能做进"既有数据管线"的能力，就不要搬进运行时**。产物格式复用 = 宿主零改动的最大杠杆（16.1）。
2. **"美术上的少"往往也是"数据上的稳"**：色板小 → 量化确定 → 烘焙可复现（16.2）。
3. **脚本里的隐式路径推导（`parents[N]`）是可移植性的定时炸弹**；一律换显式参数（16.3）。
4. **文档里的数量词/能力词要逐条复核**；抓到漂移要么改文档、要么改实现，**不许留着**（16.4）。
5. **断言只覆盖"形状"时必须写明它不覆盖"内容"**；否则测试盲区会被误读成已覆盖（16.5）。
6. **本轮"运行时零改动"**：`SpriteAtlas` / `PetRenderer` / `Core` 一行未动，全部工作量落在**素材构建期**与**两个 csproj 白名单**上。

---

## 十七、v0.1.97（2026-10-13）：拼豆动画补齐（T04）—— 「静止帧冒充动画」的收口，以及两处"刻意留白"和一个"会被盲重试放大"的失败

主题：把 `bead` 皮肤从"9 态多数是占位"补成**真实逐帧动画**，并给 `UiProbe` 补上能**真正查出"静止帧冒充动画"**的断言。本轮**运行时 `src/` 零改动**，全部工作量落在**构建期素材**（`cat.beads.json` + 6 张重烘焙 sheet）与**测试**（`Program.cs`）。

### 17.1 结果：唯一帧数对照（前 → 后）

| 状态 | v0.1.96 唯一帧 | v0.1.97 唯一帧 | 怎么做到的 |
|---|---|---|---|
| `walk` | **1** | **8** | 把 `generate_sprites.py` 的 `st_walk` 步态弧线翻成**整数拼豆格位移**，前后腿反相、落地帧双腿并拢 |
| `climb` | 2 | **8** | 交替抓握（前爪/后爪轮换上抬 + 躯干贴壁） |
| `work` | 2 | **6** | 前爪交替点击（`LEG_PAW` 3 行抬爪） |
| `pet` / `drag` / `fall` | 2 | **4** | 各 ≥2 种形状变体 + 逐帧位移：`pet` 眯眼+躯干压缩、`drag` 四肢下垂+尾上翘、`fall` 四肢张开+耳后压 |
| `idle` / `sit` / `sleep` | 1 | 1 | **刻意保持静态**（`fps=0`，见 17.2）|

### 17.2 决策：为什么 `idle` 不做"可见呼吸"（方案 a）

给 idle 加"呼吸"有两条路：

| 方案 | 做法 | 代价 |
|---|---|---|
| **a. 头部不动 + 躯干 1 格上下浮（采用）** | idle 保持 `fps=0` 的**静态帧**，浮动的"呼吸感"通过**整体帧间差异**体现，但**不动瞳孔层** | idle **看不到逐帧呼吸**（静态帧）|
| b. idle 逐帧闭眼呼吸 | 把 idle 从静态态**改成逐帧态** | **恒重重绘** |

选 a 的**决定性理由在契约层**，不是美术偏好：

- `FrameAnimator.Tick` 是 `if (_state is null || Frames.Length==0) return false;` 然后 `var fps = Fps*Playback; if (fps<=0) return false;` —— **`fps<=0` 直接返回、永远只画第 0 帧**。这是"静态态"的**定义本身**。
- 若要让 idle 有**可见**呼吸，就**必须**把 idle 从 `staticStates` 里挪出去、声明 `fps>0` —— 这等于**改契约**（`idle/sit/sleep 必须 fps=0` 是 v0.1.82 定下、UiProbe 在守的），代价是 **idle 从"零重绘"变成"恒定重绘"**，与项目"**极致轻量**"（稳态内存红线 200MB、能不动就不动）直接冲突。
- **另一个硬约束**：idle 有**瞳孔眼层**（`eyes_idle`）。若做逐帧呼吸而瞳孔层只有**单帧**，就会出现"身体在动、眼睛不动"的**错位抖动感**；要消除它又得把眼层也做成逐帧 —— 成本再翻倍。
- **结论**：本轮**不做可见呼吸**，idle 保持 `fps=0`。这是**取舍**不是遗漏；`fps=0` 是**合法**的静态声明。

> **可复用的判断**：当一个"体验增强"要**先改契约、再付持续的运行时代价**时，先问"这个代价是不是和项目的第一原则冲突"。冲突就**明确放弃并写下来**，而不是偷偷加上去。

### 17.3 决策：为什么「帧去重」断言**只盖新管线**，不全局化

T04 补的断言（在 `Program.cs` 的「v0.1.82 动画皮肤契约」项内）是：

> **凡 `fps>0` 的状态，其 N 帧像素的 SHA-256 唯一值必须 > 1**，否则判红（"静止帧冒充动画"）。

它**只作用在 `animated` 列表**（新管线 8 帧皮肤：`cat_pixel` / `cat_hd` / `phoebe_pixel` / `phoebe_hd` / `bead`）。**为什么不推广到全部皮肤？**

- **一分证据**：写断言前先跑了一遍**全皮肤唯一帧审计**（`_eval/t04/frame_uniqueness.py`）。结果：新管线 5 套**全绿**；**只有 `pixel.sleep` 与 `soft.sleep` 是 `fps=1` 却 4 帧全同** —— 同样是"静止帧冒充动画"。
- **推广的后果**：若把断言推广到旧管线，会**立刻打红 `pixel.sleep` / `soft.sleep`**。
- **修复的后果**：要它们变绿，就得**改动这两套既有皮肤**的素材 —— 而本轮**硬约束是"不动这 8 套既有皮肤"**。
- **处置**：**刻意把作用域限定在新管线**，并把旧管线问题**记为本断言的"已知盲区"** —— 同时写进 **`Program.cs` 的断言注释** + **`CHANGELOG.md` v0.1.97 的「已知盲区」小节**，避免后人误以为断言已覆盖全部 9 套皮肤。

> **可复用的判断**：**"发现了一个同类缺陷" ≠ "应该顺手扩大断言把它抓出来"**。扩大断言会把"**未纳入范围的旧资产**"变成**红**，而修复它们往往超出本轮边界。正确做法是**保留断言的作用域、把盲区显式写进交付物** —— 让后来者知道"这里有洞、为什么有洞、怎么补"。

### 17.4 🔴 踩坑：**"提交/推送失败后盲目重试"会造成可观测的副作用**（本轮最值钱的教训）

本轮（及上一轮）出现了一个**行为层**的坑，比代码坑更值得记：

- **现象**：一次 `git push` 在沙箱里被 **SIGTERM 中断**（不是真的失败，是被环境掐了）。直觉是"失败了就重试一次"。
- **真实后果**：**重试本身不是零成本的** —— 重试会**重新触发凭据查找**，而凭据管理器在某些路径下会**弹出 GUI 窗口**，**出现在用户的屏幕上**。即：一次"我想让它成功"的重试，变成了**对用户桌面的可见骚扰**。
- **对照**：那次**没有被重试**的推送，事后看是**干净完成**的（`456abab..c0645d6`）。
- **教训**：
  1. **对一个"看起来失败"的操作，先判断"失败原因会不会下次自己消失"**。环境类失败（沙箱 SIGTERM、网络被拦）**重试大概率还是同样的结果**，甚至**放大副作用**。
  2. **副作用不一定在"操作本身"，可能在"重试的路径"上**（凭据查找 → 弹窗）。**评估一次重试，要评估它的整条副作用链，而不只是它想达成的那个目标。**
  3. **能不动手就不动手的前提，是先确认"不动手不会更糟"**。这次确认的依据是：**未被重试的那次已成功**。
- **衍生政策**：本轮起**禁止 `git push`**（改为只本地提交、由发布链统一推送），正是这条教训的制度化。

> **可复用的判断**：**"重试"不是免费的**。默认行为应是"**先查清失败的因果，再决定要不要重试**"；对**环境导致**的失败，尤其要警惕"重试 = 放大副作用"。

### 17.5 方法论

1. **体验增强要过"契约 × 持续代价"两道闸**：先改契约、再付恒定运行时代价的增强，若与第一原则（极致轻量）冲突，就**明确放弃并写下来**（17.2）。
2. **断言的作用域要"写下来"，而不是"想当然"**：发现同类缺陷 ≠ 扩大断言；扩大会把范围外的旧资产变红。**保留作用域 + 显式标注盲区**才是对的（17.3）。
3. **"重试"要当作有副作用的操作来评估**：先查因，再决定；环境类失败尤其别盲重试（17.4）。
4. **补断言要有"故障注入"作证**：本轮把 `walk` 掏成全同帧 → 断言**确实判红**（`bead.walk 标 fps=7 但 8 帧像素全同`），再还原 —— 证明断言**真的会抓**，而不是"加了但从没触发过"。
5. **本轮仍是"运行时零改动"**：`SpriteAtlas` / `PetRenderer` / `Core` 一行未动，改动全在**构建期素材**与**测试**。

---

## 十八、v0.1.98（2026-10-14）：数据根与程序根分离 —— 把一条"必须记住"的红线变成"结构上不可能违反"

### 18.1 问题：用户数据长在构建产物里
`Paths.Root = AppContext.BaseDirectory`，于是 `data/` 落在 `bin/.../win-x64/data/`。而「删 bin 清体积」是本项目
**真的会做**的动作（v0.1.97 一轮就清了 ≈460 MB），它和「`data/` 里有好感度 score + config」直接冲突 →
只能定一条红线：**删 `bin/` 前必须先备份 `data/`**。2026-10-13 那次清理确实**先备份后删**（4 个文件 md5 逐条校验），
但这类红线的可靠性上限就是"操作者当次有没有记住"。

**判断**：与其加固提醒，不如**消除耦合** —— 让构建产物里**根本不存在**用户数据。

### 18.2 方案：程序根 / 数据根分离（而不是"把 data 挪个地方"）
- **`<程序根>`**（exe 目录）：只放**随包分发的只读内容** —— `plugins/`、`neko-assets/`、`builtins.lock.json`。**语义与位置都没动**。
- **`<数据根>`**：**唯一可写**位置 —— `config.json`、`logs/`、`plugins/<id>/`。
- 解析顺序：`NEKO_DATA_DIR` 环境变量 → `portable.flag` 便携模式（程序根 `data/`）→ 默认 `%AppData%\Neko`。

### 18.3 四个取舍，以及为什么这么选
| 决策 | 选择 | 理由 / 被否掉的方案 |
|---|---|---|
| 默认位置 | **`%AppData%\Neko`**（Roaming） | 否掉 `%LocalAppData%`：MSI 的 `INSTALLFOLDER` 恰好就是 `LocalAppDataFolder\Neko`，数据放那儿会与**安装目录重合**（卸载混在一起 + 概念混乱）。否掉"继续放程序根"：等于不解决问题。 |
| 旧数据怎么办 | **复制，不移动；不删源** | 移动会造出一个"中断即两边都不完整"的窗口。复制是可逆、可重复的；代价是旧目录会留着 —— 于是用 `MOVED-TO-APPDATA.txt` 承担"告知它已不是活动数据"的职责，**而不是靠删**。 |
| 便携性 | 保留 **`portable.flag`** 开关 | 绿色版是这类软件的常规诉求。用**显式开关**而非"程序根有 `data/` 就算便携"，是为了避免迁完误判：迁移不删源，若拿"目录存在"当判据，迁完会一直走便携分支。 |
| `logs/` | **不迁移** | 属可弃诊断信息，且可能很大 —— 首次启动复制它会拖慢冷启动（红线 <2.0s）。 |

### 18.4 踩坑：`Paths` 是静态初始化，环境变量必须"抢在第一次访问之前"
`Paths.Data` 是 `static readonly` —— **第一次被触碰就定死**。所以 `NEKO_DATA_DIR` 必须在**任何** `Paths.*` 访问之前设置：
`tests/UiProbe` 的 `Main` 把它放在 `Console.SetOut(...)` 之后、`Paths.EnsureCreated()` **之前**，
否则探针会先把真实数据根建出来、再改已无意义。
（同类细节：`Data` 与 `DataRootKind` 由**同一个静态元组**一次性解析，避免"位置"和"来源描述"不一致。）

### 18.5 发现：测试原来一直在写"配置"
改完才意识到 `tests/UiProbe` 末尾的「皮肤还原」会 `ConfigManager.Save(Paths.ConfigFile, …)`：在旧布局下它写的是
`bin/.../data/config.json`（"反正是构建产物，无所谓"）；**一旦数据根改到 `%AppData%`，同一行代码就变成在写用户的真实配置**。
→ 这是"**位置变更把无害操作变成有害操作**"的典型样本：**改数据落点时必须把所有写方一起审一遍**（主程序、测试、CI、安装器），
不能只看主程序。本轮同时给 `tools/ci.py` 的测试进程注入同名变量，产物收进 `bin_ci_logs/_testdata/`。

### 18.6 验证方式：拿"自己造的真值"跑 4 条分支
旧 `data/` 已在上一轮清理时删掉，所以**自造**夹具（config + 好感度 `4242` + `host/activity.json` + 一份 `logs/`）端到端跑：
1. 首启 → AppData 收到 3 个文件、好感度 `4242` **原样保留**、旧目录**未删**且多出 `MOVED-TO-APPDATA.txt`、`logs/` **未复制**；
2. 再启 → 迁移行计数**仍为 1**（不重复迁移），配置与好感度未被重置；
3. 放 `portable.flag` → 数据写回程序根，`%AppData%` **未被写入**；
4. `NEKO_DATA_DIR=<temp>` → 数据只落在指定目录。

**"隔离有效"的前后对比证据**：两次完整 CI 跑完后，真实 `%AppData%\Neko\config.json` 的 mtime **仍停在恢复时刻**
（未被探针写入），而 UiProbe 的临时数据根里出现了它自己的 `config.json` / `logs/` / `plugins/`。
沿用本项目老规矩：**假绿要故障注入证伪、正向用例要证明没被杀掉** —— 这里验的是"**不写真实数据**"这个不变式的正向用例。

### 18.7 三条教训
1. **红线的可靠性上限 = 操作者当次的记性。** 能用结构消除的，别只加提醒。
2. **改数据落点 ≠ 改一个常量。** 位置变了，**同一行代码的性质可能从"无害"变"有害"** —— 写方必须全量过一遍。
3. **迁移要可逆**：复制 + 标记 + 不删源，比"干净地移动"更适合"用户数据"这类不可承受丢失的东西。

---

## 十九、v0.2.0（2026-10-14）：把"用户找不到数据"补上，顺便给视觉改动装上可复现的取景器

### 19.1 问题不是"功能缺"，是"上一轮改动的后果没人收"
v0.1.98 把用户数据从 exe 同级 `data/` 搬到了 `%AppData%\Neko`（见 §十八）。**架构上是对的**，
但**界面上一夜之间没有任何地方告诉用户数据去哪了** —— 好感度、配置、日志的入口全部消失。
这类"改动本身正确、但留下了信息真空"的债，比功能缺口更容易被忽略：没有报错、没有断言会红。

**判断**：补一个卡片就够，但**位置要选对**。放「关于」页而不是「通用」页 —— 它和版本号、检查更新同属
"这台机器上这个安装的状态"，而「通用」页放的是用户会**经常调**的开关。

### 19.2 两个实现细节，都是"会翻车的地方"
| 细节 | 处理 | 为什么 |
|---|---|---|
| 打开目录 | `Paths.EnsureCreated()` 后 `Process.Start(new ProcessStartInfo { FileName = Paths.Data, UseShellExecute = true })`，**整段 try/catch，失败只弹提示** | 目录可能还没被建出来；且设置页**不该因为打不开一个文件夹而崩**。用 `UseShellExecute` 交给 shell 打开，避开了 `explorer.exe` 传参的引号/空格坑 |
| 显示路径 | 从 `Paths.Data` **动态取**，另标来源（默认 / 便携 / `NEKO_DATA_DIR`） | 写死示例路径会立刻说谎 —— 便携模式与环境变量覆盖下根本不是那个值 |

### 19.3 断言要断"绑定"，不要断"文案"
新 UiProbe 断言**只做三件事**：① 页面里出现的必须是**当前 `Paths.Data` 的真实值**；
② 有「数据目录」标题；③ 有「打开目录」按钮。
**刻意不断**"来源说明的文案对不对" —— 那种断言会退化成把 UI 文案抄一遍进测试（改了文案就红、
文案本身写错却照样绿），是**同义反复**而不是验证。文案对不对，交给**出图给人看**。

### 19.4 视觉改动装上"取景器"：`UIPROBE_SHOT`
本项目早就定了规矩：**视觉改动要出图给灯灯看，别自评"好看"**。但此前每次都要现搭抓图脚本。
本版把这条路固化成一个钩子：`UIPROBE_SHOT=<目录> ./UiProbe.exe` → 渲染 `settings-about.png`。

**一次真实的失败尝试（值得记）**：我本想"跑 UiProbe → 它会留下设置窗 → 再用 `tools/capture_pet.py` 截屏"。
实测**不成立**：UiProbe 结尾用 `Environment.Exit` 强制退出（注释里写着原因：WPF 窗口 + 全局钩子会留前台线程，
正常 return 不保证进程结束），所以进程一结束窗口就没了，`capture_pet.py` 直接报"未找到进程"。
⇒ 改为**离屏渲染**（`RenderTargetBitmap` + 用窗口 `BgRoot` 底色打底再画 `VisualBrush(content)`）：
不依赖窗口可见、不依赖合成输入、**CI 里同样跑得出来**。
注意它是**辅助产物、不是断言** —— 失败只打印一行，不影响闸门结论。

### 19.5 教训
1. **上一轮架构改动的"信息真空"要主动收**：改动正确 ≠ 用户体验完整；问一句"用户现在从哪找到它"。
2. **断言绑定、别断言文案**：断"取的值对不对"能抓到真 bug，断"文案对不对"只会把文案抄进测试。
3. **能固化成钩子的规矩，别再靠每次现搭**：出图审阅这条路，一次投入换长期可复现。

---

## 二十、v0.2.1（2026-10-14）：把一笔"记了账的债"还掉 —— 盲区不是"以后再说"，是"没人会回来"

### 20.1 这笔债是什么
v0.1.97 加了「帧去重」断言（凡 `fps>0` 的状态，帧像素唯一值必须 > 1），但**刻意只盖新管线 5 套皮肤**：
因为旧管线里 `pixel.sleep` / `soft.sleep` 是 `fps=1` 却 4 帧全同 —— 纳入作用域会**立刻判红**，
而修它们要动既有皮肤，当时超出任务范围。于是记为「已知盲区」，写进了 CHANGELOG 与代码注释。

**当时的决定本身是诚实的**（没假装覆盖全部）。但盲区有个通病：**下一个读注释的人会把它当"现状"而不是"待办"**。
本轮实测确认了它的实际范围，并把它关掉。

### 20.2 先量再改：不靠印象猜"还有几处"
写了个一次性测量脚本，**两种 layout 都覆盖**（`sheet` 按 `frameWidth` 切、`files` 逐帧读），
对每套皮肤每个状态算帧像素 SHA-256 唯一值：

| 结果 | 数量 |
|---|---|
| `fps > 0` 且唯一帧 ≤ 1 的状态 | **2 个**：`pixel.sleep`（sheet，4 帧全同）、`soft.sleep`（files，4 帧全同） |
| 其余 | 全部 ≥ 2 唯一帧 |

顺带发现一处**我差点误判的坑**：首轮测量时 `soft` 全部报「无图」—— 因为 `soft` 是 `layout: "files"`
（逐帧独立 PNG），我按 `sheet` 逻辑找 `neko_sleep.png` 自然找不到。
⇒ **测量脚本本身也会说谎**；结论必须基于两类 layout 都跑过的版本。

### 20.3 修法是"改声明"，不是"造动画"
两个选择：① 给这 2 个状态**做**出真动画；② 承认它们本来就是静态，**把 `fps` 改成 `0`**。选 ②，理由：
- 4 帧**像素完全相同** ⇒ 标 `fps>0` 是**假声明**，视觉上从来没有动画可言；
- `docs/skin-development-guide.md` §3 的表格**本来就写着** `=0` 用于 idle/sit/sleep ⇒ 这两套皮肤是**违反自家契约**；
- 改成 `0` 后**视觉零变化**，且省掉睡眠时每秒一次的无谓重绘（与"空闲 CPU <0.5%"同向）。
另把 `SkinRegistry` 的**内置回退皮肤**同名状态一起改 —— 否则回退与实际皮肤会悄悄漂移（"同一语义的两份拷贝"最容易烂）。

### 20.4 断言全局化：分成两条，而不是把老断言扩成一条
关键判断：**不能**简单把 `animated`（5 套新管线皮肤）数组换成"全部皮肤"就完事 ——
那条断言同时钉了 `frames==8`、静态态必须 `fps=0`、帧尺寸、`thumb.png` 等**结构性契约**，
而旧管线的约定本来就不同（例如 `pixel.idle` 是 `fps=4`，不是 0）。
⇒ 新增**一条只断一个不变量的独立断言**：「任何皮肤、任何 `fps>0` 的状态，帧像素唯一值必须 > 1」。
它不碰结构、只碰"动画真不真"，因此能安全覆盖旧管线。
断言还会打印覆盖行 `覆盖 9 套皮肤 / 64 个 fps>0 状态` —— **让"覆盖了多少"本身可被看见**，
避免它退化成一个"看起来在跑、其实没盖到"的断言。

### 20.5 故障注入：把"断言有效"证出来，而不是宣称
把 `pixel` 的 `sleep` 改回 `fps:1` → 重建 → 跑 UiProbe：
```
FAIL: v0.1.74：皮肤（…pixel/soft 元数据与旧硬编码一致） -> Exception: pixel.sleep.fps 应 0，实际 1
FAIL: v0.2.1：全部皮肤「静止帧冒充动画」不变量（fps>0 ⇒ 唯一帧 > 1）
      -> Exception: 静止帧冒充动画：pixel.sleep(fps=1, 4 帧全同)
=== 2 项失败 ===
```
还原后全绿。**两侧都验到了**：新断言会抓（假绿被证伪），合法状态不误杀（正向用例通过）。
另一个旁证：**本次改动本身就是被既有断言抓出来的**（`pixel.sleep.fps 应 1，实际 0`）——
说明 v0.1.74 那条"快照式"断言确实在干活（它的意图是"防提取时抄错"，所以值要跟着**两侧一起**改）。

### 20.6 教训
1. **盲区要"有主"**：写进文档 ≠ 会有人回来处理；能当轮关掉的就别留到"以后"。
2. **改不动结构就别硬塞**：新不变量独立成一条断言，比把老断言改宽更安全（别让管结构的断言兼职管语义）。
3. **"两处同义数据"必须同步改**：`skin.json` 与 `SkinRegistry` 内置回退是同一语义的两份拷贝，只改一处＝埋漂移。
4. **测量脚本也会说谎**：`layout` 有两种，脚本只认一种时报出来的"全都不合格"，其实是它自己没看对。

---

## 二十一、v0.2.2（2026-10-14）：一个"从来没成功过"的功能 —— 静默失败要付的账

### 21.1 起因：本来只是去"验证"一下
灯灯让我"实机验证自动更新"（因为 v0.1.98/99 终于真有远端新版本了，是历史上第一次远端比本地新）。
预期是"确认版本比较方向正确"，结果**第一步就塌了**：这个功能**从上线起就没有成功过一次**。

### 21.2 定位：一个反直觉的组合
| 检查 | 结果 |
|---|---|
| 应用日志 `[更新]` | `[WARN] 查询失败：HTTP 404`（两次真机实跑都是） |
| `.../releases/latest`、`.../tags` | **都 404 `Not Found Project`** |
| 未登录网页 `gitee.com/gddgssdfg/neko` | **403** |
| 对照公开仓库 `oschina/git-osc` | **200** |
| `git push` 到同一仓库 | **成功** |

**两个信号可以同时为真**：push 成功（凭据有效、仓库存在）与 API 404（仓库对**未登录访问者**不可见）。
我一开始的直觉是"是不是 slug 写错了"—— 被 push 成功这一条否掉了。
**教训：不要用一个信号去推断另一个信号**；要找一个**能从外部独立验证**的对照（这里是对照公开仓库的 200）。

### 21.3 真正的病根：为了"不打扰"而牺牲了"可发现"
`FetchAsync` 任何失败都只 `Log.Warn` 返回 null，UI 上则统一显示"请稍后重试"。
设计意图是好的（**没网不该崩宿主、不该弹模态打扰**），但它同时造成了两个后果：
1. **坏着没人知道** —— 只有翻日志才看得到一行 404；
2. **提示误导** —— 把"仓库不可见"（配置性、**重试一万次也不会成功**）伪装成"稍后重试"（暂时性）。

⇒ **静默失败不是免费的**：省下的"打扰"，是用"可发现性"付的账。
本项目已有同源教训（§十八 教训 1：能改结构的别只加提醒；本项目更早的"`catch{}` 吞掉真实失败"），
这次是它在**网络层**的同一个形态。

### 21.4 修法：为什么是"两级数据源"而不是二选一
第二道坎：`releases/latest` 返回的是 **Release 对象**，而本项目发布流程**只打 tag** ⇒
就算仓库转公开，`releases/latest` 依然 404。两个盲区叠在一起，所以只改一个不够。

| 方案 | 评价 |
|---|---|
| 只保留 `releases/latest` + 要求"每次发版建 Release" | 信息最全（说明/下载链接），但给发布流程**新增一个必须记住的步骤** —— 本项目刚吃过"靠人记住"的亏 |
| 只改成 `/tags` | "打 tag" 就够，**与现有 SOP 完全一致**；但丢掉了更新说明与下载页 |
| **两级：Releases 优先 → tag 回落**（选它） | 当前只有 tag 也能**立刻工作**；将来若建了 Release，**自动升级**为带说明/下载链接的形态。代价是多一次请求（每天一次，可忽略） |

配套细节：Releases 那一步失败要**静默**（新增 `quietOnFailure`）—— 否则"没有 Release"这个**常态**
会每天伪装成故障刷一条警告，等于自己制造噪声。

### 21.5 解析：不依赖接口返回顺序
Gitee `/tags` 返回数组（`[{name, commit:{sha,date}, ...}]`，结构按官方文档核对），
取最大版本时**逐个按数字段比较**，不靠顺序、不靠字符串序 ——
字符串序会把 `0.1.9` 排在 `0.1.10` 之后，这是版本号处理的经典坑（本项目自己刚定下的"patch ≤ 99"规则，
某种意义上也是在给这个坑划边界）。顺手把比较逻辑抽成 `CompareVersions`，与 `IsNewer` 共用，
避免"同一套逻辑写两遍、改一处漏一处"。

### 21.6 失败提示要"可操作"
把结论直接写进提示：**网络不可用，或发布仓库当前不可见（只支持公开仓库）**，并给一个「打开仓库页」的出口
（`RepoUrl` 永远可达，不依赖是否有 Release/tag）。**配置性错误必须与暂时性错误分开** ——
否则用户会对着一个永远不会成功的按钮反复点。

### 21.7 转公开前的安全检查（顺手做了，因为"公开"是不可逆的）
既然修完必须把仓库转公开才能生效，那就先扫一遍**会公开出去什么**：
- `.workbuddy/`（内部笔记 / handoff / memory）—— **未被跟踪**（0 文件）✅
- `_eval/`（含本轮截图、验证报告）—— 被 `/_*` 忽略 ✅
- 历史上**是否提交过**密钥类文件（`.env` / `secret` / `token` / `.pem` / `.pfx` / `*.key`）——
  `git log --all --diff-filter=A` 全历史扫描：**无** ✅
- 被跟踪文件里的**个人绝对路径**（`C:\Users\Lantern` / `G:\lantetn-li`）—— **0 处** ✅
- `.gitignore` 已排除 `data/`、`.env`、`*.key` ✅
- 仓库规模：410 个跟踪文件、`.git` 17MB（`_eval/` 149MB 不进库）

### 21.8 教训
1. **"验证某个功能"可能直接推翻它** —— 别把它当成走流程的确认；先想清楚"失败的证据会是什么样"。
2. **两个信号可以同时为真**：push 成功 ≠ API 可达。找**外部对照**（公开仓库 200）来定性。
3. **静默失败要付账**：省下的打扰，用可发现性来还。要静默的是"常态失败"，不是"配置错误"。
4. **不可逆动作（公开仓库）之前，先量"会公开什么"** —— 用 `git log --all --diff-filter=A` 扫全历史，而不是只看当前工作区。
5. **多级数据源要配"非末级静默"**，否则回落机制本身会变成噪声源。

---

## 二十二、v0.2.3（2026-10-14）：体积预算复核 —— 以及"这条红线一直没有量尺"这件事

### 22.1 起因：余量只剩 ~5%，而且红线本身没工具
红线是「体积 <180MB / 常驻 <200MB」，实测 exe 163.6 MiB（171.5 MB）、常驻 185.2 MiB
⇒ 体积余量只剩 ~5%，是几条红线里最紧的。**但更糟的是：这条红线一直没有量尺** ——
「冷启动 <2.0s」在文档里躺了很久，却没有任何工具能测它，只能靠感觉说"挺快的"。
**没有量尺的红线不是红线，是愿望。**

### 22.2 先造量尺，再谈取舍
新增 `tools/measure_startup.py`，口径 = **进程创建 → 宠物窗口出现**。
- 为什么不量"进程启动"：单文件自包含 + WPF 的耗时大头（运行时装载 / JIT / WPF 初始化 / 插件加载）
  都在窗口出现**之前**；
- 为什么不量"某个日志行"：日志级别默认 warn，Info 不落盘，且写日志早于窗口渲染完成；
- **必须隔离数据根**（`NEKO_DATA_DIR` 指向临时目录）—— 否则要么污染用户数据，要么被首启引导窗影响；
- **必须 `--dev`**，否则量到的第一个窗口是**引导窗**而不是宠物窗；
- **每轮必须杀干净**：否则托盘图标与窗口堆积，后续轮次的枚举会认错目标；
- **先预热 1 轮**：首次运行的冷文件缓存会显著偏慢（实测 1.398s vs 稳态 0.66s）。

### 22.3 一条测量纪律：**别在跑构建的时候测时间**
第一次量 `lean` 变体得到中位 1.058s，比其它变体慢 0.36s —— 差点导出"裁卫星资源会拖慢启动"的结论。
真因是我**同时**在跑另一个 `dotnet publish`，MSBuild 在抢磁盘与 CPU。
等环境静默后复测：中位 **0.730s**（与只关 R2R 的 0.698s 区间重叠 ⇒ 无实质差异）。
⇒ **时间类测量必须独占环境**；否则宁可不测，也别拿被污染的数字下结论。

### 22.4 A/B 结果（同机实测）
| 变体 | exe | 常驻 WS | 冷启动中位 |
|---|---|---|---|
| R2R 开 + 全语言（旧） | 163.6 MiB | 185.2 MiB | 0.658s |
| 只关 R2R | 147.0 MiB | 174.9 MiB | 0.698s |
| **关 R2R + `en;zh-Hans`（新）** | **132.1 MiB** | **172.7 MiB** | **0.730s** |

**为什么"关 R2R"是双赢**：直觉上 R2R 是「用磁盘体积换启动速度」，但实测它**连内存也一起吃掉**
（native 映像在磁盘与内存里都更大）。而本项目的启动大头是文件 I/O + WPF 初始化，不是 JIT ——
冷缓存首启两者 1.398s vs 1.380s。⇒ 在红线余量 >1.2s 的前提下，这笔换算**明显划算**。

**为什么"收窄语言"省得比预期多**：拆解发现 **221 个 `*.resources.dll` 共 15.59 MiB**
（ru / ja / de / ko / fr / pl / it / es / pt-BR / cs / tr / zh-Hant…）。对一个只有中英双语的桌宠，这是纯浪费。

### 22.5 最重要的一次"幸好量了"
命令行传 `-p:SatelliteResourceLanguages=en%3Bzh-Hans`，体积从 147.0 掉到 **131.0 MiB** —— 数字很好看。
但我按惯例去做**正面验证**（非单文件发布后数 `resources.dll`）：**0 个**。
即 `%3B` **没有被解码**，把**所有**卫星资源（含 `zh-Hans`）一起裁了 ⇒
**WPF 框架的 MessageBox 会显示英文的 "OK/Cancel" 而不是「确定/取消」**。
改成写进 csproj（XML 里 `;` 无需转义）后：exe 132.1 MiB、`zh-Hans/PresentationFramework.resources.dll` **在**。

> **"更小的体积"这种更漂亮的数字最容易骗过验收** —— 这里真正该看的是**反向指标**：
> "少了 1.1 MiB，是不是把该留的东西也一起丢了？" **漂亮的数字必须配一个正面验证**，不能只看方向。

### 22.6 三个"实测无效"也是结论
- `InvariantGlobalization=true` → **0 收益**：Windows 上 .NET 用**系统 ICU**，本来就没有 `icudt.dat` 可省。
  （这解释了为什么"网上说能省 30MB"在本项目不成立。）
- `DebugType=none` → **0 收益**，且该测量受**增量构建**干扰（embedded pdb 被复用，根本没重建）⇒ **不确定**。
  但即使有收益，去掉符号也会削弱本项目"日志要能定位"的底线 ⇒ **维持 `embedded`**，
  并把这份"不确定"如实写进 SOP（不假装它已被证明无用）。
- `PublishTrimmed` → **WPF 不支持**，没试（不假装试过）。
**把"试了没用"写下来，和把"试了有用"写下来同等重要** —— 否则下一个人会再花一次时间。

### 22.7 教训
1. **没有量尺的红线不是红线，是愿望**：先补工具，再谈优化。
2. **时间类测量必须独占环境**：并发构建会把 0.73s 污染成 1.06s，差点导出反向结论。
3. **更漂亮的数字必须配正面验证**：体积变小可能是"把该留的东西删了"。
4. **"两处同义"的坑在配置里也有**：同一属性写在 csproj 与走命令行行为不同（转义差异）→ 只留一处。
5. **把"试了没用"也记下来**：它同样是结论，能省掉后面每个人的一次重复劳动。

---

## 二十三、发布形态注意

- **🔴 版本号上限规则（2026-10-14，灯灯定）**：patch **不超过 99** —— `v0.1.99` 是 `0.1.x` 最后一个版本，
  下一个版本**直接跳 `v0.2.0`**，不出现 `v0.1.100`。
  **落地方式不是"写提醒"而是"写进闸门"**：`tools/check_version.py` 新增 `version_rule_error()`，
  patch > 99 直接判不一致（退出码 1）并打印应改成的版本号。理由与验证见 `CHANGELOG.md` 0.1.99。
  —— 与本项目 §十八 的第 1 条教训同源：**能用结构/工具消除的，别只加提醒。**
- `TodoList` 的磁盘格式由「裸数组」改为「JSON 字符串」（为绕开宿主 STJ 反射）。**升级兼容已验证；降级不兼容**（旧版读 JSON 字符串会失败 → 显示空清单，数据文件本身未丢）。
- 发布完整流程见 [`release-sop.md`](release-sop.md)。
- **用户数据位置（v0.1.98 起）**：默认 `%AppData%\Neko`（详见 §十八）——**打包/清场时不要再假设数据在程序根**。

---

## 附录：2026-09-11 项目审计（已收口）

> 原 `project-audit-2026-09-11.md`（三人独立审计交叉验证）的压缩存档。**批次 A/B/C 全部收口**。

**结论**：代码与测试工程健康（0 警告 0 错误、依赖精简、仓库卫生合格）。真债务在三处：① 健壮性缺口（插件后台异常能杀进程）② 可观测性为零（Release 无日志）③ 文档孤岛（权威文档停在 60 版前）。

| 批次 | 关键问题（带证据） | 处置 |
|---|---|---|
| **A** ✅ | P0-1 插件后台异常杀进程（`SchedulerImpl` 裸 Timer 回调）/ P0-2 Release 零日志（全 `Debug.WriteLine`）/ P0-3 权限「每次校验」未实现（`RequirePermission` 零调用）+ P1-7 手册版本漂移 / P1-9 `Scheduler.Cron` 说谎 | v0.1.61：异常隔离+熔断、`Core/Log.cs`、改文档诚实化（加载时校验）、删 `Cron`、手册补 `Work` |
| **B** ✅ | P1-4 安装器双轨陈旧（Inno 停 v0.1.42）/ P1-5 CHANGELOG 缺 v0.1.54/55 / P1-6 README 设置窗章节不符（4 tab→实为 5 section）/ P1-8 约 33 处空 catch | README 重写 + 发布 SOP + CHANGELOG 补段 + 空 catch 分类处理 |
| **C** ✅ | P2-11 超长文件（`PetWindow.cs` 1405 行等）/ P2-12 无 CI / P1-10 热卸载可能泄漏 ALC | `PetWindow`/`SettingsWindow` 拆 partial；加 CI 校验；ALC 退订（见 §六） |
| **P3** | 死/预留 API（`AppWake`/`Tick` 等零 Emit）/ README 目录结构与脱离条件不全 / 版本号硬编码 | 保留为「已知仍未接线」，见 `plugin-development-guide.md` 附录 A.1 |

**经复核确认无问题的维度**：架构分层无循环依赖 / `IDisposable` 成对 / 配置容错回退 / 依赖健康（3 个 NuGet 包）/ `.env` 已排除 / 测试可移植（无硬编码路径）。

---

*文档版本：v2.0（按主题归并 v0.1.0–v0.1.54 流水；v0.1.79+ 过程与全部踩坑教训保留）｜ 审计报告并入附录。*
