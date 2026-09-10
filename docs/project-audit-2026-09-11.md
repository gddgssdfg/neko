# Neko v0.1.60 项目健康度审计报告

> 审计日期：2026-09-11 ｜ 方式：三人独立审计交叉验证（编排者 + 软件工程师 + QA）
> 每一条均带**文件:行号 + 代码证据**，无推测项。已剔除经复核不成立的误报。

---

## 结论摘要

**代码与测试工程本身是健康的**：构建 0 警告 0 错误、测试可移植、依赖精简（仅 3 个 NuGet 包，版本新无冲突）、仓库卫生合格。

**真正的债务在三个地方**：
1. **健壮性缺口** —— 插件的后台异常能杀掉整个进程（违反你自己定的架构铁律）
2. **可观测性为零** —— Release 版没有任何日志，出问题无法排查
3. **文档孤岛** —— 权威文档停在 60 个版本前，且发布链路双轨僵化

---

## P0（建议优先修，影响正确性/稳定性承诺）

### 1. 插件后台异常会直接终止宿主进程
**位置**：`src/DesktopPet.App/Services/SchedulerImpl.cs:12 / :20 / :28`

```csharp
var h = new TimerHandle(new Timer(_ => callback(), null, intervalMs, intervalMs));
```

**问题**：插件通过 `Scheduler.Every()` 注册的回调跑在线程池线程上，**三处全部裸调、无 try/catch**。.NET 中 Timer 回调抛未处理异常会**直接终止进程**。而全局兜底 `App.xaml.cs:24` 只挂了 `DispatcherUnhandledException`（**仅覆盖 UI 线程**），后台线程异常无任何拦截。

**影响**：**任何一个插件的定时任务抛异常 → 整个 Neko 被杀掉**。这正面违反架构铁律第 3 条「单插件崩溃不拖垮宿主」。
**改法**：三处回调统一包 try/catch → 记日志（配合 P0-2）→ 连续失败 N 次可考虑自动禁用该插件。

---

### 2. Release 版没有日志（可观测性为零）
**位置**：全项目日志调用点 —— `App.xaml.cs:64`、`Config.cs:97/111`、`PluginContextImpl.cs:63`、`PluginHost.cs:89`、`SystemImpl.cs:14/20/26` 等

**问题**：除 `App.xaml.cs:26` 写 `errors.log`（**且只记未处理异常**）外，**所有运行日志都是 `Debug.WriteLine`**。Release 构建下 `Debug` 调用被编译器移除，**一个字节都不留**。

**影响**：这是过去反复「真机偶发崩溃只能靠猜」的根因。用户装机后出问题，你手上**没有任何线索**——`errors.log` 里可能什么都没有。
**改法**：加轻量文件日志（`data/logs/`，按天滚动、限大小），把关键路径（插件加载失败/配置解析失败/钩子安装失败/定时器异常）从 `Debug` 改为写文件。带级别与开关（默认只记 Warning+，可配置开 Debug）。

---

### 3. 插件权限「每次校验」未实现
**位置**：`src/DesktopPet.Runtime/PluginLoader.cs:194` `GrantedPermissions = declared,`

**问题**：全仓搜索 `RequirePermission` / `CheckPermission` / `HasPermission` → **零调用**。`PetApiImpl` 每个方法都是直接实现，无权限守卫。权限只在**加载时**判一次（拒绝高风险声明）。

**影响**：与架构铁律第 3 条「manifest 声明 → 宿主授予 → **每次校验**」有差距。当前"加载时拦截高风险权限"是第一道防线（有效），但已加载插件可在运行时调用全部已声明能力，**能力边界不可收紧**（比如想给某插件临时降权做不到）。
**改法**：在两个选择中明确定位 —— ①接受现状，把铁律改为「加载时授予」并写清楚；②在 API 实现层加 `EnsurePermission(scopeId, perm)` 守卫。**建议先做①**（诚实），等真有插件生态再上②。

---

## P1（应该修，影响可用性/可维护性）

### 4. 安装器双轨并存且双双陈旧
**位置**：`installer/neko.iss:6,10`

```
#define MyAppVersion "0.1.42"
#define SourceDir "..\publish\v0.1.42"
```

**问题**：Inno 脚本停在 **v0.1.42**（落后 18 版）；`installer/` 内 WiX 产物最新只到 `NekoSetup-0.1.53.msi`。**两套安装器并存、各停在不同版本，没有任何一个能出 v0.1.60 的包**。
**影响**：无法从源码一键得到当前版安装包，发布流程实质中断。
**改法**：二选一 —— 废弃 Inno 统一 WiX（推荐，`build_setup.py` 是脚本化的），或把 `.iss` 版本参数化；并加「版本号必填校验」防再次漂移。

