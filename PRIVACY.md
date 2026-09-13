# Neko 隐私说明（Privacy Policy）

> 适用版本：Neko 桌宠（C# + WPF + .NET 8，Windows 10/11）
> 最后更新：2026-09-12

Neko 是一只**完全本地运行**的桌面宠物。本文件说明 Neko 会做什么、不会做什么，以及你的数据存放在哪里、是否会联网。

---

## 一、键盘与鼠标输入

Neko 使用 Windows 低级钩子（`WH_KEYBOARD_LL` / `WH_MOUSE_LL`）监听全局键盘与鼠标事件，用途仅有三项：

| 用途 | 说明 | 是否可关闭 |
|---|---|---|
| 全局热键 | 呼出径向菜单（默认 `Ctrl+Alt+N`）、显隐老板键、调节透明度/缩放/重置位置等 | 热键为产品核心交互，随程序运行 |
| 键鼠映射窗 | 屏幕上显示键盘/鼠标示意图，实时高亮你正在按的键 | 设置页「输入」栏可关闭（`KeyboardMirrorEnabled`） |
| 打字 / 点击反应 | 打字或点击时猫做个小的瞪眼、挥手动作 | 设置页「猫咪」栏可关闭（`KeyboardReactionsEnabled`） |

**Neko 明确不做以下事情：**

- ❌ **不记录按键内容**——不保存你按了哪些键、不记录文本、不做键盘记录（keylogger）
- ❌ **不上传键鼠数据**——所有键鼠事件只在内存中即时处理，不写入文件、不发送到任何服务器
- ❌ **不注入其他进程**——不向任何程序注入代码
- ❌ **不模拟/伪造输入**——不代替你发送按键或鼠标操作
- ❌ **不读取其他程序的内存**——不挂钩、不读写游戏或其他进程

> 技术细节见 [SECURITY.md](SECURITY.md)。

---

## 二、本地数据存放位置

Neko 的全部数据都保存在程序目录下（不写注册表以外的系统位置，不写 AppData）：

| 路径 | 内容 |
|---|---|
| `data/config.json` | 你的设置（缩放、透明度、热键、主题、语言、皮肤、插件开关等） |
| `data/plugins/<插件ID>/` | 各插件的私有数据（例如：备忘录文本、待办列表、番茄钟统计、**命理插件的 DeepSeek API Key**） |
| `data/logs/` | 运行日志（按天滚动 `neko-YYYYMMDD.log`，单文件超 5MB 滚动，自动清理 7 天前旧日志） |
| `neko-assets/` | 内置像素素材（随程序分发） |
| `plugins/` | 插件程序文件（随程序分发，可自行增删） |

**Neko 不含**：用户账号、唯一标识符、跟踪器（tracker）、使用统计、遥测（telemetry）、分析（analytics）或广告代码。

---

## 三、网络访问（逐条列明）

Neko 主程序**正常运行时不需要联网**。只有以下插件在你主动使用时会访问网络：

### 1. 命理助手 · AI 咨询（八字）→ `api.deepseek.com`

- **目标地址**：`https://api.deepseek.com/chat/completions`
- **上传内容**：
  - 在**本机计算**好的命盘信息（生辰对应的四柱干支等结构化信息）
  - 你在对话框里输入的提问
  - 本次会话的对话历史（用于让 AI 保持上下文）
