# B01 圆头橘猫

2026-10-24，用户在十只不同外观、相同坐姿和表情的橘猫候选中选择 B01。`B01.png` 和 `prompt.txt` 保存选定的生成参考。

正式素材由 `tools/generate_sprites.py` 在原生 32×32 网格重画，输出到 `neko-assets/pixel/`；保留圆脸、短耳、奶油胸口、白爪与卷尾，使用有限色硬边像素。

v0.2.9 更新十组四帧动作：待机、行走、坐下、睡眠、抚摸、拖拽、下落、攀爬、工作及新增落地。睡眠保持静态；落地压低、回弹后衔接待机。五个独立眼层通过 skin.json 的逐帧 eyeOffsets 跟随头部；行走声明原画朝左，宿主按移动方向镜像。帧率由清单统一管理。

复现：

```powershell
python tools/generate_sprites.py
python tools/gen_skin_previews.py pixel
python -m unittest discover -s tests -p test_skin_tools.py -v
```

预览在 `_eval/pixel-b01-actions/preview.png` 和 `preview.gif`；实际渲染帧在同目录 `runtime/`。参考目录不会打包到运行素材中。
