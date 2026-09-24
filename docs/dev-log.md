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

## 十一、发布形态注意

- `TodoList` 的磁盘格式由「裸数组」改为「JSON 字符串」（为绕开宿主 STJ 反射）。**升级兼容已验证；降级不兼容**（旧版读 JSON 字符串会失败 → 显示空清单，数据文件本身未丢）。
- 发布完整流程见 [`release-sop.md`](release-sop.md)。

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
