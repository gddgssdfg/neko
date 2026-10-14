# Neko 插件开发标准文档（Plugin Development Guide）

> **本文件是「可执行」标准文档。** 照着它做，就能产出**能真正跑起来**的插件；每一条「必须 / 禁止」
> 都能追溯到宿主源码或 `docs/dev-log.md` / `CHANGELOG.md` 里的真实记录。
>
> | 项 | 值 |
> |---|---|
> | **最后核对日期** | **2026-09-19** |
> | **核对到的宿主版本** | **v0.1.83** |
> | Core API 版本 | **1**（稳定契约，公开发布后只增不改） |
> | 目标框架 | **net8.0-windows**（DesktopPet.Sdk 自带 WPF 引用） |
> | 依赖 | 只需引用 `src/DesktopPet.Sdk/DesktopPet.Sdk.csproj` |
> | 宿主程序版本「权威来源」 | `src/DesktopPet.App/DesktopPet.App.csproj` 的 `<Version>`（本文件**不承诺**等于当前版本；一致性由 `tools/check_version.py` 守护） |
>
> 📌 **为什么用「最后核对日期 + 核对到的版本」而不是硬编码一个版本号**：本文件面向的是**稳定的 Core API 版本 1**。宿主小版本迭代里绝大多数**没有动 `DesktopPet.Core` 的签名**，无需逐版改写；只需**每次核对后更新上表这两行**。
>
> 🤖 **本文件同时是「给 AI 的最简喂料」**：把整个文件贴给任意 AI，说「按这份文档写一个 XXX 插件」，即可产出可直接编译的代码（所有 API 签名、权限键、事件名均已对照宿主 `src/` 源码逐项核实）。最省事的入口见 **§0**；维护契约见 **附录 C**。
>
> 不确定的地方本文件一律写「待确认」，**不编**。若发现文档与实现不一致，请在文件末尾的
> 【附录 B：文档-实现一致性核对】里记录。

---

## 目录（TOC）

