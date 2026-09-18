# Neko 发布 SOP（Release Standard Operating Procedure）

> **本文件是「可执行」发布标准流程。** 每一步都有命令；每条「必须」都对应本项目**真实踩过的坑**
> （见文末「附录：检查项 ← 真实事故映射」）。
>
> | 项 | 值 |
> |---|---|
> | 适用仓库 | `G:\lantetn-li\neko` |
> | 对应版本 | v0.1.82 |
> | 打包路线 | **WiX**（`installer/build_setup.py`）；`installer/neko.iss`（Inno）为**遗留，勿用** |
> | 发布形态 | 自包含**单文件**（`PublishSelfContained`，非 `SelfContained`） |
>
> 环境：所有命令假设在仓库根目录执行。本机（Windows + Git Bash）若直接 `dotnet` 因环境变量报错，
> 用包装脚本 `bash /c/Users/Lantern/neko-dotnet-env.sh <dotnet 子命令> ...`（下文命令中的 `dotnet` 均可如此替换）。

---

## 目录（TOC）

1. [版本号一致性（强制）](#1-版本号一致性强制)
2. [文档同步（强制）](#2-文档同步强制)
3. [构建与测试闸门（强制）](#3-构建与测试闸门强制)
4. [发布形态](#4-发布形态)
5. [打包（MSI）](#5-打包msi)
6. [素材白名单（新增皮肤时强制）](#6-素材白名单新增皮肤时强制)
7. [发布前清场](#7-发布前清场)
8. [发布 checklist（可勾选）](#8-发布-checklist可勾选)
9. [附录：检查项 ← 真实事故映射](#附录检查项--真实事故映射)

---

## 1. 版本号一致性（强制）

**权威来源**：`src/DesktopPet.App/DesktopPet.App.csproj` 的 `<Version>`。
**必须全部同步**：

| 位置 | 同步要求 |
|---|---|
| `src/DesktopPet.App/DesktopPet.App.csproj` | `<Version>` —— **唯一权威**，先改这里 |
| `CHANGELOG.md` | 顶部新增 `## [x.y.z] - YYYY-MM-DD` 条目（`check_version.py` 读**首个**版本头比对） |
| `docs/dev-log.md` | 追加本版开发记录（**`check_version.py` 不检查它**，须人工确认 —— 这是历史漂移高发区） |
| `src/DesktopPet.sln` | 无应用版本号（只有 VS Format/VS 版本）；**确认未被写入应用版本** |
| git | 打 tag（`check_version.py` 会比对 HEAD tag 与版本号） |

**校验命令（发布前必跑，退出码 0 才继续）：**

```bash
python tools/check_version.py
# 它检查三件事：
#   ① csproj <Version> 是合法 x.y.z 语义化版本
#   ② csproj <Version>  ==  CHANGELOG 首个版本头
#   ③ HEAD 上的 git tag 与版本号匹配（无 tag / 非 git 仓库则跳过该项）
# 退出码：0 = 通过；1 = 不一致；2 = 环境错误（找不到文件/元素）
```

> 🔴 **dev-log 也属于「改版本必须同步」的一部分**（本项目在审计 P1-5/P1-7 中反复栽在"版本漂移"），
> 但 `check_version.py` **不覆盖 dev-log** —— 发布前请**人工**确认 `docs/dev-log.md` 有对应版本段落。

---

## 2. 文档同步（强制）

宿主 API / 行为变化后，以下文档**必须**同步，否则下一个人（或 AI）会照着过期文档干活：

| 文档 | 同步点 | 当前状态提示 |
|---|---|---|
| `docs/plugin-development-guide.md` | 头部「对应宿主版本」；API 签名 / 权限表 / 线程规则 | v0.1.82（较新） |
| `docs/skin-development-guide.md` | 头部版本；`skin.json` 字段 / `fps` 语义 / 工具命令 | v0.1.82（较新） |
| `docs/plugin-authoring-guide.md` | **头部「对应宿主版本」**；`PetState` 枚举；权限键；事件表 | ⚠️ 头部停在 **v0.1.61**，落后多版，发布时应评估是否更新 |
| `README.md` | 目录结构 / 设置窗口章节 / 常用命令 / 版本号引用 | 本版已核（v0.1.82） |
| `docs/release-sop.md`（本文件） | 版本引用、打包命令、表单路径 | 本版 v0.1.82 |

**检查手段：**

```bash
# 宿主的「单一权威源」手册头部版本号（改宿主 API 必须同步）
#   docs/plugin-authoring-guide.md 头部「对应宿主版本」
# 双语完整性（README / 界面文案若有中英双语要求）
python tools/check_i18n.py
```

> ⚠️ 审计结论：「权威文档停在 60 个版本前」是历史债。**发布时若发现手册头部落后，要么同步、要么在 README 注明「以源码为准」**（本 README 已注明）。

---

## 3. 构建与测试闸门（强制）

### 3.1 构建（0 警告 0 错误）

```bash
dotnet build src/DesktopPet.sln -c Debug
# 验收：输出 "0 个警告 / 0 个错误"（0 Warning(s) / 0 Error(s)）
```

### 3.2 三个测试套件（全绿才继续）

> 🔴 **`tests/` 不在 `src/DesktopPet.sln` 内** —— **必须各自单独构建/运行**。
> 只跑 sln 不会编译测试工程，`dotnet run` 可能跑到**陈旧二进制**（本会话真实踩过：构建竞态/假绿）。

```bash
# ① 插件加载链路（无 UI；含发布形态 / ALC 卸载 / 信任通道）
dotnet run --project tests/LoaderTest/LoaderTest.csproj -c Debug
#   任一 FAIL → 退出码 1（v0.1.77 修复「假绿」后的行为；可 `LOADERTEST_INJECT_FAILURE=1` 自检接线）

# ② UI 防回归探针（会短暂弹出宠物窗口；含窗口尺寸 / 径向菜单 / 皮肤契约断言）
dotnet run --project tests/UiProbe/UiProbe.csproj -c Debug

# ③ 插件冒烟（真加载全部插件：OnLoad → 空闲 3.2s → OnUnload → ALC 回收/CPU/内存）
dotnet run --project tests/PluginSmoke/PluginSmoke.csproj -c Debug
#   退出码：0 = 全部加载且 OnLoad 无异常
```

**验收对照（v0.1.82 基线，供参考）：**
- LoaderTest `40 PASS / 0 FAIL`（数字随版本增长，**关键看 0 FAIL 与退出码**）
- UiProbe 全 PASS（断言项随版本累加，v0.1.82 已 46+）
- PluginSmoke：全部插件加载成功、OnLoad/OnUnload 零异常、0 定时器泄漏、0 事件未解绑

> ⚠️ **测试进程可能不退出**（遗留前台线程是已知环境坑）——用完 `Environment.Exit` 强制退出。
> ⚠️ **UiProbe 若被文件锁**（进程占着 `Neko.dll`）：换输出目录重跑，如加 `-p:BaseOutputPath=bin_alt/`。

---

## 4. 发布形态

**目标**：自包含**单文件**（用户零前置、双击即用）。

`src/DesktopPet.App/DesktopPet.App.csproj` 的**关键属性（必须与现状一致）**：

```xml
<PropertyGroup>
  <PublishSingleFile>true</PublishSingleFile>
  <!-- 🔴 必须用 PublishSelfContained，绝不能写 SelfContained（见附录事故 1） -->
  <PublishSelfContained>true</PublishSelfContained>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  <EnableCompressionInSingleFile>false</EnableCompressionInSingleFile>  <!-- 内存优先，见下 -->
  <PublishReadyToRun>true</PublishReadyToRun>
  <DebugType>embedded</DebugType>
  <!-- 不启用 trimming（WPF 暂不友好） -->
</PropertyGroup>
```

**为什么 `EnableCompressionInSingleFile=false`（v0.1.60 实测取舍）**：

| 压缩 | 体积 | 稳态内存 | 结论 |
|---|---|---|---|
| `true` | ~80 MB | ~276 MB（启动解压运行时映射常驻） | ❌ 破内存红线 |
| `false` | ~174 MB | ~177 MB | ✅ **桌宠常驻，内存优先** |

**发布命令：**

```bash
dotnet publish src/DesktopPet.App/DesktopPet.App.csproj -c Release
# 约定产物目录：publish/v<version>/（打包脚本默认从 publish/v<version> 取，见下）
```

> ⚠️ `publish/` 已被 `.gitignore` 忽略；不要把它提交。

---

## 5. 打包（MSI）

**现状：MSI 不随版本自动生成，需手动执行。** 路线是 **WiX 4**（`wix.exe 4.0.5`）。

```bash
# 前提：已 dotnet publish 出 publish/v<version>/（或显式传 publish 目录）
python installer/build_setup.py <version>
#   等价于：  python installer/gen_setup_wxs.py <publish_dir> <version> <out.wxs>  →  wix build
#   产物：    installer/NekoSetup-<version>.msi
#   依赖：    dotnet tool install --global wix --version 4.0.5
```

**打包器会做什么（v0.1.78 P0-1 起）：**
`gen_setup_wxs.py` 有**显式排除规则常量集合**，把开发机运行期数据 / 噪声挡在安装包外：

| 规则 | 内容 |
|---|---|
| `EXCLUDE_TOP_DIRS` | `data`（**开发机的 config.json / 插件私有数据** —— 绝不能进包） |
| `EXCLUDE_EXACT_FILES` | `.env`（含 API Key） |
| `EXCLUDE_DIR_NAMES` | `reference` |
| `EXCLUDE_PREFIXES` | `_icon_` |
| `EXCLUDE_CONTAINS` | `preview` / `.bak-` |

> 🔴 **`installer/neko.iss`（Inno Setup）是遗留脚本，停在 v0.1.42，切勿使用**（审计 P1-4 双轨陈旧）。
> 发布**只走 WiX**（`build_setup.py`）。

**打包后自检（可选但推荐）：**
- 用生成的 `.wxs` 反查：`data/` 条目应为 **0**；`Neko.exe`、插件 dll、`neko_*.png` 素材应在。

---

## 6. 素材白名单（新增皮肤时强制）

新增**内置 / 随包发布**的皮肤时，必须给**两个** csproj 各加一行白名单，否则皮肤在真机/安装包里**不生效**：

- `src/DesktopPet.App/DesktopPet.App.csproj`
- `tests/UiProbe/UiProbe.csproj`

```xml
<None Include="..\..\neko-assets\<id>\**\*" CopyToOutputDirectory="PreserveNewest" LinkBase="neko-assets\<id>" />
```

🔴 **`LinkBase` 必须含子目录名**（`neko-assets\<id>`，不是 `neko-assets`）。
丢了子目录名 → MSBuild 的 `%(RecursiveDir)` 基准目录下移 → 素材被**摊平**到 `neko-assets\` 根级 →
运行时代码按子目录找不到 → 图标/爪印/皮肤/帧图全部丢失（v0.1.79 真实翻车，v0.1.80 修复）。

**同时必做：**

```bash
python tools/gen_skin_previews.py       # 生成/更新 thumb.png（换素材后必须重跑）
```

> 用户自制皮肤放**运行时** `neko-assets/`，**不需要**改 csproj。

---

## 7. 发布前清场

| 项 | 命令 / 检查 |
|---|---|
| 清 `bin/`、`obj/`（避免假绿） | 涉及素材/打包的改动**必须先清 bin 再测** |
| 清临时截图 / 日志 | 仓库根的 `_*.png`（`_live_shot.png`、`_win_*.png` 等）是开发噪声，**不要提交**（确认在 `.gitignore` 或删除） |
| 工作树干净 | `git status --short` —— 确认没有意外的 `M`（尤其 `SelfContained` 被改、素材被换） |
| 素材改动可辨 | `git diff --stat` 显示 `Bin N -> M bytes` = 二进制素材变动，逐项确认预期 |
| 版本一致 | `python tools/check_version.py` 通过 |

```bash
git status --short
git diff --stat
python tools/check_version.py
```

---

## 8. 发布 checklist（可勾选）

```
发布版本：v________        发布日期：____________

[ ] 1. 版本号
    [ ] DesktopPet.App.csproj 的 <Version> 已改为新版本
    [ ] CHANGELOG.md 顶部新增 [x.y.z] 条目
    [ ] docs/dev-log.md 有本版记录（check_version.py 不查，人工确认）
    [ ] src/DesktopPet.sln 未被写入应用版本号
    [ ] python tools/check_version.py  → 通过（退出码 0）

[ ] 2. 文档同步
    [ ] 两份开发指南头部版本已更新（或确认无需）
    [ ] README 的设置窗口/目录结构/命令章节与现状一致
    [ ] plugin-authoring-guide 头部版本已评估（落后则同步或注明"以源码为准"）
    [ ] python tools/check_i18n.py → 通过

[ ] 3. 构建与测试闸门
    [ ] dotnet build src/DesktopPet.sln -c Debug → 0 警告 0 错误
    [ ] LoaderTest  → 0 FAIL、退出码 0
    [ ] UiProbe     → 全 PASS
    [ ] PluginSmoke → 全部加载、OnLoad/OnUnload 零异常
    （测试工程不在 sln 内，已各自单独构建）

[ ] 4. 素材白名单（仅新增皮肤时）
    [ ] DesktopPet.App.csproj 补白名单一行（LinkBase 含子目录名）
    [ ] UiProbe.csproj 补白名单一行
    [ ] python tools/gen_skin_previews.py 已重跑 → thumb.png 已更新

[ ] 5. 发布形态核对
    [ ] csproj 用 <PublishSelfContained>（不是 <SelfContained>）
    [ ] <EnableCompressionInSingleFile>false</EnableCompressionInSingleFile>
    [ ] dotnet publish -c Release → publish/v<version>/ 产物正常

[ ] 6. 打包
    [ ] python installer/build_setup.py <version> → NekoSetup-<version>.msi
    [ ] 核对 .wxs：data/ 条目 = 0（未打包开发数据）

[ ] 7. 清场与提交
    [ ] bin/ 已清、根目录临时 _*.png 已清/忽略
    [ ] git status --short 干净（无意外 M）
    [ ] git diff --stat 中素材二进制变动均已确认
    [ ] 提交（格式：<版本号>: <主题>）
    [ ] 打 tag（与 <Version> 一致）
```

---

## 附录：检查项 ← 真实事故映射

> 这份 SOP 的每一条强制项都来自一次真实事故。**别绕过它们。**

| 事故 | 现象 | 根因 | 已固化为本文哪一步 |
|---|---|---|---|
| **1. `SelfContained` 泄漏到构建期**（v0.1.82 P0） | 右键菜单插件**全没了** | `SelfContained` 是**构建期**属性，`dotnet build`(Debug) 也产出"自包含非单文件"形态 → `AssemblyDependencyResolver` 失败 | §4 用 `PublishSelfContained`；§8 checklist 第 5 项 |
| **2. 在 `Loaded` 回调里再挂 `Loaded`**（v0.1.82 P0） | 6 个"延迟插件"从未加载 | WPF 不在事件触发中调用新挂处理器、且 `Loaded` 不重放 → `LoadDeferred()` 永不执行 | §3.2 PluginSmoke 兜底；插件文档踩坑 1 |
| **3. `LinkBase` 丢子目录 → 素材被摊平**（v0.1.79→v0.1.80） | 真机安装包图标/皮肤/帧图全丢 | 白名单把基准目录下移，`LinkBase` 未补子目录 → `%(RecursiveDir)` 变空 | §6 强制；§8 checklist 第 4 项 |
| **4. bin 残留导致「假绿」**（v0.1.79） | UiProbe「全过」但其实素材没生效 | bin 里有上一版嵌套文件；测试跑在旧产物上 | §7「涉及素材/打包的改动先清 bin 再测」 |
| **5. MSI 打包开发数据**（v0.1.78 P0-1） | 开发机 `config.json` / 插件私有数据进安装包 | 打包器唯一过滤是 `.pdb`，不读 `.gitignore` | §5 排除规则 + checklist 第 6 项 |
| **6. 压缩开关的内存取舍**（v0.1.60） | 压缩 on 稳态内存 276MB，破红线 | 启动解压运行时映射常驻内存 | §4 `EnableCompressionInSingleFile=false` + 定量数据 |
| **7. 测试不在 sln** | 只跑 sln → 测试工程未编译 → 跑到陈旧二进制 | `tests/` 不属 `src/DesktopPet.sln` | §3.2 显式"各自单独构建/运行" |
| **8. 版本漂移**（审计 P1-5 / P1-7） | 手册/CHANGELOG/安装器各停在不同版本 | 改版本未全量同步 | §1 + §2 + `check_version.py` |
| **9. 无留痕回退** | 托盘图标被换回 / `SelfContained` 被改 false，无从追溯 | 当时无版本控制 | §7 `git status --short` / `git diff --stat` 逐项确认 |

---

*文档版本：v1.0 ｜ 基于宿主 v0.1.82 撰写。若发布流程变化（如接入 CI 自动出包），请同步更新本文件。*
