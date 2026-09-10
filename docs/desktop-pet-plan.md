# 桌宠程序《Neko》技术方案与开发计划（C# / WPF 版）

> 编制日期：2026-09-01（决策已全部确认）
> 状态：**决策已定稿，可进入 P0**
> 运行环境：Windows 10（1809+）+ .NET 8 + Visual Studio 2022
> 分发范围：仅自用与朋友圈（受控分发，非开放创意工坊）
> 配套文件：`neko-sprite-spec.md`（像素猫素材规格书）

---

## 〇、已确认决策（2026-09-01，共 19 项全部敲定）

### 产品定位

| 项 | 决策 |
|---|---|
| 宠物命名 | **Neko** |
| 外观风格 | 可爱橘猫 —— 橙白配色、圆脸大眼、粗描边（非纯黑描边） |
| 精灵尺寸 | 32×32 画布，放大 **3 倍** = 96×96 显示（保持像素锐利，禁止非整数缩放） |
| 动画帧数 | **4 帧/状态** 循环 |

### 功能范围 v1

| 项 | 决策 |
|---|---|
| 首发功能 | 眼神追踪 + 边缘吸附悬挂 + 键盘映射 + 抚摸好感度（**四项全上**） |
| 空闲行为 | 待机 + 走动 + 坐下 + 睡觉（30s 坐下 / 5min 睡觉）**+ 拖拽物理**（甩动、抛物线落下） |
| 径向菜单 | **8 个槽位**，支持拖拽排序、常用置顶 |
| 说话气泡 | **做，仅插件驱动**（宿主只提供渲染，`Say()` 由插件调用，不做内置闲聊台词库） |
| 托盘菜单 | 设置 / 暂停恢复动画 / 关于·版本 + 退出（**开机自启开关不放托盘，只放设置窗口**） |

### 素材

| 项 | 决策 |
|---|---|
| 来源 | **灯灯自绘**（LibreSprite / Aseprite） |
| 规格 | 见 `neko-sprite-spec.md` —— 8 状态 × 4 帧，眼球独立分层 |
| 开发期 | 我先用**占同构占位图**跑通渲染管线，正式素材画完直接覆盖，代码零改动 |

### 工程与分发

| 项 | 决策 |
|---|---|
| 部署模式 | **两套都做** —— 开发期依赖系统 .NET 8 Runtime（8 MB）/ 发朋友用自包含（35–50 MB，零依赖） |
| 配置文件 | **JSON**（`System.Text.Json`，零依赖）；插件 manifest 仍用 **TOML**（手写注释友好） |
| 插件签名 | **v1 不做，预留接口**（自用+朋友圈，靠物理控制即可） |
| 开机自启 | **默认关**，在设置窗口里开启 |
| 插件分发 | **手动放 `.npet` 文件**（解压到 `plugins/<id>/`），v1 不做插件管理 GUI |

---

## 一、需求与设计原则

### 1.1 需求要点

像素风小猫桌宠，常驻桌面。左键抚摸，右键径向菜单打开常用插件，托盘右键进完整设置。基础功能：眼神追踪、边缘吸附、键盘映射。扩展以插件形式增删、置顶。

### 1.2 硬约束（来自需求）

- **轻量、无感运行**：常驻内存红线 < 120 MB，空闲 CPU < 0.5%
- **可扩展性**：插件化，可自加可加给朋友
- **环境要求低**：不引入额外 SDK / 复杂工具链
- **仅桌面端**：无需考虑跨平台

### 1.3 三条不可妥协的工程原则（依然适用）

1. **Dogfooding（吃自己狗粮）**：所有内置功能——眼神追踪、边缘吸附、键盘映射——**必须以插件形式实现**，与未来自写/朋友写的插件走完全相同的加载路径。某个内置功能做不成插件 = API 有洞，补 API 绝不开后门。
2. **Core API 版本化从第一天开始**：插件 manifest 声明 `ApiVersion`，宿主做兼容层。公开发布后只增不改。
3. **插件崩溃与宿主隔离**：watchdog + 超时熔断是必做项。任一插件抛异常或死循环，宿主必须存活并提示「插件 X 已崩溃」。

