# 琥珀行走动画（v0.8.4）

2026-10-29。使用内置 imagegen 工具绘制。保留琥珀朝右的手绘橘猫外观，替换原来腿部变化不足的四帧行走素材。最终文件为 `neko-assets/amber/neko_walk.png`，仍是横向四帧、每帧 256×247，不改变其他动作。

## 素材与整理

整张图集的第三格没有生成足够清楚的反向落脚，因此单独绘制反向关键姿态，选入最终第三帧。不是把图片的细微变化或散列差异当成步态合格。最终包含：前伸落脚、抬脚经过、反向落脚、另一侧抬脚经过。头部与身体注册仍是人工目视校准，后续可继续细修。

选用的原图保存在 Git 忽略的 `neko-assets/_airaw/`：

| 文件 | SHA-256 |
| --- | --- |
| amber-walk-v2.png | a9b62c02dd736028a3a4927d49d8bb6544cbf1a2fb5c95d4ffda57055713ae35 |
| amber-walk-contact-b.png | c7f63b359899816ff84de45b443135c90129243a42bc370643eddad6470dd390 |

背景 RGB 虽含棕色，检查背景像素的 alpha 为 0；不可只根据生图预览的 RGB 外观判断透明失败。整理时保留生成的 alpha，不按颜色抠图。工具只分格、缩放与底部对齐，不绘制或变形腿部。行走各帧按最大 238×178 的内容范围统一显示大小，落脚底部对齐源图 y=241；单独原图也按相同规则缩放，避免整只猫突然变大。

```powershell
./tools/pack_amber_animation.ps1 -ActionSet Walk `
  -Source neko-assets/_airaw/amber-walk-v2.png `
  -ContactBSource neko-assets/_airaw/amber-walk-contact-b.png
```

原 IdleWork 用法仍保持不变；本轮将它输出到独立目录，与现有待机、工作和缩略图逐字节比较，三者一致。

## 接入与验收

- walk 声明 facing=right，向左移动时运行时镜像全身，回到待机后使用原待机朝向。
- 6fps、四帧、walkStride=12.5。按前伸到回收落脚位置的约 50 源像素半周期位移，取整周期约 100 源像素，即 12.5 逻辑像素作初始校准。普通情绪、4 倍率时速度为 12.5×4×6÷4=75 DIP/s。
- 校准只改善平均步幅和循环速度的一致性，不保证每一接地时刻完全无滑移。四帧关键姿态不等于连续骨骼动画。不得把这些参数当成其他皮肤的通用步幅。
- 自动回归检查四帧左右逐像素镜像、透明边距、接地基线、大小/情绪变化下的速度，以及离开行走后的朝向恢复。动画质量仍需默认大小目视检查。
- NEKO_CAPTURE_AMBER_WALK 可让 ReliabilityProbe 导出真实渲染器的双向移动 GIF，固定地面刻度用于观察接地与位移。预览不修改用户配置，工作模式下仍保持原使用方式；需要自动走动时由用户启用原开关。

## 选用原画的最终提示词

内置工具，两次均设置 transparent_background=true。

图集修订（输入为以旧 amber/neko_walk.png 与当前 neko_idle.png 为参考生成的四格草稿）：

> Edit target: the supplied 2x2 orange kitten walking sprite sheet. Keep this exact cat identity, face, proportions, orange/cream palette and hand-drawn rendering. Two precise corrections required: (1) REMOVE ALL brown gradient, halo, ground and backdrop. Background pixels must have ALPHA ZERO, actual transparent PNG, with no colored glow, no checkerboard drawn. No cast shadows. (2) Correct the BOTTOM LEFT cat (frame3) to be the OPPOSITE footfall of TOP LEFT: its NEAR FRONT LEG must extend BACK toward the LEFT with its paw grounded underneath chest, and its NEAR HIND LEG must extend FORWARD toward the RIGHT with its paw grounded beneath the belly. Its FAR FRONT LEG extends forward right, FAR HIND extends back left. This must read as the reversed stride, not another copy of top-left pose. TOP RIGHT and BOTTOM RIGHT are opposite passing poses with visibly raised paws. Keep four equally sized cells, consistent right-facing side view, fixed head and body registration, fixed ground baseline, exactly four anatomically attached legs per cat, all extremities inside each cell with generous clear margins. Preserve everything else. Deliver the 2x2 production sheet on real transparent alpha.

单独反向落脚帧（输入为旧 amber/neko_walk.png，结果替换图集第三格）：

> Use case: identity-preserve. Draw ONE animation keyframe of the EXACT orange kitten in the reference, not a sheet, just one whole kitten facing RIGHT in side view, same hand-drawn orange tabby, large brown eye, cream muzzle and paw tips, happy little face, upright curled tail. This is the OPPOSITE LEG POSITION from the reference: the FORELEG CLOSEST TO VIEWER points diagonally BACKWARD to the LEFT, with its cream paw touching ground at the middle of the belly. The HIND LEG CLOSEST TO VIEWER points diagonally FORWARD to the RIGHT, with its paw touching ground just under the middle belly, slightly left of the near front paw. The far front leg reaches FORWARD RIGHT to ground, the far rear leg reaches BACK LEFT to ground, in darker orange. Exactly four attached legs. Cat's head and body keep the exact same silhouette and proportions as the reference, only the leg articulation changes. Do not draw the near front leg reaching right: it must sweep BACK LEFT. Normal relaxed walking contact, not a leap. Fully visible from tail tip to frontmost paw, generous 10% transparent padding. Genuine alpha transparency, no backdrop or shadow, no text or labels. ONE CAT ONLY.

另一版整图合成未采用；最终选择上述图集的第一、二、四帧及单独反向关键姿态。本轮没有更新睡醒、趴下和抚摸收势。
