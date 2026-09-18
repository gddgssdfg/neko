# Neko 插件开发手册（Plugin Authoring Guide）

> **本手册是自包含的。** 照着它能从零写出一个可运行的 Neko 插件，**不需要读宿主源码**。
>
> **给 AI 用**：把本文件整个贴给任意 AI，说「按这份手册写一个 XXX 插件」，就能产出可直接编译的代码。
> 手册里所有 API 签名、权限键、事件名均已对照**宿主 `src/` 源码**逐项核实。

| 项 | 值 |
|---|---|
| **最后核对日期** | **2026-09-19** |
| **核对到的宿主版本** | **v0.1.83** |
| Core API 版本 | **1**（稳定契约，公开发布后只增不改） |
| 宿主程序版本「权威来源」 | `src/DesktopPet.App/DesktopPet.App.csproj` 的 `<Version>`（本手册**不承诺**等于当前版本；一致性由 `tools/check_version.py` 守护） |

> 📌 **为什么改掉「对应宿主版本 = 某固定号」这种写法**：本手册面向的是**稳定的 Core API 版本 1**。宿主小版本迭代（`v0.1.61 → v0.1.83` 共 22 版）里，绝大多数**没动 `DesktopPet.Core` 的签名**，本手册就无需逐版更新；少数**「只增不改」**的追加（如 v0.1.83 新增 `ISystem.Log`）也只需补一小段，而非重写。旧写法把宿主版本号硬编码成「必须匹配」，既要求每版都改、又极易漏改——这正是审计 P1-7 立案的「权威文档版本漂移」（曾从 v0.1.0 漂到 v0.1.60）。
> 现改为「**最后核对日期 + 核对到的版本**」：既保留「文档新鲜度」信号，又不再随每版机械漂移。**每次核对后，请只更新上表的『最后核对日期』与『核对到的宿主版本』两行。**

---

## 0. 维护约定（改宿主 API 的人必读）

这份手册是插件 API 的**单一权威源**。任何人（包括 AI）修改以下内容后，**必须同步更新本手册**：

| 改了什么 | 必须同步本手册的哪部分 |
|---|---|
| `src/DesktopPet.Core/` 任何接口增删改 | 第 5 节 API 参考 + 第 13 节版本历史 |
| `Enums.cs` 的 `PluginPermissions` | 第 4 节权限表 |
| `Enums.cs` 的 `PetState` / `PetMood` | 第 5.6 节枚举 |
| `ICapabilityApi.cs` 的 `PetEvents` 常量 | 第 7 节事件表 |
| `PluginLogLevel.cs` / `ISystem.Log` | 第 5.5 节 `ISystem`（插件日志） |
| `PermissionChecker.cs` 的映射键（`Map`） | 第 4 节权限键表 |
| 新增/移除宿主实际广播的事件 | 第 7 节事件表的「是否已广播」列 |
| `PluginTrust.cs` / `builtins.lock.json` | 第 4.4 节信任通道 |
| 新增 `neko-assets/icons/*.png` | 第 5.4 节图标 key 列表 |
| 任何一处核对（即使无签名变化） | 更新**页首表格**的「最后核对日期」与「核对到的宿主版本」 |

**Core API 版本化铁律：公开发布后只增不改。** 已有方法签名不允许删除或改参数，需要变更就新增重载并升 `api` 版本号。

> ⚠️ **一个「声明了却没人调」的坑（本手册已按 v0.1.82 修正）**：`IPetPlugin.OnTick(TimeSpan)` 在契约里**声明了、`PluginBase` 也可重写**，但**宿主当前没有任何调用点**（`PluginHost` 只调 `OnLoad`/`OnUnload`，无 tick 循环）。周期任务请一律用 `Capability.Scheduler.Every/At`，**不要依赖 `OnTick`**。详见第 5.1 / 6 / 11 节。

---

## 1. 三十秒速览

一个 Neko 插件 = 一个文件夹，三样东西：

```
plugins/com.yourname.myplugin/
├── plugin.toml      # 清单：id、入口 dll、权限
├── MyPlugin.dll     # 编译产物（文件名要和 entry 对上）
└── assets/          # 可选：插件自带资源
```

最小插件代码：

```csharp
using DesktopPet.Core;
using DesktopPet.Sdk;

public class MyPlugin : PluginBase
{
    public override string Id => "com.yourname.myplugin";   // 必须与文件夹名一致
    public override string Name => "我的插件";

    public override void OnLoad(IPluginContext ctx)
    {
        base.OnLoad(ctx);                                   // 必须先调，否则 Pet/UI/Capability 不可用
        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "hello", Label = "打个招呼", Icon = "wave", Order = 10,
            OnInvoke = () => Pet.Say("喵！"),
        });
    }
}
```

---

## 2. 目录与部署约定

