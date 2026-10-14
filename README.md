# Neko 桌宠程序

> Windows 10 轻量像素桌宠，C# + WPF + .NET 8，VS 2022 开发。
> **程序根目录：`G:\lantetn-li\neko`**（整个程序都在 G 盘，不占 C 盘）。
> 当前版本：见 `src/DesktopPet.App/DesktopPet.App.csproj` 的 `<Version>`（本文件不硬编码版本号，避免漂移；一致性由 `tools/check_version.py` 守护）。

## 目录结构

```
G:\lantetn-li\neko\
├── src/                      # 主解决方案（VS 2022 打开 src/DesktopPet.sln）
│   ├── DesktopPet.sln
│   ├── DesktopPet.Core/      # 接口契约（net8.0，插件唯一可依赖的层）
│   ├── DesktopPet.Runtime/   # 插件加载 / 清单解析 / 权限与信任通道（net8.0）
│   ├── DesktopPet.Sdk/       # 插件基类 PluginBase / 深色控件工厂 PluginUi（net8.0-windows）
│   └── DesktopPet.App/       # WPF 宿主 → Neko.exe
├── builtins/                 # 18 个内置插件源码（编译后自动部署到 plugins/，与第三方同构）
├── samples/
│   └── SamplePlugin/         # 示例插件（dogfooding 验证）
├── tests/
│   ├── LoaderTest/           # 无 UI 插件加载链路测试（发布形态 / ALC 卸载 / 信任通道）
│   ├── UiProbe/              # UI 层防回归探针（窗口尺寸 / 径向菜单 / 皮肤契约…）
│   └── PluginSmoke/          # 真加载全部插件冒烟（OnLoad→空闲→OnUnload→ALC 回收/CPU/内存）
├── neko-assets/              # 素材：皮肤 <id>/（skin.json + 帧 PNG + thumb.png）、icons/、mirror/、ui/、tray.ico
├── plugins/                  # 运行时插件目录（用户可增删；构建产物，不入库）
│   ├── README.md
│   ├── _template/plugin.toml
│   └── com.lantern.*/        # 内置插件部署产物（仅 dll + plugin.toml）
├── installer/                # 打包：build_setup.py(WiX) / gen_setup_wxs.py / neko.iss(Inno，遗留)
├── tools/                    # 素材生成与体检脚本（generate_sprites.py / build_skin_v2.py /
│                             #   gen_skin_previews.py / analyze_assets.py / find_grid.py /
│                             #   check_version.py / check_i18n.py …）
├── docs/                     # 设计与开发文档（见文末「文档」）
├── publish/                  # 发布产物（.gitignore 忽略）
├── CHANGELOG.md              # 版本记录（改版本时必须同步）
└── README.md
```

## 版本控制（v0.1.60 起）

本项目**已启用本地 git**（此前无版本控制，导致两起无留痕回退无法追溯：托盘图标被换回、`SelfContained` 被改 false）。

```bash
# 查看改动（每次改动后先跑这个，能立刻发现非预期修改）
git status --short
git diff --stat

# 提交（格式：<版本号>: <主题>，与 csproj 版本同步）
git commit -m "v0.1.82: xxx"

# 回滚单个文件到上次提交
git checkout -- <文件路径>
```

**设计要点**：`.gitignore` 排除了 `publish/`(4.1G)、`bin/`+`obj/`、`installer/*.msi`(375M)、`data/`、`.env`（含 API Key），以及 **`plugins/com.lantern.*/`（插件部署产物）**。
只跟踪源码 + 素材 + 手写文档 + 生成脚本。

⚠️ **`plugins/` 下的插件目录不入库**——它们是各内置插件 `DeployToPlugins` target 的构建产物；插件 dll 属**非确定性构建**（源码不变、重建也会内容微变），入库会持续刷 diff 噪音。
**clone 后需先构建**才会有插件：`dotnet build src/DesktopPet.sln`（sln 含全部内置插件，构建即自动部署到 `plugins/`）。手写的 `plugins/README.md` 与 `plugins/_template/` 仍入库。

