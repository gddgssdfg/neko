# 琥珀待机与工作动画（v0.8.3）

2026-10-29。使用内置 imagegen 工具，以原来的 amber/neko_idle.png 和 amber/neko_work.png 为编辑参考，保留橘色、额纹、奶油白口鼻和胸口、大眼睛与手绘风格。输出透明四列两行的原画图集。PNG 的透明度来自生图输出，没有按颜色抠背景。

程序使用的素材为 `neko-assets/amber/neko_idle.png`、`neko_work.png` 和 `thumb.png`。待机四帧包含睁眼、轻微胸口和尾巴变化、闭眼、恢复；工作四帧为一侧抬爪、双爪落下、另一侧抬爪、半闭眼双爪落下。工作休息帧为零起始 1，休息眨眼帧为 3。

原图保存在被 Git 忽略的 `neko-assets/_airaw/amber-idle-work-v2.png`，SHA-256 为 `21ccfeac1368ed948e4a5975813254a5b5906a731bf056060bbac68e8e18eb68`。`tools/pack_amber_animation.ps1` 只做分格、统一缩放、底部对齐和缩略图输出，不绘制新动作、不移除背景。该图上下两排之间的实际空隙在高度约 53% 处，因此采用 RowSplit=0.53，避免切到上一排脚掌。

```powershell
./tools/pack_amber_animation.ps1 -Source neko-assets/_airaw/amber-idle-work-v2.png
```

最终生图提示词（内置工具，附带原待机和工作图作为参考，transparent_background=true）：

> Use case: identity-preserve. Production animation sprite sheet for existing desktop companion. Input 1 is the exact idle orange kitten identity and seated pose. Input 2 is the exact same kitten working at its brown keyboard. Preserve the existing hand-drawn cute style, huge dark brown eyes, forehead stripes, cream muzzle and chest, face proportions, orange palette and compact silhouette. Output ONE transparent PNG sheet, exactly FOUR equal columns and TWO equal rows (8 equal square cells), no labels, no grid lines. Each cat fits fully inside its own cell with consistent scale and 8% transparent padding. Keep face and body registration extremely consistent across frames; no camera or lighting changes. TOP ROW four idle animation frames in order: 1 neutral seated open eyes, 2 slight inhaling chest and tail tip curving toward left, 3 BOTH EYES FULLY CLOSED in a natural blink with relaxed upper face, chest settling and tail tip curving toward right, 4 neutral open eyes relaxed seated, tail returning. Keep grounded paws in exactly same place. BOTTOM ROW four working animation frames in order: 1 LEFT FRONT PAW clearly raised above the keyboard while right paw presses keys, 2 both front paws down on keyboard, 3 RIGHT FRONT PAW clearly raised while left paw presses keys, 4 both paws down and a gentle half blink. Keep the KEYBOARD precisely in the same position, same shape in all four bottom cells; same face and body as input2; paw lift must be visibly articulated at shoulder/elbow, not whole cat translated. Tail slight swing. No extra limbs, no text, no background, no shadows outside character, genuinely transparent alpha. This is a usable animation atlas, not a presentation mockup.

验收边界：工作台与真实渲染器均须检查。自动回归覆盖停留、输入启停、休息眨眼、打断和旧皮肤兼容；默认大小及长时间试用观感仍需继续收集反馈。本轮没有独立瞳孔层，不宣称新增眼神跟随，也没有更新步态或其他七个动作。