```
plugins/<plugin-id>/          # 文件夹名必须 == plugin.toml 里的 id
├── plugin.toml               # 必需
├── <Entry>.dll               # 必需，文件名由 entry 指定
└── assets/                   # 可选
```

- 宿主扫描 `plugins/` 的**每个一级子目录**；没有 `plugin.toml` 的跳过（记日志，不影响其他插件）
- `_` 开头的目录跳过（临时放东西用）
- 插件数据写到 `data/plugins/<plugin-id>/`（由 `Capability.Storage` 提供），**禁止写进插件目录**
  - 原因：插件目录要保持「可整体覆盖替换」，更新插件才不会丢用户数据
- 启用/禁用：改 `data/config.json` 的 **`DisabledPlugins`** 数组（⚠️ **PascalCase**——宿主 `ConfigManager` 未设命名策略，键名与 C# 属性名一致），**不改名、不移动文件夹**

### ⚠️ 插件目录里不要放 `DesktopPet.Core.dll` / `DesktopPet.Sdk.dll`

这两个是**宿主共享程序集**，宿主通过白名单把它们路由回自己已加载的实例。
构建时 `dotnet build` 会把引用到的 Core/Sdk dll 一起输出到 `bin/`，但**部署时只拷插件自己的 dll + plugin.toml**，不要把整个 `bin/` 拷进去。

---

## 3. `plugin.toml` 完整字段

```toml
[plugin]
id          = "com.yourname.myplugin"  # 必需，反向域名，全局唯一，与文件夹名一致
name        = "我的插件"                # 必需，显示名（中文 OK）
version     = "0.1.0"                  # 语义化版本
api         = 1                        # 依赖的 Core API 版本，宿主据此做兼容判断
entry       = "MyPlugin.dll"           # 必需，入口 dll 文件名（相对本目录）
author      = "你的名字"
description = "一句话说明这个插件做什么"
homepage    = "https://..."            # 可选

[permissions]
pet     = ["read", "animate"]   # 自动加 "pet." 前缀
ui      = ["register"]          # 自动加 "ui." 前缀
storage = ["local"]             # 自动加 "storage." 前缀
other   = ["scheduler"]         # 不加前缀，直接写完整键

[ui]
radial   = true     # 是否占用径向菜单槽位
settings = false    # 是否在设置窗口提供配置页

[resources]
maxMemoryMB = 16    # 内存上限（MB），超出被 watchdog 终止
```

字段类型定义见 `PluginManifest.cs`：`Id/Name/Version/Entry/Author/Description` 为 `string`，`Api` 为 `int`，`Homepage` 可空。

---

## 4. 权限模型（**易错，仔细看**）

权限是 `[Flags]` 位掩码（⚠️ v0.1.80 起底层类型为 **`ulong`**）。**实际模型为「加载时校验」**：

> manifest 声明 → 宿主解析权限键 → 判定风险等级 → **高风险权限（`file` / `process`）拒绝自动加载**；
> 加载成功后，插件可在本次已授予的权限范围内自由调用（宿主不再逐次校验）。

也就是说：**声明了哪些权限，就等于拿到了哪些能力**；高风险权限会导致插件被拒绝加载（而不是运行到一半才报错）。
因此**只声明你真正需要的最小权限集**，既是最佳实践，也能避免被高风险规则拦截。

> ⚠️ **唯一的「高风险豁免」是内置信任通道**（见 **4.4**）：只有**随包发布的官方内置插件**（id 登记在 `builtins.lock.json`、且内容摘要匹配）才能声明 `file` / `process`。
> **第三方插件不在清单内 → 声明 `file`/`process` 一律被拒载。** 这是安全底线，别指望绕过。

历史注：早期设计文档曾描述「每次调用校验」，但运行时从未实现逐次守卫（v0.1.61 已统一文档口径为真实实现）。

### 4.1 三个段会自动加前缀，一个不加

这是最容易配错的地方：

| manifest 段 | 写法 | 实际查表的键 |
|---|---|---|
| `pet` | `["animate"]` | `pet.animate` |
| `ui` | `["register"]` | `ui.register` |
| `storage` | `["local"]` | `storage.local` |
| `other` | `["scheduler"]` | `scheduler`（**不加前缀**） |

### 4.2 完整权限键表

