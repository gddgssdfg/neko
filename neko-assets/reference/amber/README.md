# 琥珀 · 手绘橘猫

生成日期：2026-10-22。使用内置 image_gen 工具生成并修订，选定原画保存在 `source.png`，运行时资源位于 `../../amber/`。

## 最终编辑提示词

Edit this production sprite sheet. Keep the same adorable orange kitten identity, cocoa outlines, apricot fur, cream belly, all nine poses in their existing row order. CRITICAL FIX: redraw layout with MUCH MORE transparent space around EACH kitten. EXACT 3 columns x 3 rows on square canvas. Every kitten including whiskers and keyboard must lie strictly within the central 60% width and 60% height of its equal square cell. Empty 20% margin at ALL FOUR sides of EVERY cell, especially between the three rows. No part may cross or touch a cell boundary. No neighboring kitten fragment may appear within another cell. Same cat SIZE across poses. Nine poses in order: idle, walking facing right, head tilted seated; curled sleep, happily petted, hanging dangling legs; falling paws splayed, climbing right, typing little keyboard. True transparent alpha background everywhere between characters, NO backdrop, no lines, no text. Use clean flat opaque fills with only gentle shading, no fuzzy semi-transparent stray pixels. Professional asset atlas with generous separation.

## 编译资源

模型输出未完全遵守等分格线，实际按透明间隙设定切分坐标，避免相邻角色碎片串入。

```powershell
python tools/ai_sheet_to_skin.py neko-assets/reference/amber/source.png neko-assets/amber --id amber --name "琥珀 · 手绘橘猫" --max-frame 256 --recommended-scale 4 --grid-x 448,874 --grid-y 440,823
python tools/gen_skin_previews.py amber
```

运行时为 256×247 源像素、九状态各四帧，`pixelArt=false`。当前动画是原画的呼吸、摆动、位移微动，不是逐帧重绘的完整肢体动画；眼睛烘焙在身体图中，没有独立眼神追踪层。原画不进入发布白名单。
