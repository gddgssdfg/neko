# Neko 插件目录

## 目录约定

```
plugins/
└── <plugin-id>/              # 每个插件一个文件夹，文件夹名 = 插件 id
    ├── plugin.toml           # 清单（必需）
    ├── <Entry>.dll           # 编译产物（必需，文件名在 plugin.toml 的 entry 指定）
    └── assets/               # 可选，插件自带资源
```

**约定要点：**

1. **文件夹名必须与 `plugin.toml` 里的 `[plugin].id` 一致**，宿主用它做唯一标识
2. 宿主启动时扫描 `plugins/` 下的**每个一级子目录**，读 `plugin.toml`，加载 `entry` 指定的 dll
3. 子目录里没有 `plugin.toml` 的会被跳过，并在日志里提示（不报错、不影响其他插件）
4. `_` 开头的目录会被跳过（临时放东西用）

## v1：手动放文件夹

```
1. 拿到插件文件夹（或把 .npet 解压开）
2. 整个丢进 plugins/
3. 重启 Neko（或在设置里点「重新加载插件」）
```

## v2 规划：拖拽安装

把 `.npet` 拖到 Neko 身上 → 自动校验签名 → 解压到 `plugins/<id>/` → 热加载。

**关键：加载器不需要改任何代码。**
拖拽安装只是「把压缩包解压到 `plugins/<id>/」这一步文件搬运，之后走的还是同一套目录扫描逻辑。这就是目录约定要先定死的原因。

---

## 插件数据放哪（重要）

**不要往自己的插件目录里写数据。**

插件目录应该始终保持「可整体覆盖替换」的干净状态——这样以后更新插件时直接覆盖整个文件夹，不会丢用户数据。

插件私有数据统一写到：

```
data/plugins/<plugin-id>/
```

宿主通过 `ICapabilityApi.Storage` 暴露，插件无需关心真实路径：

```csharp
ctx.Capability.Storage.Set("lastRemind", DateTime.Now);
var t = ctx.Capability.Storage.Get<DateTime>("lastRemind");
```

每个插件的 Storage 按 `plugin.id` 做命名空间隔离，插件之间互不可见。

---

## 启用 / 禁用

**v1 用配置文件控制，不移动、不改名文件夹。**

`data/config.json`：

```json
{
  "disabledPlugins": ["com.example.noisy-plugin"]
}
```

为什么不用「改名加 `.disabled` 后缀」或「移到 `_disabled/` 目录」：
- 移动/改名会破坏插件自更新（下次更新找不到原目录）
- 配置方式可逆，不碰文件系统结构，也不影响插件自身的文件布局

禁用后插件 dll 不会被装载，其注册的径向菜单槽位、托盘项、设置页会自动撤销。

---

## 写一个插件

📘 **完整教程：[`docs/plugin-authoring-guide.md`](../docs/plugin-authoring-guide.md)**

这份手册是**自包含**的：完整 API 签名、权限表、事件表、可编译示例、排错清单都在里面。
**贴给任意 AI 就能让它按手册生成插件。**
宿主 API 一旦变更，只需同步更新这一份手册（维护约定写在手册第 0 节）。

快速入口：

1. 复制 `_template/plugin.toml` 到 `plugins/<your-id>/plugin.toml`
2. `dotnet new classlib -f net8.0-windows` → 引用 `DesktopPet.Sdk` → 继承 `PluginBase`
3. 编译后**只拷「插件自己的 dll + plugin.toml」**到 `plugins/<your-id>/`（不要拷整个 `bin/`，会带上 Core/Sdk 的 dll）
4. 重启 Neko

最小骨架：

```csharp
using DesktopPet.Core;
using DesktopPet.Sdk;

public class MyPlugin : PluginBase
{
    public override string Id => "com.yourname.myplugin";   // 必须与文件夹名一致
    public override string Name => "我的插件";

    public override void OnLoad(IPluginContext ctx)
    {
        base.OnLoad(ctx);   // 必须第一行调用，否则 Pet / UI / Capability 为 null
        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "hello", Label = "打个招呼", Icon = "wave", Order = 10,
            OnInvoke = () => Pet.Say("喵！"),
        });
    }
}
```

---

## 内置插件

**插件型内置**（源码在 `builtins/`，主解决方案编译，产物自动部署到 `plugins/`）：

- `com.lantern.affection` 好感度 —— 抚摸积累好感度，升级触发不同反应。**这是 dogfooding 示范**，完全走第三方插件的加载路径（ALC 隔离 + 权限校验 + 事件订阅），证明 Core API 对第三方插件同样完备。

**宿主内置能力**（v1 实现为宿主渲染/物理/输入层，不作为插件）：

- 眼神追踪 —— 眼球跟随鼠标方向（渲染层反馈，每帧低延迟）
- 边缘吸附 —— 靠近屏幕边缘贴边悬挂（Climb 动画）
- 键盘映射 —— 全局热键 Ctrl+Alt+N 呼出径向菜单（WH_KEYBOARD_LL 钩子）

> 说明：眼神追踪、边缘吸附、键盘映射依赖渲染器、窗口物理和全局输入钩子，这些属于宿主底层能力。插件 API 面向「扩展功能」，它们作为宿主内置更合理。未来若需开放给第三方，可扩展 Core API 暴露全局输入事件。
