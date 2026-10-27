# Neko 图标母版

2026-10-27 使用内置 ImageGen 生成原创橘猫头像，再通过 `tools/build_brand_icons.py` 做尺寸导出和 ICO 封装。未使用 CLI/API 后备方式。

`neko-icon-source.png` 是生成母版；`neko-*.png` 为各尺寸输出。运行时只部署根目录 `app.ico` 和 `tray.ico`，两者字节相同。程序内嵌 app.ico，窗口默认使用嵌入资源，托盘加载适合系统小图标尺寸的帧，MSI 快捷方式和卸载列表也使用 app.ico。

导出尺寸：16、20、24、32、40、48、64、128、256。64 像素以上使用同一像素网格进行最近邻放大；保留透明通道。导出脚本同时生成忽略目录 `_eval/brand-icons-preview.png` 供深浅底核对。

完整生成提示词：

> Use case: logo-brand. Create ONE production Windows application icon for Neko, an original pixel orange tabby desktop companion. Transparent background. A very cute unmistakably feline FRONT-FACING HEAD ONLY: two tall triangular ears, wide round cheek silhouette, warm orange fur, three burnt-orange forehead stripes, cream muzzle, two dark brown clearly separated eyes with tiny highlights, tiny centered pink nose. Pure crisp pixel-art with deliberate square stair-step edges, visually based on a 32 by 32 pixel grid enlarged with nearest-neighbor. Limited 8-color warm palette, dark warm brown outer contour with a thin pale cream outermost keyline so visible on dark AND light taskbars. Large simple facial features readable at 16px. Centered symmetric head fills about 88 percent of square canvas with transparent breathing room, ears fully inside. Head only, no body or paws, no badges, no border tile, no circle, no letters, no typography, no watermark, no realistic fur, no gradients, no 3D, no lighting effects, no cast shadow. Friendly calm expression. This single mark will be the shared identity for tray, taskbar, executable and installer. Output a single icon, not an icon sheet or mockup.

素材沿用根目录 LICENSE-ASSETS 的许可说明。
