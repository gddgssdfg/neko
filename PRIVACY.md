# Neko 隐私说明

最后更新：2026-10-28。适用于本仓库主程序及内置插件（Windows 10/11）。自行安装的第三方插件可能有不同的数据行为，见下文。

偏好导出由用户主动触发，包含外观、称呼、互动和热键，不包含插件数据、凭据或未知扩展字段。导入在下次正常启动应用前，将原配置备份到本机数据目录；原配置备份可能含扩展字段，不应未经检查公开。安全模式跳过插件、自动更新和待导入设置。安静模式只抑制宿主气泡与托盘提醒，不会自动停掉已开启的剪贴板记录或其他插件任务。

Neko 的动画和本地工具可离线运行。数据默认保存在本机；自动更新检查、天气、AI 咨询和网页搜索会按各自触发条件联网。没有 Neko 用户账号、广告或向作者发送的使用分析/遥测。

## 键盘与鼠标

程序运行时安装 Windows 低级键鼠钩子，用于全局热键、键帽/鼠标实时反馈、打字点击动画和本地活跃时长提醒。鼠标坐标还用于眼神跟随等交互。按键码、鼠标坐标及逐次输入事件在内存中即时处理，不保存原始输入、不还原输入文本，也不上传到服务器。

设置中的键鼠映射、反应动画、眼神跟随可分别关闭；关闭这些效果并不卸载全局热键所需的钩子。退出程序会卸载钩子。主程序和内置插件不向其他进程注入代码或读取其内存，不伪造键盘/鼠标输入。

开启气泡提醒时，`host/activity.json` 会保存日期与当天累计活跃分钟，用于本地休息提醒；不包含按键、文字或逐次鼠标轨迹。它与原始输入记录是不同的数据。

## 专注与待办联动

番茄钟的当前阶段、剩余时间、暂停状态、本次完成轮数和关联待办标题只在进程内存中使用；不上传、不额外写入日志或专注历史。关闭计时面板不会停止计时；退出程序或卸载插件会丢弃当前进度。时长偏好保存在番茄钟插件本地存储，待办本身仍按待办插件的规则保存在本机。结束通知不包含待办标题。

## 皮肤创作

皮肤工作台的锚点草稿只在内存中处理。用户点击保存时写回所选皮肤目录的 skin.json，并将完整原清单备份在本机数据目录 `host/skin-backups/`；备份包含原清单中的自定义字段，不上传，不包含 PNG 副本。导出皮肤包只写入用户选择的新 ZIP 文件。当前草稿不跨重启恢复。

## 本地数据与保存位置

数据根目录按以下顺序选择：

1. 设置了 `NEKO_DATA_DIR`：使用指定目录。
2. 程序目录有 `portable.flag`：使用程序目录下的 `data/`。
3. 默认使用 `%AppData%\Neko`；无法取得该目录时才回退到程序目录。

在「设置 → 关于 → 数据目录」查看实际路径。旧版程序目录的 `data/` 可能留有迁移前副本。

| 数据根下的位置 | 内容与保存方式 |
|---|---|
| `config.json` | 主题、热键、皮肤、大小、插件启用状态等配置 |
| `host/activity.json` | 日期与累计活跃分钟，不含输入内容 |
| `plugins/<插件ID>/` | 备忘录、待办、剪贴板历史、出生资料、AI 对话等插件数据；普通插件数据为本地 JSON，并非统一加密 |
| `plugins/com.lantern.bazi/` | 用户填写的 DeepSeek API Key 使用 Windows 当前用户 DPAPI 加密；加密失败时拒绝保存。该保护不代表同用户下恶意程序无法访问数据 |
| `logs/` | 本地诊断日志，默认 warn 级，按天/大小滚动，自动清理过期日志；不自动上传 |

`neko-assets/` 与插件 DLL 所在的 `plugins/` 是程序目录中的运行文件，区别于数据根下的插件数据。

日志可能包含错误消息、堆栈、本机路径及插件标识。内置通知只记录固定状态，不记录通知标题或正文。分享日志、截图或配置前，请自行检查个人信息；第三方插件可自行产生日志。

## 剪贴板历史

**剪贴板插件默认加载，但不读取或记录剪贴板。** 仅在首次启动勾选，或在「插件 → 剪贴板」/剪贴板窗口明确开启「记录剪贴板」后监听变化；开启后不以历史窗口是否打开为前提。它会把文本保存到本地插件数据中，最多保留最近 20 条去重记录（单条最多 32,768 字符）；重启后可继续读取。默认 7 天过期，可改为 1 天、30 天或永久保留。插件加载、读取历史及运行期间每分钟会清理过期内容；程序退出或插件禁用期间不执行清理，下次加载时再清除。密码、验证码、令牌和其他敏感文字也可能被记录，当前没有自动识别敏感内容的过滤机制。

