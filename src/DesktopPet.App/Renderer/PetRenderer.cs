using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopPet.Core;
using DesktopPet.App.Core;

namespace DesktopPet.App.Renderer;

/// <summary>
/// 像素渲染器：用 DrawingVisual + 雪碧图绘制宠物。
/// 眼球独立分层叠加，眼神追踪偏移由渲染器内部维护。
/// </summary>
public sealed class PetRenderer
{
    private SpriteAtlas _atlas;   // v0.1.48：去掉 readonly（支持 SetAtlas 热切换皮肤）
    private readonly FrameAnimator _animator = new();
    private readonly Random _rng = new();
    private bool _landing;
    private bool _walkFacingRight;

    // 眼球偏移（眼神追踪）
    private Vector _eyeOffset;
    // v0.1.57：眨眼 3 步序列 open → half（半闭）→ closed → open。total ~150ms，半闭软化硬切
    // v0.1.58(任务5)：相位推进改用"绝对截止时间"——Half/Closed 的结束时刻都锚定 _blinkStartMs，
    // 由真实时钟（每 tick 累加宿主传入的真实 dt）判定到点；不再"每 tick 加固定步长"，
    // DispatcherTimer 抖动不会累积成相位时长误差（相位只受 tick 粒度量化，活动期 50ms → 显示 ~100/50ms）。
    private enum BlinkPhase { Open, Half, Closed }
    private BlinkPhase _blinkPhase = BlinkPhase.Open;
    private double _clockMs;       // 渲染器内部单调毫秒钟：每 tick 累加真实 elapsed，非 tick 计数
    private double _blinkStartMs;  // 本次眨眼开始（_clockMs 基准）；Half 结束 = start+80，Open 结束 = start+140
    private double _nextBlinkAtMs; // 下次眨眼开始（_clockMs 基准）；Open 相位下到达即触发
    private double _moodIntensity = 1.0; // v0.1.1 fix：必须显式初始化为 1.0，否则默认 0 → Playback=0 → fps=0 → FrameAnimator.Tick 早返回 → 帧永远停在 0（SetMood 只在插件主动调时才设）
    // v0.1.59(P4)：闪眼/打字活跃的截止时间由 DateTime.Now 改为 _clockMs 基准（与眨眼口径统一）。
    // 语义不变（都是"从现在起持续 ms 毫秒"），但不再受系统时钟跳变影响，且与 Tick 单调时钟一致。
    private double _flashEyesUntilMs;   // 闪眼截止（_clockMs 基准）；默认 0，_clockMs 从 0 起单调增 → 初始即视为已过期
    private string? _speechText;
    private double _speechTimer;

    // v0.1.47：打字互动（猫对键盘活动的反应）
    private double _typingUntilMs;                      // 打字活跃截止（_clockMs 基准）；GlobalInput 每 300ms 节流刷新
    private double _breathOffset;                       // 身体 Y 偏移（呼吸/打字晃动感）
    private double _eyeLookDownOffset = 1.5;            // 打字时眼球下移（"看键盘"感）

    /// <summary>帧变化时触发，宿主据此重绘</summary>
    public event Action? RenderInvalidated;

    /// <summary>v0.1.40：Say 触发时通知宿主显示像素云朵气泡（text, durationMs）。
    /// 插件调 Pet.Say → 本事件 → PetWindow → BubbleWindow。</summary>
    public event Action<string, int>? SpeechRequested;

    /// <summary>v0.1.57：眨眼 3 步序列 open → half(80ms) → closed(60ms) → open；总 ~150ms（与原 100ms 持平/略长，Phase 内平滑）
    /// HasTransientVisual 揭示"瞬时活动"——宿主据此保持主循环高频 tick（位移/拖拽/打字外加 blink/flash/speech）。</summary>
    public bool HasTransientVisual =>
        _landing ||
        _blinkPhase != BlinkPhase.Open ||
        _clockMs < _typingUntilMs ||
        _clockMs < _flashEyesUntilMs ||
        _speechTimer > 0;

    public bool IsLanding => _landing;

    /// <summary>原画行走朝左；向右移动时整套身体、眼层和遮罩一并镜像。</summary>
    public void SetWalkDirection(double velocityX)
    {
        var right = velocityX > 0;
        if (_walkFacingRight == right) return;
        _walkFacingRight = right;
        if (_animator.State?.Name == "walk") RenderInvalidated?.Invoke();
    }