### 5. CHANGELOG 缺 v0.1.54 / v0.1.55 两段（已复核确认）
**位置**：`CHANGELOG.md`

**证据**：`grep -c "^## \[0.1.54\]\|^## \[0.1.55\]"` = **0**；版本头从 `[0.1.53]` 直接跳到 `[0.1.56]`。两版仅在 `[0.1.56]` 正文里被顺带提及。
**影响**：违反你自己定的「改版本必须同步本文件」，且这两版正是**图标问题的高发期**（v0.1.55 抠白底被否、v0.1.56 改保留白底）——追溯时断档。
**改法**：从 `dev-log.md` 回溯补写两段。

### 6. README 设置窗口章节与实现完全不符
**位置**：`README.md:88-95`

| README 写的 | 实际（`SettingsWindow.cs:135-136`） |
|---|---|
| 「v0.1.5 重做，**4 tab**」 | **5 section** |
| 模型 / 窗口 / 行为 / 插件 | **通用 / 猫咪 / 输入 / 插件 / 关于** |

**问题**：分组名与结构全不同（模型→猫咪、窗口→通用、行为→输入）。
**影响**：用户按文档找不到设置项。
**改法**：整节重写为实际 5 section，补游戏模式/外观/快捷键说明。

### 7. 插件开发手册版本与枚举漂移（**这是你定的"单一权威源"，漂移是重罪**）
**位置**：`docs/plugin-authoring-guide.md:6,8,303,309`

| 项 | 手册写的 | 实际 |
|---|---|---|
| 对照版本 | **v0.1.0** | v0.1.60（落后 60 版） |
| `PetState` 枚举 | 8 个值 | **9 个**（`Enums.cs:25` 漏了 v0.1.48 新增的 `Work`） |
| 雪碧图动画组 | 8 组 | 9 组 |

**影响**：手册开头自称「贴给任意 AI 即可生成插件」——版本号停 60 版前 + 枚举缺项，会诱导 AI 生成过时代码。
**改法**：头部版本改 v0.1.60，补 `Work` 状态。**并把"手册版本号"纳入发布 SOP 的检查项**。

### 8. 约 33 处空 catch 静默吞异常
**位置**：`PetWindow.cs:347/392/580/626/656/676/686/704/1186/1193/1202/1211/1220`（其中 13 处是 `try{ConfigManager.Save}catch{}`）、`ChatScheduler.cs:72`、`ActivityTracker.cs:91`、`PluginLoader.cs:60` 等

**问题**：真实异常（磁盘满/权限/序列化失败）被完全吞掉，无日志无提示。且 `ConfigManager.Save` 内部**已**有 try/`Debug.WriteLine`（`Config.cs:103-113`），外层再包一层是**纯冗余**。
**影响**：配置写失败用户无感知，静默丢设置。
**改法**：删除 `Save` 的 13 处冗余外层包装；其余按风险分类 —— 该记日志的记（配合 P0-2），该暴露的提示。

### 9. Scheduler.Cron 契约说谎
**位置**：`Services/SchedulerImpl.cs:27-33` vs `docs/plugin-authoring-guide.md:278`

**问题**：实现注释坦白「v1 简化：忽略 cron 表达式，退化为每分钟一次」，但手册把 `Cron(string expression, ...)` 列为**正式 API**。
**影响**：插件照手册写 `Cron("0 9 * * *")` 会得到「每分钟跑一次」的错误语义，**无任何警告**。
**改法**：二选一 —— 实现最小 cron 解析，或从接口删除 `Cron` 并改手册为 `Every/At` only。**建议删除**（诚实优于半成品）。

### 10. 插件热卸载可能真实泄漏 ALC
**位置**：`PluginHost.cs:180-190` + `PluginLoader.cs:57-62`

**问题**：`ScopedUiApi.UnregisterAll` 只清注册项，插件自身的事件订阅（如 `AffectionPlugin.cs:38 _unsubscribePetted`）依赖插件 `OnUnload` **手动退订**。一旦某插件漏退订，`PluginHost.Dispose`（L168-177）的 `catch{}` 吞掉后 `Context.Unload()` 仍执行，ALC 因残留引用**不回收且无检测**。
**影响**：反复 Enable/Disable 内存缓慢增长，与内存红线冲突。
**改法**：宿主侧集中登记每个插件的事件订阅令牌并**强制退订**（不信任插件自觉）；或加 ALC 回收断言测试。

---

## P2（可以改，工程整洁度）

