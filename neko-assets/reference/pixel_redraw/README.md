# 像素猫 · 重绘版（pixel_redraw）

日期：2026-10-23。使用内置 image_gen 生成并修订造型，选定原画为 `source.png`；运行时资源位于 `../../pixel_redraw/`。原版 pixel 的身体帧和眼睛素材未覆盖，仅修复了它的设置页缩略图。

## 生成提示词摘要

严格 3×3 透明背景的橘色虎斑猫像素图集：粗实像素块、深可可描边、奶白胸口与爪子、三道额纹；每格留出透明间距。按行依次为 idle、walk、sit、sleep、pet、drag、fall、climb、work，仅 work 带小键盘。整体强调小尺寸轮廓、有限色与硬边。

## 最终修订提示词（原文）

Edit this sprite atlas, preserve the orange tabby design and strict 3x3 arrangement and all nine poses. Fix exactly these production problems: REMOVE EVERY HUMAN HAND (middle row center above pet pose; middle row right above hanging kitten), leaving ONLY THE CAT, with complete ears, no human skin-colored shapes anywhere. REMOVE THE ENTIRE WOODEN POST in bottom center climbing pose, leave only the cat reaching upward with forepaws, no support object. All nine sprites must fit within their own equal 1/3 by 1/3 square cell, centered with generous clear space. Move the first-row right cat left a little so tail never touches the right edge. Shrink all kittens to 65% of cell width and height at most. Crisp flat pixel art using small deliberate square clusters, minimal shading, same dark cocoa outline, orange cream tabby, no gradients or smoothing. Background TRUE transparent alpha, no checkerboard texture, no shadows, no grid or labels. Only allowable prop is tiny keyboard bottom right. Maintain poses row-major idle / walk right / sit tilt / curled sleep / happy closed-eyes pet / dangling surprised drag / paws-splayed fall / reaching climb / typing work.

## 编译与验证

```powershell
python -m pip install -r tools/requirements-art.txt
python tools/ai_sheet_to_skin.py neko-assets/reference/pixel_redraw/source.png neko-assets/pixel_redraw --id pixel_redraw --name "像素猫 · 重绘版" --pixel --pixel-size 64 --pixel-colors 16 --recommended-scale 4
python tools/gen_skin_previews.py
python tests/test_skin_tools.py
dotnet run --project tests/ReliabilityProbe -- --capture _eval/pixel-redraw-ui pixel_redraw
```

编译结果为 64×64 源像素、九状态各四帧整数平移微动；全皮肤使用同一 16 色调色板，透明度仅为 0/255，并预留三像素运动边距。显示推荐值为 4 倍逻辑尺寸，即 128px（每源像素显示为 2×2），使用最近邻采样。运行 PNG 总计 34,846 字节，原画不进入发布白名单。

这是姿态加微动版本；眼睛烘焙在身体图中，尚无独立眼神追踪、独立眨眼或完整的逐帧步态。原版像素猫保留这些既有能力，可继续选择使用。