| 完整键 | 枚举值 | 位 | 说明 | 风险 |
|---|---|---|---|---|
| `pet.animate` | `PetAnimate` | `1<<0` | 播放宠物动画 | 低 |
| `pet.move` | `PetMove` | `1<<1` | 移动宠物 | 低 |
| `pet.read` | `PetRead` | `1<<2` | 读取宠物位置与状态 | 低 |
| `ui.register` | `UIRegister` | `1<<3` | 注册径向菜单 / 托盘项 / 设置页 | 低 |
| `storage.local` | `Storage` | `1<<4` | 插件私有存储 | 低 |
| `scheduler` | `Scheduler` | `1<<5` | 定时任务 | 低 |
| `notify` | `Notify` | `1<<6` | 系统通知 | 低 |
| `clipboard` | `Clipboard` | `1<<7` | 剪贴板读写 | 中 |
| `network` | `Network` | `1<<8` | 网络请求（域名白名单） | 中 |
| `file` | `FileAccess` | `1<<9` | 文件系统（任意读写） | **高**（拒载，除非受信） |
| `process` | `Process` | `1<<10` | 启动进程 | **高**（拒载，除非受信） |
| **`file.own`** | `FileOwnDir` | `1<<11` | 仅写插件**自己的数据目录** `data/plugins/<id>/`（v0.1.80 新增） | 低 |
| **`media`** | `MediaKey` | `1<<12` | 发送系统媒体键（上一曲/播放暂停/下一曲，v0.1.80 新增） | 低 |

> **`file.own` 与 `media` 的意义（v0.1.80 权限细分）**：把「合法但曾被粗 token 误伤」的能力从高危 token 里拆出来——
> 只写自己的数据目录**不等于**任意文件读写；发媒体键**不等于**启动任意进程。
> 这两个是**低危**，不会命中 `IsHighRisk`，可放心声明。
> 实例：内置插件 `com.lantern.music` 就是 `other = ["media"]`（不需要 `process`）。
> ⚠️ 注意 `file.own` 是**新的细粒度 token**，与高危的 `file` **不是**同一个键——写自己的数据目录其实用 `Capability.Storage` 就够了，通常无需声明任何文件权限。

### 4.3 代码里声明

`RequiredPermissions` 用**枚举**，不用字符串：

```csharp
public override PluginPermissions RequiredPermissions =>
    PluginPermissions.PetAnimate | PluginPermissions.PetRead
    | PluginPermissions.UIRegister | PluginPermissions.Storage;
```

> **注意**：`RequiredPermissions` 是给宿主展示用的，实际授予以 manifest 为准。两边都要写，保持一致。

### 4.4 内置信任通道（`file` / `process` 的唯一豁免，v0.1.80）

宿主 exe 同级放一份 **`builtins.lock.json`**（**不在 `plugins/` 内**，避免被插件自写篡改），为「随包发布的官方内置插件」开一条**受控信任通道**：

```jsonc
{
  "schema": 1,
  "builtins": [
    { "id": "com.lantern.music",
      "manifestSha256": "…（可选，绑定 plugin.toml 内容）",
      "entrySha256":    "…（可选，绑定入口 dll 内容）" }
  ]
}
```

判定逻辑（`src/DesktopPet.Runtime/PluginTrust.cs` + `PluginLoader.TryLoadOne`）：

```
manifest 声明了高风险权限（file / process）？
  ├─ 否 → 正常加载
  └─ 是 → 该插件 id 在 builtins.lock.json 内？
            ├─ 否 → ❌ 拒载（"声明了高风险权限且未受信任"）   ← 第三方插件走这条
            └─ 是 → 清单若登记了摘要，现场 manifest/入口 dll 的 SHA-256 必须匹配
                     ├─ 匹配（或清单未登记摘要）→ ✅ 加载
                     └─ 不匹配 → ❌ 拒载（"摘要不匹配"）
```

**第三方的你要知道的：**
- **你不在清单内**（清单由构建期 `tools/gen_builtins_lock.py` 生成、随包携带）→ **声明 `file`/`process` 一定被拒载**。
- 清单**缺失 / 损坏 / schema 不符** → 宿主按「一律不信任」处理（安全兜底，绝不因异常而放行）。
- **想要「读文件」能力？** 绝大多数场景不需要它：配置/数据用 `Capability.Storage`（= `data/plugins/<id>/`），已足够。
  真要碰文件系统，目前**没有**面向第三方的高危放行路径（这是刻意的设计，不是遗漏）。

---

## 5. API 完整参考

插件通过继承 `DesktopPet.Sdk.PluginBase` 获得全部能力。命名空间：`DesktopPet.Core`。

### 5.1 `PluginBase`（基类）

```csharp
public abstract class PluginBase : IPetPlugin
{
    protected IPluginContext Context { get; }      // 完整上下文
    protected IPetApi Pet { get; }                 // 快捷：宠物控制
    protected IUIApi UI { get; }                   // 快捷：界面注册
    protected ICapabilityApi Capability { get; }   // 快捷：能力

    public abstract string Id { get; }
    public abstract string Name { get; }
    public virtual Version ApiVersion => new(1, 0);
    public virtual PluginPermissions RequiredPermissions => PluginPermissions.None;

    public virtual void OnLoad(IPluginContext ctx);   // 重写时必须先 base.OnLoad(ctx)
    public virtual void OnTick(TimeSpan delta);       // ⚠️ 宿主当前【未调用】——别依赖它（见下）
    public virtual void OnUnload();
    public virtual void Dispose();
}
```

