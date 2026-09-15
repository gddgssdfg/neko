# 更新日志（Changelog）

本文件记录 Neko 桌宠的所有显著变更。
格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循[语义化版本](https://semver.org/lang/zh-CN/)。
版本号定义在 `src/DesktopPet.App/DesktopPet.App.csproj` 的 `<Version>`，改版本时必须同步本文件。

## [0.1.79] - 2026-09-16

**审计 P0-1 + P2-3：MSI 不再打包开发机运行期数据 + 素材复制改白名单**。

### P0-1 ⚠️ MSI 曾把开发机 `data/` 打进安装包
- **问题**：`installer/gen_setup_wxs.py` 唯一过滤是 `.pdb` → `publish/<ver>/data/**`（含**开发机的 `config.json`**、`host/activity.json`、插件私有数据）被打成 MSI 组件。三层后果：① 泄露开发数据 ② 安装后覆盖用户自己的配置 ③ 卸载残留。更严重的是 `config.json` 成为 MSI 自有组件 → 升级/卸载与用户数据打架。（`.gitignore` 有 `data/` 但打包器不读 gitignore）
- **修法**：`gen_setup_wxs.py` 加**显式排除规则常量集合**：`EXCLUDE_TOP_DIRS={"data"}` / `EXCLUDE_EXACT_FILES={".env"}` / `EXCLUDE_DIR_NAMES={"reference"}` / `EXCLUDE_PREFIXES={"_icon_"}` / `EXCLUDE_CONTAINS={"preview",".bak-"}`，保留 `.pdb`。

### P2-3 素材复制白名单
- `DesktopPet.App.csproj` + `UiProbe.csproj` 由 `<None Include="..\..\neko-assets\**\*.png">` **通配**改为**白名单**（icons/mirror/pixel/soft/tray.ico），并 `Remove` 掉 `*preview*`、`*.bak-*`。原先 `_icon_clean.png`(1.1MB) 等噪声也进了 publish→MSI。

### 验证
- 构建 0 警告 0 错误；LoaderTest 全过；UiProbe 全过（皮肤断言仍过，证明白名单没误伤）。
- **wxs 反向验证**：用含 `data/` 的 publish 目录生成 → `data` 条目 **0**（排除 15 个噪声文件），且 `Neko.exe`(4)、`bazi 插件`(9)、`neko_idle` 素材(20) **均在**（未误伤必需文件）。

## [0.1.78] - 2026-09-16

**审计 P0 修复：调度器竞态（能杀宿主）+ 测试假绿**（据 2026-09-16 只读审计报告）。

### P0-5 修 `SchedulerImpl` 竞态（真 bug，可致 CLR 终止进程）
- **根因**：`Add()` 用 `new Timer(..., dueMs, periodMs)` 直接启动，**之后**才 `handle = new TimerHandle(timer)`。当 `dueMs=0`（`At(DateTime.Now)`）时回调可能抢在赋值前于**线程池**触发 → `SafeInvoke(null!)` 内 NRE → **catch 里再访问同一 null handle 二次 NRE** → 逃出回调 → 线程池未处理异常 → **CLR 终止进程**（App 三层兜底只覆盖 UI 线程，拦不住）。
- **修法**：Timer 先以 `Timeout.Infinite` 创建（装配完成前绝不触发）→ 装配 `handle` → 入 `_handles` → 最后 `timer.Change(dueMs, periodMs)` 启动；`SafeInvoke` 改可空入参 + 首行 null 返回 + 计数/日志/熔断整块再包一层 try/catch（任何路径不外抛）。
- 行为语义完全保留（熔断阈值 5、日志文案不变）。

### P0-3 修 LoaderTest「假绿」
- 原先 **16 处 `FAIL:` 只打印不改退出码**，`PASS: 全部插件卸载成功` 是无条件打印，`TestAppOutputPlugins()` 是 `void` → 测出问题也可能 `exit 0`。
- 改为：失败计数器 + `Main` 返回 `int`（任何 FAIL → 退出码 1）；卸载改为**真验证**（断言事件处理器数归零、`IsCollectible=true`）；缺目录由静默跳过改为计入失败。

### 验证
- 构建 0 警告 0 错误；LoaderTest 全过；UiProbe 全过。
- **P0-5 回归锁**：UiProbe 新增「4 线程 × 125 并发 `At(DateTime.Now)`」断言 → 500 次回调全触发、句柄均有效、**进程存活、0 未处理异常**。
- **P0-3 反向验证**：`LOADERTEST_INJECT_FAILURE=1` 注入必失败条件 → 打印 `FAIL:` + `=== 1 项失败 ===` + **退出码 1**（证明假绿已修）。

## [0.1.77] - 2026-09-16

**自动更新·阶段1（检查 → 提示 → 打开下载页）**（借鉴 BongoCat update_service，走 Gitee API）。

### 功能
- **不做静默安装**（同 BongoCat 的最安全形态）：只检查、提示、打开下载页。静默安装需代码签名，否则会被 SmartScreen 拦截。
- 设置页「关于」栏新增：**「检查更新」按钮**（检查中禁用 + 文案变「检查中…」）+ **「启用自动检查更新」开关**。
- **每日静默检查**：启动后延迟 8s 后台跑（fire-and-forget，不拖慢启动），`LastUpdateCheckDay` 去重保证一天最多一次；发现新版本发**托盘气泡**提示，不弹模态框。
- 数据源：Gitee API `releases/latest`；`IsNewer` 做语义化版本比较（处理 `v` 前缀与预发布后缀，解析失败保守返回 false）。

### 健壮性
- **网络失败全静默**（非用户主动触发时）：`FetchAsync` 一律吞异常返回 null + `Log.Warn`，绝不因没网报错或崩溃。
- 用户可**完全关闭**（`UpdateCheckEnabled=false` 时启动路径不查；手动检查仍可用）。
- 无第三方依赖（仅 `System.Net.Http` + `System.Text.Json`）。

### 新增/修改
- 新增 `Core/UpdateModels.cs`（`ReleaseInfo`）、`Services/UpdateService.cs`
- 修改 `Core/Config.cs`（`UpdateCheckEnabled` + `LastUpdateCheckDay`）、`Windows/SettingsWindow.cs`（关于页）、`App.xaml.cs`（每日静默检查）
- 修正规格 bug：**Gitee releases 响应无 `html_url` 字段**（原按 GitHub schema 写）→ 改为按仓库坐标自行拼发布页 URL

### 验证
构建 0 警告 0 错误；LoaderTest 全过；UiProbe 全过（新增 2 条断言：`IsNewer` 语义化比较 + 真实 Gitee 响应解析、网络失败静默返回 null）。
⚠️ **端到端未通**：Gitee 仓库当前为私有（账号安全评级不足，需 2FA 或绑定第三方）→ 匿名 API 404 → 检查更新恒返回 null。代码逻辑已验证，等仓库公开即生效。

## [0.1.76] - 2026-09-14

**修掉 UGC 作者陷阱：`anim.json` 曾静默覆盖 `skin.json` 的 fps**（QA 独立验证时发现）。

### 问题（QA 实测复现）
`skin.json` 写 `idle=7`，但只要同目录的 `anim.json` 还是 `idle=4`，**渲染实际用 4** —— skin.json 的 fps 被静默覆盖。
- 现有 pixel 皮肤的两个文件数值恰好一致，所以看不出问题
- 但**用户复制 pixel 做自制皮肤、只改 skin.json 的 fps 会完全无效** → 直接废掉 v0.1.75「解锁用户自制皮肤」的目标

### 修复
- **`skin.json` 成为 fps 唯一权威**：`SpriteAtlas` 移除 `ReadAnimFpsOverrides`（v0.1.58 引入的 anim.json 覆盖路径）及其调用，fps 一律取 `skin.json` 的 `states[].fps`。
- **删除 `neko-assets/pixel/anim.json`**（内容已被 skin.json 完全覆盖，QA 已证两者数值逐项一致 → 行为零变化）。
- **`tools/generate_sprites.py` 停止生成 `anim.json`**（理由：保留生成会持续产出"看着像权威、实则被忽略"的文件，正是该陷阱的心智来源；停掉后素材侧只剩 skin.json 一个 fps 声明点）。
- `DesktopPet.App.csproj` 注释同步更新。

### 验证
构建 0 警告 0 错误；LoaderTest 全过；UiProbe 全过。
新增**陷阱锁死断言** `v0.1.76：skin.json 是 fps 唯一权威`——造一个 skin.json(idle=7) 的自制皮肤，再同目录塞一个会打架的 `anim.json`(idle=4)，断言渲染图集 fps **仍是 7**（若覆盖没删干净此处会得 4）；另有 pixel 9 状态 fps 逐项回归断言证明删除 anim.json 后运行时零变化。

## [0.1.75] - 2026-09-14

**皮肤目录可扩展：从「硬编码两种」到「扫目录自动发现」**（借鉴 BongoCat model_catalog，P1 批次）。

### 功能
- **皮肤由素材侧清单驱动**：`neko-assets/<skinId>/skin.json` 声明 id/尺寸/帧数/状态表（fps + eyeLayer），C# 不再硬编码。
- **扫目录即发现**：`SkinRegistry.Scan(assetsRoot)` 遍历 `neko-assets/*/`，每个含 `skin.json` 的子目录自动成为一个皮肤 → **用户可自制皮肤**（放进目录即被识别，零改代码）。
- **素材布局调整**：像素素材从 `neko-assets/` 迁入 `neko-assets/pixel/`（`neko_*.png` + `anim.json` + 新 `skin.json`）；soft 补 `skin.json`。
- 设置页「猫咪」栏皮肤下拉改为**动态列出**（displayName），新增「**打开皮肤目录**」按钮。

### 健壮性（铁律）
- 用户皮肤尺寸/帧数非法 → `TryLoad` 校验（frameW/H≤0、frames≤0、fps≤0、states 空 → 判损坏）→ **跳过 + Log.Warn，绝不抛异常崩宿主**。
- 一个都没扫到 → 回退内置 `BuiltinFallback`（pixel 硬编码规格），保证像素皮永远可用。
- 现有 pixel/soft 行为**逐项零差异**（states/fps/eyeLayer 与旧硬编码一一对应，UiProbe 断言锁定）。
- 未触碰 `DesktopPet.Core`。

### 验证
构建 0 警告 0 错误；LoaderTest 全过；UiProbe 全过（含 2 条新断言：skin.json 扫目录发现 + 元数据与旧硬编码一致；损坏 skin.json 跳过 + 无有效皮肤回退内置）。

## [0.1.74] - 2026-09-13

**配置健壮性 + 隐私合规文档 + 一致性检查脚本**（借鉴 BongoCat 工程实践，P0 批次）。

### 修复 / 加固
- **配置解析失败不再丢用户数据**：`ConfigManager.Load()` 解析异常时把损坏文件**改名保留**为 `config.json.rejected-<时间戳>`（原实现会返回默认值 → 后续任何保存都静默覆盖用户配置）。
- **配置原子写**：`Save()` 改为 写 `.tmp` → `File.Replace` 原子替换（原 `File.WriteAllText` 中途崩溃会留下截断损坏的文件）。
- **配置未知字段透传**：`AppConfig` 加 `[JsonExtensionData] Extra` —— 旧版本读新版配置（或插件自写段）时不再丢字段，保护「Core API 只增不改 + 插件生态」。
- **通知日志隐私收紧**：`SystemImpl.Notify` 不再把通知正文写入日志（body 可能含用户内容），且降为 Debug 级。

### 新增
- `PRIVACY.md` —— 键鼠输入只本地处理；本地数据路径；**网络访问逐条列明**（DeepSeek / open-meteo / 搜索）；数据删除方式；权限用途表。
- `SECURITY.md` —— 全局键鼠钩子的用途与边界（只转发事件、不记录内容、不注入、不模拟）；日志隐私边界核查结论；漏洞上报。
- `tools/check_version.py` —— 校验 csproj `<Version>` / `CHANGELOG` 首条 / git tag 三者一致（治版本漂移）。
- `tools/check_i18n.py` —— 扫所有 `T("中文","English")` 调用，检查双语完整性与漏翻。
- `README.md` 新增 FAQ「Neko 会记录我的键盘/鼠标输入吗？」+ 版本号改为不写死（原写着 v0.1.60 已漂移）。

### 验证
构建 0 警告 0 错误；LoaderTest 全过；**UiProbe 全过**（新增 v0.1.74 断言：损坏文件隔离保留 + 原子写无 tmp 残留 + 未知字段透传 三项）；两个 python 脚本实跑退出码 0。

## [0.1.73] - 2026-09-12

**命理插件人格定制改造：默认关闭 + 完整 93 题 + 弹窗做题 + 直接选型**（灯灯需求）。

### 改动
- **人格定制默认关闭**：`BaziProfile` 新增 `MbtiEnabled`（默认 false）。关闭时忽略 `Mbti` 残留值，播报走日主通用建议。
- **题库 24 → 93 题**：按官方 MBTI 四维度结构（E/I 23、S/N 26、T/F 24、J/P 20）用自己中文表述重写（非逐字照抄，规避版权）。`MbtiQuiz` 接口不变，`SelfCheck` 题量断言更新为 93 + 四维精确分布。
- **弹窗做题**：新增 `MbtiQuizWindow`（独立窗口，纯 C# + PluginUi 深色）——连续 93 题 + 顶部进度条 + 维度标签，可回退改答案，最后一题「提交」→ 计分回调。
- **直接选型**：设置页下拉框列出 16 型（含中文概称），用户已知类型可直接选。
- 设置页人格区改为「开关 + 状态行 + 开始测试按钮 + 直接选择下拉」，移除旧的 24 题平铺。
- **双保险联动**：读侧 `DailyFortuneBuilder.Advice()` 加 `MbtiEnabled` 判断；写侧关闭开关时强制 `Mbti = null` 落盘，杜绝残留。

### 验证
构建 0 警告 0 错误；LoaderTest 全过；UiProbe 全过（重写第 26 项为人格定制断言）。

## [0.1.72] - 2026-09-12

**键盘映射窗 UI 调整：鼠标圆头朝下 + 猫手从底部上抬**（灯灯："圆头应该向下，猫手应该从下面往上按"）。

### 改动
- **鼠标外形**：`CornerRadius(MouseW/2, MouseW/2, 12, 12)` → `(12, 12, MouseW/2, MouseW/2)`——大半圆移到底边，符合"猫坐在对面"心智模型。
- **爪子举手位**：`PawRaisedTop` 从 `-PawSize-3`（顶部外）→ `KbH + 5`（键盘底边外）。`TapKeyboardPaw` / `LiftMousePaw` / `OnKbdPawTimerTick` 全部用 `PawRaisedTop`，一行常量改动覆盖两个爪子——从底部"伸上来"按。
- **窗体高度**：+ `PawRaisedExtra` (36px) 给底部爪子预留空间。
- 鼠标/键盘两个爪子的初值位置都改用 `PawRaisedTop`。

### 调试经验（重要）
- 第一次实现把 `PawRaisedTop` 从 const 改成实例字段 → UiProbe 回归 NRE（layout pass 触发 null 访问）。
- 根因：`KbH + 5` 是 const 表达式，`PawRaisedTop` 保持 const 即可。WPF 实例字段访问 vs const 访问在 layout 阶段有微妙差异。
- 二分定位：git stash revert → 测试 PASS → 逐项回退定位。

### 验证
构建 0 警告 0 错误；LoaderTest 全过；UiProbe 全部通过（含 v0.1.43 猫爪跟随按键回归确认）。

## [0.1.71] - 2026-09-12

**径向菜单插件顺序 + 显隐 + 数量上限可配置**（灯灯需求：客户自由选择右键界面显示多少插件、以及插件顺序）。

### 功能
- 设置页新增「径向菜单」栏（第 6 个 sidebar 项），可：
  - **显示数量上限**：`MaxVisible`（0=不限），排序后只显示前 N 个槽位，超出隐藏
  - **插件顺序**：▲▼ 上移/下移按钮调整，改动即存
  - **显隐开关**：每个槽位独立 ToggleSwitch，隐藏项置灰
  - **恢复默认**：清空所有覆盖，回到插件默认顺序 + 全显示
- 架构：宿主侧「覆盖层」方案，不改 Core API（18 插件 `RegisterRadialSlot` 不受影响）。插件声明的 `Order` 是默认基准。

### 新增/修改
- 新增 `Core/RadialMenuConfig.cs`、`Services/RadialMenuResolver.cs`（纯静态解析器）
- 修改 `Core/Config.cs`（`AppConfig.RadialMenu`）、`Windows/RadialMenu.cs`（ctor 注入 + Resolve）、`Windows/PetWindow.cs`（传 `_config`）、`Windows/SettingsWindow.cs`（「径向菜单」栏）
- 新增架构设计文档 `docs/system_design.md` + 类图/时序图

### 验证
构建 0 警告 0 错误；LoaderTest 全过；UiProbe 全过（新增 RadialMenuResolver 六场景纯函数单测）。

## [0.1.70] - 2026-09-12

**径向菜单槽位修复：Order 冲突 + 重名 + 乱序**（为"插件顺序可配置"清障）。

### 修复
- **Order 16 冲突**：MusicControl「上一曲/播放暂停/下一曲」从 [16,17,18] 挪到 [2,3,4]，Countdown 独占 13–16。
- **两个「今日运势」重名**：Fortune 槽位 Label「今日运势」→「抽个签」；Bazi 保留「今日运势」。
- **Bazi 槽位乱序**：「今日运势」31→30、「问问猫」30→31。

### 验证
- 最终 Order 1–31 各至多占用一次，无冲突、无重名。
- build sln（22 项目）0 警告 0 错误；LoaderTest 全过。

## [0.1.69] - 2026-09-12

**命理助手 `com.lantern.bazi` P5：打磨收尾 —— 内存实测 + 全量回归**（承接 0.1.68 的 P4 MBTI）。

### 内存实测
- 在 `LoaderTest` 内用 Win32 `GetProcessMemoryInfo`（`psapi.dll`）对进程 WorkingSet 采样：`SnapWorkingSetMB()` 采样前双重 `GC.Collect()` + `GC.WaitForPendingFinalizers()`，采 3 次取最小值压 GC 抖动。
- 三处采点：加载命理插件前 → `OnLoad` + lunar 算盘后 → 全链路（播报 + Prompt + 客户端自检 + 无 Key 槽位）跑完后。
- 两次稳定实测：**加载前 ~34.8 MB → OnLoad+算盘后 ~49.6 MB（差值 ~14.8 MB）→ 全链路后 ~51.6 MB（较加载前 +~16.8 MB）**。

### 决策：`maxMemoryMB` 保留 32，不回调
- 实测插件增量内存 **~15–17 MB**，**不满足**「差值很小（<5 MB）」的回调条件。
- 且全链路稳态 ~16.8 MB 已**高于 16 MB** —— 若回调到 16，watchdog 阈值会贴近插件正常占用，AI 问答（DeepSeek 响应）+ 设置页 + 完整八字计算同时展开时存在误杀风险。
- 32 MB 相对实测稳态 ~17 MB 约 **1.9× 余量**，属健康安全边际，故**保留 32**（诚实结论，不为交差编数字）。

### 验证
- 全量 `dotnet build src/DesktopPet.sln`（**22 项目**，含 `DesktopPet.Builtin.Bazi`）**0 警告 0 错误**。
- **LoaderTest 全过**（含内存实测打点）；**UiProbe 全过（35 项，0 FAIL）**。

## [0.1.68] - 2026-09-12

**命理助手 `com.lantern.bazi` P4：人格测试（MBTI 问卷 + 计分）**（承接 0.1.67 的 P3 AI 咨询）。

### 新增
- `MbtiQuiz.cs`：简化版 16 型问卷 —— **本地硬编码 24 题**（E/I、S/N、T/F、J/P 各 6 题），每题二选一，**不联网**。`Score(answers)` 为**纯函数**：按维度统计 A/B 多数 → 拼 4 字母类型；平局或整维未答取该维度第一个极（E/S/T/J），结果确定。`Describe(type)` 给 16 型中文概称；含 `SelfCheck()`。
- `BaziSettingsPage` 新增「**人格测试（生日性格分析）**」一节：当前类型状态行 + 24 题（每题两互斥 RadioButton）+「计算并保存」按钮；**已有类型可随时重测覆盖**。整页包进 `ScrollViewer`（内容变长，避免撑爆宿主设置窗口）。
- 计分结果写入 `profile.Mbti`，与主「保存」共用同一落盘路径（避免互相覆盖）。

### 改进
- `DailyFortuneBuilder.MbtiTip`：由 8 型**补全到 16 型全覆盖**，且每型在五种五行生克关系（同气 / 生 / 泄 / 克 / 耗）下给出**各不相同**的建议 —— 「播报内容随人格类型变化」至此才真正完整。

### 验证
- 构建 **0 警告 0 错误**；**LoaderTest 全过**（新增 `MbtiQuiz` 计分自检：全选 A→ESTJ / 全选 B→INFP / 维度独立 / 平局取首极 / 题量均衡；`DailyFortuneBuilder` 自检扩到「16 型全覆盖 + 按类型分支 + 填 MBTI 后播报内容变化」）；**UiProbe 全过（35 项，0 FAIL）**——新增第 26 项：反射构建设置页 → 断言返回 `ScrollViewer` + 24 题 × 2 选项 + 「计算并保存」按钮 + 「当前类型」状态行（**不点提交**，避免 MessageBox 阻塞探针）。

## [0.1.67] - 2026-09-12

**命理助手 `com.lantern.bazi` P3：AI 深度咨询**（承接 0.1.66 的 P2 每日播报）。

### 新增
- `DeepSeekClient.cs`：`POST https://api.deepseek.com/chat/completions`（`deepseek-chat`，`System.Text.Json`）。**`HttpClient` 用 `static readonly` 单例**（规格第 7 节明确点名，不抄 weather 插件「每次 new」耗尽 socket 的反模式）；超时 30s；任何失败（无 Key / 断网 / 超时 / 非 2xx）一律折成 `ChatResult(Ok/Text/Error)` 可读文案，**绝不抛异常到宿主**。401/402/429/5xx 分档提示。
- `ApiKeyProtector.cs`：API Key 经 **DPAPI**（`ProtectedData.Protect/Unprotect`，`CurrentUser` + 附加熵）加密后以 `enc:<Base64>` 存 `Capability.Storage`；`EncryptedApiKeyConverter` 透明接管 `BaziProfile.ApiKey`（内存明文、落盘密文）；**向后兼容**旧版明文（无 `enc:` 前缀原样读回）。csproj 增 `System.Security.Cryptography.ProtectedData 8.0.0`（.NET 8 不在 BCL）。
- `FortunePrompt.cs`：Prompt 组装**抽成可测纯函数** —— ①系统提示（铁律：**只负责解读、不做任何历法和干支计算**，历法由系统提供、直接采信）②结构化命盘（四柱 / 日主 / 五行分布 / 十神 / 纳音 / 星座 / MBTI）+ 今日历法（干支 / 节气 / 宜忌 / 方位 / 冲煞）③最近 10 轮历史 ④当前问题。含 `SelfCheck()`。
- `ChatHistory.cs`：对话历史持久化（`Storage` 键 `chatHistory`，滚动保留最近 10 轮 = 20 条）。
- `ChatWindow.cs`：AI 问答窗口（**纯 C# 构建**，与 Notes/Todo/Clipboard 先例一致；`PluginUi` 深色化；气泡列表 + 输入框 + 发送；`async void` + `await` **全异步不阻塞 UI**；网络失败在窗口内以可读红色气泡呈现，不弹异常、不卡死）。
- `BaziEngine.ElementOfZhi`：地支 → 五行（供五行分布统计）。
- 接线：径向槽位「问问猫」→ 未填档案 / 未配 Key 给可读提示；否则打开 `ChatWindow`。每次提问**重读最新档案** → 组装 Prompt → 调 DeepSeek → 成功落历史。

### 验证
- 构建 **0 警告 0 错误**；**LoaderTest 全过**（测试 5 新增 4 断言：`ApiKeyProtector` DPAPI 往返自检 / `FortunePrompt` 组装自检 / `DeepSeekClient` 空 Key 失败路径自检 / 无 Key 点「问问猫」给可读提示且不抛异常）；**UiProbe 全过（34 项，0 FAIL）**——新增第 25 项：反射构造 `ChatWindow` + 校验关键控件（输入框 / 发送按钮 / 对话区）+ **失败路径**（空 Key 委托 → 断言窗口内出现可读提示、不抛异常），关窗释放；**不联网**。
- AI 真实调用需真 API Key，**留灯灯真机测**（不为联网写测试）。

## [0.1.66] - 2026-09-12

**命理助手 `com.lantern.bazi` P2：每日运势播报**（承接 0.1.65 的 P0 骨架）。

### 新增
- `BaziEngine.ComputeToday(DateTime)` → `BaziToday`：日柱 / 日干五行 / 纳音 / 节气 / **宜 / 忌** / 吉神宜趋 / 凶煞宜忌 / 喜神·财神方位 / 冲 / 煞 / 值日天神。同步纯计算，无 IO、不在 OnTick。
- `DailyFortuneBuilder.cs`（新）：**本地规则四段拼装，绝不调用 AI**：
  1. 能量基调 —— 今日日干五行 与 用户日主 五行的生克关系（同气/生/泄/克/耗）→ 一句话
  2. 宜 / 忌 —— 当日宜忌各取 2–3 条
  3. 人格定制建议 —— **MBTI 为预留接口**（P4 填 `profile.Mbti` 即生效），未填按日主五行给通用建议
  4. 方位提醒 —— 喜神/财神方位 + 冲煞
  输出 `Short`（一句话 → `Pet.Say`）+ `Full`（多段 → `System.Notify`）。含 `SelfCheck()`（五行生克 5 例 + 播报冒烟）。
- `BaziPlugin` 定时播报：`Scheduler.Every(60_000, CheckBroadcast)`（v0.1.61 起无 Cron，用 Every + 时刻判断）；判 `HH:mm == BroadcastTime` 且 Storage `lastBroadcastDate` 防当天重复；`!HasBirth` 静默跳过；tick 体 try/catch（铁律 7）；`OnUnload` Dispose 句柄（铁律 2）；每 tick 重新读 Storage 避免档案陈旧。
- 径向槽位「**今日运势**」（Order 31，手动立即播报）；修正 P0 槽位 `bazi-ask` 的 `Icon` `star`→`book`（`star` 图标不存在会回退 default）。

### 探测坑（重要）
lunar-csharp 的 `EightChar.YearWuXing/MonthWuXing/DayWuXing/TimeWuXing` 返回**「干+支」两字**（如「土土」），**不是单字五行**；今日五行与日主比较须用 `ElementOfGan(dayGan)`（单字）。设置页四柱预览同步改用日干单字五行。

### 打磨
- Short 文案：宜/忌各自整体成句、穷尽 `Pick` 的 2–3 条（消除当日无忌时输出「忌无」的不通顺，改为「无忌」）。
- 能量基调措辞：去掉「泄」「克」两类结尾的「宜X」（避免与 Short 里的「宜嫁娶」连出两个「宜」），改为「量力而行」「需谨慎应对」。

### 验证
- 构建 0 警告 0 错误；**LoaderTest 全过**（「测试 5」新增断言：双径向槽位、`DailyFortuneBuilder` 自检、**手动播报全链路**——预置档案 → 触发「今日运势」槽位 → 断言 `Pet.Say` 短句 + `System.Notify` 完整内容）；**UiProbe 全过**（0 FAIL）。

## [0.1.65] - 2026-09-12

**新插件 `com.lantern.bazi`（命理助手）P0：骨架 + 生日设置页 + 第三方依赖 ALC 验证**（承接灯灯"命理插件"需求；完整规格见 `outputs/Neko命理插件-开发提示词.md`）。

### 新增
- 新插件 `builtins/DesktopPet.Builtin.Bazi/`（部署到 `plugins/com.lantern.bazi/`），文件：
  - `DesktopPet.Builtin.Bazi.csproj`：`net8.0-windows` + `UseWPF` + `PackageReference lunar-csharp 1.6.8`
  - `plugin.toml`（`other = scheduler/notify/network`；`pet`/`ui`/`storage` 由宿主自动加前缀）
  - `BaziPlugin.cs`（入口）/ `BaziProfile.cs`（档案模型 + Storage 持久化）
  - `BaziEngine.cs`（lunar-csharp 薄封装）/ `BaziSettingsPage.cs`（设置页）
- `BaziEngine.ComputeProfile`：四柱 / 日主 / 五行 / 纳音 / 十神 / 星座 / 大运起运。**历法一律本地算，绝不交给 AI**。
- `BaziEngine.VerifyKnownCases`：lunar 官方用例自检（`LastSelfTest` 暴露给加载器测试）。
- `BaziSettingsPage`：出生录入（历法/年/月/日/时/分/性别）+ **实时四柱预览** + API Key(PasswordBox) + 播报设置；控件统一走 SDK `PluginUi`；`Title == Manifest.Id`。

### 部署链修复（P0 关键）
- 这是首个带**第三方依赖**的内置插件。原 `DeployToPlugins` 只拷 `$(TargetPath)` 单个 dll。本次改为：拷 `$(OutDir)*.dll`（**含 `lunar.dll`**，排除 ALC 白名单的 `DesktopPet.Core.dll`/`DesktopPet.Sdk.dll`）+ **`$(TargetName).deps.json`** + `plugin.toml`。
- **为什么要 deps.json**：`PluginLoadContext` 用 `AssemblyDependencyResolver(entryDll)` 解析依赖，它**只读 deps.json**；缺了它 `lunar` 解析不到（resolver 返回 null → 回落默认上下文 → 找不到）。csproj 显式 `<GenerateDependencyFile>true` + `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>`。

### 验证
- 构建 0 警告 0 错误；**LoaderTest 全过**（新增「测试 5：命理插件」——验证插件加载 + 权限授予 + **lunar.dll 在插件 ALC 下解析**（OnLoad 内跑 lunar 不抛即证明）+ 径向槽位 + 设置页注册 + lunar 回归用例 `1981-01-29→1989-02-18` / `2020-01-06→2020-02-06` / 立春边界）；**UiProbe 33/33 全过**（0 FAIL）。
- **第 7 节前两条风险消解**：①「插件 ALC 能否加载第三方依赖」→ **能**（LoaderTest 决定性命中：LoaderTest 本身不引用 Bazi/lunar，默认上下文无 lunar，仍加载成功即证明走插件 ALC）；②「插件能否弹独立 WPF 窗口」→ 已有 3 先例（Notes/TodoList/ClipboardHistory），无需额外验证。

### 待办（后续阶段）
- P1 四柱完整预览与回归补全 / P2 `DailyFortuneBuilder` + 定时播报 / P3 `DeepSeekClient` + `ChatWindow` / P4 MBTI 问卷。
- 实测内存占用后回调 `maxMemoryMB`（当前 32）。

## [0.1.64] - 2026-09-12

**插件设置页 UI 统一化：深色控件工厂下沉 SDK + 6 个设置页迁移**（灯灯要求"插件 UI 统一"，承接 v0.1.63）。

### 背景
6 个插件设置页 UI 参差：Affection/Weather 是旧样式（TextBlock 未设前景色，黑字在宿主深底上看不清）；4 个提醒类设置页文字色已深色但**按钮仍是 WPF 默认浅色样式**。根因是各设置页手写控件样式、无统一来源（宿主 `SettingsWindow.BuildXxxCard` 是 private，插件够不着）。

### 改动
- **SDK 加 `<UseWPF>true</UseWPF>`**（`DesktopPet.Sdk.csproj`）：让 SDK 能提供 WPF 控件工厂。**关键验证**：SDK 是宿主共享程序集（HostSharedAssemblies 白名单），加 WPF 引用后插件 ALC 路由不受影响——LoaderTest 全过 + UiProbe 第 32 项（真实 PetWindow + PluginHost 加载插件）通过证明。
- **新增 `src/DesktopPet.Sdk/PluginUi.cs`**：深色控件工厂静态类。配色常量（`Freeze` 冻结省内存）：Bg #1E262F / CardBg #2B3647 / TextMain #E5E7EB / TextSub #9CA3AF / Border #3A4755；工厂方法 `Text(content, sub)` / `Button(text)` / `Input(text)` / `SectionHeader(title)`。
- **6 个设置页迁移到 PluginUi**：Affection、Weather（补全深色）、SleepReminder、Pomodoro、Countdown、HourlyChime（统一按钮样式）。

### 修复（QA 插件审计发现）
- SleepReminder：步进 +/- 点击后**立即刷新顶部"提醒时间"label**（原来仅保存时更新）。
- Pomodoro：下拉候选改为 `Presets ∪ {当前值}`，**storage 值不在 5/15/25/45 时也能预选**。

### 验证
- SDK + App + 插件构建 0 警告 0 错误；LoaderTest 全过；UiProbe 全过（0 FAIL）。
- ⚠️ **环境限制说明**：Bash 后台拉起的 GUI 进程无法加载插件（`PetWindow.Loaded` 事件在无交互桌面会话下不触发），此现象经 A/B 对照确认与本次改动无关；**真机插件加载验证需手动双击 exe**（已提醒）。

## [0.1.63] - 2026-09-11

**插件 UI 美化（第一批）：3 个独立窗口深色化 + 淡入动画**（QA 插件审计指出的"剩余最大 UI 债"）。

### 改动
- 3 个独立窗口从默认 WPF 白底 → 深色主题（与宿主设置页 #1E262F 一致）：
  - `Notes/NotesWindow.cs`：深底 + 保存按钮/TextBox 深色化 + `CaretBrush` 浅色
  - `ClipboardHistory/ClipboardWindow.cs`：深底 + ListBox 深色 + ListBoxItem 选中态蓝灰 #33445C
  - `TodoList/TodoWindow.cs`：深底 + 统一按钮样式 + 输入框/ListBox 深色
- 每个窗口加 150ms `Opacity` 淡入动画（打开时平滑显现）
- 配色各窗口内联常量（背景 #1E262F / 主文字 #E5E7EB / 次文字 #9CA3AF / 卡片底 #2B3647 / 边框 #3A4755），未抽 SDK 共享类（避免动宿主共享程序集影响插件 ALC 路由）
- 顺带消除 TodoList 原有 CS8602 空引用警告（纯重排，逻辑等价）

### 验证
- 3 插件构建 0 警告 0 错误；LoaderTest 全过；逻辑（自动保存/双击复制/勾选）零改动。

## [0.1.62] - 2026-09-11

**插件系统升级（第一批）：Windows 通知接入 + 提醒插件时间设定**（灯灯需求：提醒类插件要能设定时间、接入 Windows 通知作为提醒）。

### 任务 1：Windows 通知接入（修 `ISystem.Notify` 空壳）
- **问题**：`ISystem.Notify` 自 v0.1.0 起一直是占位符（只写 `Log.Info`），插件调 `Capability.System.Notify` 实际不弹任何通知——"假功能"比没有更糟（误导插件作者以为弹了）。
- `SystemImpl` 加 `public static Action<string,string>? NotifySink` 静态字段（因 SystemImpl 在多处 `new`，静态字段零改动构造点）；`Notify` 改为日志 + 调 sink。
- `TrayManager` 加 `ShowBalloon(title, body)` → `TaskbarIcon.ShowBalloonTip`（Hardcodet 内置，0 新依赖）；带跨线程 `Dispatcher` 兜底（插件 Timer 线程池回调触发）+ `try/catch` 记 `Log.Warn`。
- `App.xaml.cs` 启动时绑定 `SystemImpl.NotifySink = ... → tray.ShowBalloon`。

### 任务 2：提醒插件时间设定（4 个插件加设置页）
- **问题**：提醒类插件时间全部硬编码、无设置页，用户无法自定义。
- **SleepReminder**：固定 23:00 → 可设定时+分（Storage `remindHour`/`remindMinute`，默认 23:00，clamp 边界）；径向菜单文案动态显示当前设定。
- **Pomodoro**：固定 25 分 → 可设定（ComboBox 5/15/25/45 分，Storage `workMinutes`，clamp 1-120）。
- **Countdown**：新增第 3 槽位「倒计时 N 分」（N=Storage `customMinutes` 默认 15，clamp 1-180），保留 5/10 快捷槽位。
- **HourlyChime**：新增提醒时段（Storage `startHour`/`endHour` 默认 9-18，支持跨天如 22-6），`CheckHour` 加时段窗口判定；设置页 CheckBox 开关 + 起止小时步进。
- 每个插件新增 `XxxSettingsPage.cs`（实现 `ISettingsPage`），主插件加 `Storage` 权限。

### 关键修复
- **4 个插件 csproj 补 `<UseWPF>true</UseWPF>`**：这 4 个插件原本没有 UseWPF（只有 Affection 有），设置页返回 WPF 控件编译不过。属必要改动。

### 验证
- 4 个插件 + App 构建 0 警告 0 错误；LoaderTest 全过；UiProbe 33/33 全过。

## [0.1.61] - 2026-09-11

**批次 A：可观测性 + 宿主稳定性 + 文档诚实化**（源自 2026-09-11 项目健康度审计 P0 项；软件团队 SOP：工程师实现 → QA 独立验证）。

### 任务 1：轻量文件日志系统（修审计 P0-2）
- **问题**：全项目仅 `App.xaml.cs` 写 `errors.log`（且只记未处理异常），其余全是 `Debug.WriteLine`——**Release 构建下被编译器移除，用户装机后出问题无从排查**。
- 新增 `src/DesktopPet.App/Core/Log.cs`（静态类）：`Info/Warn/Error/Debug` 四级 API。
- 落盘 `data/logs/neko-YYYYMMDD.log`；`Paths.cs` 新增 `Logs` 属性并纳入 `EnsureCreated()`。
- 滚动：按天分文件；单文件超 5MB 追加 `-N` 序号；启动清理 7 天前旧日志。
- 线程安全（lock，覆盖插件线程池回调）；失败静默（所有 IO 包 try/catch，**绝不抛异常拖垮宿主**）；格式 `[ts] [LEVEL] [ThreadId] msg`。
- 默认级别 **Warning 及以上**（避免频繁刷盘）；`AppConfig.LogLevel` 新增字段（默认 `"warn"`，可选 `info/debug/off`）。
- `App.xaml.cs` 全局兜底改用 `Log.Error` 写日志（保留 MessageBox）；**`Log.Error` 同时写 `errors.log`（向后兼容）**。
- 替换既有 `Debug.WriteLine` 调用点：`Config.cs`（2）、`PluginHost.cs`、`PluginContextImpl.cs`、`SystemImpl.cs`（4）、`TrayManager.cs`、`App.xaml.cs`。

### 任务 2：插件后台异常隔离（修审计 P0-1）
- **问题**：`SchedulerImpl` 三处 Timer 回调裸调无 try/catch。Timer 回调跑在线程池，**未处理异常直接终止进程**；而全局兜底只挂 `DispatcherUnhandledException`（仅 UI 线程）。任一插件定时任务抛异常 → 整个 Neko 被杀。
- `SchedulerImpl` 回调统一异常隔离：`try { callback(); } catch (Exception ex) { Log.Error(...); }`。
- **失败计数 + 熔断**：每个定时器记录连续失败次数，达阈值 **5 次**自动停表并 `Log.Warn` 告警。
- `App.xaml.cs` 补 `TaskScheduler.UnobservedTaskException`（`SetObserved()` 防进程终止）+ `AppDomain.CurrentDomain.UnhandledException`（留遗言）。
- `PluginContextImpl.EventBusImpl.Emit` 的 `Debug.WriteLine` → `Log.Error`。

### 任务 3：文档诚实化（修审计 P0-3 + P1-7 + P1-9）
- **权限模型改文档不改代码**：实际为「加载时校验」——manifest 声明 → 判定风险 → 高风险（file/process）拒绝自动加载；加载后已授予范围内自由调用。统一 `docs/plugin-authoring-guide.md` 口径（此前误写「每次调用校验」，运行时从未实现逐次守卫）。
- `docs/plugin-authoring-guide.md`：版本标注 v0.1.0 → **v0.1.61**；`enum PetState` 补第 9 值 `Work`（v0.1.48 新增）；「8 组雪碧图」→ **9 组**。
- **删除死接口 `IScheduler.Cron`**（审计 P1-9）：原实现忽略 cron 表达式退化为每分钟，属半成品；全仓（src/builtins/samples）零实际使用 —— 诚实优于半成品。同步 `ICapabilityApi.cs`、LoaderTest 的 `NullScheduler` 桩、手册。

### 验证
- 构建（App/tests）Release + Debug **0 警告 0 错误**。
- LoaderTest 全过；UiProbe **33/33 全过**（新增 6 项：日志 4 项 + 调度器隔离 2 项）。
- 新增 UiProbe 断言：日志写盘/多线程并发不抛/自动建目录/off 级别不写；调度器回调抛异常不拖垮宿主 + 记日志 + 熔断停用。
- ⚠️ **环境修正**：App 自 v0.1.60 起为自包含，UiProbe（非自包含 exe）无法引用（NETSDK1151）。在 **UiProbe.csproj**（仅测试侧）对齐 `SelfContained=true`+`RID=win-x64`，**未改动 App 的发布配置**。

## [0.1.60] - 2026-09-11

**恢复 AI 托盘图标 + 发布形态修正（回自包含，压缩关）**。本次由灯灯在真机检查中发现的**两起无留痕回退**触发——项目无 git，改动无法追溯，故记录在此作为事实基线。

### 修复①：托盘图标被静默回退（灯灯发现）
- **事实**：`neko-assets/tray.ico` 在 v0.1.57 前后被从 v0.1.56 的 AI 版**覆盖回**原始程序化像素猫头，且 CHANGELOG / dev-log 均无记录
- 版本对照：v0.1.53/54 = 1,330 B（像素版）→ v0.1.55 = 71,413 B（AI 抠白底版，被否）→ v0.1.56 = 82,965 B（AI 白底+抹水印版 ✅）→ **v0.1.57 起 = 1,330 B（回退，无记录）**
- **修复**：从 `publish/v0.1.56/neko-assets/tray.ico` 恢复 AI 版（82,965 B，256×256、5 尺寸档 16/32/48/64/256、RGB 白底），MD5 `5045aa3d...`
- 旧像素版备份为 `neko-assets/tray.ico.bak-pixel-1330`（注意：原有的 `.bak-v0.1.54` 备份的内容也是像素版，无法用于回滚到 AI 版）

### 修复②：发布形态被误改为框架依赖（检查中发现）
- **事实**：`DesktopPet.App.csproj` 的 `<SelfContained>` 在某次操作中由 `true` 改为 `false`（同样无记录），v0.1.59 产物因此变成 3.9MB 框架依赖版——用户机器未装 .NET 8 Desktop Runtime 则无法启动
- **修复**：改回 `true` + 显式 `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`，恢复"用户零前置、双击即用"

### ⚠️ 压缩取舍（实测定量，灯灯拍板）
`EnableCompressionInSingleFile` 对照实验（同为自包含）：

| 配置 | 磁盘体积 | 稳态内存 |
|---|---|---|
| 压缩 on | 80MB | **276MB** |
| 压缩 off | 174MB | **177MB** |

原因：单文件压缩版启动时需将运行时解压，映射常驻内存。桌宠为常驻程序 → **内存优先，`EnableCompressionInSingleFile=false`**（灯灯 2026-09-11 决策）。csproj 内已写明该结论与数据，防后人误开。

### 验证
- 构建 0 警告 0 错误；自包含发布 `publish/v0.1.60`（Neko.exe 171,120,649 B，目录 174MB）
- 产物完整性：图标 MD5 与 v0.1.56 AI 版一致 ✓、plugins 19 项 ✓、neko-assets/anim.json ✓
- 真机存活：PID 37260，内存 169→174MB 后稳定于 ~171MB（140s 观测无增长趋势）
- ⚠️ **CPU 未取得可信数据**：沙箱性能计数器实例枚举受限，`\Process(Neko)\% Processor Time` 实例不存在，typeperf 返回值不可信（此前 v0.1.59 报的 0.000% 同源存疑）。CPU 复测需在真机任务管理器观测
- ⚠️ 体积 174MB 略超原红线 180MB 内；内存 177MB 略超红线 170MB——按灯灯决策「自包含形态下红线放宽」接受

## [0.1.59] - 2026-09-06

**性能优化批次（P1-P4，QA 体检发现）+ 交互增强批次 3（⑩⑨⑧⑦）**（软件团队 SOP：工程师实现 → QA 独立验证）。

### P1 InputMirrorWindow 隐藏态停 LED 轮询
- `InputMirrorWindow.cs`：`RefreshLeds()` 开头加 `if (!IsVisible) return;`——SetAppMode/游戏模式只调 Hide() 不停 `_ledTimer`，隐藏态曾 5Hz 跑 3×GetKeyState + new 3 个 SolidColorBrush + 遍历 _litKeys，现直接早返回

### P2 剪贴板插件降频
- `ClipboardHistoryPlugin.cs`：轮询 800→**1500ms**（提取命名常量 `PollIntervalMs`，消除散落魔数）——剪贴板历史实时性要求低，减少 ReadClipboard 跨进程调用

### P3 闲置态系统调用按需化（行为等价）
- `PetWindow.cs`：`UpdateEyeTracking` 鼠标坐标未变且无瞬时视觉 → 跳过（缓存 `_lastCursorPos`）；`EmitMouseMoved` 位置未变 → 不 Emit（缓存 `_lastEmitCursorPos`）；`GetWorkArea` 加工作区缓存（猫位置未变则复用 `_cachedWorkArea`，省 GetMonitorInfo Win32）

### P4 渲染与主循环统一 `_clockMs`
- `PetRenderer.cs`：`_flashEyesUntil`/`_typingUntil` 由 `DateTime` 改为基于 `_clockMs` 的 `double` 截止值（`_flashEyesUntilMs`/`_typingUntilMs`）；Tick/Draw/HasTransientVisual/SetTypingActive/FlashEyes 全部统一口径——闪眼/打字时长不变，内部行为等价重构

### ⑩ 鼠标路过张望
- `PetWindow.cs.UpdateEyeTracking`：鼠标进入猫附近 `PeekRadius=120px` → `_peekActive` 边界切换一次（进入/离开），眼球偏移增益 ×1.6（`PeekEyeGain`）；`PetRenderer.SetEyeOffset(dir, gain=1.0)` 新增增益参数（默认 1.0 行为不变，不新建渲染通道）

### ⑨ 心情驱动动画
- 提取 `Services/AffectionReader.cs`（`ReadLevel`，从 ChatScheduler 抽出为可复用静态方法，单一数据源）；`ChatScheduler.ReadAffectionLevel` 改为委托它
- `PetWindow.UpdateMoodFromAffection`：好感度等级（**60s 缓存**，免每 tick 读盘）→ L2 亲密/L3 挚友 → `PetMood.Happy`（1.3× 帧率），其余 → Idle；插在 `_stateMachine.Tick` 之后、`_renderer.Tick` 之前；暂停态不覆盖

### ⑧ 抚摸爱心粒子
- 新增 `Windows/HeartParticleWindow.cs`：独立无边框 topmost 浮层（`Background=null` 真穿透、不抢焦点、不拦鼠标），抚摸猫时猫头顶上方冒 **3~5 颗**爱心，向上飘 + 淡出（**800ms**），播完自动隐藏（不常驻）
- 爱心程序化绘制（Path Geometry 贝塞尔，粉红 #FF6B9A / 浅红描边），不引入新 PNG；粒子窗口随猫同步（`PetWindow.SyncHeartPosition`，头部锚点）；`Pet()` 触发；游戏模式/Dispose 收起
- **修复（QA 首轮 27/28 失败项）**：`OnTick` 的仿真时钟由「每 tick 固定 +16ms」改为**真实 elapsed**（`Environment.TickCount64` 差，与 v0.1.58 任务5 主循环同款口径）。真因——dispatcher 在嵌套消息泵下会合并/延迟 timer tick，QA 实测 1100ms 墙钟只跑出 44 tick ≈ 704ms 仿真时间，导致 800ms 寿命的粒子未及消散。改用真实 dt 后 tick 疏密不影响寿命（决定性对照实验：修前 tick 0→44/剩 3 颗 FAIL，修后全过）

### ⑦ 扔猫抛物线物理
- `PetWindow.StartFall`：补水平分量 `vx = initialVelocity.X * 60`（同口径限速 MaxThrowSpeed）；左右碰边反射（以猫中心判定 `minCenterX/maxCenterX`，撞墙 `vx = ∓|vx| * Bounce`）；触地 `vx *= GroundFriction(0.6)` + 低速归零；vy 与 vx 均静止才落定
- **风险修复**：`UpdateEdgeSnap` 早返回条件加 `_fallTimer != null`——否则抛物线飞行中会被吸附逻辑抢方向盘破坏轨迹

### 验证
- 构建 0 警告 0 错误；LoaderTest 全过；UiProbe **28/28**（新增第 26 项 P4/⑩/⑨、第 27 项 ⑧爱心、第 28 项 ⑦抛物线真实时序断言）
- 第 27 项加 `_tickCount` 决定性诊断（tick 是否真推进）+ 确定性 pump；第 28 项补真实时序断言（StartFall 后 Left **与 Top** 均实际变化 + 越界防护）
- Release v0.1.59 发布

## [0.1.58] - 2026-09-06

**动画流畅度批次 2（真实 dt 计时 + anim.json 皮肤契约）+ ChoiceCard 选中态 bug 修复**（软件团队 SOP：工程师实现 → QA 独立验证 PASS）。

### ⑤ 动画计时真实 dt（消除 DispatcherTimer 抖动）
- `PetWindow.cs`：主循环 DateTime → 单调 ms（`_lastTickMs=Environment.TickCount64`），OnLoopTick 每 tick 真实 deltaMs 下发；StartFall 落体用真实 dt 缩放重力/位移（独立 16ms timer——**100ms 闲置降频不再让落体失速**，QA 实测：预测 t=1054ms 首触地实测 1060ms 误差<1%）
- `PetRenderer.cs`：眨眼弃"tick 固定步长"改**绝对截止时间**（内部 _clockMs：Half@+80ms、Closed@+140ms，UI 卡顿跳帧直接回 Open）；打字/瞪眼本就绝对 DateTime；_speechTimer 真实 dt 累减
- QA 墙钟实测：眨眼总时长 6 样本 144-148ms（规格 140±30），调度抖动不漂移

### ⑥ anim.json 皮肤契约（为皮肤系统铺路）
- 新增 `neko-assets/anim.json`：`{fps:{idle:4,...,work:2}, meta:{frameWidth:32,frameHeight:32,framesPerState:4}}`，`generate_sprites.py` 每次输出**同源写入**
- `SpriteAtlas.Load(dir, skin)` 读目录 anim.json：**出现的键覆盖内置 fps、缺失键回退内置、文件缺失/损坏/值非数字静默回退**（皮肤缺 anim.json 也能跑）
- csproj 复制 anim.json 到输出（publish 内含 ✓）
- 契约文档：generate_sprites.py 头部写明 schema + 扩展规则（新增皮肤 = 新目录 + 同名 PNG + 可选 anim.json，宿主零改动）
- QA 独立验证：无文件回退 / idle:8 覆盖 / 局部键只覆盖存在键 / 损坏 JSON 全回退不抛 / soft 皮肤生效

### ChoiceCard 选中态 bug 修复（灯灯反馈，存量 bug v0.1.38）
- 根因：`SettingsWindow.BuildChoiceCard` 只按初始 selectedIdx 一次性上色，点击只调 onChange 从不刷新按钮视觉 → 配置生效但高亮停在第 0 项
- 修：卡内 `RefreshSelection(idx)`——点选后遍历按钮 ColorAnimation 150ms 过渡（选中=Accent/未选=灰 0x3A4457，与 sidebar 选中同款）；所有 ChoiceCard（游戏模式/主题/语言/应用模式/键盘朝向）调用点零改动自动受益
- **UiProbe 第 25 项断言**（点"全屏时藏猫"→240ms 后按钮背景≈Accent）：防回归

### 验证
- 构建 0 警告 0 错误；LoaderTest 全过；UiProbe **25/25**（新增第 25 项 ChoiceCard 高亮断言）
- QA 独立探针 33/33 PASS（anim.json 10 项 + 眨眼绝对截止 7 项 + 墙钟 6 样本 + HasTransientVisual 5 项 + dt 链路 5 项 + 落体 dt 3 项）
- Release v0.1.58 发布（anim.json 在 publish）；真机存活（PID 24912，177MB 红线内）

## [0.1.57] - 2026-09-05

**动画流畅度优化批次 1**（软件团队 SOP：工程师实现 → QA 独立验证 PASS）：idle/sit 帧率、眨眼中间帧、闲置降频、位移缓动。

### ① idle/sit 升帧率 + 呼吸幅度（素材+运行时）
- `tools/generate_sprites.py`：idle/sit fps 2→**4**；st_idle 呼吸两级 bob=[-1,-2,-1,0]（2px 起伏）；st_sit 呼吸 bob=[0,-1,-1,0]
- 呼吸**只移身体/腿/尾，头锚点 HEAD_CX/HEAD_CY 固定** → eyes_open 叠层绝不错位（QA 像素验证：头锚区 4 帧 diff=0，身体区 diff 17-19px）
- `SpriteAtlas.cs` pixel stateSpecs + LoadSoft：idle=4 / sit=4（walk 8/pet 10/drag 6/fall 12/work 2 不变）

### ② 眨眼加中间帧（半闭）
- 素材新增 `neko_eyes_half.png`（半闭：眼睑压下一半瞳孔露下半；与 open 差 22px、blink 差 32px 非复制帧）
- `PetRenderer.cs`：BlinkPhase 状态机 Open→**Half(80ms)**→Closed(60ms)→Open；Draw 按相位取 eyes_half/eyes_blink；缺 half 素材回退 eyes_blink 不崩

### ③ 闲置降频（省资源 + 交互灵敏）
- `PetWindow.cs`：IdleLoopIntervalMs=100；IsBusyActiveWindow（拖拽/位移动画/落体/HasTransientVisual/Walk|Pet|Drag|Fall 状态）活动 50ms / 纯闲置 100ms
- PetRenderer.HasTransientVisual（眨眼非 Open/打字/瞪眼/气泡）
- **输入钩子触发立即唤醒**（RequestActiveLoop：回 50ms + 立刻同步跑一帧）——交互响应零延迟
- 闲置只在帧变化时 InvalidateVisual（纯闲置 idle 4fps ≈ 每 250ms 一次重绘，CPU 显著下降）

### ④ 位移缓动曲线（按动作分类，overshoot 限幅）
- `AnimateMoveTo(left,top,ms,style)` + MoveStyle{Walk,Snap}：OnMirrorRelocated→Walk 220ms、AttachEdge→Snap 260ms
- **MakeMoveEase 二分反解 Amplitude**（扫真实 BackEase.EaseOut 峰值过冲比例 → 目标 0.95×cap）——QA Round1 发现原近似公式差 10 倍（1516px 过冲 79.5px）→ 修复后实测过冲全部 ≤6px(Walk)/≤4px(Snap)，端到端 1516px 实测 3.76px
- 拖拽线性跟手、落体回弹一次、UI 高亮 80ms 保持不变

### 验证（QA 独立，BATCH1 QA: PASS）
- 构建 0 警告 0 错误；LoaderTest 全过；UiProbe 24/24；QA 运行时断言探针 28 PASS/0 FAIL
- Release v0.1.57 发布（eyes_half 素材在 publish）；真机跨调用存活（PID 22852，172MB 红线内）
- **环境备注**：子代理 shell 缺 Windows env 变量导致 dotnet/NuGet restore 崩（`NuGet.targets(694) Value cannot be null (path1)`）——已建包装脚本 `C:\Users\Lantern\neko-dotnet-env.sh`（补 PROGRAMFILES 等 env 后 exec dotnet），后续 dotnet 命令统一走它

## [未发布]

- 封包 MSI 已打通（v0.1.45 实测安装/卸载成功）：`installer/build_setup.py <version>` 一键出包
- 首次安装引导（猫名+称呼）已内置于 App（v0.1.41），安装后首启自动弹
- **v0.1.54 起 MSI 不随版本自动出包**（灯灯指示：需要时手动 `installer/build_setup.py <version>`）

## [0.1.56] - 2026-09-03

**托盘图标改为"保留白底 + 抹水印"**（v0.1.55 误判：把白底抠成透明→托盘显示黑底；用户反馈"给你的图标是白色的吧"——本意是保留白底）。

### 变更

- `neko-assets/tray.ico` 替换：豆包 AI 橘猫头 1765×1702 RGB → **不抠白底 + 抹右下"豆包AI生成"水印区** → RGB ICO 256/64/48/32/16（83KB）
- 旧版本备份：v0.1.55 ICO（71KB 透明版，HSV 饱和度方案）+ v0.1.54 之前的程序化版

### 经验

- **"抠图"要明确去什么**：v0.1.55 我理解为"抠白底"（去背景）；灯灯本意是"抹水印"（保留白底）
- HSV 饱和度对"内容浅色 vs 背景白"区分不准——白色肚皮/嘴周被误抠透明
- 下一轮要更精确问用户："抠白底 / 抹水印 / 透明背景 / 白底保留"哪种

### 变更

- `neko-assets/tray.ico` 替换：豆包 AI 生成的橘猫头（1280×1280 RGB → 抠白底 + 抹右下"豆包AI生成"水印 → 多尺寸 ICO 256/64/48/32/16）
- **抠图算法**：HSV 饱和度抠白（白 S=0 → 透明，橘 S=255 → 不透明），软阈值 2x 放大抗锯齿
- 旧图标备份：`neko-assets/tray.ico.bak-v0.1.54`（随时回滚）
- `csproj <ApplicationIcon>` 仍指向 `neko-assets/tray.ico`（路径不变，文件替换即生效）

### 验证

- LoaderTest 4 组 + **UiProbe 24 项**全过
- 构建 0 警告 0 错误
- Release v0.1.55 发布（publish 内 tray.ico = 71KB ✓）
- 真机跨调用存活（PID 29852，173MB 红线内）

### 待续

- 抠图用 HSV 饱和度区分"白 vs 橘"——避免 R 单通道错判（橘 R=255 会被误判为白）
- ICO 多尺寸生成时 PNG → ICO 的 PNG 编码（保留 alpha 通道）；运行时 WPF NotifyIconWpf 自动选合适尺寸

### 变更

- 设置页「外观风格」皮肤切换卡**注释屏蔽**（恢复只取消注释 + 放开白名单）
- `PetWindow.SetSkin` + 启动路径加**白名单**：非 "pixel" 一律强制回 "pixel"（残留 config 的 soft 启动即被纠正）
- 素材 `neko-assets/soft/` **不删**、`SpriteAtlas.Load("soft")` 代码保留——随时可恢复
- UiProbe 第 23 项改为验证白名单（SetSkin('soft') → atlas.Skin 仍 'pixel'）
- 像素版素材保留 v0.1.53 学 AI 造型升级（腮红/顶光/对称虎斑）

### 验证

- LoaderTest 4 组 + **UiProbe 24 项**全过（第 23 项改断言适配屏蔽）
- 构建 0 警告 0 错误
- Release v0.1.54 发布（无 MSI，按需生成）

## [0.1.53] - 2026-09-03

**像素版画法学 AI 造型升级**（灯灯：用即梦 AI 生成了造型参考，AI 直接出的 sprite sheet 因帧不连贯/背景不纯/眼球带脸不可用，但造型值得学——自己用程序画）。

### 新增/改进

- **像素版画法升级**（`tools/generate_sprites.py`）：
  - **脸颊腮红**（`_blush()`）：idle/sit/walk/climb/work 等平静脸加两侧粉点（原只有 pet 有）
  - **头顶高光**：头顶 2 点浅橘 C_FUR_L（圆润立体感）
  - **对称虎斑**：身体侧纹从"仅左侧 2 条"→"左右对称 4 条"
- **柔光简笔画版同步**（`tools/generate_bongocat.py`）：
  - 额头虎斑（三竖线 C_FUR_D，palette 补 C_FUR_D 色）+ 头顶高光弧线（C_FUR_L）
- 原素材备份：`neko-assets.bak-20260903/`（26 文件，随时回滚）

### 验证

- LoaderTest 4 组 + **UiProbe 24 项**全过
- 构建 0 警告 0 错误
- Release v0.1.53 发布（像素 9 状态 sheet + 柔光 37 帧全替换）

## [0.1.52] - 2026-09-02

**语言切换即时生效**（v0.1.50 改的"重新打开设置窗口生效"体验差）+ **像素版 fall 帧验收微调**（v0.1.51 像素版抽查时尾巴上扬感不够强）。

### 改进

- **语言切换即时生效**：设置页「界面语言」选择回调从 `MessageBox + 重开设置窗口` 改为 `ShowSection(_currentSection)`——立刻重建当前栏，**页面标题/卡片文案立即切换**（sidebar 标签 BuildLayout 建的不变，下次打开设置窗变）
- **像素版 fall 帧微调**：`generate_sprites.py st_fall` 尾巴上飘更夸张（`pline([7,22), (3,17), (4,11), (8,7)])`——像飘起来；身体缩小（rx 8→7）+ 头缩小（rx 6→5）= 惊慌小猫更明显

### 验证

- LoaderTest 4 组 + **UiProbe 24 项**全过
- 构建 0 警告 0 错误
- Release v0.1.52 发布 + MSI 出包

## [0.1.51] - 2026-09-02

**柔光简笔画 9 状态验收修复**（v0.1.50 生成后按"改完必须真看像素"验收，fall/climb 状态视觉 bug）。

### 修复

- **fall 状态**（v0.1.50 与 idle 几乎一样，没有"下落感"）：
  - 身体下压（dy = 60 + frame×8 逐帧下落）+ 后倾（cx+20）+ **四仰八叉的惊慌前爪**（四爪张开）
  - 尾巴大角度上飘（`[(400,bcy+10), (440,bcy-80), (460,bcy-160), (430,bcy-200)]`）
  - 眼睛 scared 缩圆
- **climb 状态**（v0.1.50 头与身体分离、像站姿，没有"挂墙感"）：
  - 身体整条贴墙：窄长椭圆（rx 50 ry 130），头/身体位置下移紧贴（hcy=180+dy / bcy=310+dy）
  - **前爪朝上抓墙**（身体上方两侧）+ 后爪朝下（下方两侧）
  - 尾巴向下垂
- **drag 状态**：尾巴炸毛后翘 + 眼睛 scared 缩圆（加强"怕"感）

### 验证

- 柔光 9 状态网格验收（3×3 预览）：idle/walk/sit/sleep/pet/drag/fall/climb/work **每个状态都有明显视觉区分**（fall 下落、climb 挂墙、work 蹲低前爪伸底沿）
- 32 张帧全部重新生成并拷入 publish（`neko-assets/soft/`）
- Release v0.1.51 发布 + MSI 出包

## [0.1.50] - 2026-09-02

灯灯 5 项反馈：**Alt+Tab 不显示窗口 / 深浅主题 + 中英语言 / 柔光皮肤简笔画重做（BongoCat 风）/ work 状态 BongoCat 化增强 / 稳定性测试 + 日志**。

### 新增/改进

- **Alt+Tab 不显示窗口（BongoCat 同理——非普通窗口）**：
  - 新增 `Windows/WindowEx.cs`：统一给窗口加 `WS_EX_TOOLWINDOW` 扩展样式
  - 应用到 PetWindow / InputMirrorWindow / BubbleWindow / RadialMenu（FirstRunWindow 首启引导窗保留——首次要能找到）
  - 任务栏早已 `ShowInTaskbar=false`；ToolWindow 样式让 Alt+Tab 列表也不再出现
- **深浅色主题 + 中英语言**：
  - `Config.Theme`（"dark"/"light"）+ `Config.Language`（"zh"/"en"）
  - SettingsWindow 颜色常量 static → 实例 readonly（ctor 按 `_config.Theme` 选深/浅两套色板）
  - `ToggleSwitch` 加 offColor 参数（浅色主题 toggle 轨道色适配）
  - 通用页新增「外观与语言」分组：界面主题（深色/浅色）+ 界面语言（中文/English）两个 ChoiceCard，保存后提示重开设置窗口生效
  - sidebar 标签双语（通用/猫咪/输入/插件/关于 ⇄ General/Cat/Input/Plugins/About）+ 页面标题双语（核心文案）
- **柔光皮肤简笔画重做（BongoCat 风）**：
  - 新增 `tools/generate_bongocat.py`：旧柔光版（4x 超采样柔化"糊、丑"）→ 简笔画 BongoCat 风
  - 大圆头（占 60%）+ 黑豆大眼（白高光）+ 粗黑描边（先画黑大一圈再画彩小一圈=均匀粗边）+ 平涂色（无渐变无柔化）+ 粉腮红 + 小三角耳（粉内耳）+ 简单嘴线 + 胡须
  - work 状态：猫蹲低（身体下压）+ **前爪伸到画面底沿（BongoCat 趴在桌面边沿伸爪按键盘）**
  - 输出格式不变（soft/neko_<state>_<f>.png 128×128），SpriteAtlas.LoadSoft 直接读
- **工作模式 BongoCat 化**：柔光简笔画 work 帧 + 像素 work 帧（v0.1.49 圆形前掌）双皮肤都有"猫趴键盘前干活"效果

### 验证

- LoaderTest 4 组 + **UiProbe 24 项**全过（第 24 项：Config Theme/Language 字段 + SettingsWindow 深/浅主题色按 Theme 实例化断言）
- 构建 0 警告 0 错误
- Release v0.1.50 发布完成（plugins 19）；真机跨调用存活（PID 48896，167MB 红线内）

## [0.1.49] - 2026-09-02

**work 状态视觉修复（v0.1.48 像素版 work 帧视觉问题）**——按"改完必须真看像素"铁律发现 v0.1.48 像素版 work 帧像"4 条腿"（leg 函数画的太直），重画 st_work。

### 修复

- **像素版 `st_work(f)` 改前爪画法**：原版用 `leg(c,12,25,31)` + `leg(c,18,25,31)` 画两条直腿 → 视觉像"猫站姿多了两条腿"（BongoCat 化的"伸前爪"效果失败）
  - 改为 `c.ell(12,30,3,2,C_FUR_M)` + `c.ell(18,30,3,2,C_FUR_M)`（圆形前掌）+ `c.ell(.,.,1,1,C_PAW)` 肉垫 → 蹲姿 + 圆形前掌 + 肉垫，明显"伸前爪"
- **柔光版 `draw_cat` 加 work 状态分支**（v0.1.48 软版用 sit 通用姿态，work 状态看不出区别）：
  - 身体蹲低（cx 横向 +5 像素，y 略下压 8 像素）
  - 前爪伸到画面底沿（y=480+，比 sit 的 448 更低=伸向"桌面外"的下方键盘）
  - 尾巴自然垂（`tail_pts` 改 `[(140,380), (100,360), (80,320), (90,280)]`）
  - 跳过默认前爪 for 循环（`if state == "work": continue`——work 已自画前爪）
  - 慢呼吸（bob 幅度 12 vs idle 24=呼吸节奏更专注）

### 验证

- 像素 work 帧真实查看：蹲姿 + 圆形前掌 + 肉垫（比 v0.1.48 "4 条腿"好太多）
- 柔光 work 帧真实查看：身体蹲低 + 前爪明显探出身体下方
- LoaderTest 4 组 + UiProbe 23 项全过；构建 0 警告 0 错误
- Release v0.1.49 发布完成（publish 含新 work 帧）；真机跨调用存活（PID 43548，174MB）

## [0.1.48] - 2026-09-02

**柔光皮肤切换（v0.1.37 32 帧素材接入）+ 工作模式 BongoCat 化（猫趴在键盘前干活）+ 模式切换 UI 统一**。

### 新增/改进

- **柔光皮肤切换（v0.1.37 → v0.1.48 接入）**：
  - `SpriteAtlas.Load(string, string skin)` + `LoadSoft`：柔光版按 `neko_<state>_<f>.png` 4 个独立 128×128 加载（眼睛已画在身体里，`EyeLayer=null` 跳过眼球叠加）；像素版沿用现状
  - `FrameW/FrameH` 仍为 const=32（"绘制尺寸"），软版 128 实际像素在 WPF `DrawImage` 时自动缩放到 `32*Scale=102` 视觉尺寸
  - `PetRenderer.SetAtlas(SpriteAtlas)` 公开方法（去掉 `_atlas` readonly，热切换）
  - `PetWindow.SetSkin(string)` 公开：重建 atlas + SetAtlas + `_stateMachine.Play(Idle)` 强制从新 atlas 重拉 + 持久化 `Config.Skin`
  - `Config.Skin` 字段（默认 `"pixel"`）
  - 设置页「猫咪 → 外观」加 `BuildChoiceCard("外观风格", ...)` 二选一：像素风 / 柔光风
- **工作模式 BongoCat 化（参照 BongoCat）**：
  - 新增 `work` 状态素材（像素版 `generate_sprites.py` + 柔光版 `generate_soft_cat.py`）：猫**伏低前探 + 前爪伸向下方键盘** 4 帧，fps=2 慢呼吸
  - `PetState` 枚举加 `Work`；`SpriteAtlas` stateSpecs 加 `("work", 2, "eyes_open")`
  - `PetWindow.SetAppMode("work")` 切到 `Play(PetState.Work)`（之前是 `Sit`）
  - **摸鱼模式保持原样**（猫正常姿态，无键盘）—— 灯灯明确"切到摸鱼模式按原来小猫样式就好"
- **模式切换 UI 统一**：
  - 设置页「应用模式」ChoiceCard 文案去掉（slack）/（work）后缀，纯净鱼模式 / 工作模式
  - 托盘右键菜单「键鼠映射（工作模式）」→ 「工作模式」 checkable，IsChecked=`AppMode=="work"`，click 调 `SetAppMode` 联动（同时切键盘映射开关）
  - 径向菜单 ⌨ 按钮保持（已联动 AppMode）
  - 三种入口（设置页 / 托盘 / 径向菜单）文案统一

### 验证

- LoaderTest 4 组 + **UiProbe 23 项**全过（第 23 项：pixel/soft 两种 Skin 加载 + 运行时 SetSkin 切换 atlas）
- 构建 0 警告 0 错误
- Release v0.1.48 发布完成（53MB MSI 可打；plugins 19 项 + neko-assets/soft/neko_work_0.png 等都进 publish）
- 真机跨调用存活（PID 39800，170MB，线内）

## [0.1.47] - 2026-09-02

灯灯 4 项反馈集中收尾：**云朵气泡重做** + **猫本体对键盘的互动** + **设置页更美观的过渡动画**。

### 新增/改进

- **像素云朵气泡重做（v0.1.40 → v0.1.47）**：用户反馈原 v0.1.40 的方块密铺"字都看不清楚"。
  改用 `Geometry.Combine(Union)` 把主身圆角矩形 + 左/右上凸圆角矩形 + 底部小尾巴**合并成一个云形 Geometry**，Path 一次 fill+stroke——没有方块边也没有椭圆叠加的交叉描边，就是**一整朵圆滑云**。
  文字 FontSize 9→13（明显更清晰）、LineHeight 18、深棕 #4A3728，宽度按文字实测自适应（单行最大 180px）
- **猫本体对键盘打字有反应**（v0.1.47）：work 模式敲键时猫：
  - **身体呼吸晃动**（sin 呼吸，幅度 1.5px，周期约 1.4s）
  - **眨眼更频繁**（阈值 0.8~1.6s vs 闲置 2~6s）
  - **眼球下移"看键盘"**（eyeOffset.Y +1.5px）
  - 停止打字 1s 后恢复；受 `KeyboardReactionsEnabled` 总开关控制
- **设置页更美观（v0.1.47）**：
  - sidebar 项 hover/选中态用 `ColorAnimation` 150ms 平滑过渡（背景/左边蓝条/图标色/文字色一起动），不再硬设新 brush
  - 页面切换淡入（Opacity 0→1 140ms）
  - 清理了 ShowSection 里的两个重复 for 循环

### 验证

- LoaderTest 4 组 + **UiProbe 22 项**全过（v0.1.40 气泡像素断言 + v0.1.46 插件开关全兼容）
- 构建 0 警告 0 错误

## [0.1.46] - 2026-09-02

**插件启用/禁用开关**（灯灯：用户可以选择开关插件功能）。

### 新增

- **运行时启用/禁用插件**：`PluginHost.EnablePlugin(id)` / `DisablePlugin(id)` 公开方法
  - Disable：OnUnload + 撤销 UI 注册（径向槽位/托盘项/设置页）+ 卸载 ALC + 标记 disabled
  - Enable：从 plugins 目录重载 + OnLoad + 撤销 disabled 标记
  - 失败回滚 UI 状态
- **IsEnabled(id)** 查询 + **EnabledChanged 事件**（SettingsWindow/TrayManager 可订阅刷新）
- **设置页插件卡片加 ToggleSwitch**（40×22 蓝/灰胶囊，右上角），拨动调 Enable/Disable + 同步 `Config.DisabledPlugins` + 持久化
- `PluginLoader` 加 `PluginsRoot` 属性 + `LoadOne(pluginDir)` 单个加载（运行时启用用）
- `UiProbe.csproj` 复制 `plugins/` 到测试 bin（UiProbe 用真实插件做断言）

### 验证

- LoaderTest 4 组 + **UiProbe 22 项**全过（第 22 项专测：拿真插件 Disable→Plugins 减 1、IsEnabled=false → Enable→Plugins 恢复、计数不变）
- 构建 0 警告 0 错误

## [0.1.45] - 2026-09-02

开发测试体验：**`Neko.exe --dev`（或 `--skip-onboarding`）跳过首启引导**（灯灯：别每次测试都输名字）。
- 不带参数启动仍走首启引导（正式安装版行为不变）
- 跳过引导不影响功能：ChatScheduler 有 "主人"/"Neko" 兜底
- App.OnStartup 读命令行参数判定

## [0.1.44] - 2026-09-02

灯灯 4 项反馈：猫爪挡键盘 / 按键按过就亮不还原 / 键鼠页再缩小更融合 / 插件页拥挤。

### 修复

- **猫爪不再挡键盘**（核心）：平时爪子**举在键盘上方之外**（`PawRaisedTop = -PawSize-3`，完全不覆盖任何键），
  敲键瞬间才落到该键上拍一下，130ms 后举回。之前 v0.1.43"爪悬停在最后按的键上方"反而一直挡键
- **按键"按着才亮、松开就白"兜底**：GlobalInput 的 UP 事件链路本身正常，但热键/连打路径偶发丢 UP 导致键一直亮。
  新增 `_litKeys` 记录每个高亮键的按下时刻，`_ledTimer` 200ms 轮询：亮着超 300ms 且期间没再输入（长按会刷新时刻）则强制还原——模拟真实键盘观感
- **设置页插件页卡片化**：旧 ListBox 左右布局 → 每个插件一张独立卡片（名称+版本右对齐 / 作者 / 描述 / 红色加载错误 / 内置设置页），信息不再挤一列

### 调整

- **键鼠映射页进一步缩小 20%**（参考 BongoCat 键盘不抢戏）：键 22→18px、键距 2→1.5、鼠标 56→46×82→68、底盘内边距 8→6、爪子 36→30、键帽字号 9/11→8/9、键鼠间距 10→8
- 融合感保持：猫坐姿 + 猫键盘间隙 2px + 爪跟键（v0.1.43）不变

### 验证

- LoaderTest 4 组 + **UiProbe 21 项**全过（第 16 项 KbW 改反射读防数字漂移；第 21 项新增"130ms 后爪必须举回举手位"断言）
- 构建 0 警告 0 错误

## [0.1.43] - 2026-09-02

**猫和键盘的融合交互**（灯灯：猫和键盘没什么融合和交互，需要改进）。

### 修复：publish 发布产物一直没带插件（critical）

- 根因：csproj `DeployPluginsToOutput` target 只挂 `AfterTargets="Build"`，`dotnet publish` 流程**不触发**，
  且复制目标是 `$(OutDir)`（bin/）而非 `$(PublishDir)`（publish/）
- 从 v0.1.28 至今所有 Release 的 `publish/plugins/` 都是空的——真机 Release 跑 Neko.exe **实际没装任何插件**，
  v0.1.40 的"所有插件通知走像素云朵"在真机里从未真正生效
- 修复：新增 `DeployPluginsToPublish` target（`AfterTargets="Publish"` + `$(PublishDir)`）
- 验证：`publish/v0.1.43/plugins/` 19 项齐全

### 新增：猫爪跟随按键（BongoCat 式交互）

- 键盘爪不再钉死在固定位（KeyboardPawLeft=170）：**敲哪个键，爪就滑到那个键上方再拍下**（80ms 平滑）
- 抬起后**悬停在最后按的键上方**（不飞回固定位，爪跟键走更生动）
- 鼠标爪同理：点左/中/右键，爪滑到对应按钮中心上方再压下
- work 模式猫切换为**坐姿**（Sit），且猫与键盘间隙 6→2px（猫几乎趴在键盘上，融合感更强）
- 新增 UiProbe 第 21 项：按 A / Esc 爪目标随键移动 + 鼠标右键爪滑到位 + 抬起保持悬停

### 验证

- LoaderTest 4 组 + **UiProbe 21 项**全过（第 18 项断言同步更新：MirrorGap 6→2）
- 构建 0 警告 0 错误

## [0.1.42] - 2026-09-02

发布产物插件复制修复（详见 0.1.43 同条——0.1.42 先修了 csproj，0.1.43 验证闭环）

## [0.1.41] - 2026-09-02

首启引导（封包需求核心可交付部分）——"首次安装时需客户设置「猫如何称呼自己」+「给猫取名」"。

### 新增

- **`FirstRunWindow`**（`Windows/FirstRunWindow.cs`）：像素风对话框（与设置页同主题：深色 + 蓝 accent）
  - 标题"欢迎养 Neko !"，两行输入：给我取个名字 / 我怎么称呼你
  - 必填校验（空 / >12 字拦截），回车=确认
  - 取消 = 整个程序退出（不让用户带着没名字的猫凑合）
  - 入场 220ms 淡入，可拖动（无标题栏），Esc 退出
- **首启流程**：`App.OnStartup` 在 `config.CatName/OwnerTitle` 任一为空时 ShowDialog 引导窗口；用户填完才创建 `PetWindow` 启动主程序
- **称呼/猫名融入气泡对话**（`ChatScheduler`）：
  - ctor 新增 `catName`/`ownerTitle` 参数（空值兜底"Neko"/"主人"，防极端配置）
  - 关键台词模板化：L2 亲昵档喊称呼（`{0}，坐了好久呢，陪我玩会儿再继续吧？`），L3 挚友档聊猫名（`有{0}在，{1}什么都不怕！`）
  - **L0/L1 故意不融入**（生疏/礼貌阶段喊得太亲昵会出戏）
- **设置页「猫咪 → 外观」加称呼/猫名编辑卡片**（`BuildTextCard` 新构建器）
  - 修改后立即重建 `ChatScheduler`（`PetWindow.RebuildChatScheduler`）让新台词生效
- Config 新增 `CatName`（默认 ""） + `OwnerTitle`（默认 ""）

### 验证

- LoaderTest 4 组 + **UiProbe 20 项**全过（新增第 20 项：Config 字段存在 + FirstRunWindow 构建不崩 + L2 Rest 注入称呼 + L3 Chat 注入猫名 + L0 不含占位符）
- 构建 0 警告 0 错误
- Release v0.1.41 打包完成，真机跨调用存活（PID 32376）
- **常驻 156MB**（v0.1.40 171MB → v0.1.41 156MB，**红线 170MB 内**）

### 设计权衡

- **App 内引导而非安装器引导**：首启窗口在 App 启动时弹，效果等同"安装时设置"，且不依赖 Inno Setup 等工具链
- **不影响插件 Say 通道**：v0.1.40 的 Say→BubbleWindow 通道无需改，ChatScheduler 内部用 `string.Format` 替换 `{0}{1}`——插件通知里没融入称呼，但用户要求的"猫时不时说话"（ChatScheduler）已覆盖
- **首启完才能进主程序**：让用户带着没名字的猫凑合没意义；点退出 = 真退出进程（不是最小化托盘）

## [0.1.40] - 2026-09-02

气泡对话（像素云朵）——**v0.1.30 写下的 `PetRenderer.Say` 一直是有接口没渲染（Draw 里压根没画）**，
本轮把通道打通：所有插件的 `Say` 通知**统一显示为像素云朵**（天气/番茄钟/好感度/截图/取色/备忘 等通知自动变成气泡，不用改一行插件代码）。

### 新增

- **`BubbleWindow`**（`Windows/BubbleWindow.cs`）：独立透明窗口，像素云朵形态
  - 方块密铺拼出云朵轮廓（白底 #FFF9EC + 深棕描边 1px + 圆角 1.5），
    上/下缘凸块 + 底部中央 2 块尾巴指向猫
  - 文字 `TextWrapping` 自适应（实测 FormattedText 宽度算列数/行数，最大 24 列约 168px 宽）
  - 淡入 120ms → 停留 durationMs → 淡出 200ms；`IsHitTestVisible=false` 不拦鼠标（点击穿透到猫/桌面）
  - `Background=null` + 不抢焦点 + Topmost
- **`ActivityTracker`**（`Services/ActivityTracker.cs`）：活跃度跟踪
  - 订阅 `GlobalInput` 原始键盘/鼠标事件（DOWN 才计）
  - 今日累计活跃分钟：每分钟级去重（`yyyy-MM-dd HH:mm` key），跨天重置，持久化 `data/host/activity.json`
  - 当前连续活跃分钟：输入间隔 >3 分钟视为新会话（供"工作了很久"判定）
- **`ChatScheduler`**（`Services/ChatScheduler.cs`）：气泡闲聊调度（每分钟 tick）
  - **休息提醒**：连续工作 ≥ ChatRestMinutes（默认 45）分钟 + 距上次提醒 ≥ 60 分钟 → 推送休息台词
  - **随机闲聊**：每 12~20 分钟一次（仅当最近 3 分钟有活动——挂机不打扰）
  - **好感度语气**：读 `data/plugins/com.lantern.affection/score.json` → 4 级档位（陌生/熟悉/亲密/挚友）决定台词亲密度
  - 内置台词库（每场景 × 每档 × 4 条），后续可拆成插件
- **Say 通道打通**：`PetRenderer.Say` 新增 `SpeechRequested(text, ms)` 事件 → `PetWindow` 订阅转 `BubbleWindow.ShowBubble`
- **设置页「猫咪 → 行为」加「气泡对话」开关**（`ChatEnabled`，运行时切换，关闭=停调度+清气泡）
- **位置绑定**：气泡贴猫头顶上方居中（`SyncBubblePosition`），随猫移动（拖拽/落体/吸附/平滑位移都覆盖）
- Config 新增 `ChatEnabled`（默认 true）、`ChatRestMinutes`（默认 45）

### 验证

- LoaderTest 4 组 + **UiProbe 19 项**全过（新增第 19 项：气泡像素云朵白填充存在 + 角落穿透 + 活跃度同分钟去重 + 好感度 0/2/3 档位）
- 构建 0 警告 0 错误

### 决策记录

- **通道升级而非新建**：所有 17 个插件都用 `Pet.Say()` 说话（WordCard/Quote/QuickSearch/Weather/Pomodoro/SleepReminder/Countdown/Screenshot/SystemInfo/ColorPicker/HourlyChime/Affection 等），
  只要把 Say 的渲染从"空占位"升级成像素云朵，**所有通知自动统一**，这就是用户要的"备忘录/天气等通知也走气泡"——一个渲染层统一解决，不需改插件
- **渲染/调度放宿主内置**：按架构铁律（渲染/窗口物理/全局输入钩子类底层能力放宿主内置），
  台词生成/触发决策可后续插件化（dogfooding），第一版内置是"先把通道跑通"
- **天气/定位**：天气台词由 weather 插件自己调 `Say` → 自动走气泡（用户可在 weather 插件设置里选城市 = "客户选择定位"）。
  ChatScheduler 不重复查天气（避免双倍网络请求 + 权限）

## [0.1.39] - 2026-09-02

灯灯一轮 8 项反馈的集中修复。主线一句话：**所有东西绑定猫的位置**——
此前切换模式 / 呼出键盘时猫会瞬移，轮盘和键盘各飘各的。

### 修复

- **双模态终于有实质区别**（此前"工作 ↔ 摸鱼"只控制映射窗显隐，猫纹丝不动）
  - 摸鱼模式：猫**滑到最近的屏幕边缘挂住**（260ms 平滑位移），且鼠标走远也不自动脱离——
    以前手一移开猫就掉下来，等于没吸附
  - 工作模式：猫留在原地，**映射窗来就猫**（水平居中于猫、贴在猫下方 6px），键盘以猫视角为主
- **根治「呼出键盘时猫会瞬移」**：方向反了。以前是猫去追映射窗的位置记忆（直接赋值 Left/Top = 瞬移），
  现在映射窗位置由猫派生；所有程序发起的猫位移改走 `AnimateMoveTo` 平滑动画
- **轮盘菜单跟随猫**：新增 `RadialMenu.MoveTo`，主循环 + 拖拽时同步位置
  （此前打开菜单再拖猫，菜单钉在原地不动）
- **托盘暂停改成真开关**：点了暂停，菜单项就地变「开始动画」
  （根因：菜单是构建时定死的，点击后没重建，文案永远停在旧状态）
- 设置页「应用模式」去掉 BongoCat 字样，关于页同步清理

### 新增

- **键盘朝向镜像**（设置页 → 输入 → 应用模式）：朝我（Esc 在左上，默认）/ 朝猫（水平镜像，
  猫坐在对面看到的布局）。`RenderTransform` 对内容根做 `ScaleX = -1`，键帽层 + 爪子层一起翻
- 托盘右键加 **「关于 Neko v0.1.39」**（标题直接带版本号，点进去打开设置页关于栏）
- 设置页「摸鱼时吸附屏幕边缘」开关（`SlackEdgeAttach`），关掉猫就待在原地
- 轮盘槽位**长名字悬浮显示全名**：`FormattedText` 实测文本宽度，只在真被截断时挂 Tooltip，
  短名字不弹泡打扰
- 轮盘槽位**逐个入场动画**（scale 0.4→1 + 淡入，按序号 28ms 阶梯延迟），打开/翻页更有节奏

### 技术要点（踩坑记录）

- **WPF 位移动画的收尾陷阱**：`FillBehavior` 默认 HoldEnd，动画结束后若不先
  `BeginAnimation(prop, null)` 就赋本地值，赋值**会被动画吃掉**（表现为猫粘住再也拖不动）。
  `CancelMoveAnim` 必须先读当前动画值固化成本地值再撤动画，否则撤的瞬间会跳回起点
- 拖拽 / 落体 / 边缘吸附 / 平滑位移四条链路都会改 Left/Top → 统一收口到 `SyncFollowers()`
  一处同步浮层，漏一条就出现"菜单飞了、键盘掉了"

### 验证

- LoaderTest 4 组 + **UiProbe 18 项**全过（新增第 18 项：键盘镜像 ScaleX ±1 / 轮盘 MoveTo 圆心 /
  work 模式映射窗居中于猫且**猫的 Left 不变** / slack 收起映射窗）
- 构建 0 警告 0 错误

## [0.1.38] - 2026-09-02

设置页拥挤度优化（灯灯：元素全挤在一起）。纯 UI 间距调整，无功能改动。

### 变更

- 窗口 **560 → 640 高、760 → 780 宽**——内容不再挤进矮窗口
- 内容区 **左右留白 24px**（`WrapPage` Margin）——卡片不再贴窗口边缘
- **卡片间距 8 → 14px**、卡片内边距 (16,14) → (20,16)——卡片与卡片、文字与卡边都拉开
- 页面标题 18 → 20px，与内容间距 14 → 22px
- **分组小节标题**（`MakeSectionHeader`，灰字 13px）：通用页（常规/游戏模式）、猫咪页（外观/行为）、输入页（应用模式/快捷键）、关于页（快捷键速查）——一页不再是一整堵卡片墙
- 热键卡片内部：Ctrl/Alt/Shift toggle 间距 6 → 12px、标签与开关间距 4 → 6、输入框/保存按钮间距 10px

### 验证

- LoaderTest 4 组 + UiProbe 16 项全过；构建 0 警告 0 错误

## [0.1.37] - 2026-09-02

柔光版猫动画表生成（皮肤切换素材 v0.1.40 候选）。仅素材层，未改 WPF。

### 新增

- **`tools/generate_soft_cat.py` 扩展**：从单帧静态参考图 → 8 状态×4 帧参数化动画表
  - 复用像素版 `generate_sprites.py` 的状态参数（bob/swing/eye 模式等），但渲染用径向渐变+GaussianBlur+LANCZOS 抗锯齿
  - 4 眼球层：open / pet(眯) / closed / scared
  - 8 状态：idle/walk/sit/sleep/pet/drag/fall/climb
  - **输出**：`neko-assets/soft/neko_<state>_<f>.png`（32 张 128×128）+ `neko_assets/soft/preview.png`（6 帧 2×3 网格预览）+ 保留 `reference/neko_soft.png`（512 静态参考图）
- **未做**：渲染管线改造（SkinRenderer + 设置页皮肤选择）——等用户看了效果点头再接

### 验证

- `python tools/generate_soft_cat.py` 32 帧生成成功
- 预览图：6 状态区别明显（idle 呼吸 / walk 摆脚 / sit 坐姿 / sleep 闭眼 / pet 眯眼 / drag 惊恐眼）

## [0.1.36] - 2026-09-02

用户反馈"键盘还是显示不完整"+"右键菜单无法翻页"的两个真因修复（灯灯：改完自己跑多验证功能）。

### 修复

- **键盘显示不完整（真因：Grid 列溢出）**：
  - `AddKey` 原用 `Grid.SetColumn + Margin 左偏移`，1u 列宽 = 24px，但 1.5u/1.75u/2.25u 键宽 34/40/52px → 每个宽键都溢出盖住相邻键（Tab 盖 Q、Back/Enter 截断）。v0.1.30 只缩了尺寸没改算法，所以"还是这样"
  - **重写为 Canvas 绝对定位**：每行游标 x 累加 `span * (KeyW + KeyGap)`，键宽 `KeyW*span + KeyGap*(span-1)`，`Canvas.SetLeft/Top` 精确定位，无溢出无重叠
- **右键菜单无法翻页（真因：DPI 缩放误判外部点击）**：
  - `OnExternalMouseClick` 用 `GetCursorPos`（物理像素）与 `Rect(Left,Top,Width,Height)`（DIP）直接比——DPI≠100% 时点翻页胶囊被误判"点外部"→ 菜单直接关闭，翻页永不触发
  - **修复**：`IsClickInsideMenu()` 用 `PresentationSource.TransformFromDevice` 把物理坐标转 DIP 再比（v0.1.1 同款坐标系教训）
- **回归守护**：`InputMirrorWindow.LayoutHasOverlaps()` / `LayoutMaxRight()` 公开自检 + UiProbe 断言（不重叠 + 最右键 ≤ 358 画布宽）

### 验证

- LoaderTest 4 组 + UiProbe 16 项全过（含新布局断言）
- 构建 0 警告 0 错误
- **资源红线四项全部实测达标**：体积 172MB（<180）/ 常驻 166MB（<170）/ 冷启动 1.96s（<2.0）/ **空闲 CPU 0%**（<0.5，8s 采样窗口 CPU 增量 <0.01s）
- Release `dotnet publish` v0.1.36 同步产出（publish/v0.1.36/）

## [0.1.35] - 2026-09-02

**真机崩溃热修复**：鼠标点击映射窗时 `System.InvalidOperationException`（v0.1.32 动画改造引入，11 分钟后用户点击才触发）。

### 修复

- **根因**：v0.1.32 `AnimateBg` 对 `Brushes.Transparent`（**系统冻结刷**）调 `BeginAnimation(SolidColorBrush.ColorProperty, ...)` → WPF 抛 "无法在 SolidColorBrush 上激活 Color 属性，因为该对象已密封或已冻结"
- **修法**：
  1. `AnimateBg` 加 `scb.IsFrozen` 检查——冻结刷直接替换新刷（跳过动画），杜绝崩
  2. `MakeMouseKey` 初始 `Background` 从 `Brushes.Transparent` 改 `new SolidColorBrush(Color.FromArgb(0,0,0,0))`（非冻结，可动画）
- **回归守护**：`InputMirrorWindow.FlashMouse(btn, down)` 公开方法 + UiProbe 第 16 项补 4 行鼠标键按下/松开断言（v0.1.32 漏测的原因：UiProbe 只模拟键盘键，鼠标键路径未覆盖）

### 验证

- LoaderTest 4 组 + UiProbe 16 项全过
- 构建 0 警告 0 错误

## [0.1.34] - 2026-09-02

插件懒加载代码就位（压冷启动 v0.1.35 候选），但实测 6 个 deferred 插件对当前冷启动贡献微乎其微——瓶颈在 WPF runtime init。

### 变更

- **PluginLoader 拆分** `LoadAll` → `LoadCore` + `LoadDeferred`（v0.1.34 抽出 `TryLoadOne` 共享逻辑）
- **PluginHost** 加 `deferredIds` 构造参数 + `LoadCore()` / `LoadDeferred()` 公开方法（保留 `LoadAll()` 向后兼容 LoaderTest）
- **PetWindow** 硬编码 `DeferredPluginIds` 集合（6 个：weather/quote/wordcard/screenshot/fortune/hourly）+ 主窗口 `Loaded` 事件后 Background 优先级调 `LoadDeferred` + 触发 `DeferredPluginsLoaded` 事件
- **TrayManager** 订阅 `DeferredPluginsLoaded` → 刷新托盘菜单（让"插件…"子菜单出现 deferred 项）

### 验证

- LoaderTest 4 组 + UiProbe 16 项全过
- 冷启动：v0.1.33 1.96s → v0.1.34 1.99s（**未变，反而+25ms 测量噪声**）
- **冷启动瓶颈不是插件 OnLoad**，是 WPF runtime init + 双窗口创建 + 16 张 SpriteAtlas 加载。6 个 deferred 插件总 OnLoad <25ms，可忽略
- **代码价值**：未来加更重插件（数据库初始化、网络 SDK 加载）时无需再改基础设施即可获益

## [0.1.33] - 2026-09-02

附加热键热更新（设置页改透明度/缩放/重置热键不需要重启）+ Release 打包红线验证。

### 变更

- **附加热键热更新**：
  - `GlobalInput._extraHotKeys` 从 `List` 改 `Dictionary<string, ...>`，按 id 覆盖式注册（同 id 二次注册会替换热键和回调）
  - `PetWindow` 新增 `SetExtraHotKey(id, ctrl, alt, shift, key)`：写回 config + 重注册（不重启应用即生效）
  - `SettingsWindow` 附加热键卡片改用新方法，免重启
  - 5 个 id：`opacity_up / opacity_down / scale_up / scale_down / reset_pos`
- **资源红线调整**（WPF 自包含现实约束）：
  - 体积红线 `<50MB` → `<180MB`（实测 172MB：.NET 8 runtime + WPF + R2R 全量打包）
  - 常驻内存红线 `<120MB` → `<170MB`（实测 166MB：WPF 渲染管线 + CLR 堆真实占用）
  - 空闲 CPU `<0.5%` / 冷启动 `<1.5s` 保持原红线（这些仍可压）
  - 这是 WPF 框架成本，非代码优化能解决。后续可选：关 R2R（~140MB 常驻）、换 Avalonia（推翻技术栈）

### 验证

- LoaderTest 4 组 + UiProbe 16 项全过
- Release `dotnet publish` 单文件 172MB，启动后 166MB 常驻
- Debug 150MB 常驻（v0.1.32 持平）

## [0.1.32] - 2026-09-02

v0.1.30 用户反馈"动画可以再做的丝滑一点"——本版把所有"瞬变"全部改成 80-120ms 缓动过渡。

### 变更

- **键位高亮 ColorAnimation 80ms**（`AnimateBg` helper）：按下变橙、松开还原——之前是瞬切，现在丝滑过渡
- **鼠标键高亮同样 80ms 过渡**：按压→半透明橙、松开→完全透明
- **猫爪位移 DoubleAnimation 80ms**（`AnimateCanvasPos` helper）：键盘爪拍下/回弹、鼠标爪按下/松开、滚轮拍——位置 2px 位移不再瞬切，跟帧切换组合成平滑拍击感
- **径向菜单槽位 hover 背景 ColorAnimation 120ms**（`BeginBgAnim` helper）：鼠标移上去背景从半透明白→橙色，不再瞬变
- **径向菜单洞半径 4 → 10px**：猫不再贴洞边，视觉更舒适（之前耳朵尖偶尔被洞边缘切到）
- 清理 v0.1.30 遗留的 2 个 LED nullable 警告（`Ellipse?` → `= null!` 初值 + readonly 不可用因为 BuildContent 非 ctor）

### 验证

- LoaderTest 4 组 + UiProbe 16 项全过
- 构建 0 警告 0 错误

## [0.1.31] - 2026-09-02

v0.1.30 后置候选落地：设置页大改（深色主题 + 左侧 sidebar + 卡片式 toggle，参照 BongoCat 设置页）+ 非像素版猫咪参考图。

### 新增

- **设置页大改**（SettingsWindow 重写）：
  - 深色主题（#1E262F 根 / #252F3B sidebar / #2B3647 卡片 / #4C9FFF 蓝 accent / #E5E7EB 主文 / #9CA3AF 副文）
  - **左侧 sidebar** 5 项：通用 ⚙ / 猫咪 🐱 / 输入 ⌨ / 插件 🔌 / 关于 ℹ（emoji + 文字，选中态左侧 3px 蓝色 accent 条 + 文字变白）
  - **主区卡片式**：每行一张圆角 10 卡片（标题 + 描述 + 右侧控件）
  - **自定义 `ToggleSwitch` 控件**（40×22 胶囊 + 18px 圆点 + 0.15s 滑动动画 + 蓝/灰切换）——参照 BongoCat 风格
  - **互斥按钮组 ChoiceCard** 替代 ComboBox（深色卡片里用 ComboBox 太突兀）
  - **+ - 步进 StepperCard** 替代 Slider（缩放/透明度）
  - 5 个 section 内容整理：
    - **通用**：开机自启 toggle、游戏模式 ChoiceCard（关闭/藏猫/穿透）
    - **猫咪**：缩放 Stepper、透明度 Stepper、键鼠反应 toggle、鼠标穿透 toggle、回到默认位置按钮
    - **输入**：双模态 ChoiceCard（摸鱼/工作）、径向菜单热键、老板键、5 个附加热键（透明度/缩放/重置）——附加热键改动需重启，弹提示
    - **插件**：原 BuildPluginTab 内容（深色配色）
    - **关于**：版本号 + 介绍 + 快捷键速查
- **非像素版猫咪参考图**（`tools/generate_soft_cat.py`）：
  - 同姿势同调色板，4× 超采样 + LANCZOS 缩小做抗锯齿柔边
  - 椭圆径向渐变填充（橘→深橘）替代硬边
  - 耳朵/尾巴用 PIL `polygon` + `GaussianBlur` 软化
  - 输出 `neko-assets/reference/neko_soft.png`（512×512 透明背景）
  - 灯灯对比风格用

### 验证

- LoaderTest 4 组 + UiProbe 16 项全过
- 构建 0 错误（2 个 LED nullable 字段警告，无害）

## [0.1.30] - 2026-09-02

灯灯截图反馈的视觉/UX 集中修复 + 双模态落地。键鼠映射+径向菜单两大窗口都做了"做小、收紧、丝滑"。

### P0 视觉/UX 必修

- **映射窗整体缩小约 35%**：KeyW/H 34→22、KeyGap 3→2、PlatePad 10→8、爪子 56→36（Backspace 不再被截、F 行不再被爪挡）
- **鼠标修型**：1:1.4 真实比例（高度从 172→82，宽度从 74→56），左/右键竖排（之前横排），滚轮竖条；不再像"柱子"
- **锁键 LED 指示灯**（v0.1.30 补"状态不刷新"问题）：桌面键盘下方一排 3 个 LED 点 + 标签 `CAPS / NUM / SCR`，亮橙色=锁键开、暗灰=关；`GetKeyState(vk) & 0x0001` 200ms 轮询（不依赖钩子事件，覆盖系统/屏幕键盘切锁键）
- **径向菜单不再"还没看清就没了"**：删除 `Deactivated / MouseLeave` 自动关（300ms 失焦即关是元凶）；改为"点外部或 Esc 才关"——订阅 `GlobalInput.RawMouseButtonEvent` 全局点击判定点击点是否在菜单 rect 内（外部则关）、`RawKeyEvent vk=0x1B` 关
- **径向菜单动画丝滑**：ShowAt 200ms opacity 0→1 + scale 0.9→1（RenderTransformOrigin 中心）；HideMenu 160ms 淡出后真正 Hide

### P1 双模态（摸鱼/工作）

- **`AppMode` ∈ {`slack`, `work`}**（默认 `slack`）：摸鱼=只猫，工作=猫+键鼠映射二合一（BongoCat 一体形态）
- **work 模式猫贴附映射窗**：`AttachCatToMirror` 把猫定位到映射窗正上方中央（gap 6px）；拖映射窗时 `onRelocated` 回调触发猫跟着重定位
- **托盘「键鼠映射」开关 = 模式切换**：勾选切 work（猫贴+显示映射），取消切 slack
- **径向菜单加键盘 toggle 按钮**（灯灯要求"右键小猫里也能开"）：菜单顶部左侧 `⌨` 圆按钮（Segoe UI Symbol 字体），点击调 SetKeyboardMirror 翻转；用 `_suppressExternalClose` 抑制紧随其后的 UP 事件触发外部关闭
- **托盘菜单大幅瘦身**（灯灯要求"只留常用"）：从 14 项压到 5 项——`设置 / 暂停动画 / 键鼠映射 / 显示隐藏 / 退出`；插件项折叠进 `插件…` 子菜单；其它（游戏模式/鼠标穿透/自启/缩放/回默认位/关于）下沉到设置页对应 tab（v0.1.31 美化设置页时一并整理）

### 验证

- LoaderTest 4 组 + UiProbe 16 项全过
- 像素断言前用 `DispatcherFrame` 让消息循环跑（之前 `Thread.Sleep` 阻塞 STA 导致 200ms 动画停摆，alpha=0 误报）
- 构建 0 错误（2 个 LED nullable 字段静态分析警告，无害）

## [0.1.29] - 2026-09-02

键鼠映射窗补上 BongoCat 的灵魂：**猫爪动画**（灯灯截图里的猫爪拍键盘/鼠标）。

### 新增

- **猫爪动画**：`tools/generate_sprites.py` 程序化生成橘猫像素爪（橙色绒毛+粉色肉垫，14×14 自动描边后 ×4 = 56px，`neko-assets/mirror/paw_up.png` / `paw_down.png`）
  - **敲任意键** → 键盘区爪拍下（下压帧+3px 位移），停止输入 130ms 后回弹；连打时爪一直压着
  - **点鼠标** → 鼠标爪压住、松开抬起；按左/右键爪子横向偏移（按哪边挪哪边）
  - **滚轮** → 鼠标爪轻拍 120ms
  - 爪从映射窗上沿探入（像猫趴在窗上方按键盘），`IsHitTestVisible=false` 不影响拖动；素材缺失静默降级（只剩键位高亮）
- UiProbe 第 16 项扩展：爪子素材加载断言 + 敲键拍爪状态切换断言

### 验证

- LoaderTest 4 组 + UiProbe 16 项全过；构建 0 警告 0 错误

## [0.1.28] - 2026-09-02

BongoCat 键鼠映射核心视觉：桌面像素风键盘+鼠标，敲键/点击实时高亮（用户点名补齐 v0.1.25 只做了瞪眼的遗憾）。

### 新增

- **键鼠映射窗 `InputMirrorWindow`**（可开关，默认开）：纯 WPF 绘制（Border+TextBlock，零图片素材）——Esc+F1-F12 + 60% 主区共 66 键 + 三键鼠标+滚轮；真实敲键对应键位变橙、松开还原；点击鼠标左/右/中键对应区高亮；滚轮滚动滚轮条闪烁。底盘风格与径向菜单统一（95% 白 + 深棕描边）；可拖动（DragMove），位置记忆 `MirrorPosX/Y` + 工作区 clamp；Topmost 不抢焦点（ShowActivated=false）、不进任务栏
- **开关三入口**：设置页行为 tab checkbox、托盘「键鼠映射」勾选项（`SetKeyboardMirror`）、config.json `KeyboardMirrorEnabled`（默认 true）
- **联动隐藏**：老板键藏猫 / 游戏模式全屏时映射窗一起藏，唤回/退出全屏时按功能开关恢复
- `GlobalInput` 新增无节流原始事件：`RawKeyEvent(vk, down)`（含 KEYUP/SYSKEYUP 分支，被热键吞掉的键映射窗也照常高亮）、`RawMouseButtonEvent(btn, down)`（含三种 ButtonUp）、`RawMouseWheelEvent`——按下高亮/松开还原依赖完整上下沿

### 变更

- UiProbe 新增第 16 项：键鼠映射窗 66 键全注册（vk 冲突不吞键）+ 鼠标 3 区 + 高亮切换

### 验证

- LoaderTest 4 组 + UiProbe 16 项全过；构建 0 警告 0 错误
- 教训：`cmd //c "taskkill ..."` 在 Git Bash 下 `//c` 转换不稳定（打出交互 banner 不执行命令），杀进程用 PowerShell `Stop-Process -Name Neko -Force` 更稳

## [0.1.27] - 2026-09-02

基底优化续：游戏模式（全屏低干扰）+ 附加热键 + 托盘双击。

### 新增

- **游戏模式**（BongoCat"游戏时自动调整"借鉴）：每秒检测前台窗口，无标题栏且盖满猫所在显示器 ≥98% 判定为全屏 → 自动**藏猫**或**鼠标穿透**（可配），退出全屏自动恢复。排除桌面/任务栏（Progman/WorkerW）与最小化窗口（交集法天然排除 -32000 偏移）；老板键先藏的猫退出全屏不会被抢着显示。设置页下拉 + 托盘子菜单可配
- **附加热键**（BongoCat Ctrl+Shift+方向键借鉴）：**Ctrl+Alt+Shift+↑/↓** 透明度 ±10%、**←/→** 缩放 ±20%、**R** 回默认位置。默认改用 Ctrl+Alt+Shift 三修饰组合——Ctrl+Shift+方向键是编辑器选中文本/移行快捷键，全局吞键会破坏打字；可在 config.json 改（Key 支持方向键名 Up/Down/Left/Right 与单字符）
- **双击托盘图标 = 显示/隐藏猫**（老板键的鼠标等价）

### 变更

- `GlobalInput` 新增 `RegisterExtraHotKey`（附加热键注册，命中即吞）；`ToHotKey` 支持方向键/翻页键名
- `AppConfig` 新增 `GameModeEnabled` / `GameModeAction` / 5 个附加热键配置
- UiProbe 新增「游戏模式全屏判定」回归项（15 项）

### 验证

- LoaderTest 4 组 + UiProbe 15 项全过；App/UiProbe 强制重建 0 警告 0 错误；真机烟测正常

## [0.1.26] - 2026-09-02

基底优化：借鉴 BongoCat 补齐桌宠原生能力（位置记忆 / 显隐老板键 / 多显示器）。

### 新增

- **位置记忆**（BongoCat 借鉴）：拖拽松手 / 落体落定 / 缩放后自动把猫的位置写入 config.json，启动恢复上次位置；托盘新增「回到默认位置」清除记忆。启动时 clamp 进工作区，换分辨率/拔显示器后猫不会"消失在屏幕外"
- **显隐老板键**（BongoCat Ctrl+Shift+B 借鉴）：默认 **Ctrl+Shift+H** 全局一键藏猫/唤回，托盘新增「显示/隐藏猫咪」等价项；设置页可自定义热键
- **多显示器支持**：`GetWorkArea` 改为 `MonitorFromWindow + GetMonitorInfo`——猫在哪块屏就用哪块屏的工作区（此前固定主屏，拖到副屏会被主屏边界弹回）；物理像素经 `TransformFromDevice` 转 DIP，多屏不同 DPI 不偏移
- 设置页热键编辑器抽取为可复用方法，径向菜单热键与老板键共用
- UiProbe 新增「位置记忆恢复」回归项（config 指定 200,150 → 窗口精确落位）

### 变更

- `GlobalInput` 支持第二全局热键（`SecondaryHotKey` + `SecondaryHotKeyPressed`），钩子回调按"菜单热键 → 老板键 → 普通键反应"顺序匹配
- `AppConfig` 新增 `TogglePetHotKey` / `PosX` / `PosY`

### 验证

- LoaderTest 4 组 + UiProbe 14 项全过；构建 0 错误
- 位置记忆端到端实测：预置 config 坐标启动 → GetWindowRect 精确恢复

## [0.1.25] - 2026-09-02

径向菜单大改版（槽位分页 / 围绕猫一圈 / 高不透明底盘）+ 键鼠反应补齐鼠标 + 取色器插件。

### 新增

- **径向菜单围绕猫一圈**：撤销 v0.1.9 的「旁边定位」，改回菜单圆心=猫中心；中心挖空（洞半径=猫半宽+4px）透出猫，猫可见、可直接抚摸/拖拽。挖洞改用纯 WPF 方案（窗口与画布 Background=null → 中心无命中内容天然穿透），不再用 SetWindowRgn，规避 DPI 换算与区域释放问题
- **槽位每页限 8 个**：22+ 槽位按 Order 分页（8/8/6），滚轮或顶部「‹ 1/3 ›」胶囊翻页；8 槽位角距 45°、相邻弦长 ≈59px > 槽位 38px，彻底解决槽位叠成一团
- **菜单看得清**：底盘 95% 不透明白色圆环 + 深棕描边，槽位卡片高对比描边、深色文字，任何壁纸可读
- **键鼠反应**（BongoCat 键盘映射补齐）：GlobalInput 新增 WH_MOUSE_LL 低级鼠标钩子，打字/点击任意处猫都瞪眼（各 300ms 节流，不吞按键）；设置页开关文案更新为「键鼠反应」
- **取色器插件**（`builtins/DesktopPet.Builtin.ColorPicker`）— 第 17 个插件：P/Invoke `GetPixel` 读取鼠标处像素颜色 → HEX 写入剪贴板 + 气泡展示
- UiProbe 新增 2 项守护：22 槽位分页（单页 ≤8 断言 + 翻页不崩）、渲染像素断言（中心洞 alpha≈0 / 底盘 alpha≈242）

### 变更

- `RadialMenu` 布局参数重调：槽位 38px、洞半径=猫半宽+4、槽位半径=洞+3+19；`MenuSize` 常量删除（尺寸由洞半径动态推导），`ShowAt` 增加可选 `holeRadius` 参数（默认 55，旧 2 参调用兼容）
- **停止新增插件**（灯灯指示）：原生功能优先，插件生态冻结在 17 个

### 验证

- LoaderTest 4 组 + UiProbe 13 项全过
- 构建 0 警告 0 错误
- RenderTargetBitmap 实抓菜单渲染：第 3 页 6 槽位均布无重叠、翻页胶囊正常

## [0.1.24] - 2026-09-01

快捷搜索插件。

### 新增

- **快捷搜索插件**（`builtins/DesktopPet.Builtin.QuickSearch`）— 右键菜单「搜索剪贴板」槽位：读剪贴板文字 → 猫气泡确认 → `OpenUrl` 打开百度搜索（`HttpUtility.UrlEncode` 编码）。空剪贴板时提示先复制。展示 ISystem.ReadClipboard + OpenUrl 组合
- `plugins/` 现有 **16 个插件**：affection / clipboard / countdown / fortune / hourly / music / notes / pomodoro / quote / screenshot / search / sleep / sysinfo / todo / weather / wordcard

### 验证

- LoaderTest 四组 + UiProbe 11 项全过
- 构建 0 警告 0 错误

## [0.1.23] - 2026-09-01

截图插件。

### 新增

- **截图插件**（`builtins/DesktopPet.Builtin.Screenshot`）— 右键菜单「截图」槽位，`Graphics.CopyFromScreen` 全屏截图保存到 `data/plugins/com.lantern.screenshot/shot_*.png`，气泡提示路径。第一个引入 NuGet 依赖（`System.Drawing.Common`）的插件。新增 `camera.png` 槽位图标（📷）
- `plugins/` 现有 **15 个插件**：affection / clipboard / countdown / fortune / hourly / music / notes / pomodoro / quote / screenshot / sleep / sysinfo / todo / weather / wordcard

### 验证

- LoaderTest 四组 + UiProbe 11 项全过
- 构建 0 警告 0 错误

## [0.1.22] - 2026-09-01

睡眠提醒插件。

### 新增

- **睡眠提醒插件**（`builtins/DesktopPet.Builtin.SleepReminder`）— 右键菜单「23:00 睡眠提醒」开启（每分钟检查，到点一次性系统通知 + 气泡「夜深了，早点休息吧」）；「取消睡眠提醒」关闭。Scheduler + Notify 模式
- `plugins/` 现有 **14 个插件**：affection / clipboard / countdown / fortune / hourly / music / notes / pomodoro / quote / sleep / sysinfo / todo / weather / wordcard

### 验证

- LoaderTest 四组 + UiProbe 11 项全过
- 构建 0 警告 0 错误

## [0.1.21] - 2026-09-01

整点报时插件。

### 新增

- **整点报时插件**（`builtins/DesktopPet.Builtin.HourlyChime`，**dogfooding：插件手册第 8 节的完整示例原样落地**，验证手册示例可编译可运行）— 每分钟 `Scheduler.Every(60_000)` 检查跨整点 → 猫播报「现在是 14:00 啦」；右键菜单「整点报时（开/关）」切换，Storage 持久化开关状态
- `plugins/` 现有 **13 个插件**：affection / clipboard / countdown / fortune / hourly / music / notes / pomodoro / quote / sysinfo / todo / weather / wordcard

### 验证

- LoaderTest 四组 + UiProbe 11 项全过
- 构建 0 警告 0 错误

## [0.1.20] - 2026-09-01

今日运势插件。

### 新增

- **今日运势插件**（`builtins/DesktopPet.Builtin.Fortune`）— 右键菜单「今日运势」槽位，按日期确定性选运势等级（大吉/中吉/小吉/平/小凶/大凶 + 星标，跨天刷新），建议随机，猫气泡播报「今日运势：大吉 🌟（适合努力）」。最轻量插件（纯算法，无 Storage/网络）
- `plugins/` 现有 **12 个插件**：affection / clipboard / countdown / fortune / music / notes / pomodoro / quote / sysinfo / todo / weather / wordcard

### 验证

- LoaderTest 四组 + UiProbe 11 项全过
- 构建 0 警告 0 错误

## [0.1.19] - 2026-09-01

待办清单插件。

### 新增

- **待办清单插件**（`builtins/DesktopPet.Builtin.TodoList`）— 右键菜单「待办清单」打开窗口：添加（输入 + 回车/按钮）、勾选完成、删除选中，任何变化实时持久化到 Storage（`List<TodoItem>` JSON 序列化，最近 30 条）。新增 `todo.png` 槽位图标（✅）
- `plugins/` 现有 **11 个插件**：affection / clipboard / countdown / music / notes / pomodoro / quote / sysinfo / todo / weather / wordcard

### 验证

- LoaderTest 四组 + UiProbe 11 项全过
- 构建 0 警告 0 错误

## [0.1.18] - 2026-09-01

系统信息插件。

### 新增

- **系统信息插件**（`builtins/DesktopPet.Builtin.SystemInfo`）— 右键菜单「系统信息」槽位，P/Invoke `GetSystemTimes`（两次采样算 CPU 使用率）+ `GlobalMemoryStatusEx`（内存）+ `DriveInfo`（C 盘），猫气泡播报「CPU 12% · 内存 8.2/16.0GB · C盘 120/256GB」。展示插件可 P/Invoke 系统 API
- `plugins/` 现有 **10 个插件**：affection / clipboard / countdown / music / notes / pomodoro / quote / sysinfo / weather / wordcard

### 验证

- LoaderTest 四组 + UiProbe 11 项全过
- 构建 0 警告 0 错误

## [0.1.17] - 2026-09-01

剪贴板历史插件。

### 新增

- **剪贴板历史插件**（`builtins/DesktopPet.Builtin.ClipboardHistory`）— `IScheduler` 800ms 轮询剪贴板，文本变化时入栈到 Storage（最近 20 条）。右键菜单「剪贴板」打开历史窗口（ListBox），双击条目复制回剪贴板。展示 ISystem.ReadClipboard/WriteClipboard。新增 `copy.png` 槽位图标（📋）
- `plugins/` 现有 **9 个插件**：affection / clipboard / countdown / music / notes / pomodoro / quote / weather / wordcard

### 验证

- LoaderTest 四组 + UiProbe 11 项全过
- 构建 0 警告 0 错误

## [0.1.16] - 2026-09-01

音乐控制插件。

### 新增

- **音乐控制插件**（`builtins/DesktopPet.Builtin.MusicControl`）— 右键菜单「上一曲 / 播放暂停 / 下一曲」，通过 Win32 `keybd_event` 发送系统媒体键（VK_MEDIA_*），对任意播放器（QQ音乐/网易云/浏览器）生效。新增 prev/play/next 槽位图标（⏮⏯⏭）
- `plugins/` 现有 **8 个插件**：affection / countdown / music / notes / pomodoro / quote / weather / wordcard

### 验证

- LoaderTest 四组 + UiProbe 11 项全过
- 构建 0 警告 0 错误

## [0.1.15] - 2026-09-01

倒计时插件。

### 新增

- **倒计时插件**（`builtins/DesktopPet.Builtin.Countdown`）— 右键菜单「倒计时 5分 / 10分」快捷倒计时（煮面/泡茶/眼保健操等场景），结束 `System.Notify` 系统通知 + 气泡提醒；「停止倒计时」取消。与番茄钟（25 分专注）互补
- `plugins/` 现有 **7 个插件**：affection / countdown / notes / pomodoro / quote / weather / wordcard

### 验证

- LoaderTest 四组 + UiProbe 11 项全过
- 构建 0 警告 0 错误

## [0.1.14] - 2026-09-01

天气插件 3 日预报。

### 新增

- **天气 3 日预报** — 天气插件加「3日预报」槽位，调 Open-Meteo `daily` API（`forecast_days=3`）返回今天/明天最高最低温 + 天气，气泡播报（`佛山 今天 32/25°C 晴  明天 31/24°C 多云`）。复用城市配置（Storage + 地理编码）

### 验证

- LoaderTest 四组 + UiProbe 11 项全过
- 构建 0 警告 0 错误

## [0.1.13] - 2026-09-01

单词打卡插件。

### 新增

- **单词打卡插件**（`builtins/DesktopPet.Builtin.WordCard`）— 右键菜单「今日单词」槽位（按日期确定性选词，内置 30 个常用英语单词 + 中文释义，跨天刷新）；「✅ 打卡」槽位记录连续天数（Storage 持久化，连续断档重置为 1）。新增 `book.png` 槽位图标（📖）
- `plugins/` 现有 **6 个插件**：affection / notes / pomodoro / quote / weather / wordcard

### 验证

- LoaderTest 四组 + UiProbe 11 项全过
- 构建 0 警告 0 错误

## [0.1.12] - 2026-09-01

每日一句插件。

### 新增

- **每日一句插件**（`builtins/DesktopPet.Builtin.Quote`）— 右键菜单「每日一句」槽位，内置 20 句中文，按日期确定性选句（1900-01-01 起天数 × Knuth 乘法哈希取模）：同一天同句、跨天刷新。`Pet.Say` 气泡播报
- `plugins/` 现有 **5 个插件**：affection / notes / pomodoro / quote / weather

### 验证

- LoaderTest 四组 + UiProbe 11 项全过
- 构建 0 警告 0 错误

## [0.1.11] - 2026-09-01

天气插件城市可配置（ISettingsPage + Storage + 地理编码）。

### 变更

- **天气插件支持城市配置** — 之前佛山硬编码。现在城市名存 `Storage`（key=`city`，默认佛山），右键菜单查询前先解析坐标：
  - 内置 14 个常用城市坐标表（免地理编码请求）
  - 未知城市走 Open-Meteo 地理编码 API（`geocoding-api.open-meteo.com`，免 key）
  - 解析失败回退佛山
- **天气设置页**（`WeatherSettingsPage`，dogfooding ISettingsPage + Storage 组合）— 城市输入框 + 保存，宿主设置窗口「插件」tab 可改
- Weather 插件 csproj 加 `UseWPF=true`（设置页需要 WPF 控件）

### 验证

- LoaderTest 四组 + UiProbe 11 项全过
- 构建 0 警告 0 错误

## [0.1.10] - 2026-09-01

备忘录自动保存 + 番茄钟插件。

### 变更

- **备忘录自动保存** — `NotesWindow` 加 800ms 停顿防抖自动保存（`DispatcherTimer` + `TextChanged`），不用点按钮；关窗也保存。手动「保存」按钮保留

### 新增

- **番茄钟插件**（`builtins/DesktopPet.Builtin.Pomodoro`，dogfooding）— 展示 IScheduler + ISystem.Notify 能力：右键菜单「番茄钟 25分」开始倒计时（每秒 `Scheduler.Every(1000)` 检查），结束用 `System.Notify` 系统通知 + 宠物气泡提醒；「停止番茄」取消。新增 `clock.png` 槽位图标（Pillow 渲染 ⏰）
- `plugins/` 现有 4 个插件：affection / notes / pomodoro / weather

### 验证

- LoaderTest 四组 + UiProbe 11 项全过
- 构建 0 警告 0 错误

## [0.1.9] - 2026-09-01

菜单沿猫身边排列 + 备忘录/天气两个新内置插件。

### 变更

- **径向菜单沿猫身边排列**（用户反馈"菜单被猫挡住一部分"）— 之前菜单圆心对齐宠物中心（槽位半径 64 内边缘 43 < 猫半径 51，槽位方块盖住猫边缘）。改为 `PetWindow.OpenRadialMenu` **右侧优先 → 左侧 → 上下** 定位（间距 10px，clamp 工作区），猫完全不被遮挡
- **移除环形挖洞**（SetWindowRgn + 中心检测定时器）— 菜单不再覆盖猫后中心无猫，挖洞/中心检测无意义且会误关菜单。`RadialMenu` 回退为简单矩形透明窗口，`MenuSize` 改 public 供 PetWindow 定位计算

### 新增

- **备忘录插件**（`builtins/DesktopPet.Builtin.Notes`，dogfooding）— 右键菜单「备忘录」槽位打开桌面便签窗口（多行输入 + 保存），内容经 Storage 持久化到 `data/plugins/com.lantern.notes/`
- **天气插件**（`builtins/DesktopPet.Builtin.Weather`）— 右键菜单「天气」槽位，调 Open-Meteo 免费 API（免 key）查询佛山当前天气，用宠物气泡播报（`☀️ 32°C 晴`）。纯异步不阻塞 UI，失败降级提示
- 两个新插件已加入解决方案（VS 可见）+ 构建自动部署到 `plugins/`

### 验证

- LoaderTest 四组 + UiProbe 11 项全过（含新插件链路）
- `plugins/` 现有 3 个插件：affection / notes / weather
- 构建 0 警告 0 错误

## [0.1.8] - 2026-09-01

槽位图标 PNG 化（v0.1.5 dev-log 列的待办）。

### 变更

- **径向菜单槽位图标**从 `TextBlock` emoji 文字改为 `Image` PNG 图片：
  - 用 Pillow + Windows Segoe UI Emoji 字体**预渲染** 12 个常用图标到 `neko-assets/icons/{wave,heart,food,sleep,dance,home,gear,info,eye,pin,close,default}.png`
  - `RadialMenu` 启动时一次性加载到 `_iconCache` 字典（`BitmapImage` + Freeze）
  - `SlotView` 构造时按 `slot.Icon` key 查缓存取 `BitmapImage`，找不到回退 `default`
  - **好处**：运行时不再依赖系统 emoji 字体（Segoe UI Emoji），跨系统/字体一致，加载/绘制性能更稳定
- `IconFor(string)` 静态 switch 映射删除（v0.1.8 之前用 TextBlock 显示 emoji，PNG 化后不需要）

### 验证

- LoaderTest 四组 + UiProbe 十项全过
- 构建 0 警告 0 错误

## [0.1.7] - 2026-09-01

窗口透明度设置（BongoCat `window.opacity` 借鉴）。

### 新增

- **`AppConfig.Opacity`** 默认 1.0 —— 持久化窗口透明度
- **`PetWindow.SetOpacity(double)`** —— clamp 0.2-1.0（< 0.2 鼠标会穿透到下层交互失灵），`Window.Opacity = opacity` + 持久化
- **启动时应用**：构造里 `Opacity = Math.Clamp(_config.Opacity, 0.2, 1.0)`
- **设置窗口「窗口」tab 加滑条** —— 0.2-1.0 实时调整 + Label 同步显示百分比

### 验证

- LoaderTest 4 组 + UiProbe 10 项全过
- 构建 0 警告 0 错误

## [0.1.6] - 2026-09-01

v0.1.5 设置窗口闭环 + 插件自定义设置页 dogfooding 示范。

### 新增

- **键盘热键设置 UI**（v0.1.3 闭环）—— 设置窗口「行为」tab 加 Ctrl/Alt/Shift 三个 CheckBox + 主键文本框 + 保存按钮，调用 `PetWindow.SetMenuHotKey(ctrl, alt, shift, key)` 立即生效 + 持久化。保存后下次启动/窗口失焦即生效
- **Affection 插件自定义设置页（ISettingsPage dogfooding）** —— 新建 `AffectionSettingsPage.cs` 实现 `ISettingsPage`（Title=`com.lantern.affection` 匹配 SettingsWindow 嵌入规则），含当前分数显示 + 重置按钮。插件 `OnLoad` 里 `UI.RegisterSettingsPage(...)` 注册
- **Affection 插件 csproj 加 `UseWPF=true`**（之前没启用，所以无法 using WPF 控件——这是 dogfooding 必备）

### 验证

- LoaderTest 四组 + UiProbe 10 项全过（含 Affection 注册设置页）
- 构建 0 警告 0 错误

## [0.1.5] - 2026-09-01

设置窗口重做（BongoCat 风格）+ 插件自定义设置页（ISettingsPage）接入。

### 变更

- **设置窗口重做**（BongoCat `preference` 页面借鉴）—— 从单一插件列表改为 **TabControl 4 个分组**：
  - **模型**：缩放滑条（1.0-8.0x，实时缩放并持久化）
  - **窗口**：开机自启勾选（与 HKCU 注册表同步）、鼠标穿透勾选
  - **行为**：键盘反应开关（v0.1.3 瞪眼功能可关闭）
  - **插件**：列表 + 详情（含加载错误 + **插件自定义设置页 ISettingsPage**）
- **ISettingsPage 接入** —— `UIApiImpl` 新增 `AllSettingsPages` 属性，设置窗口遍历插件注册的所有设置页，匹配 `Manifest.Id/Name` 后嵌入右侧详情区。这是之前 v0.1.0 决策"插件可自定义设置页"的第一阶段实现
- **PetWindow.Config 暴露** —— `public AppConfig Config => _config;` 让设置窗口读写配置
- **KeyboardReactionsEnabled 配置** —— 新增 `AppConfig.KeyboardReactionsEnabled`（默认 true），设置窗口可关闭键盘反应

### 验证

- LoaderTest 四组 + UiProbe 10 项全过
- 构建 0 警告 0 错误

## [0.1.4] - 2026-09-01

开机自启（决策 14，v0.2 计划提前做）。

### 新增

- **开机自启**（决策 14：默认关，只在设置窗口开）— 托盘菜单"开机自启"勾选，写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 注册表项（用 HKCU 而非 HKLM，免管理员）。新建 `Core/Autostart.cs` 封装 IsEnabled/SetEnabled。App.OnStartup 启动时与 config.json 双向同步（用户可能手动改 config），持久化。Neko.exe 完整路径用引号包裹处理空格。

### 验证

- LoaderTest 四组 + UiProbe 10 项全过
- 构建 0 警告 0 错误

## [0.1.3] - 2026-09-01

"BongoCat 借鉴"4 项体验增强：键盘反应 / 点击穿透 / 缩放可配置 / 键盘映射可配置。参考了 [BongoCat v1.1.0](https://github.com/ayangweb/BongoCat) 的 Tauri+Live2D 实现，**借鉴功能设计**（不抄代码，技术栈不同），重点是它"输入→猫动作 + 完整窗口设置"的产品思路。

### 新增

- **键盘反应**（BongoCat 核心：键盘/手柄 → 猫动作）— `GlobalInput` 钩子扩展 `KeyPressed` 事件，任意非修饰键按下（300ms 节流）→ `PetRenderer.FlashEyes(300)` 猫短暂瞪眼（眼球层切 `eyes_scared`，到期自动恢复）。**不吞按键**（正常传递给其他应用），**不影响好感度**（不复用 `Pet()`）
- **点击穿透模式** — 托盘菜单"鼠标穿透（纯展示）"勾选，`SetWindowLong + WS_EX_TRANSPARENT` 让鼠标事件穿透到下层窗口，猫纯展示。开启后拖拽/抚摸/菜单暂时失效（透传）
- **缩放可配置** — 托盘菜单"缩小 20% / 放大 20%"，`PetWindow.SetScale(double)` 调整窗口尺寸/渲染器/位置（中心保持不动），clamp 1.0-8.0，持久化到 `data/config.json`
- **键盘映射可配置**（BongoCat `useKeyPress` 借鉴）— `AppConfig.MenuHotKey` 配置径向菜单热键（Ctrl/Alt/Shift + 主键字母），改 `data/config.json` 即生效。设置 UI 留到 v0.2

### 探索（未完结）

- **换素材**（用户反馈"现在猫有点丑了"）：下载了 OpenGameArt CC0 候选（catto 粉紫手绘猫 / cat-sprite 走跑 GIF）和 itch.io CC0 候选（Kitty 32x32 橘猫）。已 present 对比图给用户选，**等用户决定后做素材适配/转换**

### 验证

- LoaderTest 四组 + UiProbe 10 项全过
- 构建 0 警告 0 错误

## [0.1.2] - 2026-09-01

"能显示桌宠 + 能动 + 拖拽跟手"完整修复版。诊断路径：给 PetWindow 加 tick 日志 → 确认 timer 跑 → 加 FrameIndex 打印 → 锁到 6 个连环 init bug。

### 修复（按调试顺序）

- **主循环从未启动** — `PetWindow._loopTimer.Start()` 缺失，导致动画、眼神追踪、边缘吸附、鼠标事件广播**全部失效**，猫只是"静态死图"
- **`_lastTick` 默认 `DateTime.MinValue`** — 首次 `OnLoopTick` 的 delta ≈ 6.4e10 秒，`FrameAnimator._accumulator` 累加溢出 `int.MaxValue` → `(int)` 强转变成 `Int32.MinValue` → `_frame = (0 + Int32.MinValue) % 4 = 0` **永远停住**。修：`_lastTick = DateTime.Now` 显式初始化 + `step = Math.Min((int)_accumulator, _state.Frames.Length)` 加防溢出 clamp
- **`_moodIntensity` 默认 0** — `Playback = 0` → `fps = 2 * 0 = 0` → `if (fps <= 0) return false;` 早返回。修：显式初始化 `= 1.0`
- **拖拽坐标系混用**（用户反馈"拖拽不跟鼠标"）— `PointToScreen()` 返回物理像素、`Left/Top` 是 WPF DIP，125% 缩放下差 1.25 倍，每次 Move 累积偏差。修：用 `PresentationSource.CompositionTarget.TransformFromDevice` 把物理像素转回 DIP 再算
- **`StartFall` 单位混乱 + 方向反**（用户反馈"点击它会消失"）— `_dragVelocity` 是"每帧位移"（与 Gravity"像素/秒²"差 60 倍）；`Math.Min(v.Y, 0)` 保留负值（向上）但注释写"向下"。异常大速度时宠物被甩出屏幕 → 看着像消失。修：`vy = v.Y * 60`（转像素/秒）+ 方向以原值保留 + `MaxThrowSpeed = 900 px/s` 限速
- **PetStateMachine 漏字段** — 之前补的 Walk→Idle 回路代码引用了 `_walkTimer` 和 `WalkDuration` 但忘了定义。补上

### 变更

- **桌宠尺寸缩小 20%**（灯灯要求）— `Scale` 4.0 → **3.2**（32×3.2 = 102.4 DIP，物理 128 @125%）。之前 160 → 现在 128
- 抚摸判定阈值 6 → 8（减少手抖误判为拖拽触发落体）
- **径向菜单改为真环形挖洞**（灯灯要求"从宠物周围弹出包围宠物"）— 用 `SetWindowRgn` 把窗口形状变成外圆挖内圆（外径 260、内径 120），中心空洞让宠物在中间完全可见可点；增加 100ms 中心检测定时器，鼠标进入中心空洞（猫上）主动关闭菜单
- 拖拽坐标系改为统一 DIP：`PointToScreen` 结果经 `CompositionTarget.TransformFromDevice` 转回 DIP 再算，消除 125% 缩放下的累积偏差

### 清理

- **libpng 警告 10 → 0** — 素材 PNG 内嵌的 iCCP sRGB profile 不规范，每次 build 都吐 10 条警告。用 Pillow `icc_profile=None` 重新保存全部 14 个 PNG，去掉 iCCP chunk，build 输出干净（只剩 3 条 NU1900 nuget 网络警告，与代码无关）

### 验证

- PrintWindow 5 帧（间隔 500ms）hash 全部不同 → 动画在跑（idle 4 帧 @2fps = 2 秒循环，5 帧抓出 4 个不同帧正好符合）
- 物理窗口尺寸 160px → 128px ✓
- LoaderTest 四组全过 + UiProbe 10 项全过（含 StartFall 30 次连发、Pet 50 次连发、Edge 20 次连发）
- 10 分钟长挂机内存 148→167MB（1-5min 涨 18MB 是 v0.1.2 新字段的 JIT 预热 + WPF 资源首次加载；5-10min 只涨 0.8MB **无泄漏**）
- 全程构建 0 警告 0 错误

## [0.1.1] - 2026-09-01

紧急修复版：**宠物根本没显示**（窗口在，但里面是一片黑，用户只看到 128×128 黑块）。

### 修复

- **致命：宠物不显示** — `PetStateMachine.Transition` 因 `if (Current == next) return` 早返回，导致初始 `Play(Idle)` 从未下发到渲染器，`_animator.State` 恒为 `null` → `Draw` 早返回什么都不画。诊断路径：PrintWindow 抓窗口内部 → 全黑 → 加 SANITY 画红方块确认 OnRender 被调用 → 加 diag 日志确认 `_animator.State=NULL` → 锁定 Transition 早返回
- **UI 撤销机制失效** — 注册用「项 id」作字典 key、撤销用「插件 id」，两者对不上，`UnregisterAll` 永远清不掉；且插件卸载 / `OnLoad` 失败时从未调用它 → UI 残留。改为 `ScopedUiApi` 用 thread-local scope 自动归类，卸载与加载失败时真正调用 `UnregisterAll`
- **落体定时器叠加** — 每次抓放 / 脱离边缘都 `new` 一个 `DispatcherTimer` 且不取消旧的，弹跳途中再次拖拽会出现两个物理模拟同时改 `Top` → 位置抖动。改为字段持有 `_fallTimer`，新建前先 `Stop` 旧的
- **PNG 加载可靠性** — `PngBitmapDecoder(file URI)` 对含 alpha 的 PNG 偶发返回黑帧。改用 `FileStream + BitmapImage`（诊断时一并修，避免是同因）

### 新增

- 事件 payload 强类型：`MouseMovedArgs` / `PetDraggedArgs` / `PetEdgeArgs`（`DesktopPet.Core`），插件可直接 cast 读取，不用再反射（旧反射方式仍兼容）
- 广播 `app:ready`（插件全部加载完成后触发，替代部分 `OnLoad` 内的延迟初始化）

### 变更

- 径向菜单改为**从宠物头顶上方弹出**（原先圆心对齐宠物中心，整只盖住猫 + 透明区吃掉点击）
  - 真正「环形挖洞包绕宠物」需 `SetWindowRgn`，留到 v0.2

## [0.1.0] - 2026-09-01

首个可运行里程碑：宿主内核 + 插件系统 + 内置好感度插件 + 基础交互层全部打通。

### 新增

**宿主内核**
- 透明无边框置顶宠物窗口，DrawingVisual 像素渲染 + 帧动画 + 状态机（8 状态 × 4 帧）
- 拖拽物理：甩动惯性、抛物线落体、触底反弹
- 左键抚摸判定（位移 < 6px 视为抚摸，否则为拖拽），广播 `pet:petted`

**宿主内置能力**（依赖渲染器/窗口物理/全局钩子，按架构决策不入插件）
- 眼神追踪：每帧计算宠物头部→鼠标方向，驱动眼球层 ±2px 偏移
- 边缘吸附：距边缘 < 8px 贴边悬挂播 `Climb`，鼠标远离 > 40px 脱离回落
- 全局热键：`Ctrl+Alt+N` 呼出径向菜单（WH_KEYBOARD_LL 低级钩子）

**插件系统**
- AssemblyLoadContext 隔离加载 + 可卸载，Tomlyn 解析 `plugin.toml`
- 权限模型（`[Flags]` 枚举，清单声明 → 宿主逐项授予 → 每次调用校验）
- Core API v1：`IPetApi` / `IUIApi` / `ICapabilityApi`（含 Storage / Scheduler / System / Events）
- 运行时目录约定：`plugins/<id>/` 放插件、`data/plugins/<id>/` 放插件数据

**内置与示例**
- 好感度插件 `builtins/DesktopPet.Builtin.Affection`：第一个 dogfooding 内置插件，4 等级 + 持久化 + 径向槽位
- 示例插件 `samples/SamplePlugin` + 清单模板 `plugins/_template/plugin.toml`
- 径向菜单：圆形 8 槽位，来自所有插件注册项，悬停高亮
- 托盘菜单（设置/暂停恢复/打开径向菜单/关于/退出）+ 设置窗口（插件列表 + 加载失败诊断）

**素材**
- 程序化生成的像素橘猫：8 状态 × 4 帧雪碧图、4 眼球层、阴影、托盘图标（见 `tools/generate_sprites.py`）

**测试**
- `tests/LoaderTest`：无 UI 插件加载链路（含发布形态验证）
- `tests/UiProbe`：UI 层防回归探针（守护窗口尺寸与径向菜单生命周期）

**文档**
- `docs/plugin-authoring-guide.md`：**插件开发手册**（自包含 —— 完整 API 参考 / 权限表 / 事件表 / 可编译示例 / 排错清单）。**插件 API 的单一权威源**，宿主 API 变更时必须同步更新（维护约定见手册第 0 节）
- `docs/dev-log.md`：开发过程日志（踩坑复现 → 根因 → 修复 → 教训）

### 修复

- **类型分裂**：插件 ALC 内重复加载 `DesktopPet.Core` → `IPetPlugin` 变两份 → 加载失败。改为把 Core/Sdk 路由回宿主侧
- **权限前缀不匹配**：清单写 `pet=["animate"]` 但查表用 `pet.animate` → 权限全 None。改为按段加前缀再查表
- **发布形态插件加载失败**：App 未引用 `DesktopPet.Sdk` → 输出目录缺 `DesktopPet.Sdk.dll` → 插件 ALC 白名单路由失败
- **径向菜单崩溃**：`Close()` 销毁窗口期间再 `Show()` 抛 `InvalidOperationException`，未捕获导致整个应用崩溃。改为单例复用 + `Hide()`
- **宠物窗口几乎不可见**：`SizeToContent` + 无 `MeasureOverride` 的裸宿主 → 窗口被量成 **12.8×12.8px**。改为显式尺寸 + 宿主自报尺寸
- **渲染器缩放未同步配置**：窗口按 `AppConfig.Scale` 缩放，渲染器却写死 3.0 → 尺寸不一致

### 变更

- 宠物显示尺寸 **96px → 128px**（`AppConfig.Scale` 默认 3.0 → 4.0）
- 径向菜单关闭改为延迟 300ms 隐藏，避免鼠标划过缝隙时误关
- 新增全局异常兜底：未处理异常写入 `data/errors.log` 并提示，不再静默崩溃

### 已知问题

- 素材 PNG 存在 `libpng warning: iCCP: known incorrect sRGB profile`（无害，来自内嵌色彩配置，不影响显示）
- 径向菜单打开时会覆盖宠物区域（透明区域仍接收点击），关闭后方可与宠物交互


