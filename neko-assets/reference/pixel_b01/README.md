# B01 圆头橘猫

2026-10-24，用户在「十只不同外观、相同坐姿和表情、全部橘猫」候选 B01–B10 中选择了 1，即 B01。

- `B01.png` 是内置 image_gen 生成并由用户选定的造型参考，提示词在 `prompt.txt`。
- 正式运行素材由已有的原生网格绘图源 `tools/generate_sprites.py` 重画，输出到 `neko-assets/pixel/`。不是把大图直接当作 32×32 帧，也不承诺与概念图逐像素一致。
- 保留圆脸、短三角耳、小黑眼、奶油色口鼻和胸口、白爪与右侧卷尾。运行图为有限色硬边像素，无渐变或抗锯齿。
- 继续使用原版 pixel 的 32×32、每状态四帧、九状态和五个独立眼层；元数据与帧率由原 skin.json 保持不变。
- 睡眠使用独立闭眼卧姿；其他状态的头部锚点一致，以便独立眼神追踪。行走、攀爬、工作通过逐帧爪位和尾巴变化表现。

复现：

```powershell
python tools/generate_sprites.py
python tools/gen_skin_previews.py pixel
python -m unittest discover -s tests -p test_skin_tools.py -v
```

静态和动画预览在 `_eval/pixel-b01-redraw/`；参考目录不会打包到运行素材中。