---

## 二、技术选型：C# + WPF + .NET 8

### 2.1 选型理由

| 维度 | 评估 |
|---|---|
| 环境契合 | 你已有 VS 2022 + .NET 扩展，C# 主力开发能力 |
| 原生能力 | WPF 原生支持透明无边框窗口、Layered Window、托盘（`Hardcodet.NotifyIcon.Wpf`）、全局热键（`User32.RegisterHotKey`） |
| 插件模型 | `AssemblyLoadContext` 可动态加载 + 卸载 .dll |
| 包体 | 依赖系统 .NET 8 Runtime 时 < 8 MB；自包含 < 50 MB |
| 内存 | 工作集 50–80 MB（GC 工作站模式 + ReadyToRun） |
| 启动 | 冷启动 < 500 ms（ReadyToRun 跨 JIT） |

### 2.2 与原 Tauri 方案的差异

| 维度 | 之前（Tauri 2） | 现在（C# WPF） |
|---|---|---|
| 沙箱 | 必需（WASM 能力式） | **不需要**（仅自用/朋友，签名+白名单即可） |
| 插件形态 | JS / WASM 双轨 | .NET 类库（单一形态） |
| 包体 | 3–10 MB | 8 MB（依赖 Runtime）/ 35 MB（自包含） |
| 跨平台 | ✅ | ❌ Win only（用户接受） |
| 学习成本 | 需学 Rust | **零成本**（已有 C# 经验） |

**关键变化**：沙箱不是生死线后，插件系统可以做得轻很多——v1 只需 `AssemblyLoadContext` + 接口契约 + manifest 文件，第三方安全靠「谁拿得到插件.dll」物理控制。

### 2.3 关键技术点

- **透明窗口**：`WindowStyle=None` + `AllowsTransparency=True` + `Background=Transparent`
- **鼠标穿透/捕获切换**：`User32.SetWindowLong` 配合 `WS_EX_LAYERED` / `WS_EX_TRANSPARENT`
- **DPI 适配**：`WPF` 4.6+ 自动感知，加 `app.manifest` 声明 per-monitor v2
- **像素渲染**：`DrawingVisual` + `RenderTargetBitmap`，不走控件树，雪碧图 + 变换矩阵
- **系统托盘**：`Hardcodet.NotifyIcon.Wpf`（社区维护活跃，最常用）
- **全局热键**：`User32.RegisterHotKey` + WndProc 钩子
- **开机自启**：`Microsoft.Win32.Registry.CurrentUser\...\Run`，避免 UAC
- **配置**：自写轻量 JSON（`System.Text.Json`），不引入第三方库
- **TOML 解析**：用 `Tomlyn`（轻量，单文件 ~200 KB）解析 manifest

---

## 三、架构分层

```
┌─ 插件分发与签名（自用 + 朋友圈）─────────────────────┐
│  .npet 打包 · 安装/更新/卸载 · 简单签名校验         │
└──────────────────┬───────────────────────────────────┘
                   ↓
┌─ 插件生态 Plugin Layer ─────────────────────────────┐
│  内置插件（与第三方同权）   │  朋友/自写插件         │
│  眼神追踪·边缘吸附·热键     │  C# 类库 .dll          │
└──────────────────┬───────────────────────────────────┘
                   ↓
┌─ 插件运行时 Plugin Runtime ─────────────────────────┐
│  清单解析 │ 生命周期 │ 版本校验 │ 崩溃隔离+watchdog │
└──────────────────┬───────────────────────────────────┘
                   ↓
┌─ Core API · 唯一扩展契约（v1，版本化）───────────────┐
│  IPetApi        │ IUIApi           │ ICapabilityApi  │
│  动作表情气泡  │ 径向菜单托盘设置 │ 存储定时系统事件 │
└──────────────────┬───────────────────────────────────┘
                   ↓
┌─ 宿主内核 Host Core（插件永不直接触碰）──────────────┐
│ 窗口管理 │ 像素渲染 │ 输入托盘 │ 行为物理 │ 配置     │
└──────────────────┬───────────────────────────────────┘
                   ↓
┌─ 运行时底座 ────────────────────────────────────────┐
│  .NET 8 WPF · Windows 10 (1809+) · 系统托盘/热键     │
└──────────────────────────────────────────────────────┘
```