0. [给 AI 的最简清单](#0-给-ai-的最简清单)
1. [5 分钟上手：最小可用插件](#1-5-分钟上手最小可用插件)
2. [插件契约：IPlugin / PluginBase / IPluginContext](#2-插件契约)
3. [plugin.toml 字段表 + 权限表](#3-plugintoml-字段表--权限表)
4. [生命周期铁律（OnUnload 必须做的事）](#4-生命周期铁律)
5. [⚠️ 线程规则（本文件最重要的一章）](#5-线程规则本文件最重要的一章)
6. [存储规范](#6-存储规范)
7. [UI 注册：径向菜单 / 托盘 / 设置页](#7-ui-注册)
8. [调试与验证手段](#8-调试与验证手段)
9. [🔴 踩坑清单（现象 / 根因 / 正确做法）](#9-踩坑清单)
10. [完整骨架 `templates/plugin-skeleton/`](#10-完整骨架)
11. [附录 A：权限 / 事件速查（含「是否已广播」与 payload 读取）](#附录-a权限--事件速查)
12. [附录 B：文档-实现一致性核对](#附录-b文档-实现一致性核对)
13. [附录 C：维护约定（改宿主 API 的人必读）](#附录-c维护约定)
14. [附录 D：API 版本历史](#附录-dapi-版本历史)

---

## 0. 给 AI 的最简清单

> 若你要**让 AI 生成一个插件**，把本文件整个贴给它，并让它严格按下面这份清单产出。**这一节是「粘给 AI 的最小上下文」**，其余章节是展开细节。

**一个 Neko 插件 = 一个目录，三样东西：**

```
plugins/com.yourname.myplugin/          # 目录名必须 == plugin.toml 的 [plugin].id
├── plugin.toml      # 清单：id、入口 dll、权限声明
├── MyPlugin.dll     # 编译产物（文件名要和 [plugin].entry 对上）
└── assets/          # 可选：插件自带资源
```

> **📁 两个目录别混（v0.1.98 起）** —— 本文件用两个占位词：
> - **`<程序根>`** = `Neko.exe` 所在目录（**只放随包分发的只读内容**：`plugins/`、`neko-assets/`、`builtins.lock.json`）。
> - **`<数据根>`** = **唯一的可写用户数据目录**。解析顺序：① 环境变量 `NEKO_DATA_DIR` →
>   ② 程序根存在 `portable.flag` 时用 `<程序根>\data`（便携模式）→ ③ **默认 `%AppData%\Neko`**。
>
> 所以默认安装下 `<数据根>\logs` 就是 `%AppData%\Neko\logs`（**不再是 exe 旁边的 `data\logs`**）。
> 插件**永远不要自己拼数据路径** —— 用 `ctx.PluginDataDir`（= `<数据根>/plugins/<id>/`）。
> 为什么改：v0.1.97 及以前数据写在 exe 同级 `data/`，而开发/CI 会反复删 `bin/`，于是
> 「删 bin 前必须先备份 data/」成了一条**只能靠人记住**的红线 —— 现在从结构上消除了。

**AI 生成时必须遵守的 8 条（其余细节见对应章节）：**

1. **入口类继承 `DesktopPet.Sdk.PluginBase`**，`Id` 与目录名、`plugin.toml` 的 `id` **三者一致**（§1.1 / §2.2）。
2. **`OnLoad(IPluginContext ctx)` 第一行必须 `base.OnLoad(ctx)`**，否则 `Pet`/`UI`/`Capability` 为 null（§2.2）。
3. **不要用 `OnTick`**——宿主**不调用**它；周期任务一律 `Capability.Scheduler.Every/At`（§2.1 / §5 / §9 踩坑 6）。
4. **只声明你真正用到的最小权限**；`other` 段**不加前缀**，`pet`/`ui`/`storage` 段**自动加前缀**（§3.2 / §3.3）。
5. **不要声明 `file` / `process`**（高危 → 第三方一律拒载）；写自己的数据目录用 `file.own` 或直接用 `Capability.Storage`（§3.6 / §9 踩坑 6）。
6. **数据只写 `Capability.Storage`**（= `<数据根>/plugins/<id>/`），**禁止写进插件目录**（§6.1）。
7. **不做裸 `catch {}`**；要留痕用 `Capability.System.Log(PluginLogLevel.Warn, msg, ex)`（§2.3 / §9 踩坑 3）。
8. **`OnUnload` 必须清理**：`Dispose` 所有 `IScheduleHandle`、调用事件取消订阅委托（§4）。

**不要做的事**：不要把 `DesktopPet.Core.dll` / `DesktopPet.Sdk.dll` 拷进插件目录（宿主对它们做「复用宿主实例」白名单路由，带副本无效）；不要用 `dynamic` 读事件 payload；不要用 `JsonSerializer` 反射序列化**插件内定义的类型**（会钉住可回收 ALC，§5.4）。

**可编译的完整示例**见 §1.3（三件套）；更完整的「事件订阅 + 定时器 + 存储 + 径向菜单 + 通知」示例见仓库内置插件 `builtins/DesktopPet.Builtin.HourlyChime`，可直接复制改造。

---

## 1. 5 分钟上手：最小可用插件

### 1.1 一个插件 = 一个目录

宿主启动时扫描程序根目录下的 `plugins/`，**每个一级子目录**尝试读 `plugin.toml`：

```
<Neko 程序根>/
└── plugins/
    └── com.yourname.hello/      ← 目录名必须 == plugin.toml 的 [plugin].id
        ├── plugin.toml          ← 清单（必需）
        └── Hello.dll            ← 入口程序集（文件名由 [plugin].entry 指定）
```

**硬规则：**

| 规则 | 依据 |
|---|---|
| 目录名必须与 `plugin.toml` 的 `[plugin].id` **完全一致**（忽略大小写比较） | `PluginLoader.TryLoadOne`：`manifest.Id != name → "manifest id 与目录名不一致"` 并拒载 |
| 子目录**没有 `plugin.toml`** → 跳过（不计错误） | `PluginLoader.TryLoadOne` `if (!File.Exists(manifestFile)) { error = "缺少 plugin.toml"; }` |
| `_` 开头的目录 → 直接跳过 | `TryLoadOne`：`if (name.StartsWith('_')) return false;` |
| 入口 dll 必须存在 | `TryLoadOne`：`if (!File.Exists(entryDll)) { error = "入口 dll 不存在"; }` |

> 本仓库自带的 `plugins/_template/` 就是靠「`_` 前缀跳过」规则安全地躺在插件目录里。

### 1.2 构建后产物如何落到 `plugins/<id>/`

宿主在 `src/DesktopPet.App/DesktopPet.App.csproj` 里挂了两个 target，把仓库根的 `plugins/**` **整目录复制**到输出目录：

```xml
<Target Name="DeployPluginsToOutput"  AfterTargets="Build">
  <Copy SourceFiles="@(_PluginFiles)"
        DestinationFiles="@(_PluginFiles->'$(OutDir)plugins\%(RecursiveDir)%(Filename)%(Extension)')" />
</Target>
<Target Name="DeployPluginsToPublish" AfterTargets="Publish"> ... $(PublishDir)plugins\ ... </Target>
```

因此**两条落地路径**：

- **开发（Debug）**：你的插件工程自己把产物拷到 `plugins/<id>/`（内置插件的 `.csproj` 都有一个
  `DeployToPlugins` target，见 §10）。然后 `DesktopPet.App` 构建时会把它带到 `bin/.../plugins/`。
- **发布（Publish）**：`dotnet publish` 触发 `DeployPluginsToPublish`，同样把 `plugins/**` 带上。

> ⚠️ 第三方插件**不必**改宿主 `.csproj`。你只要保证编译产物最终出现在
> `plugins/<id>/<Entry>.dll` + `plugins/<id>/plugin.toml` 即可（手工拷贝也行）。

### 1.3 最小插件三件套

**`plugin.toml`**

```toml
[plugin]
id = "com.yourname.hello"
name = "你好插件"
version = "0.1.0"
api = 1
entry = "Hello.dll"
author = "你的名字"
description = "点一下径向菜单，猫跟你打招呼"

[permissions]
pet = ["animate"]      # → pet.animate
ui  = ["register"]     # → ui.register

[ui]
radial = true
settings = false

[resources]
maxMemoryMB = 16
```

**`HelloPlugin.cs`**

```csharp
using DesktopPet.Core;
using DesktopPet.Sdk;

namespace HelloPlugin;

public sealed class HelloPlugin : PluginBase
{
    public override string Id => "com.yourname.hello";   // 必须与目录名一致
    public override string Name => "你好插件";

    public override PluginPermissions RequiredPermissions =>
        PluginPermissions.PetAnimate | PluginPermissions.UIRegister;

    public override void OnLoad(IPluginContext ctx)
    {
        base.OnLoad(ctx);   // ⚠️ 必须第一行调用，否则 Pet / UI / Capability 为 null（见 §9）

        UI.RegisterRadialSlot(new RadialSlot
        {
            Id      = "hello",
            Label   = "打个招呼",
            Icon    = "wave",     // 见 §7.1 可用图标 key
            Order   = 10,         // 越小越靠前
            OnInvoke = () => Pet.Say("喵！"),
        });
    }
}
```

**`Hello.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <AssemblyName>Hello</AssemblyName>          <!-- 必须 == plugin.toml 的 entry（不含 .dll） -->
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\..\src\DesktopPet.Sdk\DesktopPet.Sdk.csproj" />
  </ItemGroup>
  <ItemGroup>
    <None Update="plugin.toml" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

**构建 + 部署（把两行改成本机实际路径）：**

```bash
# 构建（本仓库用 helper 脚本；直接 dotnet 会因环境变量缺失而崩）
bash /c/Users/Lantern/neko-dotnet-env.sh build /path/to/Hello.csproj -c Debug

# 产物落在 Hello/bin/Debug/net8.0-windows/Hello.dll
# 手工拷贝（第三方插件的最小落地方式——只拷「自己的 dll + plugin.toml」）：
mkdir -p "<Neko根>/plugins/com.yourname.hello"
cp Hello.dll     "<Neko根>/plugins/com.yourname.hello/"
cp plugin.toml   "<Neko根>/plugins/com.yourname.hello/"
```

> 🔴 **不要**把整个 `bin/` 拷进去：那会带上 `DesktopPet.Core.dll` / `DesktopPet.Sdk.dll` 的副本。
> 宿主在 ALC 里对 `DesktopPet.Core` / `DesktopPet.Sdk` 做了「复用宿主实例」处理
> （`PluginLoadContext.HostSharedAssemblies`），你带一份自己的副本**不会生效**，只会让目录变乱。

### 1.4 验证它跑起来了

1. 重启 Neko（或到设置页点「重新加载插件」，若该入口存在）；
2. 在猫身上右键 → 径向菜单里应出现「打个招呼」；
3. 点它 → 猫头顶弹出「喵！」气泡。

若没出现，先看 §8 的日志定位法，**不要**盯着界面猜。

---

## 2. 插件契约

> 全部签名逐字段摘自源码：`src/DesktopPet.Core/IPetPlugin.cs`、`src/DesktopPet.Sdk/PluginBase.cs`。

### 2.1 `IPetPlugin`（Core，所有插件必须实现）

```csharp
public interface IPetPlugin : IDisposable
{
    string Id { get; }                              // 唯一标识，反向域名，必须 == 目录名
    string Name { get; }                            // 显示名
    Version ApiVersion { get; }                     // 依赖的 Core API 版本
    PluginPermissions RequiredPermissions { get; }  // 声明所需权限
    void OnLoad(IPluginContext ctx);                // 装载时调用
    void OnTick(TimeSpan delta);                    // ⚠️ 宿主当前【未调用】——别依赖它（见 §2.2 注）
    void OnUnload();                                // 卸载时调用
}
```

> 宿主**不读取**插件实例上的 `RequiredPermissions` 来做校验——权限是从 `plugin.toml` 解析的
> （见 §3）。`RequiredPermissions` 目前主要用于**代码内自描述**与未来按声明授权。两者**必须保持一致**（见 §9 踩坑 5）。

### 2.2 `PluginBase`（Sdk，推荐继承）

`src/DesktopPet.Sdk/PluginBase.cs` —— 第三方插件与官方内置插件用**完全相同**的基类（dogfooding）：

```csharp
public abstract class PluginBase : IPetPlugin
{
    protected IPluginContext Context { get; private set; } = null!;  // OnLoad 后可用
    protected IPetApi          Pet        => Context.Pet;            // 宠物控制快捷方式
    protected IUIApi           UI         => Context.UI;             // 界面注册快捷方式
    protected ICapabilityApi   Capability => Context.Capability;     // 能力快捷方式

    public abstract string Id { get; }
    public abstract string Name { get; }
    public virtual Version ApiVersion => new(1, 0);
    public virtual PluginPermissions RequiredPermissions => PluginPermissions.None;

    public virtual void OnLoad(IPluginContext ctx) => Context = ctx;
    public virtual void OnTick(TimeSpan delta) { }
    public virtual void OnUnload() { }
    public virtual void Dispose() { }
}
```

> ⚠️ 覆盖 `OnLoad` 时**第一行必须 `base.OnLoad(ctx)`**——`Pet`/`UI`/`Capability` 三个快捷属性靠它赋值。
> 不调用 → 三者在后续访问时抛 `NullReferenceException`。

> 🔴 **`OnTick` 是「声明了但宿主未接线」的 API（v0.1.82 核对）**：契约（`IPetPlugin`）与 `PluginBase` 都保留了它，
> 但宿主 `PluginHost` **没有任何调用点**——`OnLoad` 之后只会被调 `OnUnload` / `Dispose`，**不存在 ~10Hz 的 tick 循环**。
> **故不要用 `OnTick` 做周期逻辑**（写了不会跑）。周期任务一律用 **`Capability.Scheduler.Every/At`**。

### 2.3 `IPluginContext` —— 能力入口与全部方法签名

`src/DesktopPet.Core/IPetPlugin.cs` 定义上下文；`IPetApi.cs`、`IUIApi.cs`、`ICapabilityApi.cs`
定义**三个能力入口**（`Pet` / `UI` / `Capability`）。历史文档称之为「四个入口」（第 4 个是 `Log`），
`Log` 的实际落点见本节下方说明（落到 `Capability.System.Log`）。

```csharp
public interface IPluginContext
{
    Version HostVersion { get; }    // 宿主程序版本
    string  DataRoot { get; }       // 宿主数据根目录（默认 %AppData%\Neko，见文首「两个目录别混」）
    string  PluginDataDir { get; }  // 本插件数据目录（<数据根>/plugins/<id>/）
    IPetApi         Pet { get; }        // 入口 1：宠物控制
    IUIApi          UI { get; }         // 入口 2：界面注册
    ICapabilityApi  Capability { get; } // 入口 3：能力（存储/调度/系统/事件）
    // 入口 4 = 日志？—— 未做成 IPluginContext 成员，v0.1.83 起落在 Capability.System.Log（见下）
}
```

> 📌 **关于「第 4 入口 `Log`」**：立项规划里的第 4 个入口是 `Log`，但它**始终没有**做成 `IPluginContext` 成员。
> **v0.1.83 起，插件日志能力已补齐** —— 通过**既有的** `Capability.System.Log(...)` 暴露（遵守「Core API 只增不改」，未动 `IPluginContext`）。
> 所以：**`IPluginContext` 只有 `Pet` / `UI` / `Capability` 三个能力入口**（+ `HostVersion` / `DataRoot` / `PluginDataDir` 三个属性）这一结构与 v0.1.82 前一致、**未变**；
> 变的是：插件**现在有了宿主日志通道**（别再以为"插件没有 `Log` 这样的公开 API"了——见下方入口 3 的 `ISystem.Log` 与 §8.1）。
> （历史：v0.1.82 及以前确实无插件日志 API，只能用 `Debug.WriteLine` —— **Release 无监听器时等于静默**，这正是本次补 `Log` 的动因，见 §9 踩坑 3。）

#### 入口 1：`IPetApi`（宠物控制）

> **全部方法已在宿主侧封送到 UI 线程**（见 §5.2），插件可直接调用，无需自己封送。

```csharp
public interface IPetApi
{
    Task PlayAnimationAsync(PetState state, AnimationOptions? opts = null);
    void SetMood(PetMood mood);
    void Say(string text, int durationMs = 3000);
    Point2 GetPosition();                              // 左上角像素坐标
    Task MoveToAsync(Point2 target, int durationMs = 500);
    Rect2 GetWorkArea();                               // 可用桌面区域（扣除任务栏）
}

public sealed record AnimationOptions
{
    public bool   Loop               { get; init; } = true;
    public bool   Interrupt          { get; init; } = true;
    public double FrameRateOverride  { get; init; } = -1;
}
```

> ⚠️ 注意：`PlayAnimationAsync` / `MoveToAsync` **不是** `void`——它们是 `Task`，但宿主实现里
> `return Task.CompletedTask;`（实际效果是「发后不理」的封送）。可以 `await`，也可以不 await。

#### 入口 2：`IUIApi`（界面注册）

```csharp
public interface IUIApi
{
    void RegisterRadialSlot(RadialSlot slot);
    void RegisterTrayMenuItem(TrayMenuItem item);
    void RegisterSettingsPage(ISettingsPage page);
    void UnregisterAll(string scopeId);   // 卸载时宿主会按插件 id 调用
}
```

> ⚠️ `Register*` **必须在 `OnLoad` 内调用**。宿主用「作用域」把注册归到当前插件：
> `UIApiImpl.RequireScope()` 在 scope 为 null 时抛
> `InvalidOperationException("UI 注册必须在插件 OnLoad 内通过 ScopedUiApi 调用")`。

#### 入口 3：`ICapabilityApi`（存储 / 调度 / 系统 / 事件）

```csharp
public interface ICapabilityApi
{
    IPluginStorage Storage   { get; }
    IScheduler     Scheduler { get; }
    ISystem        System    { get; }
    IEventBus      Events    { get; }
}

public interface IPluginStorage
{
    T? Get<T>(string key);
    void Set<T>(string key, T value);
    void Remove(string key);
    bool Contains(string key);
}

public interface IScheduler
{
    IScheduleHandle Every(int intervalMs, Action callback);
    IScheduleHandle At(DateTime time, Action callback);
    // ⚠️ v0.1.61 起**没有** Cron（「移除未实现的 Cron——诚实优于半成品」）
}

public interface IScheduleHandle : IDisposable { bool IsActive { get; } }

public interface ISystem
{
    void Notify(string title, string body);            // 真实 Windows 通知
    string? ReadClipboard();
    void WriteClipboard(string text);
    void OpenUrl(string url);
    string? PickFile(string filter = "");

    // v0.1.83 新增（🔴 只增不改，上面 5 个成员一字未动）：写一条插件日志
    void Log(PluginLogLevel level, string message, Exception? exception = null);
}

public interface IEventBus
{
    Action On(string evt, Action<object?> handler);   // 返回「取消订阅」委托
    void Emit(string evt, object? payload = null);
}
```

##### 入口 3 补充：`ISystem.Log` —— 插件日志（v0.1.83 新增）

`src/DesktopPet.Core/PluginLogLevel.cs`（新文件）+ `ISystem.Log`：

```csharp
public enum PluginLogLevel { Debug, Info, Warn, Error }

// ISystem 上的新成员（签名精确）：
void Log(PluginLogLevel level, string message, Exception? exception = null);
```

**语义（务必按此理解）：**

| 要点 | 说明 |
|---|---|
| **宿主自动加前缀** | 宿主会拼上 **`[插件:<id>]`**（如 `[插件:com.lantern.weather]`）便于定位是哪个插件在报。**你**不要自己拼这个前缀。 |
| **级别映射** | 与宿主日志一一对应：`Debug→Debug`、`Info→Info`、`Warn→Warn`、`Error→Error`。 |
| **默认不落盘** | 宿主默认级别 `warn` → **`Debug` / `Info` 默认不落盘**（只有用户把 `<数据根>/config.json` 的 `logLevel` 调成 `debug`/`info` 才会写出）。`Warn` / `Error` 默认落盘。 |
| **异常堆栈** | 只有 `Error` 级别会带上 `exception` 的堆栈；其它级别忽略该参数。 |
| **永不抛** | 实现**保证不抛异常**（宿主日志设施内部 try/catch 兜底）——可以放心在 `catch` 里调用它。 |
| **隐私** | 日志**会落盘**，**不要把用户隐私内容**（剪贴板正文、待办标题、API Key…）写进 `message`。 |

**用法（常见于 `catch` 里留痕，替代原先的静默 `catch{}`）：**

```csharp
try
{
    DoSomethingRisky();
}
catch (Exception ex)
{
    // ✅ 走宿主日志：带 [插件:<你的id>] 前缀，Warn/Error 默认落盘
    Capability.System.Log(PluginLogLevel.Warn, "抓取失败，已跳过本轮", ex);
}

// ✅ Info/Debug 适合高频埋点（默认不落盘，调试时才开 logLevel）
Capability.System.Log(PluginLogLevel.Debug, $"tick 完成，缓存 {n} 条");
```

> 🔴 **不要再用 `Debug.WriteLine` 记插件日志**：Release 构建下**没有调试器监听**时它等同于**静默丢弃**——这正是本次把日志通道补进 `Capability.System` 的直接原因（与「`catch{}` 全吞」是同一类"静默失败"病根，见 §9 踩坑 3）。
> `Debug.WriteLine` 仅适合**本地调试**（VS 附加时看「输出」窗口）；**任何"要让用户/你事后能查"的日志，都走 `Capability.System.Log`。**

---

## 3. plugin.toml 字段表 + 权限表

解析逻辑：`src/DesktopPet.Runtime/ManifestParser.cs`；权限映射：`PermissionChecker.cs`。

### 3.1 `[plugin]` 段

| 字段 | 类型 | 默认值 | 必填 | 说明 / 校验 |
|---|---|---|---|---|
| `id` | string | — | ✅ | 反向域名。**必须 == 目录名**，否则拒载。缺失 → `FormatException` |
| `name` | string | `""` | 建议 | 显示名 |
| `version` | string | `"0.1.0"` | 否 | 语义化版本 |
| `api` | int | `1` | 否 | 依赖的 Core API 版本（当前 = 1） |
| `entry` | string | — | ✅ | 入口 dll 文件名（相对插件目录）。缺失 → `FormatException` |
| `author` | string | `""` | 否 | |
| `description` | string | `""` | 否 | |
| `homepage` | string? | null | 否 | 可选 |

### 3.2 `[permissions]` 段 —— 四组，前缀自动拼接

`PermissionChecker.FromManifest` 的规则（**关键**）：

| toml 段 | 自动加的前缀 | 例子 |
|---|---|---|
| `pet = [...]` | `pet.` | `["animate"]` → `pet.animate` |
| `ui = [...]` | `ui.` | `["register"]` → `ui.register` |
| `storage = [...]` | `storage.` | `["local"]` → `storage.local` |
| `other = [...]` | 无 | `["scheduler","file.own"]` → `scheduler` / `file.own` |

> 🔴 **最常见错误**：把 `scheduler` / `notify` / `clipboard` / `network` / `media` / `file.own`
> 写进了 `pet` 或 `ui` 段。它们**只认 `other` 段**（或 `other` 不加前缀的完整键）。
> 例如 `notify` 写成 `pet = ["notify"]` 会拼成 `pet.notify` → 命中不了任何映射 → 被静默忽略。

### 3.3 权限 token 全表 → 位 → Capability

| token | `PluginPermissions` 位 | 值 | 对应 API | 风险 |
|---|---|---|---|---|
| `pet.animate` | `PetAnimate` | `1<<0` | `IPetApi.PlayAnimationAsync` | 低 |
| `pet.move` | `PetMove` | `1<<1` | `IPetApi.MoveToAsync` | 低 |
| `pet.read` | `PetRead` | `1<<2` | `IPetApi.GetPosition` / `GetWorkArea` | 低 |
| `ui.register` | `UIRegister` | `1<<3` | `IUIApi.Register*` | 低 |
| `storage.local` | `Storage` | `1<<4` | `ICapabilityApi.Storage` | 低 |
| `scheduler` | `Scheduler` | `1<<5` | `ICapabilityApi.Scheduler` | 低 |
| `notify` | `Notify` | `1<<6` | `ICapabilityApi.System.Notify` | 低 |
| `clipboard` | `Clipboard` | `1<<7` | `System.ReadClipboard/WriteClipboard` | 中 |
| `network` | `Network` | `1<<8` | （宿主侧域名白名单） | 中 |
| `file` | `FileAccess` | `1<<9` | 任意文件系统 | **高** |
| `process` | `Process` | `1<<10` | 启动任意进程 | **高** |
| `file.own` | `FileOwnDir` | `1<<11` | 仅写 `<数据根>/plugins/<id>/` | 低（v0.1.80 细分） |
| `media` | `MediaKey` | `1<<12` | 发系统媒体键 | 低（v0.1.80 细分） |

写入位置：

```toml
[permissions]
pet     = ["read", "animate", "move"]   # → pet.read / pet.animate / pet.move
ui      = ["register"]                    # → ui.register
storage = ["local"]                       # → storage.local
other   = ["scheduler", "notify", "clipboard", "network"]
#          ↑ 这些必须在 other；不加前缀
```

> 未在映射表里的未知 token 会被**静默忽略**（`ToFlags`：`if (Map.TryGetValue(key, out var flag))`）。

### 3.4 `[ui]` 段

| 字段 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `radial` | bool | `false` | 是否占用径向菜单槽位 |
| `settings` | bool | `false` | 是否在设置窗口提供配置页 |

> ⚠️ **`ui.settings` 当前不被宿主强制校验**：设置页的展示只判断
> `page.Title == 插件的 Id 或 Name`（`SettingsWindow.cs`），不看 `ui.settings`。
> 但**声明失真**仍属契约错误（CHANGELOG v0.1.82 P1-1 明确记录：6 个插件「注册了设置页却写 `ui.settings=false`」，
> 已统一补齐）——请如实声明 `settings = true`。

### 3.5 `[resources]` 段

| 字段 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `maxMemoryMB` | int | `16` | 插件内存上限声明（MB）。**当前未强制**；仅作声明 |

### 3.6 🔴 高风险权限 + 信任清单机制（源码为准）

**判定**：`PermissionChecker.IsHighRisk(p) => (p & (FileAccess | Process)) != 0`
—— 只有 `file` / `process` 两个 token 是高危。`file.own` / `media` **不是**高危。

**加载拦截**（`PluginLoader.TryLoadOne`）：

```csharp
if (PermissionChecker.IsHighRisk(declared) &&
    !_trust.IsTrusted(manifest.Id, manifestFile, entryDll, out var why))
{
    error = $"插件声明了高风险权限且未受信任，拒绝自动加载: {declared}（{why}）";
    return false;   // 直接拒载
}
```

**信任清单**（`src/DesktopPet.Runtime/PluginTrust.cs`，`PluginTrustSet`）：

- 文件名：`builtins.lock.json`，放在 **exe 同级**（`App` 根），**不在 `plugins/` 内**（防止插件自写篡改）。
- schema：

  ```json
  {
    "schema": 1,
    "builtins": [
      { "id": "com.lantern.music" },
      { "id": "com.lantern.screenshot",
        "manifestSha256": "…（可选，绑定 manifest 内容）",
        "entrySha256":    "…（可选，绑定入口 dll 内容）" }
    ]
  }
  ```

- 判定：id 必须在清单内；若为某 id 登记了摘要，则现场文件 SHA-256 必须匹配。清单缺失 / 坏 JSON /
  `schema != 1` → 返回 `Empty`（**一律不信任**）+ warning，**绝不抛异常**。

> 🔴 **生产环境现状（务必知道）**：宿主侧 `PluginHost` 目前这样构造加载器：
> `_loader = new PluginLoader(pluginsRoot);` —— **没有传入信任集**，即信任集恒为 `Empty`。
> **后果**：在生产（Neko.exe）里，**任何**声明 `file` / `process` 的插件都会被拒载，
> **包括本应是官方内置的插件**——信任通道在 App 侧**尚未接线**，只在测试
> （`tests/LoaderTest`、`tests/PluginSmoke`）里被构造使用。
>
> 这也是为什么 v0.1.80 把官方内置的 `music`/`screenshot` 从粗 token 细分为低危
> `media` / `file.own`（见 §9 踩坑 6）——**它们是绕开高危拦截，而不是靠信任通道放行**。
>
> 对你的插件意味着：**不要声明 `file` / `process`**。你多半只需要 `file.own`（写自己的数据目录）。

---

## 4. 生命周期铁律

宿主对每个插件的完整生命周期（`PluginHost` / `LoadedPlugin`）：

```
加载(ALC 隔离) → 构造实例 → OnLoad(ctx) → [运行中：由你注册的事件订阅 / Scheduler 定时器 / UI 回调驱动]
              → 禁用/退出时：UnregisterAll(id) → Instance.OnUnload() → Instance.Dispose() → ALC.Unload()
```

> ⚠️ **没有 `OnTick` 主循环**：`IPetPlugin.OnTick` 存在但宿主从未调用（见 §2.1）。运行期的所有权都靠
> `Capability.Events.On` / `Capability.Scheduler.Every/At` / `UI.RegisterRadialSlot(...).OnInvoke` 这三类回调。

`LoadedPlugin.Dispose()` 的真实顺序：

```csharp
try { Instance.OnUnload(); } catch { }   // 你的清理逻辑
try { Instance.Dispose(); } catch { }    // IDisposable
Context.Unload();                        // 卸载可回收 ALC
```

> ⚠️ 两个回调的异常都被 **catch 吞掉**（`catch { /* 卸载回调失败不影响卸载 */ }`）。
> 即：`OnUnload` 里抛异常**不会让宿主崩**，但会**静默中断**你的后续清理——所以清理要逐项 try/catch。

### OnUnload 里**必须**做的事

| 必须做 | 不做的后果 | 正确写法 |
|---|---|---|
| **Dispose 所有 `IScheduleHandle`** | `System.Threading.Timer` 仍在线程池触发；回调跑在已卸载插件上 → 或持续占用 CPU，或抛异常刷 `<数据根>/logs`；连续失败 5 次后该定时器会被调度器自动停用（`MaxConsecutiveFailures`） | `_timer?.Dispose(); _timer = null;` |
| **解绑事件订阅** | `IEventBus.On` 返回的「取消订阅」委托不调用 → 宿主每次 `Emit` 都会调用一个已卸载对象的处理器 → 内存/ALC 泄漏 + 异常 | `_offPet = Capability.Events.On(PetEvents.PetPetted, …);` → `OnUnload` 里 `_offPet?.Invoke();` |
| **Dispose `HttpClient`（若是你自建的单例）** | 一个 `static readonly HttpClient` 会**钉住**可回收 ALC → 插件卸载后程序集**收不掉**（见 §9 踩坑 2） | 改「可释放单例」：`_http?.Dispose(); _http = null;` |
| **注销 UI 注册** | 宿主 `DisablePlugin`/`Dispose` 会调用 `UIApiImpl.UnregisterAll(id)` 兜底，但**运行时热重载/异常路径**下不保证；残留槽位会指向已死回调 | 通常**不需要手动**；宿主按 id 批量撤销。若你能拿到 `IUIApi` 可显式 `UI.UnregisterAll(Id)` |
| **停掉自建窗口 / 计时器** | WPF 窗口/`DispatcherTimer` 残留 → 前台线程不退出（本仓库已知「测试进程不退出」坑） | `_window?.Close(); _dt?.Stop();` |

> `IUIApi.UnregisterAll(scopeId)` 的兜底位置：`PluginHost.DisablePlugin` 与 `PluginHost.Dispose`
> 都会 `try { _uiApi.UnregisterAll(p.Manifest.Id); } catch { }`。**不要依赖它覆盖你的一切清理**。

---

## 5. 线程规则（本文件最重要的一章）

### 5.1 事实：回调跑在哪个线程

| 来源 | 线程 | 依据 |
|---|---|---|
| `Capability.Scheduler.Every/At` 回调 | **`System.Threading.Timer` 线程池线程（MTA）** | `src/DesktopPet.App/Services/SchedulerImpl.cs`：`new Timer(_ => SafeInvoke(...))` |
| `IPetApi` 各方法（Say/SetMood/PlayAnimation/MoveTo/GetPosition/GetWorkArea） | **宿主已统一封送到 UI 线程** | `src/DesktopPet.App/Services/PetApiImpl.cs` 的 `OnUi(...)` |
| `SystemImpl.ReadClipboard/WriteClipboard` | **宿主已封送到 UI 线程** | `src/DesktopPet.App/Services/SystemImpl.cs` 的 `OnUi(...)` |
| 事件总线 `IEventBus.Emit` 的处理器 | 取决于 `Emit` 的调用线程（宿主从哪 emit 就在哪跑） | `PluginContextImpl.EventBusImpl.Emit` 直接 `h(payload)` |
| 径向菜单 `RadialSlot.OnInvoke` / 托盘 `TrayMenuItem.OnClick` | **UI 线程**（由 WPF 点击事件直接调起） | `RadialMenu.cs` / `TrayManager.cs` |
| `ISettingsPage.BuildContent()` | **UI 线程**（设置窗构建时调用） | `SettingsWindow.cs` |

### 5.2 已被宿主「兜住」的部分

`PetApiImpl` 是本类**唯一**影响宠物的入口，所以「一处封送 = 全部插件受益」：

```csharp
private void OnUi(Action action)
{
    var d = _ui ?? Application.Current?.Dispatcher;
    if (d is null || d.CheckAccess()) { action(); return; }
    // 发后不理的方法用 BeginInvoke（不阻塞）：
    //   阻塞式封送会让线程池线程傻等 UI 线程，多插件同时定时会线程池饥饿；
    //   异步路径上的异常没人接得住（会直接崩进程）→ 必须在回调内兜住。
    d.BeginInvoke(new Action(() => { try { action(); } catch (Exception ex) { Log.Warn(...); } }));
}

private T OnUi<T>(Func<T> f, T fallback)
{
    var d = _ui ?? Application.Current?.Dispatcher;
    if (d is null || d.CheckAccess()) return f();
    try { return d.Invoke(f); }   // 要返回值 → 阻塞式 Invoke
    catch (Exception ex) { Log.Warn(...); return fallback; }
}
```

**结论**：`Pet.Say / Pet.SetMood / Pet.PlayAnimationAsync / Pet.MoveToAsync / Pet.GetPosition / Pet.GetWorkArea`、
`System.ReadClipboard / WriteClipboard` —— 这些你**可以直接从定时器回调里调**，不用自己封送。

### 5.3 ⚠️ 必须自己封送的部分

**其它 UI 亲和操作**（不在上面白名单里的 WPF 对象）**必须自己封送到 UI 线程**，否则抛
`The calling thread cannot access this object because a different thread owns it`
（「调用线程无法访问此对象」）——而且这类异常**常被插件的 `catch {}` 吞掉**，变成**静默失效**。

典型清单（都要自己封送）：

- 自己创建的 **WPF 窗口**（`Window.Show()` / `Close()` / 改 `Width/Height` / 改 `Children`）
- `System.Windows.Clipboard`（**要求 STA**；线程池是 MTA）——注：`SystemImpl` 已封送，
  但若你在插件里**直接**用 `System.Windows.Clipboard`，就绕过了那个封装，必须自己封送
- `Freezable`（画刷/位图）在创建或修改后跨线程使用前，若未 `Freeze()` 也不行
- 直接操作宿主可视化树 / `DispatcherObject` 派生对象

**正确封送模板：**

```csharp
using System.Windows;

// 需要一个「UI 线程的 Dispatcher」。用 Application.Current 即可（桌面宿主一定有 App）。
private static void OnUi(Action action)
{
    var d = Application.Current?.Dispatcher;
    if (d is null || d.CheckAccess()) { action(); return; }   // 已在 UI 线程 or 无 App

    // 「发后不理」用 BeginInvoke（异步、不阻塞）；异步路径上的异常必须自己兜住，
    // 否则会变成 Dispatcher 未处理异常 → 直接崩进程（App 的 DispatcherUnhandledException 覆盖不到你这里）。
    d.BeginInvoke(new Action(() =>
    {
        try { action(); }
        catch (Exception ex) { /* 记你自己的日志；绝不外抛 */ }
    }));
}

// 需要「返回值」时才用阻塞式 Invoke（会在 UI 线程等你，别在 UI 线程自己调，否则死锁）
private static T OnUi<T>(Func<T> f, T fallback)
{
    var d = Application.Current?.Dispatcher;
    if (d is null || d.CheckAccess()) return f();
    try { return d.Invoke(f); }
    catch (Exception ex) { /* 记录并返回 fallback */ }
    return fallback;
}
```

使用示例（自建窗口，跑在线程池定时器里）：

```csharp
private void OnTimer()   // 线程池线程！
{
    // ❌ 错误：直接操作 WPF 窗口 → 抛"调用线程无法访问此对象" → 若被 catch{} 吞 → 静默失效
    // _myWindow.Width = 200;

    // ✅ 正确：封送到 UI 线程
    OnUi(() => { _myWindow.Width = 200; _myWindow.Show(); });
}
```

**`BeginInvoke` vs `Invoke` 的选择：**

| 场景 | 用哪个 | 原因 |
|---|---|---|
| 发后不理（改属性、Show、Say…） | `BeginInvoke` | 不阻塞线程池；避免多插件同时定时导致线程池饥饿 |
| 需要拿返回值（读控件状态…） | `Invoke` | 必须同步等结果；**不要在 UI 线程里对 UI 线程再 Invoke（死锁）** |

> 📌 **TDD 的教训（真实发生）**：一度把 `PetApiImpl.Say` 改成阻塞式 `Invoke`，UiProbe 断言直接报
> 「Pet.Say 在 3 秒内未返回 —— 封送疑似死锁」。根因正是：测试主线程在跑断言、没在泵消息，`Invoke`
> 永远等不到。这才促成了「发后不理用 `BeginInvoke`」的最终决策。

### 5.4 ⚠️ 资源规则：不要用 STJ 反射序列化「插件内定义的类型」（会钉住可回收 ALC）

> 这一条与线程无关，但同属「插件里最容易静默踩坏宿主资源」的规则，放在 §5 一起记。

- **规则**：**不要**用 `JsonSerializer.Serialize/Deserialize<T>` 直接序列化**定义在插件程序集内的类型**
  （含**匿名类型**）。`System.Text.Json` 的反射式序列化会在**进程级共享缓存**里留下对「插件类型」的强引用，
  导致插件卸载后**可回收 ALC 收不回**（`alc = N`，~9MB）。
- **改用**：`Utf8JsonWriter` 手写（适合请求体这类小 JSON），或 `[JsonSerializable]` + **源生成 `JsonSerializerContext`**。
- **安全**：序列化 **BCL / 宿主契约类型**（`string`、`Dictionary<string,string>`、`DesktopPet.Core` 里的类型）——它们的
  元数据缓存在默认 ALC，**不会钉住插件**。
- **持久化注意**：宿主 `JsonPluginStorage` 的 `Get<T>`/`Set<T>` 走**同一条 STJ 反射路径** ⇒
  **把自定义类型存过盘的插件会阻止自身 ALC 回收**。优先持久化简单/BCL 类型；复杂结构自行用 `Utf8JsonWriter`
  存成字符串，或把结构定义在 `DesktopPet.Core`。
- **影响面**：不影响功能，只影响「禁用/启用插件或热重载时的内存回收」。完整排查与实测数据见 §9「踩坑 2」。

---

## 6. 存储规范

### 6.1 铁律：不要把数据写进自己的插件目录

插件目录必须保持「可整体覆盖替换」——这样以后更新插件直接覆盖整个文件夹，不会丢用户数据。

**正确**：用 `ICapabilityApi.Storage`，真实路径 = `<数据根>/plugins/<插件id>/`：

```csharp
ctx.Capability.Storage.Set("lastRemind", DateTime.Now);
var t = ctx.Capability.Storage.Get<DateTime>("lastRemind");
```

**禁止**：往 `AppContext.BaseDirectory`、自己的 `plugins/<id>/` 里写（会被下次更新覆盖）。

### 6.2 `JsonPluginStorage` 的语义（源码：`src/DesktopPet.App/Services/JsonPluginStorage.cs`）

- 一个 key ↔ 一个文件：`<数据根>/plugins/<id>/<key>.json`。
- `Set<T>`：`JsonSerializer.Serialize(value, { WriteIndented = true })` 覆盖写。
- `Get<T>`：文件不存在 → 返回 `default`；反序列化异常 → **catch 吞掉，返回 `default`**（不抛）。
- `Contains`：就是文件是否存在。
- `Remove`：删除该文件。
- 命名空间按 `plugin.Id` 隔离，**插件之间互不可见**（`PluginHost.BuildContext` 里
  `new JsonPluginStorage(Paths.PluginDataDir(id))`）。

> ⚠️ **两个要留意的语义**：
> 1. `Get<T>` 反序列化失败**静默返回 default**——若你的数据结构改了版本，会「读回默认值」而不是报错，
>    请自行加版本字段做迁移。
> 2. key 会直接拼成文件名。**避免在 key 里用路径分隔符 / `..` / 非法文件名字符**。

数据生命周期示例（用 `Contains` 区分「未设置」与「设为默认值」）：

```csharp
var start = Capability.Storage.Contains("startHour")
    ? Capability.Storage.Get<int>("startHour")
    : DefaultStartHour;   // 来自 builtins/HourlyChime 的真实写法
```

---

## 7. UI 注册

### 7.1 径向菜单槽位 `RadialSlot`

```csharp
public sealed record RadialSlot
{
    public required string Id      { get; init; }   // 槽位唯一 id（建议 "<插件id>:<功能>" 或短横线格式）
    public required string Label   { get; init; }   // 显示文字
    public string  Icon            { get; init; } = "default";  // 图标 key
    public int     Order           { get; init; } = 100;        // 越小越靠前
    public required Action OnInvoke { get; init; }               // 点击回调（在 UI 线程）
}
```

**`Order` 排序与分页**（源码：`src/DesktopPet.App/Windows/RadialMenu.cs`）：

- 有效顺序：**用户覆盖优先**，否则用插件声明的 `Order`（`RadialMenuResolver.EffectiveOrder`）。
- 平局按 `Id` 字典序稳定排序。
- **每页最多 8 个**（`MaxPerPage = 8`）；`_pageCount = ceil(total / 8)`。
- 8 槽位角距 45°，相邻弦长 ≈ 59px > 槽位 38px，**不重叠**。
- 翻页：滚轮 / 点顶部胶囊 `‹ n/m ›`；打开时回到第一页。
- 全局可见数量上限：`RadialMenuConfig.MaxVisible`（`0` = 不限，用户可在设置页改）。

**可用图标 key**（宿主缓存 `neko-assets/icons/*.png`，未匹配 → 回退 `default.png`）：

```
book  camera  clock  close  copy  dance  default  eye  food  gear
heart home    info   next   pin   play   prev     sleep todo  wave
```

> 图标来自 `RadialMenu.LoadIcons()` 扫描 `neko-assets/icons/` 的**文件名（去扩展名）**。
> 用表外的 key → 静默回退 `default`（**不报错**，所以图标"没对上"很难发现）。

### 7.2 托盘菜单项 `TrayMenuItem`

```csharp
public sealed record TrayMenuItem
{
    public required string Id    { get; init; }
    public required string Label { get; init; }
    public bool  IsSeparator     { get; init; }   // true → 插入分隔线
    public Action? OnClick       { get; init; }   // null → 该项置灰不可点
}
```

宿主消费（`TrayManager`）：把全部插件托盘项折叠进**「插件…」子菜单**，避免主菜单臃肿。
`IsSeparator=true` 插分隔线；`OnClick=null` 时 `IsEnabled=false`。

> 📌 托盘子菜单在**构建菜单时**读取一次；插件注册发生在 `OnLoad`（启动早期），
> 用户打开托盘菜单时可见。**运行时新注册的托盘项需等菜单重建**。

### 7.3 设置页 `ISettingsPage`

```csharp
public interface ISettingsPage
{
    string Id { get; }
    string Title { get; }
    object BuildContent();   // 返回承载控件（通常是 Panel / FrameworkElement）
}
```

**🔴 关键规则（否则设置页根本不显示）**：宿主只把满足下列条件的插件设置页嵌入「插件」栏目卡片：

```csharp
// SettingsWindow.cs
foreach (var page in _pet.UIApi.AllSettingsPages)
    if (page.Title == p.Manifest.Id || page.Title == p.Manifest.Name)
        sp.Children.Add((FrameworkElement)page.BuildContent());
```

→ **你的 `ISettingsPage.Title` 必须等于插件的 `Id` 或 `Name`**。
`builtins/HourlyChime` 的做法是 `Title => "com.lantern.hourly"`（= Id）。

**用 SDK 的深色控件工厂 `PluginUi`（`src/DesktopPet.Sdk/PluginUi.cs`）保持风格统一：**

```csharp
using DesktopPet.Sdk;

var stack = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(8) };
stack.Children.Add(PluginUi.SectionHeader("时间设置"));
stack.Children.Add(PluginUi.Text("提醒时间", sub: true));
stack.Children.Add(PluginUi.Input("23:00"));
stack.Children.Add(PluginUi.Button("保存"));
```

`PluginUi` 提供的成员：色刷 `Bg` / `CardBg` / `TextMain` / `TextSub` / `Border`，
控件工厂 `Text(content, sub)` / `Button(text)` / `Input(text)` / `SectionHeader(title)`。
其色刷内部 `Freeze()`（`Frozen(r,g,b)`）——冻结后可跨线程共享、省渲染开销（见 §9 踩坑 4）。

### 7.4 运行时启用 / 禁用

| 行为 | 宿主实现 |
|---|---|
| 禁用 | `PluginHost.DisablePlugin(id)`：`OnUnload` → `UnregisterAll(id)` → `Dispose`（卸载 ALC）→ 记入 `_disabled` |
| 启用 | `PluginHost.EnablePlugin(id)`：重新 `LoadOne` → `OnLoad` → 移出 `_disabled` |
| 持久化 | 禁用清单存 `<数据根>/config.json` 的 **`DisabledPlugins`**（PascalCase——`ConfigManager` 未设命名策略；**不移动、不改名文件夹**） |

---

## 8. 调试与验证手段

### 8.1 日志

- 位置：`<数据根>/logs/neko-YYYYMMDD.log`（按天滚动；单文件超 5MB 追加 `-N` 序号；
  启动清理 7 天前旧日志）。
- 行格式：`[时间] [级别] [ManagedThreadId] 消息`。**`[ThreadId]` 对排查线程问题极有用**。
- 默认级别 `Warning`；`Debug` / `Info` **需在配置里开启**才会落盘（避免频繁刷盘）。
  - 配置：`<数据根>/config.json` 的日志级别项（`Log.Initialize(level)`；接受 `off/debug/info/warn/error`）。
- `Log.Error` 会**同时**追加到 `<数据根>/errors.log`（兼容旧名；⚠️ 是 `errors.log` 复数，见 `Log.cs:26`）。
- **插件写日志**（v0.1.83）：用 **`Capability.System.Log(PluginLogLevel.<级>, msg, ex?)`** —— 宿主自动加 `[插件:<id>]` 前缀，`Warn/Error` 默认落盘、`Debug/Info` 默认不落盘、永不抛。签名与语义见 §2.3 入口 3。**不要再靠 `Debug.WriteLine`**（Release 下静默）。
- v0.1.82 起，插件**加载失败**会写明原因（`PluginHost.ReportErrors`）：

  ```
  [插件] LoadCore：N 个插件加载失败
  [插件] com.lantern.xxx 加载失败：<确切原因>
  ```

  > 此前 `_errors` 只收集不落盘 → 「插件全没了」时日志干净得像没事，排查只能靠猜。

**定位法（推荐顺序）：**

```bash
# 0) 先确定数据根：默认 %AppData%\Neko；便携模式为 <程序根>\data；被 $NEKO_DATA_DIR 覆盖时取它
D="${NEKO_DATA_DIR:-$APPDATA/Neko}"      # PowerShell: $D = if ($env:NEKO_DATA_DIR) { $env:NEKO_DATA_DIR } else { "$env:APPDATA\Neko" }
# 1) 找今天的日志
ls "$D/logs/"
# 2) 只看插件相关（含插件自报日志 [插件:<id>] 与宿主加载日志 [插件]）
grep -a "\[插件" "$D"/logs/neko-*.log
# 3) 看是否有跨线程 "调用线程无法访问此对象"（→ §5）
grep -a "调用线程无法访问\|InvalidOperationException" "$D"/logs/neko-*.log
```

### 8.2 启动参数

| 参数 | 作用 |
|---|---|
| `Neko.exe --dev` | 跳过首次引导（`--skip-onboarding` 同义）——开发期不用每次输名字 |
| `Neko.exe --settings` | 启动后直接打开设置页 |
| `Neko.exe --settings=<栏目>` | 打开设置页并定位到指定栏目（如 `--settings=input`），改完 UI 好截图核对 |

### 8.3 `tests/LoaderTest/` —— 无 UI 的加载链路测试

覆盖：manifest 解析、权限授予、高风险信任通道、`OnLoad` 调用、Storage 隔离、
**ALC 卸载真验证**（断言事件处理器数归零、`IsCollectible=true`）。

```bash
bash /c/Users/Lantern/neko-dotnet-env.sh build tests/LoaderTest/LoaderTest.csproj -c Debug
bash /c/Users/Lantern/neko-dotnet-env.sh run --project tests/LoaderTest/LoaderTest.csproj -c Debug
# 任一 FAIL → 退出码 1（v0.1.77 修复「假绿」后的行为）

# 反向自检（证明「FAIL 会改退出码」这条接线是通的）：
LOADERTEST_INJECT_FAILURE=1 bash /c/Users/Lantern/neko-dotnet-env.sh run --project tests/LoaderTest/LoaderTest.csproj -c Debug
# 期望：打印 FAIL + 退出码 1
```

### 8.4 `tests/PluginSmoke/` —— 真加载全部插件

用**真实** `PluginLoader` + 可回收 ALC 加载 `输出目录/plugins/` 下**全部**插件，
逐个执行 `OnLoad → 空闲 3.2s（让定时器 tick）→ OnUnload → 强制 GC`，输出：
加载成败、OnLoad/OnUnload 异常、`PrivateMemorySize` / GC 堆增量、**ALC 能否回收**、闲置 CPU 增量。

```bash
bash /c/Users/Lantern/neko-dotnet-env.sh build tests/PluginSmoke/PluginSmoke.csproj -c Debug
bash /c/Users/Lantern/neko-dotnet-env.sh run --project tests/PluginSmoke/PluginSmoke.csproj -c Debug
# 退出码：0 = 全部加载且 OnLoad 无异常；1 = 有失败
```

**诊断开关**（用于二分定位「某插件 ALC 收不回」）：

```bash
# 只跑匹配插件
... run --project tests/PluginSmoke/PluginSmoke.csproj -c Debug -- --only bazi
# 只加载+卸载，不调 OnLoad（区分「加载即泄漏」/「OnLoad 引起泄漏」）
... -- --skip-onload
# 不调 OnLoad，改按清单反射调用插件自检静态方法
... -- --selftest all
```

> ⚠️ **安全约束**：PluginSmoke **绝不调用任何槽位回调**（不触发截图写盘 / 媒体键 / 网络请求），
> 系统能力走纯内存替身（不碰真实剪贴板、不弹通知、不开浏览器）。

### 8.5 已知环境坑（省你时间）

- 直接跑 `dotnet` 会因环境变量缺失而崩 → 一律用 `bash /c/Users/Lantern/neko-dotnet-env.sh <dotnet 子命令> ...`。
- 测试进程偶发**不退出**（遗留前台线程）→ PluginSmoke/LoaderTest 已在结尾用 `Environment.Exit` 强制。
  若你自写测试也创建了 WPF 窗口，请照做。
- **涉及素材/打包的改动，必须先清 `bin` 再测**（v0.1.79 的「UiProbe 全过」就是 bin 残留导致的**假绿**）。

---

## 9. 踩坑清单

> 以下每条都在本项目**真实发生过**。按「现象 / 根因 / 正确做法」三段式写。

### 踩坑 1 —— 在 `Loaded` 回调里再挂 `Loaded`，延迟加载永不执行

- **现象**：`weather / quote / wordcard / screenshot / fortune / hourly` 这 6 个「延迟插件」**从来没被加载过**
  （发布版同样中招：`<数据根>/plugins/` 只有 11 个）。
- **根因**：`PetWindow.LoadPlugins()` 是被 App 的 `Loaded` 处理器调用的，而它内部又**无条件**
  `Loaded += (_, _) => LoadDeferred();`。**WPF 不会在事件触发过程中调用新挂的处理器，且 `Loaded` 不会重放**
  → `LoadDeferred()` 永不执行。**这是 WPF `Loaded` 事件的语义，不是 bug 报告里能查到的"偶发"**。
- **正确做法**：先判断当前是否已加载完，已加载就直接排后台任务，否则才挂一次性处理器：

  ```csharp
  void QueueDeferredLoad() => Dispatcher.BeginInvoke(new Action(() => {
      _pluginHost.LoadDeferred();
      DeferredPluginsLoaded?.Invoke();
  }), DispatcherPriority.Background);

  if (IsLoaded) QueueDeferredLoad();          // 已加载完 → 直接排
  else Loaded += (_, _) => QueueDeferredLoad(); // 还没加载完 → 挂一次
  ```

- **修复实测**：**18/18 插件全部加载**。

### 踩坑 2 —— STJ 反射序列化钉住可回收 ALC（附：一个被证伪的假设）

- **现象**：插件（Bazi）卸载后，其 `AssemblyLoadContext` **回收不了**（`alc = N`），残留 ~9MB 私有内存。
  **不影响功能**，只影响「禁用/启用插件或热重载时的内存回收」。
- **真根因（已实测确认）**：`System.Text.Json` 的**反射式序列化**在进程级共享缓存里留下了对
  **「插件程序集内定义的类型」**的强引用。
  - 触发点：`builtins/DesktopPet.Builtin.Bazi/DeepSeekClient.cs` 的 `BuildRequestBody()`（约 `:113-122`）
    用 `JsonSerializer.Serialize(payload)` 序列化一个**插件程序集内的匿名类型**；
    由 `BaziPlugin.cs:40` 的 `DeepSeekClient.SelfCheck()`（其 `:191` 调 `BuildRequestBody`）在 OnLoad 期触发。
  - **`lunar.dll` 已排除**：`--selftest lunar` 实测 `alc = Y`（原先「疑似 lunar 静态缓存」是**推测，已被数据否定**）。

**决定性二分实验（同时是「怎么定位这类问题」的范例，用 `tests/PluginSmoke` 跑）**

A) 二分 6 项 OnLoad 自检（`--selftest <清单>`，均跳过 `OnLoad`）：

| 开关 | alc |
|---|---|
| `--skip-onload` | **Y** |
| （完整 OnLoad，无开关） | **N** |
| `--selftest none` | **Y** |
| `--selftest lunar` | **Y** |
| `--selftest fortune` | **Y** |
| `--selftest api`（DPAPI） | **Y** |
| `--selftest mbti` | **Y** |
| `--selftest prompt` | **Y** |
| **`--selftest client`** | **N** ← 唯一 |
| `--selftest all` | **N** |

B) 机理对照（`--probe`，对插件自定义类型做一次序列化）：

| 模式 | 行为 | alc |
|---|---|---|
| `none` | 不序列化 | Y |
| `typeonly` | 只 `GetType()` + `Activator.CreateInstance()` | **Y**（排除「反射建对象」本身） |
| `default` | `JsonSerializerOptions.Default` | N |
| `owned` | 新建 options | N |
| `ownresolver` | 新建 options + 新建 `DefaultJsonTypeInfoResolver` | **N** |
| **`writer`** | `Utf8JsonWriter` 手写同份 JSON（绕开 STJ 反射） | **Y** ✅ |
| `store` | 走宿主 `IPluginStorage.Set<T>` 路径 | **N** |

**读法**：换 options / 换 resolver **都无效** ⇒ 钉根是 STJ **反射路径上的进程级共享状态**，
不是 options / resolver 实例 —— 所以任何「options 级」的改法都够不着它；`writer = Y` 指出了正确的修复方向。

- **正确做法（插件作者必守）**：
  1. **不要**用 `JsonSerializer.Serialize/Deserialize<T>` 直接序列化**定义在插件程序集内的类型**（含匿名类型）。
  2. 改用 **`Utf8JsonWriter` 手写**（适合请求体这类小 JSON），或声明 `[JsonSerializable]` +
     **源生成 `JsonSerializerContext`**（走编译期元数据，不进反射缓存）。
  3. 序列化 **BCL / 宿主契约类型**（`string`、`Dictionary<string,string>`、`DesktopPet.Core` 里的类型）**是安全的**
     —— 它们的元数据缓存在默认 ALC，不会钉住插件。
  4. **持久化的务实建议**：宿主 `src/DesktopPet.App/Services/JsonPluginStorage.cs:32` 的 `Get<T>`/`Set<T>`
     走**同一条 STJ 反射路径**（`--probe store` 实测 = N）⇒ **凡是把自定义类型存过盘的插件，都会阻止自身 ALC 回收**。
     宿主不可能为「未知的插件类型」提供源生成上下文，所以：**优先持久化简单/BCL 类型**；
     需要复杂结构时，**自行用 `Utf8JsonWriter` 存成字符串**，或把结构定义在 `DesktopPet.Core`（宿主契约类型，安全）。

- **方法论教训（保留，被证伪的假设本身很有价值）**：审计起初假设是 `static readonly HttpClient` 钉住了 ALC，
  改成「可释放单例 + `OnUnload` 里 Dispose」后**复测 `ALC 仍为 N`**——**假设被证伪**。
  ① 「改对了但没解决问题」也要**如实记录**，别含糊过去；② 真正定位靠**逐项二分**（`--selftest` 分自检项、
  `--probe` 分机理），而不是靠「最像的那个嫌疑」；③ 即便 `HttpClient` 不是根因，
  「插件里的 `static` 长生命周期字段会被 ALC 一起钉住」这条**依然成立**，仍应避免。

### 踩坑 3 —— `catch {}` 全吞 → 静默失效

- **现象**：剪贴板历史插件的 `1.5s` 轮询**永远是空的**、且毫无提示；倒计时等插件
  「到点系统通知弹了、**猫的气泡不弹**」。
- **根因**：`System.Windows.Clipboard` 要求 STA，而插件定时器回调在线程池（MTA）→ 抛
  「调用线程无法访问此对象」；插件里 `catch {}` **全吞** → 静默失效，长期没被发现。
- **正确做法**：
  1. **不要裸 `catch {}`**——至少用 **`Capability.System.Log(PluginLogLevel.Warn, "…", ex)`**（v0.1.83 新增，见 §2.3）或 `Notify` 记一笔，或向用户冒泡。静默失效最难查。
  2. **别用 `Debug.WriteLine` 当插件日志**——Release 构建无调试器监听时它**完全静默**（与 `catch{}` 是同一「留不下痕」的病根）。要"事后能查"就必须走 `Capability.System.Log`（宿主落 `<数据根>/logs/`）。
  3. 剪贴板/窗口等 STA/UI 亲和操作，**统一封送到 UI 线程**（§5.3）。
  4. 用「功能上必须能看到变化」的方式验证（气泡/眼睛/像素变化），而不是「没报错就算过」。

### 踩坑 4 —— 静态画刷不 `Freeze()` → 跨线程/高频渲染纯损耗

- **现象**：3 个插件窗口（`NotesWindow`/`TodoWindow`/`ClipboardWindow`）共 **16 处** `static readonly Brush`
  是裸 `new SolidColorBrush(...)`。
- **根因**：未 `Freeze()` 的 `Freezable` 不能跨线程安全共享，且 WPF 渲染时每次使用都可能做额外处理；
  宿主是常驻内存程序（内存红线 < 200MB），静态资源应冻结。
- **正确做法**：照 `src/DesktopPet.Sdk/PluginUi.cs` 的 `Frozen(r,g,b)` 写法——创建后立即 `brush.Freeze()`：

  ```csharp
  private static SolidColorBrush Frozen(byte r, byte g, byte b)
  {
      var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
      brush.Freeze();   // 冻结后可跨线程共享、省渲染开销
      return brush;
  }
  ```

  或直接用 `PluginUi.TextMain` / `CardBg` 等已冻结的共享色刷。

### 踩坑 5 —— manifest 与代码不符（声明失真）

- **现象 A**：4 个插件（`Countdown / Pomodoro / SleepReminder / Weather`）**用了 `Capability.Storage` 却没在
  `plugin.toml` 写 `storage`**。
- **现象 B**：6 个插件（`Affection / Countdown / HourlyChime / Pomodoro / SleepReminder / Weather`）
  **注册了设置页却写 `ui.settings = false`**。
- **根因**：代码里用了能力，但清单没同步声明；当时宿主**不按 manifest 过滤能力**（所以功能没坏），
  但**声明失真**会在「按声明授权」真正落地时突然失效——属契约正确性缺陷。
- **正确做法**：
  1. 用了 `Capability.Storage` → 清单必须写 `storage = ["local"]`。
  2. 注册了 `ISettingsPage` → 清单必须写 `[ui] settings = true`。
  3. 代码里的 `RequiredPermissions`（overridden 属性）与 `plugin.toml` 的 `[permissions]` **必须一致**。
  4. 自查：`grep -n "Capability.Storage\|RegisterSettingsPage\|RegisterRadialSlot" 你的代码` 对照清单。

### 踩坑 6 —— 高风险权限被拒载

- **现象**：`com.lantern.music` / `com.lantern.screenshot` **每次启动都被拒载**（从来没工作过）。
- **根因**：它们声明了粗 token `process` / `file`，命中 `IsHighRisk(FileAccess | Process)`；
  而生产侧 `PluginHost` **未接入信任清单** → 恒为 `Empty` → 一律拒载。
- **正确做法**：
  1. **不要声明 `file` / `process`**。写自己的数据目录用 `file.own`（低危），发媒体键用 `media`（低危）。
  2. 官方内置若确需高危 → 必须**同时**命中 `builtins.lock.json` 信任清单（且摘要匹配）。
     但注意：**生产 App 侧尚未接线该清单**（见 §3.6），所以当前实际策略是「一律拒载」。
  3. 修法参考：官方把 `process`→`media`、`file`→`file.own`（v0.1.80）。

### 踩坑 7 —— 插件加载失败而日志干净（可诊断性）

- **现象**：右键菜单里插件**全没了**（只剩"无快捷项"），但 `<数据根>/logs/` 里**什么都没有**。
- **根因**：`PluginHost._errors` 只收集、不落盘；而真实原因是构建形态错误（`SelfContained` 泄漏到
  构建期 → `AssemblyDependencyResolver` 失败，code `-2147450750`）。
- **正确做法**：
  1. 宿主已加 `ReportErrors` 把失败原因写日志（v0.1.82）。
  2. **你排查插件问题时的第一动作 = 看 `<数据根>/logs` 里的 `[插件]` 行**，别靠猜。
  3. 相关构建坑：`DesktopPet.App.csproj` 必须用 `<PublishSelfContained>` 而**不是** `<SelfContained>`
     （后者会影响 Debug 构建，导致所有插件加载失败）。这条是**宿主侧**的坑，但你若自行搭建宿主构建脚本会踩到。

### 踩坑 8 —— 类型分裂：插件 ALC 里出现 `DesktopPet.Core` 的副本

- **现象**：宿主 `IsAssignableFrom` 判定失败，插件**加载不进来**（`IPetPlugin` 被识别成两个不同类型）。
- **根因**：插件 ALC 里又加载了一份 `DesktopPet.Core`（或 `DesktopPet.Sdk`）→ 同一个接口在**两个程序集实例**里各有一份类型。
- **正确做法**：宿主已用 **`PluginLoadContext.HostSharedAssemblies` 白名单**把 `DesktopPet.Core` / `DesktopPet.Sdk` 路由回**宿主已加载的实例**。
  **插件侧对策**：**不要**把 `DesktopPet.Core.dll` / `DesktopPet.Sdk.dll` 拷进插件目录（只拷「自己的 dll + plugin.toml」，别整份 `bin/`）。
- **依据**：立项第一天 dogfooding 示例插件一跑就抓到（`docs/dev-log.md` 2026-09-01「坑 1」）。

### 踩坑 9 —— 权限前缀不匹配：声明了却全部变 `None`

- **现象**：所有插件权限全变成 `None`。
- **根因**：清单写 `pet = ["animate"]`，查表却用 `pet.animate` 匹配（或把 `scheduler` 写进了 `pet`/`ui` 段）。
- **正确做法**：宿主按段自动加前缀（`pet.` / `ui.` / `storage.`），**只有 `other` 段不加前缀**（§3.2）。把 `scheduler`/`notify`/`clipboard`/`network`/`media`/`file.own` 放进 `other`。
- **依据**：`docs/dev-log.md` 2026-09-01「坑 2」。

### 踩坑 10 —— 宿主没引用 Sdk 导致「发布形态」加载失败

- **现象**：无头启动 `Neko.exe` 后插件数据目录没被创建 → 插件在**真实运行时根本没加载**（但源码 bin 下的 `LoaderTest` 却是绿的）。
- **根因**：宿主 `App.csproj` **没引用 `DesktopPet.Sdk`** → App 输出目录缺 `DesktopPet.Sdk.dll` → 插件 ALC 的白名单找不到 Sdk → 路由失败。
- **正确做法**：宿主必须显式引用 Sdk（`ProjectReference`，App 侧已修复）。**这条是宿主开发侧的坑**——插件作者无影响，但你自己搭宿主时必须注意。
- **教训（比 bug 更重要）**：`LoaderTest` 直接测源码 `bin`（自带 Sdk.dll）会**完美掩盖发布形态的缺失** → 必须补「加载真实 App 输出目录」的测试（现已固化）。
- **依据**：`docs/dev-log.md` 2026-09-01「坑 3」。

---

## 10. 完整骨架

> 📁 `templates/plugin-skeleton/` —— **可直接复制**的最小插件模板。
>
> 🔴 **绝对不要**把它加进 `src/DesktopPet.sln`，也不要让任何现有 `.csproj` 引用它
> （否则会破坏解决方案构建）。它只是给人 / 给 Agent 复制的模板。

模板内容：

| 文件 | 说明 |
|---|---|
| `MyPlugin.cs` | 最小插件实现：径向槽位 + 线程安全的定时器 + 存储 + OnUnload 清理 |
| `plugin.toml` | 最小清单（含权限注释） |
| `MyPlugin.csproj` | 构建配置 + 自动部署到 `plugins/<id>/` 的 target |
| `README.md` | 复制后要改哪几处 |

复制步骤：

```bash
# 1) 复制整个骨架
cp -r templates/plugin-skeleton plugins/com.yourname.myplugin
# 2) 三处改名：目录名、plugin.toml 的 id / entry、csproj 的 AssemblyName、类名
# 3) 构建
bash /c/Users/Lantern/neko-dotnet-env.sh build plugins/com.yourname.myplugin/MyPlugin.csproj -c Debug
```

---

## 附录 A：权限 / 事件速查

### A.1 `PetEvents` 常量（`src/DesktopPet.Core/ICapabilityApi.cs`）

> ⚠️ **注意「是否已广播」列**：订阅**未广播**的事件**不会报错**，只是**永远不触发**。**别依赖它们**——初始化逻辑放 `OnLoad`，或订阅已广播的 `app:ready`。

| 常量 | 事件名 | payload 类型 | 是否已广播 |
|---|---|---|---|
| `AppReady` | `"app:ready"` | `null` | ✅ v0.1.1+（插件全部加载完成后广播，可用于替代部分 `OnLoad` 内的延迟初始化） |
| `PetPetted` | `"pet:petted"` | `null` | ✅ |
| `PetDragged` | `"pet:dragged"` | `PetDraggedArgs(X, Y)` | ✅ |
| `PetLanded` | `"pet:landed"` | `null` | ✅ |
| `PetEdge` | `"pet:edge"` | `PetEdgeArgs(Side, Attached)`（Side: 0左 1右 2上 3下） | ✅ |
| `MouseMoved` | `"mouse:moved"` | `MouseMovedArgs(X, Y)` 全局鼠标坐标，**约 5Hz 节流** | ✅ |
| `AppWake` | `"app:wake"` | — | ❌ 预留，未广播 |
| `AppSleep` | `"app:sleep"` | — | ❌ 预留，未广播 |
| `ScreenChange` | `"screen:change"` | — | ❌ 预留，未广播 |
| `Tick` | `"tick"` | — | ❌ 预留，未广播（⚠️ **不能用 `OnTick(delta)` 代替——宿主未调用它**；周期逻辑改用 `Capability.Scheduler.Every`） |

### A.2 强类型事件 payload（可跨程序集直接 cast）

```csharp
public sealed record MouseMovedArgs(double X, double Y);
public sealed record PetDraggedArgs(double X, double Y);
public sealed record PetEdgeArgs(int Side, bool Attached);  // Side: 0左 1右 2上 3下
```

### A.3 `PetState` / `PetMood` / 几何

```csharp
public enum PetState { Idle, Walk, Sit, Sleep, Pet, Drag, Fall, Climb, Work }
public enum PetMood  { Idle, Happy, Sleepy, Angry, Sad }

public readonly record struct Point2(double X, double Y) { public static readonly Point2 Zero; }
public readonly record struct Rect2(double X, double Y, double Width, double Height)
{
    public double Right  => X + Width;
    public double Bottom => Y + Height;
}
```

### A.4 如何读取事件 payload（v0.1.1+ 强类型直接 cast）

从 v0.1.1 起，宿主 payload 用 **public record**（替代之前的匿名类型），跨程序集**可以直接 cast**，不用反射：

```csharp
// ✅ 推荐（v0.1.1+，强类型 cast）
Capability.Events.On(PetEvents.MouseMoved, p =>
{
    if (p is MouseMovedArgs a) { var x = a.X; var y = a.Y; }
});

// ✅ 或模式匹配
Capability.Events.On(PetEvents.PetEdge, p =>
{
    if (p is PetEdgeArgs { Side: 0, Attached: true }) { /* 贴左边 */ }
});

// ✅ 旧反射方式（v0.1.0 兼容，仍可用；payload 字段名稳定）
private static double ReadDouble(object? payload, string name)
{
    if (payload is null) return 0;
    var prop = payload.GetType().GetProperty(name);
    return prop?.GetValue(payload) is double d ? d : 0;
}

// ❌ 不要用 dynamic：v0.1.1 已确认会抛 RuntimeBinderException
//     （跨程序集访问 public record 成员的动态绑定仍受可访问性限制）
```

---

## 附录 B：文档-实现一致性核对

> 本附录记录「本文件与宿主源码」的**已核实一致项**与**发现的不一致项**（供团队决策）。
> 核对基于 v0.1.82 源码逐字段阅读。

### B.1 已核实一致（重点）

| 项 | 结论 |
|---|---|
| `IPetPlugin` / `PluginBase` / `IPluginContext` 三接口签名 | 与源码逐字段一致 |
| `IPetApi` 6 个方法 + `AnimationOptions` | 一致（注意 `PlayAnimationAsync` / `MoveToAsync` 返回 `Task`） |
| `IUIApi` / `RadialSlot` / `TrayMenuItem` / `ISettingsPage` | 一致 |
| `ICapabilityApi` 四成员 + `IPluginStorage` / `IScheduler` / `ISystem` / `IEventBus` | 一致 |
| 权限 token → 位 → Capability 映射 | 与 `PermissionChecker.Map` 一致（13 个 token） |
| 高风险 = `FileAccess | Process`；`file.own` / `media` 为低危 | 一致 |
| `Scheduler` 回调在线程池；`IPetApi` 已封送 UI 线程 | 与 `SchedulerImpl` / `PetApiImpl` 一致 |
| 存储路径 `<数据根>/plugins/<id>/` | 与 `Paths.PluginDataDir` / `JsonPluginStorage` 一致 |
| 径向菜单每页 8、`Order` 排序、图标回退 `default` | 与 `RadialMenu.cs` 一致 |

### B.2 发现的不一致 / 需宿主决策项（重要）

1. ~~**`IPluginContext` 没有 `Log` 入口**~~ → **✅ 已于 v0.1.83 补齐**（以 `Capability.System.Log` 形式暴露，**未动 `IPluginContext`**）。
   <br>（历史，v0.1.82 及以前）任务描述提到「四个入口 `Pet`/`UI`/`Capability`/`Log`」，但源码里
   `IPluginContext` 只有 `Pet`/`UI`/`Capability` 三个 API 入口（外加 `HostVersion`/`DataRoot`/`PluginDataDir` 三个属性）；
   `Log` 当时是宿主**内部**静态类（`src/DesktopPet.App/Core/Log.cs`），**不在** Core/Sdk 里。
   <br>**现状**：新增 `PluginLogLevel` 枚举（`src/DesktopPet.Core/PluginLogLevel.cs`）+ `ISystem.Log(...)`（只增不改），
   宿主自动加 `[插件:<id>]` 前缀、`Warn/Error` 默认落盘、**永不抛**。**注意**：落实形式是"既有 `ISystem` 加成员"，
   所以 **`IPluginContext` 仍只有三个能力入口**（这点没变）——见 §2.3 入口 3 与 §8.1。

2. ~~**信任清单 `builtins.lock.json` 在生产 App 侧未接线**~~ → **✅ 已于 v0.1.82 接线**。
   <br>（历史）`PluginHost` 曾 `new PluginLoader(pluginsRoot)` **未传** `PluginTrustSet` → 运行时信任集恒为 `Empty`，
   任何 `file`/`process` 声明都被拒载（含官方内置）——即"设计好了从未接线"。
   <br>**现状**：`PluginHost` 构造时调用 `LoadBuiltinTrust()` 从 exe 同级读 `builtins.lock.json` 并传入
   （`PluginHost.cs:46` + `LoadBuiltinTrust` `:95-104`），内置高危插件（如 `com.lantern.music`）可正常加载；
   清单**缺失/损坏/schema 不符** → 退回「一律不信任」（安全兜底，行为与旧版逐字节一致）。第三方仍无豁免（见 §3.6）。

3. **manifest 权限声明「解析但不强制」**。
   `PermissionChecker.FromManifest` 解析出 `GrantedPermissions` 并存入 `LoadedPlugin`，
   但 `PluginHost.BuildContext` **不按权限过滤能力**——插件拿到的是完整的 `Capability`。
   CHANGELOG v0.1.82 P1-1 已明确记录「宿主当前不按 manifest 过滤 `Storage` 能力」，
   因此「未声明也能用」是**当前事实**，声明失真**功能上不报错**。→ 文档按要求仍写「必须如实声明」。

4. **`ui.settings` 不参与设置页展示判定**。
   `SettingsWindow` 只按 `ISettingsPage.Title == Id/Name` 匹配，不读 manifest 的 `ui.settings`。
   → 声明仍应如实写（契约正确性），但**不要指望靠它控制显隐**。

5. **`[resources].maxMemoryMB` 未强制**。
   仅作声明，当前无 watchdog 强制终止逻辑。

### B.3 已实测确认的资源 / 生命周期事实（非「不一致」，是插件作者必守）

| 事实 | 结论 | 依据 |
|---|---|---|
| 插件 ALC 回收不了的真根因 | **`System.Text.Json` 反射式序列化插件内定义的类型**（含匿名类型）会在进程级共享缓存里留下强引用 | `--selftest client` 是唯一 `alc=N` 项；`--probe default/owned/ownresolver` 均 N、`writer`=Y |
| 「`static HttpClient` 钉住 ALC」 | **已证伪**（改成可释放单例 + OnUnload Dispose 后 `alc` 仍为 N） | `--probe` / 二分实验；保留为方法论教训（见 §9 踩坑 2） |
| `lunar.dll` 静态缓存 | **已排除** | `--selftest lunar` = Y |
| 宿主 `JsonPluginStorage.Get<T>/Set<T>` | 走**同一条 STJ 反射路径** ⇒ **把自定义类型存过盘的插件会阻止自身 ALC 回收**（宿主无法为未知插件类型提供源生成上下文） | `--probe store` = N |
| 影响面 | 不影响功能，只影响「禁用/启用插件或热重载时的内存回收」，单次量级 ~9MB | 同上 |

> 以上数据由 team-lead 侧的二分实验实测得出（`tests/PluginSmoke` 的 `--selftest` / `--probe` 开关），
> 已被本文 §5.4 与 §9 踩坑 2 采用。

---

## 附录 C：维护约定

> **改宿主 API 的人（包括 AI）必读。** 本文件是**插件 API 的单一权威源**。修改下列内容后，**必须同步更新本文件对应章节**：

| 改了什么 | 必须同步本文件的哪部分 |
|---|---|
| `src/DesktopPet.Core/` 任何接口增删改 | §2 API 参考 + 附录 D 版本历史 |
| `Enums.cs` 的 `PluginPermissions` | §3.3 权限 token 表 |
| `Enums.cs` 的 `PetState` / `PetMood` | §2 / 附录 A.3 |
| `ICapabilityApi.cs` 的 `PetEvents` 常量 | 附录 A.1 事件表 |
| `PluginLogLevel.cs` / `ISystem.Log` | §2.3 入口 3（插件日志） |
| `PermissionChecker.cs` 的映射键（`Map`） | §3.2 / §3.3 权限键表 |
| 新增/移除宿主实际广播的事件 | 附录 A.1 的「是否已广播」列 |
| `PluginTrust.cs` / `builtins.lock.json` | §3.6 信任通道 |
| 新增 `neko-assets/icons/*.png` | §7.1 图标 key 列表 |
| 任何一处核对（即使无签名变化） | 更新**页首表格**的「最后核对日期」与「核对到的宿主版本」 |

**Core API 版本化铁律：公开发布后只增不改。** 已有方法签名不允许删除或改参数；需要变更就新增重载并升 `api` 版本号。

---

## 附录 D：API 版本历史

**Core API 版本 = 1**（自 v0.1.0 起未变）。下表记录**宿主版本演进中影响插件作者的变化**，以及**本文件的核对记录**：

| 日期 | 宿主版本 | API | 变更 |
|---|---|---|---|
| 2026-09-01 | v0.1.0 | 1 | 首个可用版本。`IPetApi` / `IUIApi` / `ICapabilityApi` 三大接口；权限；事件常量；payload 为匿名类型 |
| — | v0.1.1 | 1 | 事件 payload 改**强类型 record**（`MouseMovedArgs`/`PetDraggedArgs`/`PetEdgeArgs`），可跨程序集直接 cast；广播 `app:ready` |
| — | v0.1.5+ | 1 | `ISettingsPage` 接入宿主设置窗口（`Title == Manifest.Id 或 Name`） |
| — | v0.1.48 | 1 | `PetState` 新增 **`Work`**（工作模式）→ 共 9 个状态 |
| — | v0.1.61 | 1 | `IScheduler.Cron` 移除（半成品）；`AppConfig.LogLevel` 日志设施 |
| — | v0.1.80 | 1 | **权限细分**：新增 `file.own`(`FileOwnDir`) / `media`(`MediaKey`)，均为低危；新增**内置信任通道**（`builtins.lock.json`），是 `file`/`process` 的唯一豁免；`PluginPermissions` 底层类型改 **`ulong`** |
| 2026-09-11 | v0.1.82 | 1 | 逐项对照源码复核（跨 21 个宿主版本）：补 `file.own`/`media` 权限、补信任通道、补 8 个图标 key、修正 config 键名（`DisabledPlugins`）、**更正 `OnTick` 为「宿主未调用」**、明确其替代方案为 `Scheduler.Every` |
| 2026-09-19 | v0.1.83 | 1 | **新增插件日志通道**：`PluginLogLevel` 枚举 + `ISystem.Log(level, msg, ex?)`（🔴 只增不改）；宿主加 `[插件:<id>]` 前缀、`Debug`/`Info` 默认不落盘、永不抛 |

**已兑现（历史「待改进」项，现已完成）**：
- ✅ 事件 payload 改强类型 record（v0.1.1）
- ✅ 广播 `app:ready`（v0.1.1）；`app:wake`/`app:sleep`/`screen:change`/`tick` **仍为预留未广播**
- ✅ 设置页插件自定义承载（v0.1.5+ 已接入）
- ✅ **插件日志 API**（v0.1.83：`ISystem.Log`；历史「四入口含 Log」的规划以「既有 `ISystem` 加成员」落地，未新增 `IPluginContext` 成员）
- ✅ 内置信任通道**接线**（v0.1.82：`PluginHost.LoadBuiltinTrust`）

**已知仍有出入/未接线（供维护者注意）**：
- `IPetPlugin.OnTick`：**声明但宿主未调用**（保留契约，勿依赖）。
- `PetEvents.AppWake/AppSleep/ScreenChange/Tick`：**预留，未广播**。

---

*文档版本：v2.0（合并插件手册）｜ 基于宿主 v0.1.83 源码与 dev-log/CHANGELOG 撰写；踩坑 2 根因按实测二分结论修订。*

