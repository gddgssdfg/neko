# 琥珀入睡、苏醒与抚摸收势（v0.8.5）

2026-10-29。本批使用内置 imagegen 工具，以琥珀当前的待机、睡眠、抚摸、工作和坐姿图为参考绘制八个中间姿态。新增五张横向四帧 PNG，每帧 256×247：

| 动作文件（neko-assets/amber/） | 来源帧 | 中间姿态（图集零起始编号） | 目标帧 | 播放时长 |
| --- | --- | --- | --- | --- |
| neko_sleep_enter.png | sit[0] | 0、1：趴低、蜷起 | sleep[0] | 0.5 秒 |
| neko_wake_idle.png | sleep[0] | 1、0：展开、抬头 | idle[0] | 0.4 秒 |
| neko_wake_work.png | sleep[0] | 2、3：抬头靠近键盘、起身 | work[1] | 0.4 秒 |
| neko_pet_end_idle.png | pet[0] | 4、5：收回歪头、回正 | idle[0] | 约 0.33 秒 |
| neko_pet_end_work.png | pet[0] | 6、7：转向键盘、双爪放下 | work[1] | 约 0.33 秒 |

时长为普通情绪倍率，随现有情绪播放倍率变化。sleep_enter 接在自然的 sit → sleep 路径上；没有给 work → sleep 或任意姿态之间补全过渡，不宣称覆盖所有状态组合。

## 素材整理

源图为 Git 忽略的 `neko-assets/_airaw/amber-transitions-v1.png`，SHA-256：`7d3a8247650b712283fb462374cca5f4d87296f72239d6c0303fb3c58c50c9c4`。

```powershell
./tools/pack_amber_transitions.ps1 -Source neko-assets/_airaw/amber-transitions-v1.png
```

工具根据 alpha 空白寻找列间和每列上下两排的分隔，避免机械对半切到耳朵或键盘；若找不到清楚间隙则拒绝整理。中间姿态做等比缩放与底部对齐，不重画五官、不变形身体、不按颜色抠背景。每组首尾帧使用 BGRA 行复制，避免经过绘图混合产生透明边缘的颜色舍入。工作目标使用既有 typingRestFrame=1，休息时不会以抬爪姿态收尾。原九组基础 PNG 未改写。

## 运行与验收

- skin.json 声明五组附加动作与五条路线，复用现有可中断过渡机制，无新增计时器、Core 状态或插件接口。
- 重复请求同一目标不会重新播放过渡；拖拽、再次抚摸、改目标或换皮肤会撤销旧目标。插件 Loop=false 保持直接单次播放。
- ReliabilityProbe 比较首尾像素与原动作的对应帧，并检查五条路线完成、工作休息帧、重复请求和交互中断。
- 设置 NEKO_CAPTURE_AMBER_TRANSITIONS 后，测试可导出默认 4 倍率的实际渲染 GIF。各片段独立展示 source → transition → target，50ms 采样，不改用户配置。工作台目前仍只能单独播放每个动作，运行时串联预览另行开发。
- 四帧关键姿态改善了直接换图的突兀感，仍需持续试用细修五官、尾巴位置和键盘衔接；不等于完整连续骨骼动画，也没有替其他皮肤补画。

## 最终提示词

内置 imagegen，transparent_background=true。依次附带 amber 的 idle、sleep、pet、work、sit 图作为参考。

> Use case: identity-preserve. Production intermediate animation poses for Amber, the EXACT cute hand-drawn orange tabby kitten in the supplied references. Reference 1 = neutral seated character identity, reference2 = curled sleeping destination, reference3 = happy tilted-head petting pose, reference4 = brown keyboard working pose, reference5 = seated tilted-head starting pose. Preserve head size, orange/cream palette, forehead stripes, large brown eyes, fluffy outlines, short limbs and character identity. Output ONE transparent atlas with FOUR equal columns and TWO equal rows, exactly EIGHT fully separated kitten sprites. No text/grid or backdrop, true transparent alpha. In reading order: TOP ROW column1: cat lowering from seated into a crouch, elbows bent, head dipping toward front paws, sleepy half-closed eyes, tail beginning to curl around body; column2: cat nearly curled on ground, chest very low, cheek close to front paws, closed eyes, tail wrapping beside body, intermediate before the exact curled sleep reference; column3: waking kitten with head lifted from curled resting body, sleepy half-open eyes, front paws reaching toward a BROWN KEYBOARD immediately in front, chest still low; column4: kitten almost upright in the exact working silhouette behind the keyboard, both front paws lowered onto keys, eyes opening, tail settling right. BOTTOM ROW column1: cat recovering from the happy petting head tilt, head still tilted about 15 degrees, relaxed closed-smile eyes, seated body, NO keyboard; column2: head nearly upright, soft half-open eyes, seated body and cream chest like neutral reference, NO keyboard; column3: recovering from petting into work, head slight happy tilt, shoulders leaning forward, front paws reaching toward brown keyboard, tail passing behind body; column4: upright working kitten with both paws resting on brown keyboard, head almost neutral with relaxed half-open eyes. Do NOT put keyboard in top1/top2/bottom1/bottom2. For work poses keep the same compact brown keyboard design, camera and perspective as ref4. All paws remain anatomically attached. Same source scale of head in every cell; crouching cats MUST occupy less height than upright cats, do not enlarge their heads to fill cell. Ground baseline consistent, 10% padding so ears/tail never clip. No hands touching cat, no hearts, no floating objects, no labels, no glow or cast shadow. Deliver usable animation art, not a mockup.