**铁律**：插件只能看到 Core API（`DesktopPet.Core` 项目的接口），绝不引用 Host Core 任何类型。Host Core 可重构不影响插件。

---

## 四、Core API 草案（v1，P3 冻结）

### 4.1 接口定义

```csharp
namespace DesktopPet.Core;

public interface IPetPlugin
{
    string Id { get; }                              // e.g. "com.lantern.eyetrack"
    string Name { get; }
    Version ApiVersion { get; }                     // 契约版本
    PluginPermissions RequiredPermissions { get; }  // [Flags]
    void OnLoad(IPluginContext ctx);
    void OnUnload();
    void OnTick(TimeSpan delta);                    // 主循环 tick
}

public interface IPetApi
{
    Task PlayAnimationAsync(string name, AnimationOptions? opts = null);
    void SetMood(PetMood mood);
    void Say(string text, int durationMs = 3000);
    Point GetPosition();
    Task MoveToAsync(Point target, int durationMs = 500);
    Rect GetBounds();                               // 可用桌面区域
}

public interface IUIApi
{
    void RegisterRadialSlot(RadialSlot slot);       // slot.Order 越小越靠前，支持置顶
    void RegisterTrayMenuItem(TrayMenuItem item);
    void RegisterSettingsPage(ISettingsPage page);
    void UnregisterAll(string scopeId);
}

public interface ICapabilityApi
{
    IPluginStorage Storage { get; }                 // 隔离 KV，命名空间 = plugin.Id
    IScheduler Scheduler { get; }                   // Every/Cron/At
    ISystem System { get; }                         // Notify/Clipboard/OpenUrl/PickFile
    IEventBus Events { get; }                       // 订阅宿主事件
}

public interface IPluginStorage
{
    T? Get<T>(string key);
    void Set<T>(string key, T value);
    void Remove(string key);
}
```

### 4.2 权限模型（[Flags]）

```csharp
[Flags]
public enum PluginPermissions
{
    None        = 0,
    PetAnimate  = 1 << 0,    // 控制宠物动作
    PetMove     = 1 << 1,    // 移动宠物
    UIRegister  = 1 << 4,    // 注册菜单/设置页
    Storage     = 1 << 8,
    Scheduler   = 1 << 9,
    Notify      = 1 << 12,
    Clipboard   = 1 << 16,   // 剪贴板
    Network     = 1 << 20,   // 受限 HTTP
    FileAccess  = 1 << 24,   // 高危，宿主二次确认
    Process     = 1 << 28,   // 高危
}
```

---

## 五、插件格式

### 5.1 目录结构

```
plugins/
└── eye-tracking/
    ├── plugin.toml          # manifest
    ├── EyeTracking.dll      # 编译产物
    ├── assets/              # 可选资源
    └── SIGNATURE            # 可选 ed25519 签名
```

### 5.2 plugin.toml

```toml
[plugin]
id          = "com.lantern.eyetrack"
name        = "眼神追踪"
version     = "0.1.0"
api         = 1                       # Core API 版本
entry       = "EyeTracking.dll"
author      = "Lantern"
description = "让小猫的眼睛跟着鼠标转"

[permissions]
pet         = ["animate", "read"]
events      = ["mouse:move"]

[ui]
radial      = false
settings    = true

[resources]
maxMemoryMB = 16
```

### 5.3 .npet 包（zip 改名）

```
eye-tracking.npet
├── plugin.toml
├── EyeTracking.dll
├── assets/
└── SIGNATURE              # 可选，v1 暂不强求
```

