# Neko · 像素橘猫桌面伙伴

[English](README.en.md) · [GitHub](https://github.com/gddgssdfg/neko) · [Gitee](https://gitee.com/gddgssdfg/neko)

**Neko 是一款面向 Windows 10/11 的桌面宠物与工作陪伴软件。** 一只可互动的像素橘猫常驻桌面，陪你打字、处理待办、专注工作和适时休息；也支持更换皮肤、接入插件和制作自己的外观。

*Neko is an interactive pixel orange cat for Windows, with focus timers, to-do lists, keyboard and mouse feedback, custom skins, and plugins.*

![Neko 像素橘猫](neko-assets/pixel/thumb.png)

## 可以用它做什么

| 场景 | 软件提供的功能 |
| --- | --- |
| 桌面陪伴 | 抚摸、拖动、眼神跟随、动作切换和贴边互动，让猫陪在桌面一角。 |
| 打字与鼠标反馈 | 显示键鼠面板，实时反馈按键与鼠标活动；猫和键盘大小可分别调整。 |
| 专注与休息 | 番茄钟支持暂停、继续和手动休息计时；锁屏或休眠时暂停，回来手动继续。 |
| 整理小事 | 用待办清单和便签记录事项；选中未完成待办即可发起专注，完成计时不会自动勾选任务。 |
| 按自己的节奏使用 | 可切换工作/摸鱼模式、开启安静模式、找回屏幕外的猫，并备份外观和互动偏好。 |
| 定制与创作 | 导入皮肤与插件；在皮肤工作台中播放、逐帧检查动作、编辑眼层锚点、模拟视线与遮罩，备份保存后校验并导出皮肤包。 |

项目包含 18 个内置插件，覆盖专注、待办、便签、天气、系统信息等工具。可按需管理；剪贴板记录与自动更新检查在首次引导中默认不勾选。

## 开始使用

**系统：Windows 10/11，x64。** 主程序使用 C#、WPF 与 .NET 8；目前不支持 macOS/Linux。

1. 查看 [GitHub Releases](https://github.com/gddgssdfg/neko/releases) 是否有公开发布的安装包或便携包。源码更新可能早于安装包；若暂无公开包，可按下方说明构建。
2. 启动 Neko，完成首次引导；名字和称呼可以使用默认值。
3. 右键猫咪打开功能菜单，通过托盘进入设置；拖动猫咪调整位置。
4. 从菜单打开专注计时或待办清单；在「设置 → 外观」更换皮肤、调整大小或打开皮肤工作台。

不同皮肤的动画和眼神支持有所不同。「琥珀」支持待机眨眼、跟随键鼠输入的交替敲击、双向行走步态，以及入睡、苏醒和抚摸结束的过渡；其他动作与皮肤仍在逐步完善。

专注进度仅保留在本次运行中；关闭计时面板后继续计时，退出程序或禁用番茄钟插件会清除当前进度。皮肤 ZIP 需先解压，再选择包含 `skin.json` 的目录导入。

## 隐私与扩展

- 桌宠动画、专注和本地工具可以离线使用，不要求 Neko 账号；没有广告或向作者发送的使用遥测。
- 全局键鼠监听用于热键、动画、键鼠反馈和本地活跃时长提醒，不保存原始按键内容或鼠标轨迹，不上传这些输入数据。
- 剪贴板记录需主动开启；开启后内容保存在本机，可暂停、清空并设置过期清理。
- 天气、网页搜索、AI 咨询及自动更新等功能会按各自触发条件联网，详见 [隐私说明](PRIVACY.md)。
- DLL 插件能执行本机代码，当前不是安全沙箱。只安装可信来源的插件；导入后保持停用，由用户手动开启。

用户数据默认保存在 `%AppData%\Neko`；便携包使用程序目录下的 `data/`。实际位置可在「设置 → 关于 → 数据目录」查看。[安全说明与漏洞反馈](SECURITY.md)

## 从源码运行

需要 Windows、.NET 8 SDK 和 Python 3。使用 PowerShell 在仓库根目录执行：

```powershell
git clone https://github.com/gddgssdfg/neko.git
cd neko
$env:PythonExe = (Get-Command python).Source
dotnet run --project src/DesktopPet.App/DesktopPet.App.csproj -c Debug
```

构建会生成并部署内置插件。使用 Gitee 时可替换克隆地址，需要具有该仓库的访问权限。完整构建、测试、素材工具和打包方法见 [开发与维护](docs/development.md)。当前源码版本以 [项目文件](src/DesktopPet.App/DesktopPet.App.csproj) 为准，变更见 [CHANGELOG](CHANGELOG.md)。

## 文档与开发方向

- [专注与待办联动](docs/focus-companion.md) · [常驻、安静模式与恢复](docs/recovery.md)
- [皮肤工作台](docs/skin-workbench.md) · [皮肤制作](docs/skin-development-guide.md) · [动作参数与过渡](docs/skin-motion.md)
- [插件开发](docs/plugin-development-guide.md) · [插件骨架](templates/plugin-skeleton) · [架构](docs/architecture.md)
- [长期路线](docs/roadmap.md) · [发布流程](docs/release-sop.md)

Neko 仍在持续开发中。当前重点是角色动作、低打扰工作陪伴和创作者工具；皮肤工作台已支持预览、锚点编辑、视线与遮罩检查、备份保存与打包，眼眶编辑和运行时过渡串联预览仍在后续计划中。

## 许可

| 内容 | 许可 |
| --- | --- |
| 源代码与项目文本 | [MIT](LICENSE)，允许商业使用。 |
| 本项目有权授权且未另行声明的美术素材 | [CC BY-NC 4.0](LICENSE-ASSETS)，须署名，仅限非商业使用；商业用途需另行授权或替换素材。 |
| 第三方依赖与单独声明的内容 | 遵循各自许可，见 [第三方声明](THIRD-PARTY-NOTICES.txt)。 |

这是一个个人开发项目，按原样提供。GitHub 与 Gitee 同步维护源码；发行附件、Issue 和仓库权限分别管理。