> ⚠️ 重写 `OnLoad` **必须第一行调用 `base.OnLoad(ctx)`**，否则 `Pet` / `UI` / `Capability` 是 null。

> 🔴 **`OnTick` 是「已声明但宿主未接线」的 API（本手册实测 v0.1.82）**：契约里保留了它（也允许重写），但**宿主 `PluginHost` 没有任何调用点**——`OnLoad` 之后只会被调 `OnUnload` / `Dispose`，**没有 ~10Hz 的 tick 循环**。
> **故：不要用 `OnTick` 做任何周期逻辑**（写了也不会跑，还以为是 bug）。周期任务一律用 **`Capability.Scheduler.Every(...)`**；一次性定时用 **`Capability.Scheduler.At(...)`**。
> （历史：更早的文档/注释声称「约 10Hz 被宿主调用」，与实现不符；此处按源码更正。）

### 5.2 `IPluginContext`

```csharp
Version HostVersion { get; }   // 宿主程序版本
string  DataRoot { get; }      // 宿主数据根目录
string  PluginDataDir { get; } // 插件数据目录 data/plugins/<id>/
IPetApi Pet { get; }
IUIApi  UI { get; }
ICapabilityApi Capability { get; }
```

### 5.3 `IPetApi` — 宠物控制

```csharp
Task PlayAnimationAsync(PetState state, AnimationOptions? opts = null);
void SetMood(PetMood mood);
void Say(string text, int durationMs = 3000);
Point2 GetPosition();                              // 左上角像素坐标
Task MoveToAsync(Point2 target, int durationMs = 500);
Rect2 GetWorkArea();                               // 扣除任务栏的可用区域
```

`AnimationOptions`：`Loop` (默认 true)、`Interrupt` (默认 true)、`FrameRateOverride` (默认 -1 = 不覆盖)。

### 5.4 `IUIApi` — 界面注册

```csharp
void RegisterRadialSlot(RadialSlot slot);
void RegisterTrayMenuItem(TrayMenuItem item);
void RegisterSettingsPage(ISettingsPage page);
void UnregisterAll(string scopeId);   // 撤销某插件注册的全部 UI
```

```csharp
public sealed record RadialSlot
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string Icon { get; init; } = "default";  // 宿主解析为内置图标
    public int Order { get; init; } = 100;          // 越小越靠前
    public required Action OnInvoke { get; init; }
}

public sealed record TrayMenuItem
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public bool IsSeparator { get; init; }
    public Action? OnClick { get; init; }
}

public interface ISettingsPage
{
    string Id { get; }
    string Title { get; }
    object BuildContent();   // 返回 WPF 控件
}
```

> **v0.1.5+ 真实接入宿主设置窗口**：宿主 `SettingsWindow` 的**「插件」栏**（sidebar 第 5 个 section，v0.1.31 起为卡片式）逐插件卡片遍历 `_uiApi.AllSettingsPages`，**匹配 `Title == p.Manifest.Id` 或 `Title == p.Manifest.Name`** 后调用 `BuildContent()` 嵌入**该插件卡片内**显示。**Title 必须用 `Manifest.Id` 或 `Manifest.Name` 才能匹配**。示例见 Affection 插件（`AffectionSettingsPage` —— 当前分数 + 重置按钮）。
>
> ⚠️ 第三方插件要做自定义 WPF 设置页，**项目必须 `<UseWPF>true</UseWPF>`**（net8.0-windows TFM 已自带，引用 `System.Windows.Controls` 等）才能编译 WPF 控件。

**图标可选值**（`RadialSlot.Icon`，v0.1.8 起改为 PNG 资源）：宿主启动时一次性加载 `neko-assets/icons/*.png` 到缓存（用 Pillow + Segoe UI Emoji 预渲染），`Icon` 字段就是 PNG 文件名（不含后缀）。**未匹配时回退 `default`**。**运行时不再依赖系统 emoji 字体**，跨系统一致。

当前可用 key（**v0.1.82 实测共 20 个**，取自 `neko-assets/icons/*.png`）：

```
wave  heart  food  sleep  dance  home  gear  info  eye  pin
close default book camera clock  copy  next  play  prev  todo
```

> ⚠️ 旧手册只列了 12 个（`wave/heart/food/sleep/dance/home/gear/info/eye/pin/close/default`），**漏了 8 个**：`book`（便签/待办类）、`camera`（截图）、`clock`（报时/番茄钟/倒计时）、`copy`（剪贴板）、`next`/`play`/`prev`（媒体控制）、`todo`（待办）。
> 用到的 key 必须在上面这张表里；写错会静默回退 `default`（不报错）。

### 5.5 `ICapabilityApi` — 能力

```csharp
IPluginStorage Storage { get; }     // 插件私有 KV 存储
IScheduler     Scheduler { get; }   // 定时任务
ISystem        System { get; }      // 系统能力
IEventBus      Events { get; }      // 事件总线
```

**存储**（按 `plugin.Id` 隔离命名空间，插件间互不可见）：