v1 分发方式（**够用优先**）：
- 朋友拿到的就是一份 `.npet`，解压到 `plugins/<id>/` 即可
- 后续再加签名、版本号自动升级、自动卸载

### 5.4 运行时目录布局（程序根目录）

```
Neko/
├── Neko.exe
├── plugins/                      ← 用户可自由增删的插件目录
│   ├── README.md                 # 目录约定说明
│   ├── _template/plugin.toml     # 插件清单模板
│   ├── com.lantern.eyetrack/     # 每个插件一个文件夹
│   │   ├── plugin.toml
│   │   ├── Plugin.EyeTracking.dll
│   │   └── assets/
│   └── com.lantern.edgesnap/
├── data/                         ← 运行时数据（不随插件更新丢失）
│   ├── config.json
│   └── plugins/                  # 插件私有存储，按 id 分目录
│       └── com.lantern.eyetrack/
└── assets/                       # 内置像素猫雪碧图
```

### 5.5 加载器与安装器解耦（关键设计）

**加载器只认目录结构，不关心插件是怎么来的。**

```
加载器职责（v1 就写死，之后不改）：
  扫描 plugins/ 下每个一级子目录
    → 跳过 "_" 开头的目录
    → 读 plugin.toml，解析 manifest
    → 校验 api 版本兼容性
    → 校验权限声明合法性
    → 用 AssemblyLoadContext 加载 entry dll
    → 实例化 IPetPlugin，调用 OnLoad(ctx)
```

**安装器只是文件搬运。** 所以 v2 加拖拽安装时：

```
拖拽 .npet → 校验签名 → 解压到 plugins/<id>/ → 调用加载器热加载
```

**加载器一行代码都不用改** —— 这就是目录约定必须先定死的原因。若 v1 把「安装」和「加载」耦合在一起（比如加载器直接读压缩包），v2 加拖拽就得重写加载逻辑。

详细约定见 `plugins/README.md`。

### 5.6 启用 / 禁用机制

**v1 用配置文件控制，不移动、不改名文件夹：**

```json
{
  "disabledPlugins": ["com.example.noisy-plugin"]
}
```

| 方案 | 为什么不用 |
|---|---|
| 改名加 `.disabled` 后缀 | 破坏插件自更新，下次更新找不到原目录 |
| 移到 `_disabled/` 目录 | 同上，且文件位置变了插件内部相对路径可能失效 |
| **config.json 维护禁用列表** ✅ | 不碰文件系统结构，可逆，不影响插件自身布局 |

禁用后：dll 不装载，其注册的径向菜单槽位 / 托盘项 / 设置页自动撤销。

### 5.7 插件数据存储位置

**插件数据一律写到 `data/plugins/<plugin-id>/`，禁止写进自己的插件目录。**

理由：插件目录必须保持「可整体覆盖替换」的干净状态，这样更新插件时直接覆盖整个文件夹不会丢用户数据。

宿主通过 `ICapabilityApi.Storage` 封装，插件不感知真实路径：

```csharp
ctx.Capability.Storage.Set("lastRemind", DateTime.Now);
var t = ctx.Capability.Storage.Get<DateTime>("lastRemind");
```

Storage 按 `plugin.id` 做命名空间隔离，插件之间互不可见。

---

## 六、七阶段开发路线