    // v0.1.58(任务5)：眨眼相位时长（毫秒）。锚定 _blinkStartMs：Half 到 start+HalfMs、Open 到 start+TotalMs。
    // 名义值 80/60ms（总 140ms）；活动期 tick 50ms 量化后实际显示约 Half 100ms + Closed 50ms = 150ms。
    private const double BlinkHalfMs = 80;
    private const double BlinkClosedMs = 60;
    private const double BlinkTotalMs = BlinkHalfMs + BlinkClosedMs;

    public PetRenderer(SpriteAtlas atlas)
    {
        _atlas = atlas;
        // 首次眨眼安排在启动后 2~6s（_clockMs 从 0 起）
        _nextBlinkAtMs = 2000 + _rng.NextDouble() * 4000;
    }

    /// <summary>v0.1.48：热切换皮肤（运行时换 atlas）。清 _animator 缓存强制下帧从新 atlas 拉帧。</summary>
    public void SetAtlas(SpriteAtlas atlas)
    {
        _atlas = atlas;
        _socketBrushCache.Clear();
        _landing = false;
        _animator.SetState(null);
        RenderInvalidated?.Invoke();
    }

    public SpriteAtlas Atlas => _atlas;
    public double Scale { get; set; } = 3.2; // 与 AppConfig.Scale 默认值保持一致（32px ×3.2 ≈ 102px）

    public void Play(PetState state, bool loop = true)
    {
        _landing = false;
        var name = state.ToString().ToLowerInvariant();
        var restart = !loop || _animator.IsComplete || _animator.Loop != loop;
        _animator.Loop = loop;
        _animator.SetState(_atlas.States.GetValueOrDefault(name), restart);
        RenderInvalidated?.Invoke();
    }

    /// <summary>皮肤可选的落地缓冲动作，播放一次后回待机；旧皮肤保持原行为。</summary>
    public void PlayLanding()
    {
        if (!_atlas.States.TryGetValue("land", out var state)) return;
        _landing = true;
        _animator.Loop = false;
        _animator.SetState(state, restart: true);
        RenderInvalidated?.Invoke();
    }

    public void SetMood(PetMood mood)
    {
        // v1：情绪影响帧率倍率，后续扩展
        _moodIntensity = mood switch
        {
            PetMood.Happy => 1.3,
            PetMood.Sleepy => 0.6,
            _ => 1.0,
        };
    }

    /// <summary>v0.1.47：标记打字活跃（身体晃动 + 频繁眨眼 + 眼球下移看键盘），持续到 now+duration。
    /// PetWindow.OnInputReaction 每 300ms 节流调用一次，保持打字时猫一直"有反应"。</summary>
    public void SetTypingActive(TimeSpan duration)
    {
        // v0.1.59(P4)：截止时间改用 _clockMs 基准（+ms）；下次 Tick 会把它推进到真实时刻。
        var until = _clockMs + duration.TotalMilliseconds;
        if (until > _typingUntilMs) _typingUntilMs = until;
    }

    public void Say(string text, int durationMs)
    {
        _speechText = text;
        _speechTimer = durationMs / 1000.0;
        // v0.1.40：转发给宿主渲染像素云朵气泡（_speechText 保留为旧兼容，Draw 不再直接画）
        SpeechRequested?.Invoke(text, durationMs);
    }

