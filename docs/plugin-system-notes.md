# Neko 插件系统 · 已验证的开发备忘

> 2026-09-01 P0 完成，dogfooding 测试抓到了 3 个真实 bug，记录修复方案，供后续开发直接引用。

## 架构（已验证可跑）

```
plugins/<plugin-id>/
├── plugin.toml          # Tomlyn 解析
├── <Entry>.dll          # 插件程序集
└── assets/
```

**加载链路**：扫描目录 → 跳过 `_` 开头 → 解析 manifest → 校验 id 与目录名一致 → 权限授予 → PluginLoadContext(ALC) 加载 → 实例化 IPetPlugin → OnLoad(ctx)

## 三个必踩的坑（已验证修复）

### 坑 1：类型分裂（IsAssignableFrom 失败）

**现象**：插件 dll 在独立 ALC 里，`DesktopPet.Core.dll` 也被塞进插件目录后重新加载，导致插件看到的 `IPetPlugin` 和宿主判断用的 `IPetPlugin` 是**两个不同类型** → `typeof(IPetPlugin).IsAssignableFrom(type)` 返回 false，报「未找到实现 IPetPlugin 的类型」。

**修复**：`PluginLoadContext.Load` 里把 `DesktopPet.Core` / `DesktopPet.Sdk` 路由回宿主已加载实例：

```csharp
private static readonly HashSet<string> HostSharedAssemblies = new() {
    "DesktopPet.Core", "DesktopPet.Sdk" };
protected override Assembly? Load(AssemblyName name) {
    if (HostSharedAssemblies.Contains(name.Name ?? "")) {
        var existing = Default.Assemblies.FirstOrDefault(a =>
            string.Equals(a.GetName().Name, name.Name, OrdinalIgnoreCase));
        if (existing is not null) return existing;
    }
    var path = _resolver.ResolveAssemblyToPath(name);
    return path is null ? null : LoadFromAssemblyPath(path);
}
```

**教训**：任何「宿主 + 插件」共享的接口/模型程序集，插件必须解析到宿主侧，禁止插件自带副本。

### 坑 2：权限段前缀映射

**现象**：manifest `[permissions] pet=["animate","read"]`，但 `PermissionChecker` 用 `pet.animate` 这种完整键查表，数组里的 `animate` 匹配不上 → 权限全部为 None。

**修复**：按段加前缀再查表：

```csharp
result |= ToFlags(m.Permissions.Pet, "pet.");        // animate → pet.animate
result |= ToFlags(m.Permissions.Ui, "ui.");          // register → ui.register
result |= ToFlags(m.Permissions.Storage, "storage."); // local → storage.local
```

Map 键统一用完整键：`pet.animate / pet.move / pet.read / ui.register / storage.local / scheduler / notify / clipboard / network / file / process`。

### 坑 3：宿主没引用 Sdk → 发布形态插件加载失败

**现象**：`PluginLoader` + LoaderTest（源码 bin）测都通过，但真实运行 Neko.exe 时插件 OnLoad 不执行、数据目录不创建。

**根因**：`DesktopPet.App.csproj` 没引用 `DesktopPet.Sdk`。App 输出目录因此**没有 `DesktopPet.Sdk.dll`**。插件 ALC 需要 Sdk（继承 `PluginBase`），但坑1的 `HostSharedAssemblies` 白名单要在宿主 `Default.Assemblies` 里找到 Sdk 才路由回宿主；宿主没加载 Sdk → 找不到 → 插件加载失败。

**为什么 LoaderTest 没抓到**：LoaderTest 通过 ProjectReference 间接引用了 Sdk 项目，它的运行目录**有** Sdk.dll，掩盖了 App 输出目录缺依赖的问题。

**修复**：`DesktopPet.App.csproj` 必须显式引用 `DesktopPet.Sdk`，确保宿主进程加载 Sdk.dll，ALC 才能路由回宿主。

**防复发**：LoaderTest 加「测试4」——直接加载**真实 App 输出目录**的插件（验证 App 只输出 dll+toml 时能否加载），不要只用源码 bin 测。

**教训**：测试环境的依赖装配 ≠ 发布形态。凡是宿主共享程序集，宿主必须真正引用，且测试要覆盖"发布输出目录"。

## 验证方式（无 UI，纯控制台）

`tests/LoaderTest/` 用 `PluginLoader` + 最小宿主实现（TestStorage/TestEventBus/TestPetApi/TestUIApi）验证加载链路，不依赖 WPF 桌面。
四组测试：① 示例插件链路 ② 好感度插件（事件+持久化+升级反应） ③ 全部卸载 ④ 真实 App 输出目录完整链路（加载+OnLoad+Storage）。

## 目录 / 引用关系

```
DesktopPet.App ─┬─→ DesktopPet.Core
                ├─→ DesktopPet.Runtime ─→ DesktopPet.Core
                └─→ DesktopPet.Sdk        ← 必须引用，保证宿主加载 Sdk.dll（坑3）
plugins/* ─────→ DesktopPet.Sdk ─→ DesktopPet.Core
```

- `DesktopPet.Sdk` 是 `net8.0-windows`，`DesktopPet.Core`/`Runtime` 是 `net8.0`（纯库无 UI 依赖）
- 插件目录 `_` 开头跳过；`data/plugins/<id>/` 存插件私有数据（禁止写进插件目录）
- 禁用：`data/config.json` 的 `disabledPlugins` 数组，不改名不移动