| 阶段 | 内容 | 验收标准 |
|---|---|---|
| **P0 技术验证** | 透明窗口、鼠标穿透、托盘、全局热键、内存基线 | 窗口贴边不抖，空闲内存 < 80 MB |
| **P1 宿主内核** | `DrawingVisual` 像素渲染、动画状态机、事件总线、配置读写 | 猫能走能拖能落，加载雪碧图 5 个状态 |
| **P2 插件系统** | `AssemblyLoadContext` 加载器、manifest 解析、版本校验、watchdog | 一个空插件装载并 tick |
| **P3 内置重构** ⭐ | 眼神追踪 / 边缘吸附 / 键盘映射 全部改写为插件 | 内置功能全插件化 → **API 冻结** |
| **P4 交互层** | 左键抚摸、状态机加分、右键径向菜单（可配置槽位）、托盘设置页 | 菜单可拖拽排序、常用置顶 |
| **P5 打包分发** | 单文件发布、`.npet` 安装/卸载、注册表自启 | 朋友拷一份即可用 |
| **P6 SDK 与模板** | `dotnet new pet-plugin` 模板、API 参考、3 个示例插件 | 朋友能独立写出插件 |

**P3 仍是胜负手**——把内置功能强行改写为插件，会一次性暴露 Core API 的全部漏洞。这一关通过才允许冻结 API。

---

## 七、资源预算与性能清单

### 7.1 硬指标

| 指标 | 目标 | 红线 |
|---|---|---|
| 安装包（依赖系统 .NET 8） | < 8 MB | 15 MB |
| 安装包（自包含） | < 50 MB | 80 MB |
| 常驻内存 | 50–80 MB | 120 MB |
| 空闲 CPU | < 0.5% | 2% |
| 冷启动 | < 500 ms | 1.5 s |
| 磁盘占用 | < 30 MB | 60 MB |

### 7.2 优化清单（.NET 8 + WPF 专项）

**AOT / JIT：**
1. 启用 `<PublishReadyToRun>true</PublishReadyToRun>`（跨 JIT，启动 +30%）
2. `<TieredCompilation>true</TieredCompilation>`（默认开）
3. `<ServerGarbageCollection>false</ServerGarbageCollection>`（桌宠是工作站负载）
4. `<ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>`（不需要并发回收）

**WPF：**
5. 不引用 `System.Windows.Controls` 完整主题，自己用 `DrawingVisual` 画
6. 像素渲染走 `RenderTargetBitmap` + `DrawingContext`，不建控件树
7. 透明窗口 + `WindowChrome`，避免 DWM 重新合成整窗口
8. 关闭 `EnableDefaultAssemblyLoadContextResolver` 不必要的探测

**运行期：**
9. 鼠标采样 30Hz（自定义计时器，不走 WPF mousemove 高频）
10. 空闲时主循环 sleep 50 ms
11. 配置仅在变更时落盘（dirty flag）
12. 插件懒加载（启动时只装 `manifest.toml` 列表，启用时才装 DLL）
13. 禁用插件不占内存（`AssemblyLoadContext.Unload()`）
14. 托盘图标用 `System.Drawing.Icon` 而非 WPF Image（启动时一次性解码）
15. 不写循环日志，需要时用 `Trace.WriteLine` + DebugView

**发布：**
16. `<PublishSingleFile>true</PublishSingleFile>`
17. `<SelfContained>true</SelfContained>`（**v0.1.60 定稿**：用户零前置、双击即用；代价是体积 ~174MB / 稳态内存 ~177MB）
18. `<EnableCompressionInSingleFile>false</EnableCompressionInSingleFile>`（**v0.1.60 实测决策**：压缩 on = 80MB/276MB、off = 174MB/177MB；桌宠常驻 → 内存优先，关压缩）
19. `<DebugType>embedded</DebugType>`（打包 PDB）
20. **不启用 trimming**（WPF 尚不友好）

### 7.3 发布方案：两种 .NET 部署模式

| 模式 | 包体 | 用户前置 | 推荐场景 |
|---|---|---|---|
| **依赖系统 Runtime** | 8 MB | 需装 .NET 8 Desktop Runtime（一次 60 MB） | 你自己、懂技术的 |
| **自包含** | 35–50 MB | 零依赖 | 朋友 |

建议：**开发期用依赖模式（快），发朋友用自包含（简单）**。VS 的「发布」向导两种都一行参数。

---

## 八、Visual Studio 2022 项目结构

### 8.1 解决方案布局

