# Neko 安全说明（Security Policy）

> 适用版本：Neko 桌宠（C# + WPF + .NET 8，Windows 10/11）
> 最后更新：2026-09-12

本文件说明 Neko 的输入监听边界、日志隐私边界，以及安全问题上报方式。

---

## 一、全局键盘 / 鼠标监听的用途与边界

Neko 通过 Windows 低级钩子实现全局输入感知：

- 键盘：`SetWindowsHookEx(WH_KEYBOARD_LL, ...)`
- 鼠标：`SetWindowsHookEx(WH_MOUSE_LL, ...)`
- 实现位置：`src/DesktopPet.App/Services/GlobalInput.cs`

### 用途

1. **全局热键**：径向菜单、显隐老板键、透明度/缩放/重置位置等（命中热键时会吞掉该次按键，避免传递给前台程序）
2. **键鼠映射窗**：把按键/鼠标事件转发给映射窗做实时高亮
3. **反应动画**：打字/点击时触发猫的小动作（内部已做 300ms 节流）

### 边界（Neko 明确做什么、不做什么）

| Neko 会做 | Neko 不会做 |
|---|---|
| 在内存中即时转发按键/鼠标**事件**（按键码、按下/松开） | 记录按键内容、保存文本、做键盘记录 |
| 用按键码驱动 UI 高亮与动画 | 把输入写入文件或发送到网络 |
| 命中热键时吞掉该次按键 | 向其他进程注入代码 |
| 读取修饰键状态（`GetAsyncKeyState`） | 模拟/伪造键盘鼠标输入 |
| 轮询全局鼠标坐标（`GetCursorPos`） | 读取或修改其他进程内存 |
| — | 安装任何驱动、请求管理员权限 |

钩子回调**只做轻量转发**（不做耗时逻辑），`Dispose` 时通过 `UnhookWindowsHookEx` 卸载。

Neko 以 `asInvoker`（当前用户权限）运行，**不请求管理员权限**。

---

## 二、日志隐私边界

Neko 的日志实现位于 `src/DesktopPet.App/Core/Log.cs`，写入 `data/logs/neko-YYYYMMDD.log`（本地，按天滚动，保留 7 天，**从不上传**）。

### 核查结论（2026-09-12 逐点核查全部 `Log.*` 调用点）

- ✅ **`GlobalInput.cs` 中没有任何 `Log.*` 调用**——键鼠事件、按键码、鼠标坐标均**不会进入日志**。
- ✅ **未发现任何落明文按键名、窗口标题、进程路径、设备 ID 的日志调用**。
- ✅ 剪贴板读取/写入失败时只记录**异常消息**，不记录剪贴板内容。
- ✅ 异常日志记录的是异常堆栈与消息，不含键鼠内容。

日志调用点清单（共 11 处，均为异常/状态类信息）：

| 位置 | 记录内容 |
|---|---|
| `App.xaml.cs:31/42/48` | 未处理异常（堆栈） |
| `App.xaml.cs:78` | 开机自启同步失败信息 |
| `Core/Config.cs:114/128` | 配置解析/保存失败信息 |
| `Services/TrayManager.cs:55/136` | 托盘气泡失败、暂停状态 |
| `Services/SystemImpl.cs:23` | **通知的标题与正文**（见下方说明） |
| `Services/SystemImpl.cs:30/36/42` | 剪贴板/打开链接的失败信息 |
| `Services/SchedulerImpl.cs:52/56` | 定时任务失败信息 |
| `Services/PluginHost.cs:89` | 已加载的插件名称 |
| `Services/PluginContextImpl.cs:64` | 插件事件处理异常（含**事件名**，如 `pet:petted`） |

> **待产品决策项（如实上报，未自行修改）**：`Services/SystemImpl.cs:23`
> `Log.Info($"[通知] {title}: {body}")` 会把**通知的标题与正文**写入日志。
> 通知内容由插件自身产生（例如"该喝水了"），**不是键鼠输入**；但若某个插件把用户输入（如待办标题、备忘录内容）当作通知正文，则可能**间接**进入本地日志。
> 当前日志仅在本机、不上传，风险有限。是否改为不落通知正文（或降为 Debug 级），属产品决策，留待团队确认。

---

## 三、数据与网络

- 所有用户数据保存在程序目录 `data/` 下，详见 [PRIVACY.md](PRIVACY.md)。
- 网络访问仅有：命理 AI（`api.deepseek.com`）、天气（`api.open-meteo.com`）、主动搜索（由浏览器打开百度）。**主程序与其余插件不联网**。
- API Key（DeepSeek）由用户自行填写并保存在本机 `data/plugins/com.lantern.bazi/`，**不会被上传给 Neko 作者**。

---

## 四、漏洞上报（Reporting a Vulnerability）

我们重视安全问题。若你发现潜在安全漏洞，请**负责任地**通过以下方式私下联系，不要在公开 Issue 中披露细节：

- **邮箱**：`security@example.com`（占位邮箱，发布前请替换为实际联系方式）

请在报告中尽量包含：受影响版本、复现步骤、影响范围、可能的修复建议。我们会在确认后尽快回复并处理。

---

## English Summary

- Neko installs Windows low-level keyboard/mouse hooks (`WH_KEYBOARD_LL` / `WH_MOUSE_LL`) **only** for global hotkeys, the on-screen input mirror, and small reaction animations. It never records keystroke content, never persists or uploads input, never injects into or simulates input for other processes, and never requests administrator privileges.
- `Log.*` call sites were audited: **`GlobalInput.cs` contains no logging at all**; no keystroke names, window titles, process paths, or device IDs are written to logs. Clipboard content is not logged.
- The only network calls are the Weather plugin (`api.open-meteo.com`), the Bazi AI plugin (`api.deepseek.com`), and opening a search URL in the user's own browser.
- Report vulnerabilities privately to `security@example.com` (placeholder).
