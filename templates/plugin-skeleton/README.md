# 插件骨架（plugin-skeleton）

> 可直接复制的最小 Neko 插件模板。配套文档见
> [`docs/plugin-development-guide.md`](../../docs/plugin-development-guide.md)。

## ⚠️ 边界

- **不要**把本目录加进 `src/DesktopPet.sln`。
- **不要**让任何现有 `.csproj` 引用本目录。
- 它只是给人 / 给 AI Agent **复制**用的模板，参与构建会破坏解决方案。

## 怎么用

```bash
# 1) 复制到 plugins/<你的插件id>/
cp -r templates/plugin-skeleton plugins/com.yourname.myplugin

# 2) 改四处名字（保持一致）
#    · 目录名                     = com.yourname.myplugin
#    · plugin.toml  [plugin].id   = com.yourname.myplugin
#    · plugin.toml  [plugin].entry= MyPlugin.dll      （默认即可）
#    · MyPlugin.csproj <AssemblyName> = MyPlugin       （默认即可）
#    · MyPlugin.cs  类名 / Id / Name / RequiredPermissions = 你的内容

# 3) 构建
bash /c/Users/Lantern/neko-dotnet-env.sh build plugins/com.yourname.myplugin/MyPlugin.csproj -c Debug
# 若 csproj 里的 DeployToPlugins target 生效，产物已落在同目录；
# 否则手工把 MyPlugin.dll + plugin.toml 拷到插件目录。

# 4) 重启 Neko（或用设置页的「重新加载插件」，若可用），右键猫看到「打个招呼」即成功。
```

## 骨架里已经帮你避开的坑

| 坑 | 骨架里的处理 |
|---|---|
| `OnLoad` 忘记调 `base.OnLoad(ctx)` → `Pet/UI/Capability` 为 null | `OnLoad` 第一行 `base.OnLoad(ctx);` |
| 定时器不 Dispose → 卸载后仍触发 | `OnUnload` 里 `_timer?.Dispose()` |
| 线程池回调直接碰 UI → 静默失效 | 提供 `OnUi(...)` 封送模板 + 注释说明 |
| 声明与代码不符 | `RequiredPermissions` 与 `plugin.toml` 已对齐 |
| 把数据写进插件目录 | 用 `Capability.Storage`（落 `data/plugins/<id>/`） |

## 复制后请自查

- [ ] 目录名 == `plugin.toml` 的 `id`
- [ ] `entry` == 实际 dll 文件名
- [ ] `RequiredPermissions` 覆盖了代码里用到的每一项能力
- [ ] 用了 `Capability.Storage` → `storage = ["local"]`
- [ ] 注册了 `ISettingsPage` → `[ui] settings = true`
- [ ] `scheduler` / `notify` / `clipboard` / `network` 写在 `other` 段
- [ ] **没有**声明 `file` / `process`