### 11. 超长文件（可维护性红线）
`PetWindow.cs` **1405 行**、`SettingsWindow.cs` 818、`InputMirrorWindow.cs` 767、`UiProbe/Program.cs` 859。

`PetWindow` 单类混合了：状态机驱动 + 眼神追踪 + 边缘吸附 + 双模态 + 爱心粒子 + 抛物线物理 + 热键 + 径向菜单。本会话改 ⑦ 时就要同时触碰 `StartFall`/`UpdateEdgeSnap` 多处。
**建议**：按关注点拆 `partial`（`PetWindow.Physics.cs` / `.EyeTracking.cs` / `.AppMode.cs`）。

### 12. 无 CI / 无自动化触发
无 `.github/`，全项目无 yml。测试（LoaderTest/UiProbe）**只能手动 `dotnet run`**。
**影响**：回归靠人肉记忆 —— 这正是「无留痕回退」的温床。
**建议**：加 GitHub Actions（build + 两个测试 + **版本号一致性检查**）。测试本身已确认**可移植**（`tests/*.csproj` 用相对引用，无硬编码路径）。

### 13. 技术方案文档已严重偏离现实
**位置**：`docs/desktop-pet-plan.md`

| 项 | 文档写 | 实际 |
|---|---|---|
| 静止阈值 | 20 px/s（:65） | `RestVelocity=100.0`（`PetWindow.cs:147`，**差 5 倍**） |
| 径向菜单 | 8 槽位（:42） | 分页 >8 |
| 内置插件 | 4 个（:51-54） | **17 个** |
| 状态数 | 8 个（:26） | 9 个 |

**建议**：这是**立项稿**，偏离属正常 —— 加醒目过期声明（「⚠️ 历史设计稿，以现行代码为准」）比逐条改更有效。

### 14. 无发布 SOP 文档
发布流程靠记忆，散落在 dev-log 各处。
**建议**：新增 `docs/release-sop.md`：改 csproj 版本 → CHANGELOG → dev-log → build → 测试 → publish →（可选）出包 → git tag。

---

## P3（锦上添花）

| # | 问题 | 位置 | 说明 |
|---|---|---|---|
| 15 | 死/预留 API | `ICapabilityApi.cs:49-56` | `AppWake/AppSleep/ScreenChange/Tick` 常量**零 Emit**（全仓仅定义无广播）。手册已标「❌预留」，保持或删除 |
| 16 | README 目录结构不全 | `README.md:9-32` | 未列 `installer/`、`tools/` 其余生成脚本、`plugins/` 实际内容 |
| 17 | README 脱离条件不完整 | `README.md:84` | 写「鼠标远离 40px 脱离」；实际摸鱼模式下**不自动脱离**，只有拖拽才脱（`PetWindow.cs:961`） |
| 18 | 版本号硬编码散落 | `installer/neko.iss`、README 等 | 建议统一从 csproj 读或纳入 CI 校验 |
| 19 | installer 残留中间产物 | `installer/tmp_min3.wixpdb` 等 | 已被 .gitignore 忽略，仅磁盘卫生 |
| 20 | dev-log 未提交 | `git status` = `M docs/dev-log.md` | 建议提交保持工作树干净 |

---

## 经复核**确认无问题**的维度

- **架构分层**：Core 三接口定义清晰，`PluginHost.ScopedUiApi` 作用域隔离合理，**无循环依赖**（Core 不反向引用 App/Runtime）
- **IDisposable 成对**：7 个实现均成对
- **DispatcherTimer**：爱心粒子表有 `IsEnabled` 守卫、落体表有防叠加早返回
- **配置容错**：解析失败回退默认值，健壮
- **插件手册 API 签名**：`IScheduleHandle/IScheduler/ISystem/PetEvents` 与实际**逐行比对一致**（只有版本号与枚举遗漏）
- **依赖健康**：仅 3 个 NuGet 包，版本新、无冲突、无已知高危
- **仓库卫生**：`.env`（含 API Key）已正确排除，257 文件 / 1.1MB
- **测试可移植性**：无硬编码路径，可上 CI

---

## 建议的推进顺序

| 批次 | 内容 | 理由 |
|---|---|---|
| **批次 A** | P0-1 插件异常隔离 + P0-2 日志系统 + P0-3 权限定位 | 健壮性硬伤，且 1 依赖 2（异常要记日志） |
| **批次 B** | P1-4 安装器统一 + P1-5 CHANGELOG 补缺 + P1-6/7 文档修正 + P1-8 空 catch | 交付链路与文档诚信，成本低见效快 |
| **批次 C** | P2-12 CI + P2-11 拆文件 + P1-10 ALC 退订 | 长期工程健康 |
| **随时** | P3 各项 | 顺手清理 |
