# Neko 安全说明

最后更新：2026-10-25。适用于当前主程序与内置插件。

## 输入与运行权限

全局键鼠钩子用于热键、实时输入示意与动画。原始按键码和鼠标坐标不写入文件或上传；活跃时长提醒只保存日期及累计分钟，详见 [隐私说明](PRIVACY.md)。退出时卸载钩子。程序以当前用户权限运行，不请求管理员权限，不安装驱动，不向其他进程注入代码。

## 数据、凭据和日志

- 数据默认保存在 `%AppData%\Neko`，支持便携模式及 `NEKO_DATA_DIR`。实际路径可在「设置 → 关于」查看。
- 剪贴板默认不监听，需主动开启后才记录最近 20 条文本，默认 7 天过期。可暂停监听和清空磁盘历史。它可能包含敏感内容，禁用插件不会自动删除旧数据。
- 普通插件数据（包括备忘录、出生资料和对话）并非统一加密。保存的 DeepSeek API Key 使用 Windows 当前用户 DPAPI 加密；加密失败拒绝保存，不回落到明文。
- 主程序不记录原始输入；通知日志只含固定状态，不含通知标题/正文。日志仍可能含错误堆栈、本机路径或插件标识，不自动上传；提交问题前应检查和脱敏。
- 不要公开 `data/`、用户数据根、密钥、签名证书或未经审查的日志。忽略规则不能移除历史里已提交的文件。

## 网络边界

启用自动版本检查时优先访问公开 GitHub Release，失败时尝试 Gitee（首次启动可选择；可关闭）；天气查询访问 Open-Meteo；用户主动发送 AI 咨询时访问 DeepSeek；搜索由默认浏览器打开百度。各功能的触发条件及发送内容见 [PRIVACY.md](PRIVACY.md)。程序不静默安装更新。

## 第三方插件

插件 DLL 在宿主进程内以当前用户权限执行。清单权限、能力 API 和可回收的 AssemblyLoadContext 不构成操作系统安全沙箱；恶意插件仍可能直接调用 .NET 或原生系统 API。仅安装可信插件，审查源码及分发来源。内置信任清单用于校验内置组件，不代表任意第三方插件已通过安全审查。

## 报告安全问题

当前没有专用安全邮箱。请先通过仓库维护者的个人主页确认可用的私下联系方式；若 GitHub 仓库已启用 Private vulnerability reporting，可使用 Security → Report a vulnerability。

不要在公开 Issue、评论或附件中直接发布 API Key、个人数据或尚未修复漏洞的利用细节。无法建立私下联系时，可先提交不含敏感细节的联系请求，等待维护者回复。请提供受影响版本、复现条件和影响范围，并对示例数据脱敏。

- [GitHub 仓库](https://github.com/gddgssdfg/neko)
- [Gitee 仓库](https://gitee.com/gddgssdfg/neko)

## English summary

Neko processes raw keyboard/mouse events in memory, without recording or uploading them. Aggregate active minutes and explicitly enabled clipboard history are local data. User data defaults to `%AppData%\Neko`; see the privacy policy for alternate locations and deletion.

Saved DeepSeek keys use current-user DPAPI, but ordinary plugin data is not uniformly encrypted. Review logs before sharing. Enabled update checks prefer public GitHub Releases with Gitee fallback; weather, AI chat and browser search have the documented network behavior.

Third-party DLL plugins run in-process with user privileges and are not sandboxed. Install only trusted plugins. There is no dedicated security mailbox at present; establish a private channel with the maintainer or use GitHub private vulnerability reporting if enabled. Do not post sensitive data or exploit details in public issues.