在剪贴板窗口/插件设置关闭记录会注销监听和轮询；也可禁用整个插件。暂停不会删除已有历史；「清空全部历史」会删除磁盘历史。旧版历史会迁移并保留，无法确定的旧记录时间从迁移时计算，未明确同意的旧配置默认暂停记录。要手工删除历史，先禁用插件并退出程序，再删除数据根下该插件目录（`plugins/com.lantern.clipboard/`；以插件清单 ID 为准）。不要把用户数据目录提交到公开仓库。

## 网络访问

所有网络服务都可能收到正常请求所需的 IP 地址等连接信息。Neko 不控制第三方服务如何处理数据。

| 功能 | 目标与发送内容 | 触发与关闭方式 |
|---|---|---|
| 版本检查 | `api.github.com` 仓库公开 Releases API，失败时尝试 `gitee.com` Releases API；不发送用户配置、剪贴板或输入内容 | 首次启动可选择自动检查（默认不勾选），旧配置保留原设置；启用后每天首次启动延迟检查；「设置 → 关于」可关闭自动检查或手动查询。下载页由浏览器打开，不静默安装 |
| 天气 | `geocoding-api.open-meteo.com` 接收输入的城市名，`api.open-meteo.com` 接收对应经纬度 | 主动查询天气/预报时访问；禁用天气插件可关闭 |
| 命理 AI 咨询 | `api.deepseek.com/chat/completions` 接收命盘、问题和用于上下文的历史；用户 API Key 作为鉴权凭据发给 DeepSeek | 主动发送咨询消息时访问；禁用插件或不发送消息即可避免请求。API Key 不发送给 Neko 作者 |
| 快速搜索 | 默认浏览器打开 `https://www.baidu.com/s?wd=<关键词>`；搜索词进入地址及浏览器历史 | 主动搜索时触发；禁用搜索插件可关闭 |

AI 对话历史还会保存在本机（最近 10 轮）。涉及出生资料或其他个人信息时，请先确认愿意发送给 AI 服务提供方。

本表针对当前主程序和内置插件，不涵盖你另外安装的插件或系统/浏览器自身的联网行为。仓库在 GitHub 和 Gitee 同步，并不表示程序同时向两处发送版本查询；当前优先 GitHub，失败时尝试 Gitee；不访问私有仓库凭据，也不内置作者令牌。

## 其他系统能力与第三方插件

- 开机自启：在设置中主动开启时写入当前用户启动项；可在设置中关闭。
- 截图：主动调用截图功能时读取屏幕，并按操作复制或保存图像。
- 托盘、媒体控制等：用于对应的本地工具功能；以当前用户权限运行，不安装驱动。
- 插件 DLL 在宿主进程中执行。权限声明、API 校验与加载上下文有助于管理插件，但**不是操作系统安全沙箱**。只安装可信来源的插件；禁用会停止后续加载/执行，不自动清除已保存数据。

## 删除数据

1. 如不再使用，先在设置中关闭开机自启，再退出 Neko。
2. 打开之前在「设置 → 关于 → 数据目录」确认的数据根，按需删除单个插件目录或整个数据根；删除整个数据根会清除设置、插件数据、API Key、对话和日志。
3. 如曾使用便携模式、`NEKO_DATA_DIR`、旧版迁移或手工备份，还需分别检查那些目录；卸载或删除程序文件不保证清除这些副本。
4. 本机删除不会撤回已发送给第三方服务的数据，也不会自动清除浏览器历史、系统剪贴板或用户另存的截图/导出文件。

## English summary

Neko's animation and local tools work offline. Data defaults to `%AppData%\Neko`; `NEKO_DATA_DIR` overrides it, and `portable.flag` selects the program's `data/` directory. The actual directory is shown in Settings → About.

- Raw keystrokes and mouse trajectories are not persisted or uploaded. Local activity reminders can save a date and aggregate active minutes.
- Clipboard recording is off by default and only starts after explicit opt-in in onboarding or plugin settings. It persists up to 20 recent entries locally (32,768 characters each), with 7-day default retention (1/7/30 days or forever). Sensitive copied text is not automatically filtered. Pausing unregisters the listener but keeps history; Clear deletes persisted entries. Old history migrates without automatically opting in.
- Notes, profiles and AI chat history are ordinary local plugin data, not all encrypted. The saved DeepSeek key is protected with Windows current-user DPAPI.
- When enabled, automatic update checks contact public GitHub Releases first, then Gitee on failure. Onboarding leaves automatic checks unchecked; existing preferences are preserved. Explicit weather requests contact Open-Meteo; sending AI chat contacts DeepSeek with chart data, conversation context and the user's API key for authentication; web search opens Baidu in the default browser.
- Logs remain local but may contain exception messages and paths. Review them before sharing.
- Third-party DLL plugins run in-process with user privileges; the plugin API is not a security sandbox.
- Exit Neko before deleting the actual data root. Also check portable/legacy/custom locations and backups. Local deletion does not erase data already sent to third parties.

本说明随功能变更更新。安全问题的报告方式见 [SECURITY.md](SECURITY.md)。