    public void Tick(TimeSpan delta)
    {
        // v0.1.58(任务5)：单调时钟先行累加真实 elapsed（眨眼/闪眼/打字全部锚定它）
        _clockMs += delta.TotalMilliseconds;

        _animator.Playback = _moodIntensity;
        var changed = _animator.Tick(delta);
        if (_landing && _animator.IsComplete)
        {
            Play(PetState.Idle);
            changed = true;
        }

        // v0.1.47：打字活跃检测 → 眨眼更频繁 + 身体呼吸晃动（看键盘效果在 Draw 里）
        bool typing = _clockMs < _typingUntilMs;
        double blinkMax = typing ? 1.6 : 6.0;   // 眨眼间隔上限：打字 1.6s / 闲置 6s（间隔分布见 ScheduleNextBlink）
        if (typing)
        {
            // sin 呼吸：幅度 1.5px，周期约 1.4s；身体随打字轻微起伏（用 _clockMs 秒做相位，行为等价）
            _breathOffset = Math.Sin(_clockMs / 1000.0 * 4.5) * 1.5;
        }
        else
        {
            _breathOffset = 0;
        }

        // v0.1.58(任务5)：眨眼 3 步序列，按"绝对截止时间 + 真实时钟"推进。
        // 每 tick 先把真实 elapsed 累进 _clockMs（等价单调毫秒钟），再与锚定 _blinkStartMs 的
        // 截止时间比较：Half→Closed 在 start+80ms、Closed→Open 在 start+140ms。这样 DispatcherTimer
        // 抖动只会让相位切换"迟到到下一个 tick"，而不会逐 tick 累积成误差。
        switch (_blinkPhase)
        {
            case BlinkPhase.Open:
                if (_clockMs >= _nextBlinkAtMs)
                {
                    _blinkPhase = BlinkPhase.Half;
                    _blinkStartMs = _clockMs;
                    changed = true;
                }
                break;
            case BlinkPhase.Half:
                if (_clockMs - _blinkStartMs >= BlinkTotalMs)
                {
                    // UI 卡顿导致单个长 tick 直接越过整个眨眼窗口：不补显示 Closed，直接回 Open
                    ScheduleNextBlink(blinkMax);
                    changed = true;
                }
                else if (_clockMs - _blinkStartMs >= BlinkHalfMs)
                {
                    _blinkPhase = BlinkPhase.Closed;
                    changed = true;
                }
                break;
            case BlinkPhase.Closed:
                if (_clockMs - _blinkStartMs >= BlinkTotalMs)
                {
                    _blinkPhase = BlinkPhase.Open;
                    ScheduleNextBlink(blinkMax);
                    changed = true;
                }
                break;
        }

        // 气泡计时
        if (_speechTimer > 0)
        {
            _speechTimer -= delta.TotalSeconds;
            if (_speechTimer <= 0) _speechText = null;
        }

        if (changed) RenderInvalidated?.Invoke();
    }

    /// <summary>v0.1.58(任务5)：眨眼结束回到 Open 后，安排下一次眨眼绝对时刻（_clockMs 基准）。
    /// 间隔语义与 v0.1.57 一致：闲置 uniform(2,6]s；打字 uniform(1.6,2]s（更频繁）。</summary>
    private void ScheduleNextBlink(double blinkMax)
    {
        _blinkPhase = BlinkPhase.Open;
        var lo = Math.Min(2.0, blinkMax);
        var hi = Math.Max(2.0, blinkMax);
        _nextBlinkAtMs = _clockMs + (lo + _rng.NextDouble() * (hi - lo)) * 1000.0;
    }

