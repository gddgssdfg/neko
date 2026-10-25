# 发布流程（v0.4.0 起）

日常开发在 master 提交，通过 `python tools/sync_remotes.py` 同步 GitHub 和 Gitee；Gitee 当前可保持私有，客户端优先使用公开 GitHub Release。

1. 同步修改 App csproj 的 Version 和 CHANGELOG；运行 `python tools/check_version.py`、Python tests 和 `python tools/ci.py`。
2. 提交代码并同步两端。GitHub master / PR 会自动运行 Windows 构建和回归。
3. 准备发版时创建与版本完全一致的 `vX.Y.Z` 标签，再同步两端。GitHub 标签工作流先测试，再使用固定的 .NET 8.0.32 和 WiX 4.0.5 出包。
4. 工作流生成 MSI、便携 ZIP、SHA256SUMS；打包包含源码/素材许可、依赖实际许可及对应运行时 notices，排除用户数据、凭据、日志与开发参考图。
5. 标签推送或手动运行指定版本标签的工作流，都只创建 Release **草稿**。下载附件核对版本、校验值、首次启动和升级体验后，维护者再公开草稿；不把源码 tag、草稿或预发布当成正式安装更新。
6. Git 推送不会同步 Release 附件、Issue 或仓库权限。需要 Gitee 下载镜像时单独上传同一份经过核验的附件，并比较 SHA-256，不重新打另一份包。

本地可运行 `python tools/package_release.py`；先安装 WiX 4.0.5 与资产工具依赖，设置 PythonExe 为实际 Python。产物默认在 `publish/release/v<版本>/artifacts/`。脚本不覆盖已有出包目录，可用 `--output` 指定新目录；`--skip-build` 仅接受脚本此前生成且应用/运行时版本匹配的 BUILD-INFO.json。

更新检查总超时 15 秒，优先 GitHub 正式 Release，失败时尝试 Gitee。无 Release 与网络/限流/解析失败分别显示，取消不报错。Gitee 私有时匿名返回 404，不向用户分发作者令牌。

当前发布运行时固定在 .NET 8.0.32（微软官方 2026-10-13 补丁）。更新此固定值时应核对微软发布元数据并重新测试，不因开发机 SDK 较旧而回落到旧运行时。

历史已清理的旧克隆应重新克隆，避免将撤回素材带回公开分支。完整迁移备份只保存在本机忽略目录，不随源码发布。