- **凭据**：需要你**自行填写 DeepSeek API Key**（在「命理助手」设置页填写，保存在本机 `data/plugins/com.lantern.bazi/` 下，不会上传给 Neko 作者）
- **触发时机**：仅当你**主动**在命理助手设置页点击「AI 咨询」并发送消息时
- **可关闭**：不使用该功能即完全不联网；命理插件可整插件禁用
- **第三方约束**：你与 DeepSeek 之间的数据受 [DeepSeek 隐私政策](https://www.deepseek.com/) 约束，Neko 无法控制

### 2. 天气 → `api.open-meteo.com` / `geocoding-api.open-meteo.com`

- **目标地址**：
  - `https://geocoding-api.open-meteo.com/v1/search`（把你输入的城市名转成经纬度）
  - `https://api.open-meteo.com/v1/forecast`（按经纬度取天气）
- **上传内容**：你输入的城市名；随后是经纬度坐标
- **凭据**：无需账号、无需 API Key（Open-Meteo 为免费开放接口）
- **触发时机**：插件按你设置的间隔取天气时
- **可关闭**：禁用天气插件即可

### 3. 快速搜索 → 由你的浏览器打开 `www.baidu.com`

- **行为**：Neko 调用系统「打开链接」，由你的**默认浏览器**访问 `https://www.baidu.com/s?wd=<关键词>`
- **说明**：Neko **自身不请求**该网站；请求由你的浏览器发起，受浏览器与百度的隐私政策约束
- **触发时机**：仅当你主动使用搜索功能时

### 4. 其他

除上述三项外，**Neko 主程序与其余内置插件不发起任何网络请求**。Neko 不含远程图片、广告网络或分析服务。

> 校验方式：全项目网络调用点仅集中于 `builtins/DesktopPet.Builtin.Weather`、`builtins/DesktopPet.Builtin.Bazi` 与 `builtins/DesktopPet.Builtin.QuickSearch`。

---

## 四、权限与用途对应

| 系统能力 | 用途 | 何时启用 |
|---|---|---|
| 全局键盘监听 | 全局热键、键鼠映射窗、打字反应 | 随程序运行（反应类可在设置页关闭） |
| 全局鼠标监听 | 键鼠映射窗、点击反应、眼神追踪/边缘吸附 | 随程序运行（反应类可在设置页关闭） |
| 文件读写 | 读写 `data/` 下的配置、插件数据、日志 | 随程序运行 |
| 开机自启（注册表 `HKCU\...\Run`） | 开机自动启动 Neko | 你在设置页开启时 |
| 剪贴板读写 | 剪贴板插件（查看/复制历史） | 你在插件中主动使用时 |
| 网络访问 | 天气 / 命理 AI / 打开搜索页 | 见第三节，均在你主动使用时 |
| 屏幕截图 | 截图插件 | 你主动截图时 |
| 系统托盘 | 显示托盘图标与菜单 | 随程序运行 |

Neko 以**当前用户权限**运行，**不请求管理员权限**，不安装驱动。

---

## 五、数据的删除

- 删除程序目录下的 **`data/`** 文件夹，即可清除**全部用户数据**（设置、插件数据、API Key、日志）。
- 卸载程序不会自动删除 `data/`（避免误删你的备忘录/待办），请按需手动删除。
- 卸载后若曾有开机自启，请在「任务管理器 → 启动」中确认无残留项（正常卸载会移除）。

---

## 六、本说明的变更

若本说明发生实质性变更，将在项目仓库中更新本文件并注明「最后更新」日期。

---

## English Summary

Neko is a **fully local** Windows desktop pet.

- **Keyboard/mouse**: global low-level hooks are used **only** for global hotkeys, the on-screen keyboard/mouse mirror, and small typing/click reaction animations. Neko **never records keystroke content, never uploads input, never injects into or simulates input for other processes**. Reaction features can be disabled in Settings.
- **Local data**: everything lives under the program's `data/` folder (`config.json`, `data/plugins/<id>/`, `data/logs/`). No accounts, trackers, telemetry, analytics, or ads.
- **Network (only when you actively use a feature)**:
  - Bazi AI chat → `api.deepseek.com` (sends locally-computed chart data + your question + chat history; requires your own DeepSeek API key, stored locally).
  - Weather → `api.open-meteo.com` / `geocoding-api.open-meteo.com` (sends the city name you type, then its coordinates; no account required).
  - Quick search → opens `www.baidu.com` in **your** browser (Neko itself does not call that site).
  - No other network calls exist in the app or its other plugins.
- **Deleting data**: remove the `data/` folder to erase all user data.