```csharp
T?  Get<T>(string key);
void Set<T>(string key, T value);
void Remove(string key);
bool Contains(string key);
```

**定时任务**：

```csharp
IScheduleHandle Every(int intervalMs, Action callback);
IScheduleHandle At(DateTime time, Action callback);
// IScheduleHandle : IDisposable { bool IsActive { get; } }
// 注：v0.1.61 移除了 Cron —— 原实现忽略表达式退化为每分钟，属半成品；
//     需要周期任务请用 Every，需要定时一次请用 At（诚实优于半成品）。
```

> **回调异常隔离（v0.1.61）**：宿主对每个定时任务回调做了 try/catch 隔离——
> 你的回调抛异常**不会**拖垮宿主（会写日志），但**连续失败 5 次后该定时器会被自动停用**。
> 所以请确保回调内自行处理可预期异常，避免被熔断。

**系统能力**：

```csharp
void    Notify(string title, string body);        // 真实 Windows 通知（v0.1.82 起，非占位）
string? ReadClipboard();
void    WriteClipboard(string text);
void    OpenUrl(string url);
string? PickFile(string filter = "");

// v0.1.83 新增（🔴 只增不改，上面 5 个成员一字未动）：写一条插件日志
void Log(PluginLogLevel level, string message, Exception? exception = null);
```

`PluginLogLevel`（`src/DesktopPet.Core/PluginLogLevel.cs`）：`Debug | Info | Warn | Error`。

**`ISystem.Log` 语义（v0.1.83 新增）**：
- 宿主自动加 **`[插件:<id>]`** 前缀（如 `[插件:com.lantern.weather]`）——**你不要自己拼前缀**。
- 级别映射一一对应；宿主默认 `warn` → **`Debug`/`Info` 默认不落盘**（把 `data/config.json` 的 `logLevel` 调成 `debug`/`info` 才记录），`Warn`/`Error` 默认落盘；**仅 `Error` 记录 `exception` 堆栈**。
- 实现**永不抛异常**——可安全地在 `catch` 里调用。
- ⚠️ **日志会落盘**：不要把用户隐私（剪贴板正文、待办标题、API Key…）写进 `message`。
- ⚠️ **别再用 `Debug.WriteLine`** 记插件日志——Release 下没有调试器监听时它**等于静默**。这正是本次补 `Log` 的直接动因。

```csharp
try { DoRisky(); }
catch (Exception ex)
{
    // 宿主拼上 [插件:<你的id>]；Warn 默认落盘，留痕不再是"静默"
    Capability.System.Log(PluginLogLevel.Warn, "抓取失败，已跳过本轮", ex);
}
```

**事件总线**：

```csharp
Action On(string evt, Action<object?> handler);   // 返回取消订阅委托
void   Emit(string evt, object? payload = null);  // 向其他插件广播
```

### 5.6 枚举与类型

```csharp
enum PetMood { Idle, Happy, Sleepy, Angry, Sad }
enum PetState { Idle, Walk, Sit, Sleep, Pet, Drag, Fall, Climb, Work }

readonly record struct Point2(double X, double Y);   // Point2.Zero
readonly record struct Rect2(double X, double Y, double Width, double Height);  // .Right / .Bottom
```

> `PetState` 对应 9 组雪碧图动画：`Idle` 待机 / `Walk` 走动 / `Sit` 坐下 / `Sleep` 睡觉 /
> `Pet` 被抚摸 / `Drag` 被拖拽 / `Fall` 下落 / `Climb` 贴边悬挂 / `Work` 工作模式（v0.1.48 新增）。

---

## 6. 生命周期

```
宿主启动 → 扫描 plugins/ → 解析 plugin.toml → 校验 id 与目录名 → 权限授予（高风险→校验信任清单）
        → ALC 隔离加载 dll → 实例化 → OnLoad(ctx)
        → [运行中] 由你注册的「事件订阅 / Scheduler 定时器 / 径向菜单回调」驱动
                   ⚠️ 没有 OnTick 主循环——周期逻辑靠 Capability.Scheduler.Every
        → 退出 / 禁用 / 热重载 → OnUnload() → Dispose() → ALC 卸载
```

> 📌 **「运行中」这三类回调才是插件干活的入口**：`Capability.Events.On(...)`（宿主事件）、`Capability.Scheduler.Every/At(...)`（定时）、`UI.RegisterRadialSlot(...).OnInvoke`（用户点击）。**不要等 `OnTick`。**

**必须做的清理**（不做会导致热重载后重复注册或资源泄漏）：

```csharp
private Action? _unsub;
private IScheduleHandle? _timer;

public override void OnLoad(IPluginContext ctx)
{
    base.OnLoad(ctx);
    _unsub  = Capability.Events.On(PetEvents.PetPetted, OnPetted);
    _timer  = Capability.Scheduler.Every(5000, Tick5s);
}

public override void OnUnload()
{
    _unsub?.Invoke();   // 取消事件订阅
    _timer?.Dispose();  // 停止定时器
}
```