```
DesktopPet.sln
├── src/
│   ├── DesktopPet.App/            # WPF 主程序 (WinExe)
│   │   ├── Window/
│   │   │   ├── PetWindow.xaml(.cs)
│   │   │   ├── RadialMenuWindow.xaml(.cs)
│   │   │   └── SettingsWindow.xaml(.cs)
│   │   ├── Renderer/
│   │   │   ├── SpriteRenderer.cs       # DrawingVisual
│   │   │   ├── FrameAnimator.cs        # 帧计时
│   │   │   └── SpriteAtlas.cs          # 雪碧图加载
│   │   ├── Input/
│   │   │   ├── MouseTracker.cs         # 30Hz 采样
│   │   │   ├── GlobalHotkey.cs         # RegisterHotKey
│   │   │   └── PetInteraction.cs       # 左键抚摸检测
│   │   ├── System/
│   │   │   ├── TrayManager.cs          # NotifyIcon
│   │   │   ├── EdgeSnap.cs             # Win32 API
│   │   │   ├── ScreenHelper.cs         # 多显示器 + DPI
│   │   │   └── AutoStart.cs            # 注册表
│   │   ├── Behavior/
│   │   │   ├── StateMachine.cs
│   │   │   ├── Physics.cs              # 重力、抛物线
│   │   │   └── MoodController.cs
│   │   ├── Config/
│   │   │   ├── ConfigManager.cs
│   │   │   └── PetConfig.cs
│   │   ├── Pet/
│   │   │   └── PetController.cs        # 业务入口
│   │   ├── App.xaml(.cs)               # 启动编排
│   │   └── DesktopPet.App.csproj
│   ├── DesktopPet.Core/            # 接口定义（插件可引用）
│   │   ├── IPetPlugin.cs
│   │   ├── IPetApi.cs
│   │   ├── IUIApi.cs
│   │   ├── ICapabilityApi.cs
│   │   ├── IPluginContext.cs
│   │   ├── Models/                   # 枚举/DTO
│   │   └── DesktopPet.Core.csproj
│   ├── DesktopPet.Runtime/          # 插件运行时
│   │   ├── PluginLoader.cs
│   │   ├── PluginHost.cs
│   │   ├── ManifestParser.cs         # Tomlyn
│   │   ├── PluginContext.cs
│   │   ├── PermissionChecker.cs
│   │   ├── Watchdog.cs
│   │   └── DesktopPet.Runtime.csproj
│   └── DesktopPet.Sdk/              # 插件 SDK（打包给朋友）
│       ├── PluginBase.cs             # 抽象基类
│       ├── Attributes.cs
│       └── DesktopPet.Sdk.csproj
├── plugins/                        # 运行时插件目录（用户可自由增删）
│   ├── README.md                   # 目录约定说明
│   ├── _template/plugin.toml       # 清单模板
│   └── <plugin-id>/                # 每个插件一个文件夹
├── plugins-src/                    # 内置插件源码（与第三方完全同构）
│   ├── Plugin.EyeTracking/
│   ├── Plugin.EdgeSnap/
│   ├── Plugin.HotkeyMap/
│   └── Plugin.Affection/
├── assets/                         # 像素猫雪碧图 + 托盘图标（已生成）
│   ├── neko_idle.png               # 128x32，4 帧横排
│   ├── neko_walk.png
│   ├── neko_sit.png
│   ├── neko_sleep.png
│   ├── neko_pet.png
│   ├── neko_drag.png
│   ├── neko_fall.png
│   ├── neko_climb.png
│   ├── neko_eyes_open.png          # 眼球独立分层
│   ├── neko_eyes_pet.png
│   ├── neko_eyes_blink.png
│   ├── neko_eyes_scared.png
│   ├── neko_shadow.png
│   └── tray.ico
├── tools/
│   ├── generate_sprites.py         # 像素猫生成脚本（参数化，可重跑）
│   └── PluginTemplate/             # dotnet new pet-plugin 模板
└── docs/
    ├── api.md
    ├── plugin-dev.md
    └── distribution.md
```

