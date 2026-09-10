# Neko 桌宠程序

> Windows 10 轻量像素桌宠，C# + WPF + .NET 8，VS 2022 开发。
> **程序根目录：`G:\lantetn-li\neko`**（整个程序都在 G 盘，不占 C 盘）。
> 当前版本 **v0.1.60**（定义在 `src/DesktopPet.App/DesktopPet.App.csproj` 的 `<Version>`）。

## 目录结构

```
G:\lantetn-li\neko\
├── src/                      # 主解决方案
│   ├── DesktopPet.sln        # VS 2022 打开这个
│   ├── DesktopPet.Core/      # 接口契约（net8.0）
│   ├── DesktopPet.Runtime/   # 插件加载器（net8.0）
│   ├── DesktopPet.Sdk/       # 插件基类（net8.0-windows）
│   └── DesktopPet.App/       # WPF 宿主 → Neko.exe
├── builtins/                 # 内置插件源码（编译后自动部署到 plugins/）
│   └── DesktopPet.Builtin.Affection/   # 好感度（dogfooding 示范）
├── samples/
│   └── SamplePlugin/         # 示例插件（dogfooding 验证）
├── tests/
│   ├── LoaderTest/           # 无 UI 插件加载链路测试
│   └── UiProbe/              # UI 层防回归探针（窗口尺寸 / 径向菜单生命周期）
├── neko-assets/              # 像素素材（程序化生成）
├── plugins/                  # 运行时插件目录（用户可增删）
│   ├── README.md
│   └── _template/plugin.toml
├── docs/                     # 设计文档 + 开发日志
├── tools/
│   └── generate_sprites.py   # 素材生成脚本
└── CHANGELOG.md              # 版本记录
```

## 版本控制（v0.1.60 起）

本项目**已启用本地 git**（此前无版本控制，导致两起无留痕回退无法追溯：托盘图标被换回、`SelfContained` 被改 false）。

```bash
# 查看改动（每次改动后先跑这个，能立刻发现非预期修改）
git status --short
git diff --stat

# 提交（格式：<版本号>: <主题>，与 csproj 版本同步）
git commit -m "v0.1.61: xxx"

# 回滚单个文件到上次提交
git checkout -- <文件路径>
```

**设计要点**：`.gitignore` 排除了 `publish/`(4.1G)、`bin/`+`obj/`、`installer/*.msi`(375M)、`data/`、`.env`（含 API Key）。
仓库仅 1.1MB / 257 文件，只跟踪源码 + 素材 + 文档 + 生成脚本。

⚠️ **素材改动肉眼难以分辨**（PNG/ICO 是二进制）——`git diff --stat` 会显示 `Bin N -> M bytes`，这是发现"图标被意外替换"最有效的手段。

## 常用命令

```bash
# 构建
cd G:\lantetn-li\neko\src && dotnet build DesktopPet.sln

# 跑插件加载链路测试（无 UI，含发布形态验证）
cd G:\lantetn-li\neko\tests\LoaderTest && dotnet run

# 跑 UI 防回归探针（会短暂弹出宠物窗口，守护窗口尺寸与径向菜单）
cd G:\lantetn-li\neko\tests\UiProbe && dotnet run

# 重新生成像素素材
python G:\lantetn-li\neko\tools\generate_sprites.py
```

## 运行

- **VS 2022**：打开 `G:\lantetn-li\neko\src\DesktopPet.sln`，**设 `DesktopPet.App` 为启动项目**，F5
  - ⚠️ 启动项目若停在 `DesktopPet.Core`（类库，只产 .dll）会报「不是有效的 Win32 应用程序」
- **命令行**：`G:\lantetn-li\neko\src\DesktopPet.App\bin\Debug\net8.0-windows\Neko.exe`

## 交互速查

| 操作 | 效果 |
|---|---|
| 左键快击 | 抚摸（播 Pet 动画，广播 `pet:petted`） |
| 左键拖拽 | 抓起 / 甩出（落体 + 触底反弹，已修 DPI 坐标不跟手）|
| 右键 / `Ctrl+Alt+N` | 呼出径向菜单（**真环形挖洞**，猫在中心完全可见可点）|
| 拖到屏幕边缘 | 贴边悬挂（鼠标远离 40px 脱离回落） |
| 打字 | 猫瞪眼 300ms（v0.1.3 键盘反应，行为 tab 可关）|
| 托盘右键 | 设置 / 暂停恢复 / 打开径向菜单 / 鼠标穿透 / 缩放±20% / 开机自启 / 关于 / 退出 |

## 设置窗口（v0.1.5 重做，BongoCat 风格 4 tab）

| Tab | 配置项 |
|---|---|
| 模型 | 缩放滑条 1.0-8.0x（实时+持久化）|
| 窗口 | 开机自启勾选（HKCU 注册表）/ 鼠标穿透（WS_EX_TRANSPARENT）/ 透明度滑条 0.2-1.0 |
| 行为 | 键盘反应开关 / 菜单热键编辑（Ctrl/Alt/Shift + 主键字母）|
| 插件 | 插件列表 + 详情（含加载错误 + 插件 ISettingsPage 自定义页）|

## 文档

**插件开发**
- `docs/plugin-authoring-guide.md` — 📘 **插件开发手册（权威源，自包含）**
  完整 API 签名 / 权限表 / 事件表 / 可编译示例 / 排错清单。
  **贴给任意 AI 就能让它按手册生成插件**，不用读宿主源码。
  宿主 API 变更时必须同步更新这一份（维护约定见手册第 0 节）
- `plugins/README.md` — 插件目录约定、数据存放、启用禁用（部署者视角）
- `docs/plugin-system-notes.md` — 插件系统内部实现备忘（3 个已修复的坑）

**项目**
- `CHANGELOG.md` — 版本记录（改版本时同步更新）
- `docs/dev-log.md` — **开发日志**：开发过程、踩过的坑、决策依据
- `docs/desktop-pet-plan.md` — 主技术方案（架构分层 / Core API / 目录规范 / 路线）
- `docs/neko-sprite-spec.md` — 像素素材规格