---

## 7. 事件表

常量定义在 `PetEvents`。**注意「是否已广播」列** —— 订阅未广播的事件不会报错，只是永远不触发。**别依赖它们**，初始化逻辑放 `OnLoad` 或订阅 `app:ready`。

| 常量 | 事件名 | payload 类型 | 是否已广播 |
|---|---|---|---|
| `PetEvents.PetPetted` | `pet:petted` | `null` | ✅ |
| `PetEvents.PetDragged` | `pet:dragged` | `PetDraggedArgs(X, Y)` | ✅ |
| `PetEvents.PetLanded` | `pet:landed` | `null` | ✅ |
| `PetEvents.PetEdge` | `pet:edge` | `PetEdgeArgs(Side, Attached)`（Side: 0左 1右 2上 3下） | ✅ |
| `PetEvents.MouseMoved` | `mouse:moved` | `MouseMovedArgs(X, Y)` 全局鼠标坐标，**约 5Hz 节流** | ✅ |
| `PetEvents.AppReady` | `app:ready` | `null` —— 插件全部加载完成后广播，可用于替代部分 `OnLoad` 内的延迟初始化 | ✅ v0.1.1+ |
| `PetEvents.AppWake` | `app:wake` | — | ❌ 预留，未广播 |
| `PetEvents.AppSleep` | `app:sleep` | — | ❌ 预留，未广播 |
| `PetEvents.ScreenChange` | `screen:change` | — | ❌ 预留，未广播 |
| `PetEvents.Tick` | `tick` | — | ❌ 预留，未广播（⚠️ **不能用 `OnTick(delta)` 代替——宿主未调用它**；周期逻辑改用 `Capability.Scheduler.Every`） |

### 7.1 如何读取 payload（v0.1.1+ 直接 cast）

从 v0.1.1 起，宿主 payload 用 **public record**（替代之前的匿名类型），跨程序集**可以直接 cast**，不用反射：

```csharp
// 反射方式（v0.1.0 兼容，仍可用）
private static double ReadDouble(object? payload, string name)
{
    if (payload is null) return 0;
    var prop = payload.GetType().GetProperty(name);
    return prop?.GetValue(payload) is double d ? d : 0;
}

// 推荐方式（v0.1.1+，强类型 cast）
Capability.Events.On(PetEvents.MouseMoved, p =>
{
    if (p is MouseMovedArgs a)
    {
        var x = a.X;
        var y = a.Y;
    }
});

// 或用模式匹配
Capability.Events.On(PetEvents.PetEdge, p =>
{
    if (p is PetEdgeArgs { Side: 0, Attached: true }) { /* 贴左边 */ }
});
```

> 旧反射代码仍然兼容（payload 字段名稳定）。`dynamic` **不能用**（v0.1.1 已确认：会抛 `RuntimeBinderException`，跨程序集访问 public record 成员的动态绑定仍受可访问性限制）。

---

## 8. 完整可编译示例

一个「整点报时」插件，覆盖事件订阅 + 定时器 + 存储 + 径向菜单 + 通知。

### 8.1 `MyPlugin.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>MyPlugin</AssemblyName>
    <RootNamespace>MyPlugin</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <!-- 与解决方案一起开发时用 ProjectReference；独立开发改用下面的 Reference + HintPath -->
    <ProjectReference Include="..\..\src\DesktopPet.Sdk\DesktopPet.Sdk.csproj" />
    <!--
    <Reference Include="DesktopPet.Sdk">
      <HintPath>..\..\src\DesktopPet.Sdk\bin\Release\net8.0-windows\DesktopPet.Sdk.dll</HintPath>
    </Reference>
    -->
  </ItemGroup>
</Project>
```

> `TargetFramework` 必须是 `net8.0-windows` —— `DesktopPet.Sdk` 是 `net8.0-windows`（设置页需要 WPF 类型）。

### 8.2 `plugin.toml`

```toml
[plugin]
id          = "com.yourname.hourly"
name        = "整点报时"
version     = "0.1.0"
api         = 1
entry       = "MyPlugin.dll"
author      = "你"
description = "每个整点让猫提醒你一次"

[permissions]
pet     = ["animate", "read"]
ui      = ["register"]
storage = ["local"]
other   = ["scheduler", "notify"]

[ui]
radial   = true
settings = false

[resources]
maxMemoryMB = 16
```

### 8.3 `HourlyPlugin.cs`

```csharp
using DesktopPet.Core;
using DesktopPet.Sdk;

namespace MyPlugin;

public sealed class HourlyPlugin : PluginBase
{
    private const string LastHourKey = "lastHour";

    private IScheduleHandle? _timer;
    private Action? _unsubPetted;