**注意 `plugins/` 与 `plugins-src/` 分离**：内置插件源码放在 `plugins-src/` 由主解决方案编译，产物输出到 `plugins/`。这样内置插件在运行时目录里和朋友写的插件没有任何区别——dogfooding 铁律的物理体现。

### 8.2 项目引用关系

```
DesktopPet.App ─┬─→ DesktopPet.Core
                ├─→ DesktopPet.Runtime ─→ DesktopPet.Core
                └─→ DesktopPet.Sdk (可选，仅 IDE 提示用)
plugins/* ─────→ DesktopPet.Sdk ─→ DesktopPet.Core
```

**关键约束**：`DesktopPet.Runtime` 不能引用 `DesktopPet.App`（方向单向，否则插件 DLL 反向依赖宿主就死循环了）。Core 是唯一的"水陆两栖"层——宿主和插件都引用。

### 8.3 项目设置要点（`.csproj`）

`DesktopPet.App.csproj`：
```xml
<PropertyGroup>
  <OutputType>WinExe</OutputType>
  <TargetFramework>net8.0-windows</TargetFramework>
  <UseWPF>true</UseWPF>
  <Nullable>enable</Nullable>
  <LangVersion>latest</LangVersion>
  <ApplicationIcon>assets\tray.ico</ApplicationIcon>
</PropertyGroup>
```

发布配置（自包含 + 单文件，**与 csproj 实际值对齐，v0.1.60**）：
```xml
<PropertyGroup>
  <PublishSingleFile>true</PublishSingleFile>
  <SelfContained>true</SelfContained>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  <PublishReadyToRun>true</PublishReadyToRun>
  <EnableCompressionInSingleFile>false</EnableCompressionInSingleFile>
  <DebugType>embedded</DebugType>
</PropertyGroup>
```

---

## 九、Win10 兼容性清单

| 项 | 注意 |
|---|---|
| 最低版本 | Windows 10 1809 (Build 17763) |
| .NET 8 | 完全支持，要求 Desktop Runtime |
| WPF 透明 | 需开启 `AllowsTransparency` + DWM 合成（Win10 默认开） |
| DPI | 在 `app.manifest` 声明 `PerMonitorV2` |
| 鼠标穿透 | `WS_EX_LAYERED` + `WS_EX_TRANSPARENT` 标志切换 |
| 全局热键 | `RegisterHotKey` 无 UAC 弹窗 |
| 自启 | 写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 免 UAC |
| 托盘 | Win10 通知中心会自动收集托盘图标，配置 `ShowBalloonTip` 行为 |
| 屏幕缩放 | 175%/200% 缩放下需手动验证雪碧图锐度 |

---

## 十、内置功能清单（v1）

### 10.1 宠物状态（8 个，均 4 帧循环）

| 状态 | 帧率 | 触发 | 眼球层 |
|---|---|---|---|
| `Idle` 待机 | 2 fps | 默认 | ✅ |
| `Walk` 随机游走 | 8 fps | 随机决定方向游走 | ✅ |
| `Sit` 坐下 | 2 fps | 无操作 30 s | ✅ |
| `Sleep` 睡觉 | 1 fps | 无操作 5 min | ❌ 自带闭眼 |
| `Pet` 被抚摸 | 10 fps | 左键点击宠物 | ✅ 弯月眼 |
| `Drag` 被拖拽 | 6 fps | 左键长按拖拽 | ✅ 惊恐眼 |
| `Fall` 落下 | 12 fps | 拖拽释放 | ✅ 瞪大眼 |
| `Climb` 悬挂 | 4 fps | 吸附屏幕边缘 | ✅ |

### 10.2 交互

