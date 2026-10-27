# 开发与维护 / Development and maintenance

[中文首页](../README.md) · [English overview](../README.en.md)

## 环境与构建 / Build prerequisites

Windows 10/11 x64、.NET 8 SDK、Python 3；也可用带 .NET 桌面开发工作负载的 Visual Studio 2022 打开 `src/DesktopPet.sln`，将 `DesktopPet.App` 设为启动项目。

Windows 10/11 x64, .NET 8 SDK, and Python 3 are required. Alternatively, open `src/DesktopPet.sln` in Visual Studio 2022 with the .NET desktop workload and set `DesktopPet.App` as the startup project.

```powershell
$env:PythonExe = (Get-Command python).Source
dotnet build src/DesktopPet.sln -c Debug
dotnet run --project src/DesktopPet.App/DesktopPet.App.csproj -c Debug
```

`PythonExe` 必须指向可工作的 Python 解释器，不能是未安装运行时的商店别名。构建会生成内置插件并部署到输出目录；`plugins/com.lantern.*/`、`bin/`、`bin_ci/` 和 `publish/` 等是构建产物，不入库。

`PythonExe` must point to a working interpreter, not an unconfigured Windows Store alias. Builds generate and deploy the built-in plugins. Generated plugin folders, binaries, CI outputs, and release packages are excluded from version control.

## 目录 / Repository layout

| Path | Purpose |
| --- | --- |
| `src/DesktopPet.App/` | WPF 宿主 / Desktop host |
| `src/DesktopPet.Core/` | 共享接口与事件契约 / Shared API and event contracts |
| `src/DesktopPet.Runtime/` | 插件加载与清单解析 / Plugin loading and manifest parsing |
| `src/DesktopPet.Sdk/` | 插件基类与 UI 辅助 / Plugin base and UI helpers |
| `builtins/` | 18 个内置插件 / 18 built-in plugins |
| `neko-assets/` | 皮肤、图标及 UI 素材 / Skins, icons, and UI assets |
| `samples/`, `templates/` | 扩展示例与骨架 / Extension examples and starters |
| `tests/` | 加载、UI、生命周期与素材回归 / Loading, UI, lifecycle, and asset checks |
| `tools/`, `installer/` | 校验、素材和出包脚本 / Validation, asset, and packaging tools |

## 验证 / Verification

```powershell
python tools/check_version.py
python -m pip install -r tools/requirements-art.txt
python tools/ci.py
```

本地 CI 构建主解决方案和独立测试工程，检查零警告/零错误、插件加载与卸载回收、UI、配置保存和素材工具。UI 探针会短暂打开真实窗口。测试工程不在主解决方案中，单独调试时必须重新构建，避免运行旧二进制。

Local CI builds the solution and separate test projects, then checks warnings/errors, plugin loading and unload collection, UI behavior, configuration saves, and asset tools. UI probes briefly open real windows. Test projects are outside the main solution; rebuild them before running to avoid stale binaries.

针对素材工具单独验证 / Asset-tool checks:

```powershell
python -m unittest discover -s tests -p 'test_*.py' -v
```

素材工具使用 Pillow。生成素材后应检查变化并更新缩略图，不应仅因修改代码就重绘全部素材。

Asset tools use Pillow. Review generated changes and update thumbnails when changing art; ordinary code changes do not require regenerating all assets.

## 提交与同步 / Commits and synchronization

提交前检查 `git status --short` 和 `git diff --stat`。不提交用户数据、日志、凭据、构建产物或本地备份。修改软件版本时同步项目 `<Version>` 与 CHANGELOG；纯文档改动无需为了提交而改软件版本。

Inspect the working tree before committing. Do not commit user data, logs, credentials, build outputs, or local backups. Update the project version and changelog together when changing the app version; documentation-only updates do not need an app version bump.

```powershell
git remote -v
python tools/sync_remotes.py
```

本项目的 `origin` 指向 Gitee，`github` 指向 GitHub。同步脚本先对两端执行 Git dry-run 检查，再推送 master 和标签，不强推、不保存令牌。两端代码推送可能部分成功；失败后先核对远端再重试。Git 推送不会同步 Release 附件、Issue 或仓库可见性，也不会发布安装包。

In this project, `origin` points to Gitee and `github` to GitHub. The sync tool runs Git dry-run checks against both remotes before pushing master and tags, without force-pushing or saving tokens. A network failure can leave only one remote updated; check the remote state before retrying. Git pushes do not synchronize release attachments, issues, visibility, or publish installers.

旧提交中撤回的素材已清理。旧克隆应备份本地工作后重新克隆，避免将已清理历史再次推回。

Withdrawn assets were removed from Git history. Back up local work and re-clone older checkouts rather than reintroducing the removed history.

## 打包 / Packaging

安装 WiX 4.0.5 并满足上述构建环境后 / After installing WiX 4.0.5 and the prerequisites above:

```powershell
python tools/package_release.py
```

具体参数和发布检查以 [发布 SOP](release-sop.md) 与脚本帮助为准。构建产物不是公开 Release；公开前须检查附件和 SHA256 校验文件。第三方依赖许可随包分发。

See the [release procedure](release-sop.md) and script help for options and checks. A local build is not a public release; review attachments and SHA256 checksums before publication. Include the required third-party license notices.
