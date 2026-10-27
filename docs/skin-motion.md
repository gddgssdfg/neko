# 皮肤动作参数与过渡（v0.6.0）

以下字段放在 skin.json 根对象，均为可选。旧皮肤缺失 motion / transitions 时保持原来的眨眼、起伏、切换和 72 DIP/s 行走行为。无需改 Core 插件接口。

```json
{
  "motion": {
    "walkStride": 10,
    "gazeMaxX": 0.55,
    "gazeMaxY": 0.3,
    "blinkMinSeconds": 2.5,
    "blinkMaxSeconds": 6.5,
    "typingBlinkMinSeconds": 1.8,
    "typingBlinkMaxSeconds": 3,
    "typingBob": 0.18,
    "typingBobPeriodSeconds": 1.6
  },
  "transitions": [
    { "from": "sleep", "to": "work", "via": "sit" },
    { "from": "sleep", "to": "idle", "via": "sit" }
  ]
}
```

## 单位与限制

- walkStride：每完整 walk 动画周期前进的距离，以 32×32 逻辑画幅为基准，范围 0–64。实际速度 = 步幅 × 显示倍率 × walk.fps ÷ walk 帧数 × 情绪播放倍率。0 表示不移动；缺省使用旧速度。请依据真实落脚动作测量，不要通过改速度掩盖缺失的步态。
- gazeMaxX / gazeMaxY：逻辑像素；最大为 0.65 / 0.35，0 可关闭对应方向位移。仍会进一步受素材眼眶安全范围和椭圆限幅约束。已有画死的眼睛不会自动获得眼球跟随。
- 四个眨眼间隔：秒，范围 0.5–60，最小值不得大于最大值。须有可用的眼睛图层，参数不会凭空生成眨眼素材。
- typingBob：0–0.5 逻辑像素，随显示倍率缩放；0 关闭。周期为 0.5–10 秒。只作用于 idle/work，约 180ms 平滑进入与收尾，直接交互立即停止。身体与眼层一起移动，建议在画幅边缘保留余量。

motion 中已知字段缺省使用保守默认值；显式非法类型、非有限值、越界值或反向间隔会拒绝该皮肤，并记录诊断。

## 过渡规则

transitions 最多 32 条，不允许重复 from/to；目标只允许 idle/sit/sleep/work，防止中间动画延迟拖拽等直接交互。来源允许这四种姿态，v0.6.1 起还允许 pet，供抚摸结束收势。from/to/via 都须引用已声明状态，via 的 fps 必须大于 0。中间动作沿用该皮肤统一 frames 与 layout，可复用 sit，也可声明单独的动作文件。使用 pet 来源的皮肤需要 v0.6.1 或更新宿主。

via 播放一次后，进入请求的目标状态；重复请求同一目标不会重新开始过渡。用户发出新的动作会取消旧过渡，旧动画完成后不会把新动作抢回。显式 Loop=false 不插入过渡，保持插件单次动画语义。缺少可解码中间动作时运行时直接播放目标；导入检查会拒绝缺图的包。

v0.6.1 的原始 pixel 皮肤已有五组专用四帧动作：sleep_enter（8fps）、wake_idle/wake_work（10fps）、pet_end_idle/pet_end_work（12fps）。新动作将眼睛直接绘入帧内，eyeLayer 为 null；最后一帧与中性目标姿态像素一致。其他皮肤仍沿用已有坐起帧，静态坐姿（fps=0）保持直接切换；没有把 32px 部件强行缩放替换其他画风。

## 行为保护

抚摸结束由宿主主循环计时，重复抚摸会重置最新反馈时长；暂停、隐藏和锁屏时不消耗反馈时长。新的动作请求会取消旧反应，工作模式结束后恢复 work。自动走动仍需用户主动启用。

验证预览：ReliabilityProbe 设置环境变量 NEKO_CAPTURE_CHARACTER 后，会从真实渲染器导出 pixel 与 pixel_redraw 的动作 GIF，保存在指定目录。预览用于检查姿态和衔接，不能代替默认大小和多 DPI 的实机观感检查。