- **左键抚摸**：点击落在宠物矩形内 → 切 `Pet` 状态 1.5 s → 触发 `pet:pet` 事件（好感度由插件处理）
- **右键径向菜单**：以宠物中心弹出 **8 槽位**圆环，支持拖拽排序、常用置顶
- **拖拽物理**：左键长按 → 跟随鼠标（记录速度矢量）→ 释放后按惯性抛物线落下 → 触底反弹衰减
- **滚轮**：可绑定插件命令（如切换装扮、调节音量）
- **说话气泡**：宿主只提供渲染能力，**内容完全由插件通过 `Say()` 驱动**，内置不做台词库

### 10.3 内置插件（必须插件化 —— dogfooding 铁律）

| 插件 | 权限 | 订阅事件 | 职责 |
|---|---|---|---|
| `EyeTracking` | PetAnimate, PetRead | `mouse:move` | 计算鼠标方向 → 眼球层 ±2px 偏移 + 随机眨眼 |
| `EdgeSnap` | PetMove, PetRead | `screen:change`, `tick` | 边缘吸附悬挂、任务栏站立 |
| `HotkeyMap` | UIRegister, Storage | `hotkey:trigger` | 全局热键 → 动作/插件命令映射 |
| `Affection` | PetAnimate, Storage | `pet:pet`, `tick` | 抚摸累积好感度 → 解锁动作/表情 |

**这四个插件与朋友的插件走完全相同的加载路径和 API。** 若某个做不成插件，说明 Core API 有洞，补 API 不开后门。

### 10.4 拖拽物理参数（P1 后期）

| 参数 | 值 | 说明 |
|---|---|---|
| 重力加速度 | 1800 px/s² | 屏幕像素坐标系 |
| 释放速度继承系数 | 0.6 | 甩动时保留 60% 鼠标速度 |
| 触底反弹系数 | 0.35 | 每次反弹损失 65% 能量 |
| 静止阈值 | 20 px/s | 低于此速度判定为落地，切回 Idle |

---

## 十一、决策状态

**全部 19 项已确认，无遗留问题**（详见第〇章）。

| 编号 | 事项 | 状态 |
|---|---|---|
| A1–A4 | 命名 / 风格 / 尺寸 / 帧数 | ✅ Neko / 可爱橘猫 / 32×32×3 / 4 帧 |
| B1–B5 | v1 功能 / 行为 / 槽位 / 托盘 / 气泡 | ✅ 全上 / 全套+物理 / 8 槽 / 三项 / 插件驱动 |
| C1–C2 | 素材来源 / 帧数 | ✅ 灯灯自绘 / 4 帧 |
| D1–D5 | 部署 / 签名 / 配置 / 自启 / 分发 | ✅ 两套 / 预留 / JSON / 默认关 / 手动 |
| E1–E3 | 称呼 / 城市 / 风格 | ✅ 哥们 → 灯灯 / 佛山 / 直接高效 |

### 下一步：进入 P0

1. 装 `.NET 8 SDK` + 建 VS 2022 解决方案（5 个项目骨架）
2. 跑通透明窗口 + 鼠标穿透 + 托盘 + 全局热键
3. 实测空闲内存 / CPU 基线，对照第 7.1 节红线
4. 我这边同步生成占位雪碧图，让渲染管线能立刻验证

**阻塞项**：等灯灯画出 `idle` 和 `walk` 两组帧即可完整验证状态机，其余状态可边开发边补。

---

## 十二、给 CodeBuddy 的启动提示词

> 若转到 CodeBuddy 继续，可直接用：
>
> 「我要从零开发 Windows 10 桌宠程序，技术栈 C# + WPF + .NET 8，使用 VS 2022。先读 `desktop-pet-plan.md`。**核心设计原则**：内置功能必须以插件形式实现（dogfooding），插件只通过 `DesktopPet.Core` 接口与宿主交互，绝不直接引用宿主内部。**当前阶段 P0**：搭出透明无边框无标题栏窗口 + 鼠标穿透 + 系统托盘 + 全局热键的最小可运行骨架，测出空闲内存和 CPU 基线数字。**资源红线**：常驻内存 < 120 MB（目标 80 MB），空闲 CPU < 0.5%，冷启动 < 1.5 s。」