    public override string Id => "com.yourname.hourly";   // 必须与文件夹名、manifest id 一致
    public override string Name => "整点报时";

    public override PluginPermissions RequiredPermissions =>
        PluginPermissions.PetAnimate | PluginPermissions.PetRead
        | PluginPermissions.UIRegister | PluginPermissions.Storage
        | PluginPermissions.Scheduler | PluginPermissions.Notify;

    public override void OnLoad(IPluginContext ctx)
    {
        base.OnLoad(ctx);   // 必须：否则 Pet / UI / Capability 为 null

        // 每分钟检查一次是否跨过整点
        _timer = Capability.Scheduler.Every(60_000, CheckHour);

        // 抚摸时喵一声
        _unsubPetted = Capability.Events.On(PetEvents.PetPetted, _ => Pet.Say("喵~", 1500));

        // 径向菜单：手动触发一次报时
        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "hourly-now",
            Label = "现在几点",
            Icon = "info",
            Order = 20,
            OnInvoke = () => Announce(),
        });
    }

    private void CheckHour()
    {
        var now = DateTime.Now;
        var last = Capability.Storage.Get<int>(LastHourKey);
        if (now.Hour == last) return;

        Capability.Storage.Set(LastHourKey, now.Hour);
        Announce();
    }

    private void Announce()
    {
        var text = $"现在是 {DateTime.Now:HH:mm} 啦";
        Pet.Say(text, 3000);
        Capability.System.Notify("Neko", text);
    }

    public override void OnUnload()
    {
        _timer?.Dispose();       // 停止定时器
        _unsubPetted?.Invoke();  // 取消事件订阅
    }
}
```

---

## 9. 构建与部署

```bash
# 1. 构建
cd <你的插件项目目录>
dotnet build -c Release

# 2. 只拷「插件自己的 dll + plugin.toml」到 plugins/<id>/
#    不要把整个 bin/ 拷进去（会带上 Core/Sdk 的 dll）
copy bin\Release\net8.0-windows\MyPlugin.dll  G:\lantetn-li\neko\plugins\com.yourname.hourly\
copy plugin.toml                              G:\lantetn-li\neko\plugins\com.yourname.hourly\

# 3. 重启 Neko（或在设置窗口重新加载插件）
```

内置插件（源码在 `builtins/`）用 MSBuild target 自动部署，可参考
`builtins/DesktopPet.Builtin.Affection/DesktopPet.Builtin.Affection.csproj`：

```xml
<Target Name="DeployToPlugins" AfterTargets="Build">
  <PropertyGroup>
    <_PluginDir>$(ProjectDir)..\..\plugins\com.lantern.affection</_PluginDir>
  </PropertyGroup>
  <MakeDir Directories="$(_PluginDir)" />
  <Copy SourceFiles="$(TargetPath)" DestinationFolder="$(_PluginDir)" SkipUnchangedFiles="true" />
  <Copy SourceFiles="$(ProjectDir)plugin.toml" DestinationFolder="$(_PluginDir)" SkipUnchangedFiles="true" />