⚠️ **素材改动肉眼难以分辨**（PNG/ICO 是二进制）——`git diff --stat` 会显示 `Bin N -> M bytes`，这是发现"图标被意外替换"最有效的手段。

## 常用命令

```bash
# 构建主解决方案（含全部内置插件；构建即自动部署到 plugins/）
dotnet build src/DesktopPet.sln -c Debug

# ⚠️ tests/ 不在 sln 内，必须各自单独构建/运行（否则会跑到陈旧二进制）
dotnet run --project tests/LoaderTest/LoaderTest.csproj -c Debug    # 插件加载链路（无 UI）
dotnet run --project tests/UiProbe/UiProbe.csproj -c Debug          # UI 防回归探针（会短暂弹宠物窗口）
dotnet run --project tests/PluginSmoke/PluginSmoke.csproj -c Debug  # 真加载全部插件冒烟

# 版本号一致性检查（csproj ↔ CHANGELOG ↔ git tag）
python tools/check_version.py

# 重新生成素材
python tools/generate_sprites.py
python tools/gen_skin_previews.py     # 换皮肤素材后必须重跑（生成 thumb.png）
```

> 本机（Windows + Git Bash）若直接 `dotnet` 因环境变量报错，改用包装脚本：
> `bash /c/Users/Lantern/neko-dotnet-env.sh <dotnet 子命令> ...`

## 运行

- **VS 2022**：打开 `G:\lantetn-li\neko\src\DesktopPet.sln`，**设 `DesktopPet.App` 为启动项目**，F5
  - ⚠️ 启动项目若停在 `DesktopPet.Core`（类库，只产 .dll）会报「不是有效的 Win32 应用程序」
- **命令行**：`G:\lantetn-li\neko\src\DesktopPet.App\bin\Debug\net8.0-windows\Neko.exe`

## 两种运行模式（v0.1.39 / v0.1.48）

可在「设置 → 输入 → 应用模式」或托盘右键「工作模式」切换，互斥：

| 模式 | 行为 |
|---|---|
| **摸鱼模式**（slack） | 收起键盘映射窗；猫滑到**最近的屏幕边缘挂住**（Climb）。可选「摸鱼时吸附屏幕边缘」关闭。**该模式下鼠标走远也不会自动脱离**，只有拖拽才会把猫拉下来。 |
| **工作模式**（work） | 猫伏在键盘映射窗前"干活"（参照 BongoCat）；键盘映射窗打开、高亮你按下的键。 |

## 交互速查

| 操作 | 效果 |
|---|---|
| 左键快击 | 抚摸（播 Pet 动画，广播 `pet:petted`） |
| 左键拖拽 | 抓起 / 甩出（落体 + 触底反弹，已修 DPI 坐标不跟手）|
| 右键 / 径向菜单热键（默认 `Ctrl+Alt+N`）| 呼出径向菜单（**真环形挖洞**，猫在中心完全可见可点；每页最多 8 槽，超出可翻页）|
| 拖到屏幕边缘 | 贴边悬挂（**非摸鱼模式**下，鼠标远离吸附点 40px 脱离回落）|
| 打字 / 点击 | 猫短暂瞪眼回应（v0.1.40 键鼠反应 + v0.1.47 点击互动；在「猫咪 → 行为」可关）|

## 托盘菜单（v0.1.30 起瘦身，只留常用项）

托盘图标右键：**设置 / 暂停动画（就地切成"开始动画"）/ 工作模式（勾选，联动应用模式）/ 显示·隐藏猫咪 / 插件…（仅当有插件注册托盘项时出现）/ 关于 Neko vX / 退出**。

> 开机自启、鼠标穿透、缩放、透明度等**不在托盘**——都在设置窗口里（这符合"托盘只留常用"的既定取舍）。