    /// <summary>绘制当前帧到 DrawingContext</summary>
    public void Draw(DrawingContext dc)
    {
        var state = _animator.State;
        if (state is null) return;
        var mirror = state.Name == "walk" && state.Facing switch
        {
            SkinFacing.Left => _walkFacingRight,
            SkinFacing.Right => !_walkFacingRight,
            _ => false,
        };
        if (mirror) dc.PushTransform(new MatrixTransform(-1, 0, 0, 1, SpriteAtlas.FrameW * Scale, 0));

        var frame = state.Frames[_animator.FrameIndex];
        // v0.1.47：身体 Y 偏移（打字时呼吸/晃动感）
        var target = new Rect(0, _breathOffset, SpriteAtlas.FrameW * Scale, SpriteAtlas.FrameH * Scale);

        // 身体帧
        dc.DrawImage(frame.Bitmap, target);

        // 眼球层（叠加）—— 键盘反应瞪眼优先，其次 3 步眨眼相位（half/closed），最后正常眼球层。
        // v0.1.86：眨眼相位**优先用皮肤自己的 per-state 层**（`{state.EyeLayer}_half` / `_closed`），
        // 再退回**全局** blink 层（eyes_half / eyes_blink，仅像素系皮肤有），最后退回状态睁眼层。
        //   为什么需要 per-state：一套皮肤里 idle 与 sit 的眼睛位置不同，**一个全局 blink 层必然
        //   对其中一个错位**（这也正是「新皮肤要么没有眨眼、要么眨眼错位」的死结）。per-state 闭合层
        //   由「同一张静图的闭眼版」生成（见 tools/build_skin_v2.py），与身体帧同源同位置 → 天然对齐。
        // ⚠️ 所有「按键名取眼球层」处都套「取不到 → 退下一级」兜底：皮肤未提供某键时若直接得到 null，
        //   会把整层丢弃 → 露出「被抹掉瞳孔的身体帧」→ 眼睛短暂空白。缺素材 = 该特效/相位不生效
        //   （眼睛保持有神），而不是"变空白"。以后新增按键名取层处务必照此兜底。
        if (state.EyeLayer is not null)
        {
            BitmapSource? eye;
            string? eyeKey;
            if (_clockMs < _flashEyesUntilMs)
            {
                // 瞪眼（键盘反应）：eyes_scared → 本状态眼球层
                eyeKey = FirstEyeKey("eyes_scared", state.EyeLayer);
                eye = eyeKey is null ? null : _atlas.GetEye(eyeKey);
            }
            else
            {
                eyeKey = _blinkPhase switch
                {
                    // 半闭：per-state `_half` → per-state `_closed`（无半闭就用完全闭合顶）→ 全局 → 状态层
                    BlinkPhase.Half => FirstEyeKey(state.EyeLayer + "_half", state.EyeLayer + "_closed",
                                                   "eyes_half", "eyes_blink", state.EyeLayer),
                    // 闭合：per-state `_closed` → 全局 eyes_blink → 状态层
                    BlinkPhase.Closed => FirstEyeKey(state.EyeLayer + "_closed", "eyes_blink", state.EyeLayer),
                    _ => FirstEyeKey(state.EyeLayer),
                };
                eye = eyeKey is null ? null : _atlas.GetEye(eyeKey);
            }
            if (eye is not null && eyeKey is not null)
            {
                // Anchor motion follows the head, while gaze remains relative to that anchor.
                // Apply the transform to the layer, clipping and socket mask together.
                var anchor = state.EyeOffsets.Count > _animator.FrameIndex ? state.EyeOffsets[_animator.FrameIndex] : default;
                dc.PushTransform(new TranslateTransform(anchor.X * SpriteAtlas.FrameW * Scale / eye.PixelWidth,
                    _breathOffset + anchor.Y * SpriteAtlas.FrameH * Scale / eye.PixelHeight));
                // 眼神偏移：眼球层整体平移；v0.1.47 打字时眼球下移（看键盘）
                double eyeDown = (_clockMs < _typingUntilMs) ? _eyeLookDownOffset : 0;
                var offset = ScreenEyeOffset(eyeKey, eyeDown);
                if (mirror) offset = new Vector(-offset.X, offset.Y);
                var eyeRect = new Rect(offset.X, offset.Y, SpriteAtlas.FrameW * Scale, SpriteAtlas.FrameH * Scale);

                // v0.1.88：用「眼眶」（该状态的闭合层轮廓）当遮罩，把平移后的瞳孔**裁在眼内**。
                // 为什么仅限制位移还不够：高清皮的瞳孔几乎填满眼白（实测 cat_hd 瞳 29×29 vs 眶 34×32，
                // 白圈只有 2~5px）→ 只要一动就贴边。裁剪后瞳孔滑到边界自然被切掉（真猫斜视时虹膜
                // 也被眼睑遮挡），永远不会出现在眼眶之外 —— 即灯灯实报的「眼睛超出眼眶，看着太恐怖」。
                var socketBrush = SocketMaskBrush(state.EyeLayer, eyeKey);

                // v0.1.89（性能修复：高清皮肤卡顿）：把眼球层的绘制区裁到**该层非透明像素的包围盒**
                // （+ 当前眼神偏移 + 2px 余量）。此矩形覆盖所有可能产生非透明像素的位置，被裁掉的
                // 都是**已经透明**的像素 → 输出**逐像素不变**（故 SkinRenderProbe 的 offset=0 md5 不变）。
                // 收益：把 PushOpacityMask 的中间表面 / 位图栅格化的面积从「整画幅」（cat_hd 128×128）
                // 缩到「眼睛实际占用区」（cat_hd 约 44×20），遮罩开销几乎归零。
                //   实测（_bench/RenderBench, cat_hd idle, scale=4, NearestNeighbor）：
                //     眼睛绘制：无遮罩 252µs / 遮罩(全画幅) 588µs / 遮罩+裁到眼bbox **89µs**。
                // 仅当皮肤提供该层 bbox 时裁剪（新管线皮肤才有）；旧皮肤 TryGetEyeBBox=false → 路径不变。
                var clip = EyeClipRect(eye, eyeKey, offset);
                if (clip is not null) dc.PushClip(new RectangleGeometry(clip.Value));
                if (socketBrush is not null) dc.PushOpacityMask(socketBrush);
                dc.DrawImage(eye, eyeRect);
                if (socketBrush is not null) dc.Pop();
                if (clip is not null) dc.Pop();
                dc.Pop();
            }
        }

        // 阴影（底部）
        if (_atlas.Shadow is not null)
        {
            var sh = new Rect(
                SpriteAtlas.FrameW * Scale * 0.5 - 16 * Scale,
                SpriteAtlas.FrameH * Scale - 6 * Scale,
                32 * Scale, 16 * Scale);
            dc.DrawImage(_atlas.Shadow, sh);
        }
        if (mirror) dc.Pop();
    }