</Target>
```

---

## 10. 调试与排错

**看不到插件？按这个顺序查：**

1. **文件夹名 == `plugin.toml` 的 `id`？** 不一致会加载失败
2. **`entry` 的文件名和实际 dll 一致？** 区分大小写无所谓，但文件名必须完全对上
3. **`TargetFramework` 是 `net8.0-windows`？** 写成 `net8.0` 会引用不上 Sdk
4. **`OnLoad` 里调了 `base.OnLoad(ctx)`？** 没调的话 `Pet`/`UI`/`Capability` 全是 null
5. **权限声明全了吗？** 只调 `Pet.Say` 却没声明 `pet.animate`——加载时权限不匹配可能导致功能不可用；**高风险权限（`file`/`process`）会直接拒载**（第三方插件没有豁免，见 4.4）
6. **是不是把周期逻辑写在 `OnTick` 里了？** 宿主不调用 `OnTick`（见 5.1）——改用 `Capability.Scheduler.Every`
7. 打开设置窗口看「加载失败诊断」，或看 `data/logs/neko-*.log`（v0.1.61 起统一日志设施；warn 及以上落盘）

**调试输出**：宿主侧统一走 `DesktopPet.App.Core.Log`；**插件侧用 `Capability.System.Log(PluginLogLevel.<级>, msg, ex?)`（v0.1.83）**，日志落 `data/logs/neko-*.log` 并带 `[插件:<id>]` 前缀。
`Debug.WriteLine` **只适合 VS 附加调试**（看「输出」窗口）——**Release 无监听器时等于静默**，不要用它当插件日志。

**无 UI 验证**：不想启动桌面时，可用 `tests/LoaderTest` 验证加载链路（它用最小宿主实现跑通加载 → OnLoad → 事件 → 卸载）。

---

## 11. 约束与红线

1. **只碰 Core API**，不要反射宿主内部类型、不要引用 `DesktopPet.App`
2. **数据只写 `Capability.Storage`**（即 `data/plugins/<id>/`），不写插件目录、不写宿主目录
3. **`OnUnload` 必须清理**：取消事件订阅、释放定时器，否则热重载后会重复注册
4. **别指望 `OnTick`**：宿主当前**不调用** `OnTick(delta)`（见 5.1）；周期逻辑用 `Capability.Scheduler.Every`。**也不要在任何回调里做耗时阻塞**（事件/调度器回调都会影响宿主流畅度）
5. **不要在插件目录放 `DesktopPet.Core.dll` / `DesktopPet.Sdk.dll`**
6. 单个插件崩溃不应拖垮宿主 —— 宿主有 watchdog + 熔断，但插件自身也应做好异常捕获

---

## 12. 已知坑（前人踩过）

**坑 1 · 类型分裂**：插件 ALC 里如果加载了 `DesktopPet.Core` 的**副本**，`IPetPlugin` 会变成两个不同类型，宿主识别不出插件。
→ 宿主已用白名单把 Core/Sdk 路由回自己。**插件侧对策**：不要往插件目录塞 Core/Sdk 的 dll。

**坑 2 · 权限前缀不匹配**：manifest 写 `pet = ["animate"]`，查表却是 `pet.animate`。
→ 宿主已自动加前缀。**插件侧对策**：记住 `other` 段**不加前缀**（见 4.1）。

**坑 3 · 宿主没引用 Sdk 导致发布形态加载失败**（宿主开发侧的坑，记录在此供参考）：
宿主进程必须真正加载 `DesktopPet.Sdk.dll`，否则插件 ALC 的白名单路由找不到 Sdk。
→ 已修复（App 显式引用 Sdk）。**插件侧无影响**，但如果你自己搭宿主，注意这点。

---

## 13. API 版本历史（本手册变更记录）

**Core API 版本 = 1**（自 v0.1.0 起未变）。下表记录**宿主版本演进中影响插件作者的变化**，以及**本手册的核对记录**：

| 日期 | 宿主版本 | API | 变更 |
|---|---|---|---|
| 2026-09-01 | v0.1.0 | 1 | 首个可用版本。IPetApi / IUIApi / ICapabilityApi 三大接口；权限；事件常量；payload 为匿名类型 |
| — | v0.1.1 | 1 | 事件 payload 改**强类型 record**（`MouseMovedArgs`/`PetDraggedArgs`/`PetEdgeArgs`），可跨程序集直接 cast；广播 `app:ready` |
| — | v0.1.5+ | 1 | `ISettingsPage` 接入宿主设置窗口（`Title == Manifest.Id 或 Name`） |
| — | v0.1.48 | 1 | `PetState` 新增 **`Work`**（工作模式）→ 共 9 个状态 |
| — | v0.1.61 | 1 | `IScheduler.Cron` 移除（半成品）；`AppConfig.LogLevel` 日志设施 |
| — | v0.1.80 | 1 | **权限细分**：新增 `file.own`(`FileOwnDir`) / `media`(`MediaKey`)，均为低危；新增**内置信任通道**（`builtins.lock.json`），是 `file`/`process` 的唯一豁免；`PluginPermissions` 底层类型改 **`ulong`** |
| **2026-09-11** | **v0.1.82** | **1** | **本手册逐项对照源码复核**（跨 21 个宿主版本）。修正：补 `file.own`/`media` 权限、补信任通道、补 8 个图标 key、修正 config 键名（`DisabledPlugins`）、**更正 `OnTick` 为「宿主未调用」**、明确其替代方案为 `Scheduler.Every` |
| **2026-09-19** | **v0.1.83** | **1** | **新增插件日志通道**：`PluginLogLevel` 枚举 + `ISystem.Log(level, msg, ex?)`（🔴 只增不改）；宿主加 `[插件:<id>]` 前缀、`Debug`/`Info` 默认不落盘、永不抛。本手册同步补 §5.5 `ISystem` + 页首版本号 |

**已兑现（历史「待改进」项，现已完成）**：
- ✅ 事件 payload 改强类型 record（v0.1.1）
- ✅ 广播 `app:ready`（v0.1.1）；`app:wake`/`app:sleep`/`screen:change` **仍为预留未广播**
- ✅ 设置页插件自定义承载（v0.1.5+ 已接入）
- ✅ **插件日志 API**（v0.1.83：`ISystem.Log`；历史「四入口含 Log」的规划以「既有 `ISystem` 加成员」落地，未新增 `IPluginContext` 成员）
- ✅ 内置信任通道**接线**（v0.1.82：`PluginHost.LoadBuiltinTrust`）

**已知仍有出入/未接线（供维护者注意）**：
- `IPetPlugin.OnTick`：**声明但宿主未调用**（保留契约，勿依赖）。
- `PetEvents.AppWake/AppSleep/ScreenChange/Tick`：**预留，未广播**。