## 设置窗口（v0.1.31 起重做为 sidebar 卡片式；当前 **6 个 section**）

左侧固定 sidebar，右侧按 section 渲染卡片。**section 列表**（源码 `SettingsWindow.cs`）：

| Section | 职责 | 主要卡片 |
|---|---|---|
| **通用** general | 全局开关与外观语言 | 常规：**开机自启动**（写 HKCU Run）；游戏模式：互斥三选「关闭 / 全屏时藏猫 / 全屏时鼠标穿透」；外观与语言：**界面主题**（深色/浅色）、**界面语言**（中文/English，即时生效）|
| **猫咪** pet | 猫本身的外观与行为 | 外观：**缩放倍率**（1.0–8.0，步进 0.2，显示 `Nx (32·N px)`）、**窗口透明度**（0.2–1.0）、**外观风格（皮肤选择器：一行一套皮肤 + 缩略图，读 `neko-assets/*/skin.json`）**、打开皮肤目录、**猫的名字**、**猫怎么称呼你**；行为：**键鼠反应**、**气泡对话**、**鼠标穿透**、回到默认位置 |
| **输入** input | 应用模式与快捷键 | 应用模式：互斥「摸鱼模式 / 工作模式」；摸鱼时吸附屏幕边缘；**键盘朝向**（朝我/朝猫=镜像）；快捷键：**径向菜单热键**、**显示·隐藏猫咪（老板键）**、透明度 ±10%、缩放 ±20%、回到默认位置（**附加热键自 v0.1.33 起热更新，改完无需重启**）|
| **径向菜单** radial | 径向菜单的槽位治理（v0.1.71 新增）| 显示数量：最多显示槽位数（0 = 不限）；**插件顺序与显隐**：逐个槽位 ▲▼ 调序 + 显隐开关（标注「超出上限 / 已隐藏」）；重置：恢复默认 |
| **插件** plugin | 插件管理（每插件一张卡片）| 名称 + 版本 + **启用/禁用开关**（热启用/禁用，立即 OnLoad/OnUnload）+ 作者 + 描述 + **加载错误**（红色行）+ 插件自带的 `ISettingsPage` 自定义设置页（v0.1.44 起卡片式）|
| **关于** about | 版本与更新 | 版本号（读程序集）；**自动检查更新**（卡片 + 开关，默认每天启动静默检查一次）；快捷键速查 |

> ⚠️ **皮肤选择器（v0.1.82）**：外观列表已从旧的 WrapPanel 标签改为「一行一个皮肤 + 48px 预览缩略图」；缩略图来自 `neko-assets/<id>/thumb.png`（缺失显示占位，不影响选择）。切换皮肤时若作者声明了 `recommendedScale`，宿主会自动套用推荐缩放（并把「缩放倍率」卡片同步刷新）。
>
> ⚠️ **懒加载**：为压冷启动，宿主只立即加载"核心插件"；联网/重资源类插件（如天气/一言/单词卡/截图/运势/整点报时）在主窗口显示后**后台异步加载**（`PluginHost.LoadCore` / `LoadDeferred`）。插件页展示的是**当前已加载**的插件。

## 🔒 隐私与安全

### Neko 会记录我的键盘/鼠标输入吗？

**不会。** Neko 的全局键盘/鼠标监听（低级钩子）只用于三件事：**全局热键**、**键鼠映射窗实时高亮**、**打字/点击时的小反应动画**——所有输入只在内存中即时处理，**不记录按键内容、不写入文件、不上传网络、不注入其他进程、不模拟输入**。其中反应类功能可在设置页关闭。

- Neko 主程序**不联网**。仅当你**主动**使用天气（`api.open-meteo.com`）、命理 AI 咨询（`api.deepseek.com`，需自填 API Key）、搜索（由浏览器打开百度）时才会访问网络。
- 用户数据（配置 / 好感度 / 插件私有数据 / 日志）默认保存在 **`%AppData%\Neko`**（v0.1.98 起；
  之前是 exe 同级的 `data/`）。想要绿色便携形态，就在程序根放一个 `portable.flag` 文件，数据即改回程序根 `data/`。
  **删除该目录即清除全部用户数据。**