    /// <summary>v0.1.88：眼眶遮罩画刷缓存（key = 眼球层名 + Scale）。每帧新建画刷会拖慢渲染，故缓存并 Freeze。</summary>
    private readonly Dictionary<string, ImageBrush?> _socketBrushCache = new();

    /// <summary>
    /// v0.1.88：取「眼眶遮罩」—— 该状态闭合层（`{eyeLayer}_closed`）的 alpha 轮廓，**固定在原坐标、不随瞳孔平移**。
    /// 用它做 <c>PushOpacityMask</c> 即可把平移后的瞳孔裁在眼内。
    /// <para>返回 null 表示不该裁：皮肤没有闭合层（旧皮肤 → 完全保持原行为），或当前画的**就是**闭合层本身
    /// （闭眼时整只眼都在动，裁它没意义）。</para>
    /// </summary>
    private ImageBrush? SocketMaskBrush(string? stateEyeLayer, string currentKey)
    {
        if (string.IsNullOrEmpty(stateEyeLayer)) return null;
        if (currentKey == stateEyeLayer + "_closed") return null;      // 闭眼层自己不需要裁
        string cacheKey = stateEyeLayer + "@" + Scale.ToString("0.##");
        if (_socketBrushCache.TryGetValue(cacheKey, out var cached)) return cached;

        ImageBrush? brush = null;
        var socket = _atlas.GetEye(stateEyeLayer + "_closed");
        if (socket is not null)
        {
            // ⚠️ Viewport 必须用**绝对**坐标锚在画幅原点：默认的相对模式会把遮罩跟着被绘制的矩形一起平移，
            //    那样等于没裁（瞳孔带着遮罩跑）。
            brush = new ImageBrush(socket)
            {
                Stretch = Stretch.Fill,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, SpriteAtlas.FrameW * Scale, SpriteAtlas.FrameH * Scale),
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
            };
            brush.Freeze();
        }
        _socketBrushCache[cacheKey] = brush;
        return brush;
    }

    /// <summary>
    /// v0.1.89（性能）：眼球层绘制区的裁剪矩形 = 该层非透明像素包围盒 → 目标坐标，再加当前眼神偏移与余量。
    /// <para>为什么安全：bbox 之外的源像素全透明，裁掉它们不改变任何输出像素；故渲染结果与不裁剪**逐像素一致**。</para>
    /// <para>为什么高效：它把 <c>PushOpacityMask</c> 需要的中间表面与位图栅格化限制在眼睛的小矩形内。</para>
    /// <para>返回 null 表示该层无 bbox 数据（旧皮肤）→ 调用方不做裁剪，保持既有渲染路径。</para>
    /// </summary>
    private Rect? EyeClipRect(BitmapSource eye, string eyeKey, Vector offset)
    {
        if (!_atlas.TryGetEyeBBox(eyeKey, out var bb) || bb.Width <= 0 || bb.Height <= 0) return null;
        double sx = SpriteAtlas.FrameW * Scale / eye.PixelWidth;    // 源像素 → 屏幕像素（X）
        double sy = SpriteAtlas.FrameH * Scale / eye.PixelHeight;   // 源像素 → 屏幕像素（Y）
        var r = new Rect(
            offset.X + bb.X * sx, offset.Y + bb.Y * sy,
            bb.Width * sx, bb.Height * sy);
        r.Inflate(2, 2);   // 余量：覆盖重采样/抗锯齿在边缘向外扩散的 1~2px
        return r;
    }

    /// <summary>按顺序返回第一个**实际存在**的眼球层 key（全都不存在则 null）。
    /// 兜底链的核心语义：缺素材 = 该相位不生效（眼睛保持有神），而不是把整层丢掉变空白。</summary>
    private string? FirstEyeKey(params string?[] keys)
    {
        foreach (var k in keys)
            if (!string.IsNullOrEmpty(k) && _atlas.GetEye(k!) is not null) return k;
        return null;
    }

    /// <summary>Bound gaze relative to the head. Missing socket data must never mean unlimited movement.</summary>
    private Vector ScreenEyeOffset(string eyeKey, double eyeDownLogical)
    {
        // Eyelids and smiling eyes are facial expressions, not sliding pupils.
        if (eyeKey is "eyes_blink" or "eyes_pet" || eyeKey.EndsWith("_closed", StringComparison.OrdinalIgnoreCase)
            || eyeKey.EndsWith("_half", StringComparison.OrdinalIgnoreCase) || _animator.State?.Name == "pet")
            return default;
        double k = _atlas.SourcePixelsPerLogical;
        var safe = _atlas.GetEyeSafeShift(eyeKey);
        double maxX = 0.65 * k, maxY = 0.35 * k;
        if (safe.X > 0 || safe.Y > 0) { maxX = Math.Min(maxX, safe.X); maxY = Math.Min(maxY, safe.Y); }
        double x = Math.Clamp(_eyeOffset.X*k, -maxX, maxX);
        double y = Math.Clamp((_eyeOffset.Y+eyeDownLogical)*k, -maxY, maxY);
        // Elliptical travel avoids exaggerated corner/diagonal looks.
        double radius = Math.Sqrt((maxX > 0 ? x*x/(maxX*maxX) : 0) + (maxY > 0 ? y*y/(maxY*maxY) : 0));
        if (radius > 1) { x /= radius; y /= radius; }
        return new Vector(x*Scale/k, y*Scale/k);
    }

    /// <summary>眼神追踪：设置眼球偏移（由宿主每帧从鼠标位置计算）。
    /// v0.1.59(⑩)：新增 gain 参数——鼠标路过张望时放大偏移（增益 > 1），默认 1.0 行为不变。</summary>
    public void SetEyeOffset(Vector normalizedDirection, double gain = 1.0)
    {
        if (!double.IsFinite(normalizedDirection.X) || !double.IsFinite(normalizedDirection.Y)) normalizedDirection = default;
        if (normalizedDirection.LengthSquared > 1) normalizedDirection.Normalize();
        gain = double.IsFinite(gain) ? Math.Clamp(gain, 0, 2) : 1;
        var next = normalizedDirection * 2 * gain; // 基础限幅 ±2px，按 gain 缩放
        // v0.1.82：**偏移真的变了才重绘**。
        // 坑：eye offset 只在 Draw 里被读，而重绘原先只由「动画帧推进 / 眨眼相位切换」触发。
        // 静止皮肤（待机/坐/睡的 fps = 0）下动画永不推进 —— 于是眼神追踪会「算了但永远不画」，
        // 表现为"鼠标动、眼睛不动"的静默失效（不报错、日志也干净）。
        // 这里按子像素容差判断（<0.01）避免每 tick 都重绘，同时保证静止皮肤下眼神真的跟得上。
        if (Math.Abs(next.X - _eyeOffset.X) < 0.01 && Math.Abs(next.Y - _eyeOffset.Y) < 0.01) return;
        _eyeOffset = next;
        RenderInvalidated?.Invoke();
    }

    /// <summary>键盘反应（BongoCat 借鉴）：短暂"瞪眼"——眼球层切到 scared，到期自动恢复</summary>
    public void FlashEyes(int ms)
    {
        // v0.1.59(P4)：截止时间改用 _clockMs 基准（+ms）
        _flashEyesUntilMs = _clockMs + ms;
        RenderInvalidated?.Invoke();
    }
}