- 无账号、无遥测、无统计、无广告。

详见 [PRIVACY.md](PRIVACY.md)（隐私说明）与 [SECURITY.md](SECURITY.md)（安全说明与漏洞上报）。

## 文档

**插件开发**
- `docs/plugin-development-guide.md` — 📘 **插件开发标准文档（可执行，v0.1.83）**：§0 给 AI 的最简清单 / 5 分钟上手 /
  契约全签名 / `plugin.toml` 与权限表 / 生命周期铁律 / **⚠️ 线程规则** / 存储 / UI 注册 / 调试 / **踩坑清单** /
  附录（权限·事件速查 / 一致性核对 / 维护约定 / API 版本历史）
- `templates/plugin-skeleton/` — 可直接复制的最小插件骨架（**勿加入 sln**）
- `plugins/README.md` — 插件目录约定、数据存放、启用禁用（部署者视角）

**皮肤开发**
- `docs/skin-development-guide.md` — 🎨 **皮肤开发标准文档（可执行，v0.1.83）**：`skin.json` 字段表 /
  **`fps` 语义** / 9 状态 / 帧图布局 / 眼球层 / `thumb.png` / 素材管线工具 / CSProj 白名单 / AI 生图要点 /
  常见错误速查 / 附录：自绘素材规格（调色板 · Aseprite 导出 · 质检清单）

**项目**
- `CHANGELOG.md` — 版本记录（改版本时同步更新）
- `docs/dev-log.md` — **开发日志**：开发过程、踩过的坑、决策依据（文末附 2026-09-11 项目审计存档）
- `docs/architecture.md` — 🏛 **架构文档**：分层 / 技术选型 / Core API / 宿主内核要点 / 资源红线 / 径向菜单设计 / 立项决策与差异校准
- `docs/release-sop.md` — 🚀 **发布 SOP**：版本/文档同步 → 构建与测试闸门 → 发布形态 → 打包 → 清场 checklist
- `docs/ai-asset-prompts.md` — AI 素材生成提示词

---

## 许可证（License）

本项目采用 **代码与素材分离** 的双许可：

| 范围 | 许可 | 允许商用 |
|---|---|---|
| **源代码** —— `src/` `builtins/` `tools/` `tests/` `samples/` `templates/` 及全部 `.md` `.toml` `.json` 文本 | **MIT** | ✅ 允许 |
| **美术素材** —— `neko-assets/` 下全部图像、图标、截图等 | **CC BY-NC 4.0** | ❌ **不允许**（商用需另行授权） |

- MIT 全文 → [`LICENSE`](LICENSE)
- 素材许可、署名要求与商用授权 → [`LICENSE-ASSETS`](LICENSE-ASSETS)

> ⚠️ **要商用请注意**：代码部分（MIT）可直接商用；
> 但**猫 / 菲比的形象素材不能商用**——请替换为自绘素材，或联系作者取得授权。

### 第三方依赖

| 依赖 | 许可 | 用途 |
|---|---|---|
| [Hardcodet.NotifyIcon.Wpf](https://github.com/hardcodet/wpf-notifyicon) | MIT | 托盘图标 |
| [Tomlyn](https://github.com/xoofx/Tomlyn) | MIT | `plugin.toml` 解析 |
| [lunar-csharp](https://github.com/6tail/lunar-csharp) | MIT | 农历 / 八字换算 |
| `System.Drawing.Common` · `System.Security.Cryptography.ProtectedData` | MIT | .NET 官方库 |

### 免责声明

「Neko」是**个人学习项目**，与任何公司或组织无关，按「原样」提供、不附带任何担保。
隐私说明见 [`PRIVACY.md`](PRIVACY.md)，安全问题反馈见 [`SECURITY.md`](SECURITY.md)。
